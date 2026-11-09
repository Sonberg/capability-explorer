using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Explorer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class TaskQueryProcessTests
{
    [Fact]
    public async Task All_six_granted_operations_match_http_for_identical_inputs_with_process_local_snapshot_pins()
    {
        await using var fixture = await TaskQueryTestHost.CreateAsync();
        using var factory = new GrantedTaskFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var process = Start(fixture.Root);
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Exchange(process, 1, "initialize", new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "synthetic-parity-test", version = "1" }
            });
            await Send(process, new { jsonrpc = "2.0", method = "notifications/initialized" });
            var snapshotCall = await Exchange(process, 2, "tools/call", new { name = "snapshot_resolve", arguments = new { } });
            var snapshotEnvelope = snapshotCall.GetProperty("result").GetProperty("structuredContent");
            var httpSnapshot = await client.GetFromJsonAsync<JsonElement>("/api/tasks/v1/snapshot");
            AssertEquivalent(httpSnapshot, snapshotEnvelope);
            var snapshot = snapshotEnvelope.GetProperty("snapshot");
            var id = snapshot.GetProperty("id").GetString()!;
            var generation = snapshot.GetProperty("generation").GetInt64();
            var httpSnapshotId = httpSnapshot.GetProperty("snapshot").GetProperty("id").GetString()!;
            var pin = $"snapshot={Uri.EscapeDataString(httpSnapshotId)}&generation={generation}";
            const string key = "http|forecast-cs/Forecast.Api|GET /api/v2/forecasts/{}";
            const string path = "src/Forecast.Api/Forecast.Api.csproj";
            var calls = new (string Tool, object Arguments, string Route)[]
            {
                ("models_list", new { snapshotId = id, generation, limit = 50 }, $"/models?{pin}&limit=50"),
                ("decision_references", new { snapshotId = id, generation, modelId = fixture.BpmnModelId }, $"/models/decision-references?{pin}&id={fixture.BpmnModelId}"),
                ("contract_consumers", new { snapshotId = id, generation, key }, $"/contracts/consumers?{pin}&endpoint={Uri.EscapeDataString(key)}"),
                ("evidence_list", new { snapshotId = id, generation, key }, $"/evidence?{pin}&key={Uri.EscapeDataString(key)}"),
                ("source_excerpt", new { snapshotId = id, generation, repo = "forecast-cs", commit = TaskQueryTestHost.ForecastCommit, path, start = 1, end = 3 },
                    $"/source?{pin}&repo=forecast-cs&commit={TaskQueryTestHost.ForecastCommit}&path={Uri.EscapeDataString(path)}&start=1&end=3")
            };
            var requestId = 3;
            foreach (var (tool, arguments, route) in calls)
            {
                var call = await Exchange(process, requestId++, "tools/call", new { name = tool, arguments });
                var raw = call.GetProperty("result").GetProperty("structuredContent");
                var http = await client.GetFromJsonAsync<JsonElement>("/api/tasks/v1" + route);
                AssertEquivalent(http, raw);
                Assert.Contains(raw.GetProperty("status").GetString(), new[] { "ok", "partial" });
                Assert.True(fixture.EnvelopeSchema.Evaluate(raw).IsValid);
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Now listening", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            await StopIfRunning(process);
        }
    }

    private static void AssertEquivalent(JsonElement http, JsonElement mcp)
    {
        var left = JsonNode.Parse(http.GetRawText())!;
        var right = JsonNode.Parse(mcp.GetRawText())!;
        // Files-mode snapshot IDs are process-local epochs, not portable content hashes.
        foreach (var root in new[] { left, right })
            if (root["snapshot"] is JsonObject snapshot)
            {
                Assert.Equal(snapshot["id"]!.GetValue<string>(), snapshot["currentId"]!.GetValue<string>());
                snapshot["id"] = "process-local";
                snapshot["currentId"] = "process-local";
            }
        Normalize(left);
        Normalize(right);
        Assert.True(JsonNode.DeepEquals(left, right), $"HTTP: {left}\nMCP: {right}");

        static void Normalize(JsonNode node)
        {
            if (node is JsonObject obj)
                foreach (var (key, value) in obj.ToList())
                {
                    if (value is null) obj.Remove(key);
                    else Normalize(value);
                }
            else if (node is JsonArray array)
                foreach (var value in array)
                    if (value is not null) Normalize(value);
        }
    }

    [Fact]
    public async Task Raw_stdio_handshake_tools_and_denial_match_the_real_http_entry_then_exit_on_eof()
    {
        using var process = Start();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var initialized = await Exchange(process, 1, "initialize", new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "independent-raw-test", version = "1" }
            });
            Assert.True(initialized.TryGetProperty("result", out _), initialized.ToString());
            await Send(process, new { jsonrpc = "2.0", method = "notifications/initialized" });
            var tools = await Exchange(process, 2, "tools/list", new { });
            Assert.Equal(new[]
            {
                "contract_consumers", "decision_references", "evidence_list",
                "models_list", "snapshot_resolve", "source_excerpt"
            }, tools.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()).Order(StringComparer.Ordinal));

            var call = await Exchange(process, 3, "tools/call", new
            {
                name = "snapshot_resolve",
                arguments = new { }
            });
            var envelope = call.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal("denied", envelope.GetProperty("status").GetString());

            using var factory = new DeniedTaskFactory();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/tasks/v1/snapshot");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var http = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var field in new[] { "schema", "operation", "status", "mode", "reason" })
                Assert.Equal(http.RootElement.GetProperty(field).ToString(), envelope.GetProperty(field).ToString());

            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await process.StandardOutput.ReadToEndAsync());
            Assert.DoesNotContain("Now listening", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            await StopIfRunning(process);
        }
    }

    [Fact]
    public async Task Malformed_raw_tool_arguments_return_invalid_instead_of_defaulting_to_current_selection()
    {
        using var process = Start();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Exchange(process, 1, "initialize", new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "independent-raw-test", version = "1" }
            });
            await Send(process, new { jsonrpc = "2.0", method = "notifications/initialized" });
            var call = await Exchange(process, 2, "tools/call", new
            {
                name = "models_list",
                arguments = new { snapshotId = 123, generation = "1" }
            });
            Assert.True(call.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Equal("invalid", call.GetProperty("result").GetProperty("structuredContent").GetProperty("status").GetString());
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            await StopIfRunning(process);
            await stderr;
        }
    }

    [Fact]
    public async Task Oversized_raw_line_terminates_the_transport_without_a_success_response()
    {
        using var process = Start();
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(new string('x', 65_537));
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("", await stdout);
            Assert.Contains("line", await stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await StopIfRunning(process);
        }
    }

    [Fact]
    public async Task Raw_stdio_cancellation_aborts_a_running_git_read_without_a_response_and_keeps_the_process_usable()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var fixture = await TaskQueryTestHost.CreateAsync();
        var shimDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "git-shim")).FullName;
        var started = Path.Combine(fixture.Root, "git-started");
        var gitPidFile = Path.Combine(fixture.Root, "git-pid");
        var finished = Path.Combine(fixture.Root, "git-finished");
        const string path = "src/Forecast.Api/Forecast.Api.csproj";
        var target = TaskQueryTestHost.ForecastCommit + ":" + path;
        var wrapper = Path.Combine(shimDirectory, "git");
        await File.WriteAllTextAsync(wrapper, """
            #!/bin/sh
            case "$*" in
              *"$CE_GIT_TARGET"*)
                printf '%s\n' "$$" > "$CE_GIT_PID"
                : > "$CE_GIT_STARTED"
                sleep 30
                : > "$CE_GIT_FINISHED"
                ;;
            esac
            exec "$CE_REAL_GIT" "$@"
            """);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var process = Start(fixture.Root, shimDirectory, new Dictionary<string, string>
        {
            ["CE_REAL_GIT"] = FindExecutable("git"),
            ["CE_GIT_TARGET"] = target,
            ["CE_GIT_STARTED"] = started,
            ["CE_GIT_PID"] = gitPidFile,
            ["CE_GIT_FINISHED"] = finished,
        });
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Exchange(process, 1, "initialize", new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "raw-cancellation-test", version = "1" }
            });
            await Send(process, new { jsonrpc = "2.0", method = "notifications/initialized" });
            var snapshotCall = await Exchange(process, 2, "tools/call", new { name = "snapshot_resolve", arguments = new { } });
            var snapshot = snapshotCall.GetProperty("result").GetProperty("structuredContent").GetProperty("snapshot");

            await Send(process, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = "source_excerpt",
                    arguments = new
                    {
                        snapshotId = snapshot.GetProperty("id").GetString(),
                        generation = snapshot.GetProperty("generation").GetInt64(),
                        repo = "forecast-cs",
                        commit = TaskQueryTestHost.ForecastCommit,
                        path,
                        start = 1,
                        end = 3,
                    }
                }
            });
            await WaitForFile(started, TimeSpan.FromSeconds(10));
            await Send(process, new
            {
                jsonrpc = "2.0",
                method = "notifications/cancelled",
                @params = new { requestId = 3, reason = "cancel the in-flight source read" }
            });

            var pendingResponse = process.StandardOutput.ReadLineAsync();
            var cancellationWindow = await Task.WhenAny(pendingResponse, Task.Delay(300));
            Assert.NotSame(pendingResponse, cancellationWindow);
            var gitPid = int.Parse(await File.ReadAllTextAsync(gitPidFile), System.Globalization.CultureInfo.InvariantCulture);
            await WaitForProcessExit(gitPid, TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(finished), "The cancelled Git command continued past the cancellation notification.");

            await Send(process, new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "snapshot_resolve", arguments = new { } } });
            var resumedLine = await pendingResponse.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(resumedLine);
            using (var resumed = JsonDocument.Parse(resumedLine))
                Assert.Equal(4, resumed.RootElement.GetProperty("id").GetInt32());

            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Now listening", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            await StopIfRunning(process);
        }
    }

    private static Process Start(string? grantedFixture = null, string? gitShimDirectory = null,
        IReadOnlyDictionary<string, string>? additionalEnvironment = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Fixtures.RepositoryRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var key in start.Environment.Keys.Where(key =>
                     key.StartsWith("TaskQueries", StringComparison.OrdinalIgnoreCase) ||
                     key is "EXPLORER_SOURCE" or "CE_DATABASE" or "ASPNETCORE_URLS").ToArray())
            start.Environment.Remove(key);
        if (gitShimDirectory is not null)
            start.Environment["PATH"] = gitShimDirectory + Path.PathSeparator + start.Environment["PATH"];
        if (additionalEnvironment is not null)
            foreach (var (key, value) in additionalEnvironment) start.Environment[key] = value;
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--mcp-stdio");
        start.ArgumentList.Add("--source");
        start.ArgumentList.Add("files");
        start.ArgumentList.Add("--artifacts");
        start.ArgumentList.Add(Fixtures.ExpectedDirectory);
        start.ArgumentList.Add("--capabilities");
        start.ArgumentList.Add(grantedFixture is null
            ? Path.Combine(Fixtures.RepositoryRoot, "fixtures", "task-process-absent-config.json")
            : Path.Combine(grantedFixture, "capabilities.json"));
        if (grantedFixture is not null)
            foreach (var (key, value) in GrantedSettings(grantedFixture))
            {
                start.ArgumentList.Add("--" + key);
                start.ArgumentList.Add(value);
            }
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the real task-query executable.");
    }

    private static async Task Send(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> Exchange(Process process, int id, string method, object parameters)
    {
        await Send(process, new { jsonrpc = "2.0", id, method, @params = parameters });
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line);
        Assert.Equal(id, document.RootElement.GetProperty("id").GetInt32());
        return document.RootElement.Clone();
    }

    private static async Task StopIfRunning(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static string FindExecutable(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(directory => Path.Combine(directory, name)).First(File.Exists);

    private static async Task WaitForFile(string path, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(path) && deadline.Elapsed < timeout) await Task.Delay(20);
        Assert.True(File.Exists(path), $"Timed out waiting for {Path.GetFileName(path)}.");
    }

    private static async Task WaitForProcessExit(int pid, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return;
            }
            catch (ArgumentException) { return; }
            await Task.Delay(20);
        }

        Assert.Fail($"Git process {pid} remained alive after cancellation.");
    }

    private sealed class DeniedTaskFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("source", "files");
            builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
            builder.UseSetting("capabilities", Path.Combine(Fixtures.RepositoryRoot, "fixtures", "task-process-absent-config.json"));
        }
    }

    private static Dictionary<string, string> GrantedSettings(string root)
    {
        var settings = new Dictionary<string, string>
        {
            ["source"] = "files",
            ["artifacts"] = Fixtures.ExpectedDirectory,
            ["capabilities"] = Path.Combine(root, "capabilities.json"),
            ["collections"] = Path.Combine(root, "bundle"),
            ["repos"] = Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"),
            ["workspace"] = Path.Combine(root, "workspace"),
            ["requests"] = Path.Combine(root, "requests")
        };
        for (var i = 0; i < TaskQueryOperations.All.Count; i++) settings[$"TaskQueries:Operations:{i}"] = TaskQueryOperations.All[i];
        var repos = new[] { "forecast-cs", "pricing-node", "api-cs", "worker-cs", "web-ts" };
        for (var i = 0; i < repos.Length; i++) settings[$"TaskQueries:Repositories:{i}"] = repos[i];
        return settings;
    }

    private sealed class GrantedTaskFactory(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            foreach (var (key, value) in GrantedSettings(root)) builder.UseSetting(key, value);
        }
    }
}
