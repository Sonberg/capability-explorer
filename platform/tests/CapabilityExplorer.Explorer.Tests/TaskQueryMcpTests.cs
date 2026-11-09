using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Explorer;
using Json.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Direct, non-SDK tests of <see cref="BoundedLineInputStream"/>'s bound: the SDK's own
/// <c>StreamServerTransport.ReadLineAsync</c> buffers an unbounded line and has no self-test for this, so these
/// assert the wrapper's own behaviour against a controlled inner stream rather than trusting the SDK sample.</summary>
public sealed class BoundedLineInputStreamTests
{
    [Fact]
    public async Task Lines_within_the_bound_pass_through_unmodified_and_the_bound_resets_at_each_newline()
    {
        var payload = "abcd\nabcd\n"u8.ToArray();
        await using var inner = new MemoryStream(payload);
        await using var bounded = new BoundedLineInputStream(inner, maxLineBytes: 4);

        var buffer = new byte[64];
        var read = await bounded.ReadAsync(buffer, CancellationToken.None);

        Assert.Equal(payload, buffer[..read]);
    }

    [Fact]
    public async Task A_single_line_exceeding_the_bound_faults_the_stream()
    {
        var payload = "abcde"u8.ToArray(); // 5 bytes, no newline, over a 4-byte bound
        await using var inner = new MemoryStream(payload);
        await using var bounded = new BoundedLineInputStream(inner, maxLineBytes: 4);

        await Assert.ThrowsAsync<IOException>(() => bounded.ReadAsync(new byte[64], CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task End_of_stream_passes_through_as_a_zero_length_read()
    {
        await using var inner = new MemoryStream([]);
        await using var bounded = new BoundedLineInputStream(inner);

        Assert.Equal(0, await bounded.ReadAsync(new byte[16], CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_of_a_pending_read_passes_through_unchanged()
    {
        await using var inner = new NeverCompletesStream();
        await using var bounded = new BoundedLineInputStream(inner);
        using var cts = new CancellationTokenSource();

        var pending = bounded.ReadAsync(new byte[16], cts.Token).AsTask();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    /// <summary>A read that never completes on its own, so cancellation is the only way it ever returns —
    /// exactly the shape of a blocked stdin read waiting on an operator who never sends another line.</summary>
    private sealed class NeverCompletesStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0; // unreachable: Task.Delay(Infinite, token) only ever completes by cancelling
        }
    }
}

/// <summary>Direct tests of <see cref="TaskQueryMcpGate"/>'s admission/deadline policy, isolated from any real
/// stdio transport so the four-slot/no-queue and ten-second-budget behaviour is verified in milliseconds, not by
/// inference from a slower end-to-end run.</summary>
public sealed class TaskQueryMcpGateTests
{
    private static TaskQueryEnvelope FakeOkEnvelope() => new(
        "models_list", "ok", "files", null, new TaskQueryCoverage([], "within-selection-complete"), null, null, []);

    [Fact]
    public async Task A_fifth_concurrent_call_is_rejected_immediately_not_queued_behind_the_four_admitted_calls()
    {
        using var gate = new TaskQueryMcpGate();
        var release = new TaskCompletionSource();
        var entered = Enumerable.Range(0, TaskQueryMcpGate.MaxConcurrent).Select(_ => new TaskCompletionSource()).ToArray();
        var admitted = entered.Select(signal => gate.RunAsync("models_list", "files", async token =>
        {
            signal.SetResult();
            await release.Task.WaitAsync(token);
            return FakeOkEnvelope();
        }, CancellationToken.None)).ToArray();

        await Task.WhenAll(entered.Select(e => e.Task)).WaitAsync(TimeSpan.FromSeconds(5));

        var clock = Stopwatch.StartNew();
        var fifth = await gate.RunAsync("models_list", "files",
            _ => throw new InvalidOperationException("a rejected call must never run its body"), CancellationToken.None);
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 1000, $"the fifth call waited {clock.ElapsedMilliseconds}ms; it must be rejected without waiting");
        Assert.Equal("limit", fifth.Status);
        Assert.Equal("concurrency-exceeded", fifth.Reason);

        release.SetResult();
        await Task.WhenAll(admitted);
    }

    [Fact]
    public async Task Releasing_an_admitted_call_frees_its_slot_for_the_next_caller()
    {
        using var gate = new TaskQueryMcpGate();
        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var first = gate.RunAsync("models_list", "files", async token =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token);
            return FakeOkEnvelope();
        }, CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Fill the remaining three slots and hold them so only releasing `first` can admit a new call.
        var holdRelease = new TaskCompletionSource();
        var others = Enumerable.Range(0, TaskQueryMcpGate.MaxConcurrent - 1)
            .Select(_ => gate.RunAsync("models_list", "files", async token =>
            {
                await holdRelease.Task.WaitAsync(token);
                return FakeOkEnvelope();
            }, CancellationToken.None)).ToArray();

        releaseFirst.SetResult();
        await first;

        var next = await gate.RunAsync("models_list", "files", _ => Task.FromResult(FakeOkEnvelope()), CancellationToken.None);
        Assert.Equal("ok", next.Status);

        holdRelease.SetResult();
        await Task.WhenAll(others);
    }

    [Fact]
    public async Task A_caller_abort_is_rethrown_not_swallowed_and_still_releases_the_slot()
    {
        using var gate = new TaskQueryMcpGate();
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource();

        var aborted = gate.RunAsync("models_list", "files", async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return FakeOkEnvelope();
        }, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);

        // The slot must be released even though the body was aborted by the caller, not by the gate's own deadline.
        var next = await gate.RunAsync("models_list", "files", _ => Task.FromResult(FakeOkEnvelope()), CancellationToken.None);
        Assert.Equal("ok", next.Status);
    }

    [Fact]
    public async Task A_body_that_outlives_the_ten_second_budget_is_reported_as_a_cancelled_time_budget_envelope()
    {
        using var gate = new TaskQueryMcpGate();
        var clock = Stopwatch.StartNew();

        var result = await gate.RunAsync("models_list", "files", async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return FakeOkEnvelope();
        }, CancellationToken.None);

        clock.Stop();
        Assert.Equal("cancelled", result.Status);
        Assert.Equal("time-budget", result.Reason);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(15));

        // The slot released on deadline must be immediately reusable.
        var next = await gate.RunAsync("models_list", "files", _ => Task.FromResult(FakeOkEnvelope()), CancellationToken.None);
        Assert.Equal("ok", next.Status);
    }
}

/// <summary>A genuine stdio-shaped MCP session: the real SDK <see cref="McpServer"/>/<see cref="McpClient"/> wired
/// over the same <see cref="BoundedLineInputStream"/>/<see cref="TaskQueryMcpGate"/>/tool-dispatch composition
/// <see cref="TaskQueryMcpHost.RunAsync"/> uses, but over in-process duplex pipes. Separate real-process tests
/// verify the actual stdio handshake, cancellation, and bounded response framing. This is independent
/// lifecycle/parity proof, not the SDK's own self-test and not a fake/golden-only check.</summary>
public sealed class TaskQueryMcpStdioTests
{
    [Fact]
    public void Worst_case_escaped_request_id_is_included_in_the_complete_mcp_response_bound()
    {
        var envelope = new TaskQueryEnvelope("models_list", "ok", "files", null,
            new TaskQueryCoverage([], "within-selection-complete"), null, new string('x', 350_000), []);
        var envelopeBytes = TaskQueryJson.SerializeBounded(envelope);
        using var document = JsonDocument.Parse(envelopeBytes);
        var unboundedResult = new CallToolResult
        {
            IsError = false,
            Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(envelopeBytes) }],
            StructuredContent = document.RootElement.Clone(),
        };
        var requestId = new string('<', 60_000);
        var rawRequest = $"{{\"jsonrpc\":\"2.0\",\"id\":\"{requestId}\",\"method\":\"tools/call\",\"params\":{{\"name\":\"snapshot_resolve\",\"arguments\":{{}}}}}}";
        Assert.InRange(Encoding.UTF8.GetByteCount(rawRequest), 1, BoundedLineInputStream.DefaultMaxLineBytes);

        var unboundedResponse = JsonSerializer.SerializeToUtf8Bytes(
            new { jsonrpc = "2.0", id = requestId, result = unboundedResult }, TaskQueryJson.Options);
        Assert.True(unboundedResponse.Length > TaskQueryJson.MaxEnvelopeBytes);

        var boundedResult = TaskQueryMcpHost.ToCallToolResult(envelope);
        var boundedResponse = JsonSerializer.SerializeToUtf8Bytes(
            new { jsonrpc = "2.0", id = requestId, result = boundedResult }, TaskQueryJson.Options);

        Assert.Equal("limit", boundedResult.StructuredContent!.Value.GetProperty("status").GetString());
        Assert.True(boundedResponse.Length <= TaskQueryJson.MaxEnvelopeBytes);
    }

    [Theory]
    [InlineData(300_000, "ok")]
    [InlineData(350_000, "limit")]
    public void Mcp_wire_bound_counts_text_and_structured_copies_without_reducing_http_envelope_limit(int dataLength, string expectedMcpStatus)
    {
        var envelope = new TaskQueryEnvelope("models_list", "ok", "files", null,
            new TaskQueryCoverage([], "within-selection-complete"), null, new string('x', dataLength), []);
        var httpBytes = TaskQueryJson.SerializeBounded(envelope);
        Assert.Equal("ok", JsonDocument.Parse(httpBytes).RootElement.GetProperty("status").GetString());
        Assert.True(httpBytes.Length < TaskQueryJson.MaxEnvelopeBytes);

        var result = TaskQueryMcpHost.ToCallToolResult(envelope);
        Assert.Equal(expectedMcpStatus, result.StructuredContent!.Value.GetProperty("status").GetString());
        Assert.True(TaskQueryJson.McpResultFitsWireBound(result));
        var serializedResult = JsonSerializer.SerializeToUtf8Bytes(result, TaskQueryJson.Options);
        Assert.True(serializedResult.Length + TaskQueryJson.McpResponseFramingBytes <= TaskQueryJson.MaxEnvelopeBytes);
    }

    [Fact]
    public async Task Tools_list_advertises_exactly_the_six_task_query_tools_with_object_input_schemas()
    {
        await using var session = await McpTestSession.StartAsync();

        var tools = await session.Client.ListToolsAsync();

        Assert.Equal(TaskQueryOperations.All.Count, tools.Count);
        Assert.Equal(
            TaskQueryOperations.All.Order(StringComparer.Ordinal),
            tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, t => Assert.Equal(JsonValueKind.Object, t.JsonSchema.ValueKind));
    }

    [Fact]
    public async Task A_tool_call_round_trips_a_schema_valid_envelope_as_both_text_and_structured_content()
    {
        await using var session = await McpTestSession.StartAsync();

        var result = await session.Client.CallToolAsync("snapshot_resolve", new Dictionary<string, object?>());

        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        var structured = result.StructuredContent!.Value;
        Assert.Equal("ok", structured.GetProperty("status").GetString());
        Assert.Equal("snapshot_resolve", structured.GetProperty("operation").GetString());

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var fromText = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal(structured.GetProperty("snapshot").GetProperty("id").GetString(), fromText.GetProperty("snapshot").GetProperty("id").GetString());

        // The same schema the HTTP envelope is checked against (AC6: HTTP and MCP share one sealed snapshot/shape).
        var schema = TaskQueryTestHost.LoadEnvelopeSchema();
        Assert.True(schema.Evaluate(structured, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid);
    }

    [Fact]
    public async Task An_unknown_tool_name_is_reported_as_a_tool_level_error_not_a_protocol_fault()
    {
        await using var session = await McpTestSession.StartAsync();

        var result = await session.Client.CallToolAsync("not_a_real_operation", new Dictionary<string, object?>());

        Assert.Equal(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("unknown tool", text, StringComparison.Ordinal);
    }

    /// <summary>Regression for finding #4: a wrong-typed, overflowed or unknown argument must be rejected BEFORE
    /// any retrieval, as an <c>invalid</c> envelope with a specific reason code - never silently coerced into a
    /// "field absent" default (the old behaviour this replaces).</summary>
    [Fact]
    public async Task Malformed_tool_arguments_are_rejected_as_invalid_before_any_retrieval()
    {
        await using var session = await McpTestSession.StartAsync();

        var unknown = await session.Client.CallToolAsync("models_list", new Dictionary<string, object?> { ["notARealField"] = "x" });
        Assert.NotNull(unknown.StructuredContent);
        Assert.Equal("invalid", unknown.StructuredContent!.Value.GetProperty("status").GetString());
        Assert.Equal("unknown-argument-notARealField", unknown.StructuredContent!.Value.GetProperty("reason").GetString());

        var wrongType = await session.Client.CallToolAsync("models_list", new Dictionary<string, object?> { ["limit"] = "not-a-number" });
        Assert.Equal("invalid", wrongType.StructuredContent!.Value.GetProperty("status").GetString());
        Assert.Equal("invalid-argument-limit", wrongType.StructuredContent!.Value.GetProperty("reason").GetString());

        var wrongKindString = await session.Client.CallToolAsync("models_list", new Dictionary<string, object?> { ["kind"] = 42 });
        Assert.Equal("invalid", wrongKindString.StructuredContent!.Value.GetProperty("status").GetString());
        Assert.Equal("invalid-argument-type-kind", wrongKindString.StructuredContent!.Value.GetProperty("reason").GetString());

        var overflow = await session.Client.CallToolAsync("models_list", new Dictionary<string, object?> { ["limit"] = 99999999999999999999.0 });
        Assert.Equal("invalid", overflow.StructuredContent!.Value.GetProperty("status").GetString());
        Assert.Equal("invalid-argument-limit", overflow.StructuredContent!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Caller_cancellation_during_a_call_yields_no_response_and_the_gate_still_recovers()
    {
        // The underlying operation here completes in microseconds, so a cancel issued "during" the round trip
        // races the response; pre-cancelling makes the caller-abort path deterministic end-to-end over the real
        // in-process transport. (The gate's own mid-flight-abort path — a body genuinely still running when the
        // caller cancels — is exercised deterministically against TaskQueryMcpGate directly, above.)
        await using var session = await McpTestSession.StartAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var call = session.Client.CallToolAsync("snapshot_resolve", new Dictionary<string, object?>(),
            cancellationToken: cts.Token).AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        // The gate's slot must still be free for the next caller even though this call's response never arrived.
        var next = await session.Client.CallToolAsync("snapshot_resolve", new Dictionary<string, object?>());
        Assert.NotEqual(true, next.IsError);
    }
}

/// <summary>Wires the real <see cref="McpServer"/> over one end of an in-process duplex pipe pair and connects a
/// real <see cref="McpClient"/> over the other end — the exact <see cref="TaskQueryMcpHost.CallToolAsync"/>/
/// <see cref="ToolCatalog"/> composition <c>RunAsync</c> uses, minus the console streams and generic host.</summary>
internal sealed class McpTestSession : IAsyncDisposable
{
    private readonly TaskQueryTestHost _host;
    private readonly TaskQueryMcpGate _gate;
    private readonly CancellationTokenSource _serverShutdown;
    private readonly Task _serverRun;

    public McpClient Client { get; }

    private McpTestSession(TaskQueryTestHost host, TaskQueryMcpGate gate, CancellationTokenSource serverShutdown, Task serverRun, McpClient client)
    {
        _host = host;
        _gate = gate;
        _serverShutdown = serverShutdown;
        _serverRun = serverRun;
        Client = client;
    }

    public static async Task<McpTestSession> StartAsync()
    {
        var host = await TaskQueryTestHost.CreateAsync();
        var gate = new TaskQueryMcpGate();

        // clientToServer: the client writes requests, the server reads them. serverToClient: the server writes
        // responses, the client reads them. Pipe() gives each direction its own reader/writer stream pair.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "capability-explorer-task-queries", NullLoggerFactory.Instance);
        var serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "capability-explorer-task-queries", Version = "1" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = ToolCatalog.Tools }),
                CallToolHandler = (context, token) => TaskQueryMcpHost.CallToolAsync(context, host.Service, gate, token),
            },
        };

        var serverShutdown = new CancellationTokenSource();
        var server = McpServer.Create(serverTransport, serverOptions, NullLoggerFactory.Instance, host.Services);
        var serverRun = server.RunAsync(serverShutdown.Token);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
        var client = await McpClient.CreateAsync(clientTransport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "task-query-mcp-tests", Version = "1" },
        }, NullLoggerFactory.Instance, CancellationToken.None);

        return new McpTestSession(host, gate, serverShutdown, serverRun, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _serverShutdown.CancelAsync();
        try { await _serverRun; } catch (OperationCanceledException) { /* expected shutdown */ }
        _serverShutdown.Dispose();
        _gate.Dispose();
        await _host.DisposeAsync();
    }
}
