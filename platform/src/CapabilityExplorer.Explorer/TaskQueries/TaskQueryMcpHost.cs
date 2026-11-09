using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace CapabilityExplorer.Explorer;

/// <summary>Stdio MCP front end for the six bounded task-query tools, over the same <see cref="TaskQueryService"/>
/// the HTTP front end (<see cref="TaskQueryApi"/>) uses. Wired directly against the low-level
/// <see cref="ModelContextProtocol.Server.McpServer"/>/<see cref="StreamServerTransport"/> surface the referenced
/// 2.4.0 SDK actually exposes (verified by reflection against the installed package; it has no ASP.NET-style
/// <c>AddMcpServer</c>/<c>With...Handler</c> hosting builder). The parent integrates this as the very first
/// statement in <c>Program.cs</c>: <c>if (args.Contains("--mcp-stdio")) { await TaskQueryMcpHost.RunAsync(args); return; }</c>
/// so nothing here ever shares a process with the web host.</summary>
public static class TaskQueryMcpHost
{
    public static async Task RunAsync(string[] args, CancellationToken shutdown = default)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // stdout is the JSON-RPC wire; every log line must go to stderr instead.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        var configuration = builder.Configuration;
        var options = ExplorerOptions.From(configuration);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<TaskQueryAccessPolicy>();
        builder.Services.AddSingleton<TaskQueryCursorCodec>();
        builder.Services.AddSingleton<TaskQueryMcpGate>();

        var source = configuration["source"] ?? Environment.GetEnvironmentVariable("EXPLORER_SOURCE") ?? "files";
        if (source is not ("files" or "postgres"))
            throw new InvalidOperationException("source must be files or postgres");

        if (source == "files")
        {
            var state = new ExplorerState(options);
            builder.Services.AddSingleton(state);
            builder.Services.AddHostedService(_ => state);
            builder.Services.AddSingleton<ITaskEvidenceSource, FileTaskEvidenceSource>();
        }
        else
        {
            var database = configuration["database"] ?? Environment.GetEnvironmentVariable("CE_DATABASE");
            if (database is not { Length: > 0 }) throw new InvalidOperationException("source=postgres requires CE_DATABASE");
            // Mirrors Program.cs's own postgres-mode guard exactly: the pinned invariant globalization runtime is
            // required for collation-stable Postgres text search, not just a files-mode convenience.
            if (!AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) || !invariant)
                throw new InvalidOperationException("PostgreSQL search requires the pinned invariant globalization runtime");
            var dataSource = NpgsqlDataSource.Create(database);
            builder.Services.AddSingleton(dataSource);
            builder.Services.AddSingleton<ExplorerQueryCursor>();
            builder.Services.AddSingleton<IExplorerQueries, PostgresExplorerQueries>();
            var dbConfiguration = new DatabaseExplorerConfiguration(options);
            builder.Services.AddSingleton(dbConfiguration);
            builder.Services.AddSingleton<ITaskEvidenceSource>(sp => new PostgresTaskEvidenceSource(
                sp.GetRequiredService<IExplorerQueries>(), sp.GetRequiredService<NpgsqlDataSource>(), dbConfiguration)
                .WithGit(dbConfiguration.Git));
        }

        builder.Services.AddSingleton<TaskQueryService>();

        using var host = builder.Build();
        await host.StartAsync(shutdown).ConfigureAwait(false);
        try
        {
            var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            var service = host.Services.GetRequiredService<TaskQueryService>();
            var gate = host.Services.GetRequiredService<TaskQueryMcpGate>();

            await using var transport = new StreamServerTransport(
                new BoundedLineInputStream(Console.OpenStandardInput()),
                Console.OpenStandardOutput(),
                "capability-explorer-task-queries",
                loggerFactory);

            var serverOptions = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "capability-explorer-task-queries", Version = "1" },
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = ToolCatalog.Tools }),
                    CallToolHandler = (context, token) => CallToolAsync(context, service, gate, token),
                },
            };

            var server = McpServer.Create(transport, serverOptions, loggerFactory, host.Services);
            await server.RunAsync(shutdown).ConfigureAwait(false);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> context, TaskQueryService service, TaskQueryMcpGate gate, CancellationToken caller)
    {
        var operation = context.Params?.Name ?? "";
        if (!TaskQueryOperations.All.Contains(operation))
        {
            return ErrorResult($"unknown tool '{operation}'");
        }

        // Reject a wrong-typed, overflowed, or unrecognized argument BEFORE any retrieval: previously an
        // unparseable argument silently became "missing" (the field's default), masking a caller error as an
        // empty/default query instead of a typed invalid response.
        if (!TryValidateArguments(operation, context.Params?.Arguments, out var request, out var invalidReason))
        {
            return ToCallToolResult(TaskQueryDeadline.Reject(operation, service.Mode, TaskQueryStatus.Invalid, invalidReason!,
                $"invalid: {invalidReason}"));
        }

        var envelope = await gate.RunAsync(operation, service.Mode, token => Dispatch(service, operation, request, token), caller).ConfigureAwait(false);
        return ToCallToolResult(envelope);
    }

    internal static CallToolResult ToCallToolResult(TaskQueryEnvelope envelope)
    {
        var bytes = TaskQueryJson.SerializeBounded(envelope);
        var result = CallToolResultFor(bytes);
        if (TaskQueryJson.McpResultFitsWireBound(result)) return result;

        return CallToolResultFor(JsonSerializer.SerializeToUtf8Bytes(TaskQueryJson.LimitEnvelope(envelope), TaskQueryJson.Options));
    }

    private static CallToolResult CallToolResultFor(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return new CallToolResult
        {
            IsError = document.RootElement.GetProperty("status").GetString() is "denied" or "invalid" or "limit" or "cancelled" or "unavailable",
            // The same ce.task-query.v1 envelope is exposed twice: as text (for clients that only render content
            // blocks) and as structuredContent (for clients that validate/consume it as typed JSON directly).
            Content = [new TextContentBlock { Text = System.Text.Encoding.UTF8.GetString(bytes) }],
            StructuredContent = document.RootElement.Clone(),
        };
    }

    internal static Task<TaskQueryEnvelope> Dispatch(TaskQueryService service, string operation, TaskQueryRequest request, CancellationToken token) =>
        operation switch
        {
            TaskQueryOperations.SnapshotResolve => service.SnapshotResolveAsync(request, token),
            TaskQueryOperations.ModelsList => service.ModelsListAsync(request, token),
            TaskQueryOperations.DecisionReferences => service.DecisionReferencesAsync(request, token),
            TaskQueryOperations.ContractConsumers => service.ContractConsumersAsync(request, token),
            TaskQueryOperations.EvidenceList => service.EvidenceListAsync(request, token),
            TaskQueryOperations.SourceExcerpt => service.SourceExcerptAsync(request, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    /// <summary>Strict argument validation: every argument name must be one this operation's schema declares
    /// (<see cref="ToolCatalog.FieldNames"/>), and every present value must match its declared JSON kind
    /// (string/integer/object) with no silent truncation on numeric overflow. Finding #4 fix.</summary>
    internal static bool TryValidateArguments(string operation, IDictionary<string, JsonElement>? arguments,
        out TaskQueryRequest request, out string? error)
    {
        error = null;
        if (arguments is null) { request = new TaskQueryRequest(); return true; }

        var allowed = ToolCatalog.FieldNames(operation);
        foreach (var key in arguments.Keys)
        {
            if (!allowed.Contains(key)) { request = new TaskQueryRequest(); error = $"unknown-argument-{key}"; return false; }
        }

        IReadOnlyDictionary<string, string>? selection = null;
        if (arguments.TryGetValue("selection", out var selectionElement))
        {
            if (selectionElement.ValueKind != JsonValueKind.Object) { request = new TaskQueryRequest(); error = "invalid-argument-type-selection"; return false; }
            var map = new Dictionary<string, string>();
            foreach (var property in selectionElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) { request = new TaskQueryRequest(); error = "invalid-argument-type-selection"; return false; }
                map[property.Name] = property.Value.GetString()!;
            }

            selection = map;
        }

        string? localError = null;
        bool TryString(string name, out string? value)
        {
            value = null;
            if (!arguments.TryGetValue(name, out var element)) return true;
            if (element.ValueKind != JsonValueKind.String) { localError = $"invalid-argument-type-{name}"; return false; }
            value = element.GetString();
            return true;
        }

        bool TryInt(string name, out int? value)
        {
            value = null;
            if (!arguments.TryGetValue(name, out var element)) return true;
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var n)) { localError = $"invalid-argument-{name}"; return false; }
            value = n;
            return true;
        }

        bool TryLong(string name, out long? value)
        {
            value = null;
            if (!arguments.TryGetValue(name, out var element)) return true;
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var n)) { localError = $"invalid-argument-{name}"; return false; }
            value = n;
            return true;
        }

        if (!TryString("cap", out var cap) || !TryString("view", out var view) || !TryString("snapshotId", out var snapshotId) ||
            !TryString("cursor", out var cursor) || !TryString("modelId", out var modelId) || !TryString("kind", out var kind) ||
            !TryString("key", out var keyArg) || !TryString("repo", out var repo) || !TryString("commit", out var commit) ||
            !TryString("path", out var path) || !TryLong("generation", out var generation) ||
            !TryInt("limit", out var limit) || !TryInt("start", out var start) || !TryInt("end", out var end))
        {
            request = new TaskQueryRequest();
            error = localError;
            return false;
        }

        request = new TaskQueryRequest(Selection: selection, Cap: cap, View: view, SnapshotId: snapshotId, Generation: generation,
            Cursor: cursor, Limit: limit, ModelId: modelId, Kind: kind, Key: keyArg, Repo: repo, Commit: commit, Path: path,
            Start: start, End: end);
        return true;
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = message }, TaskQueryJson.Options) }],
    };
}

/// <summary>The six tool definitions advertised by <c>tools/list</c>; one fixed catalogue, no pagination needed.</summary>
internal static class ToolCatalog
{
    public static readonly IList<Tool> Tools = TaskQueryOperations.All.Select(Describe).ToList();

    private static Tool Describe(string operation) => new()
    {
        // Tool name is the bare operation name (spec.md rev4's "Six operations" table column), never a
        // "task_query." prefix the original implementation invented.
        Name = operation,
        Description = operation switch
        {
            TaskQueryOperations.SnapshotResolve => "Resolve the current (or a pinned) task-query snapshot and the repositories it grants.",
            TaskQueryOperations.ModelsList => "List model products visible in the resolved snapshot, optionally filtered by kind.",
            TaskQueryOperations.DecisionReferences => "Resolve BPMN decision references for one model into reviewed/unreviewed targets.",
            TaskQueryOperations.ContractConsumers => "List HTTP-link candidate consumers for one model's integrations.",
            TaskQueryOperations.EvidenceList => "List the evidence entries recorded for one evidence key.",
            TaskQueryOperations.SourceExcerpt => "Read a bounded, cited-file source excerpt (<=200 lines, +/-4 context).",
            _ => operation,
        },
        InputSchema = JsonSerializer.SerializeToElement(InputSchemaFor(operation)),
    };

    /// <summary>The exact set of argument names this operation's tool schema declares; shared by both the
    /// advertised <c>inputSchema</c> (<see cref="InputSchemaFor"/>) and strict argument validation
    /// (<see cref="TryValidateArguments"/>) so the two can never drift apart.</summary>
    internal static IReadOnlySet<string> FieldNames(string operation)
    {
        var fields = new HashSet<string>(["selection", "cap", "view", "snapshotId", "generation", "cursor", "limit"], StringComparer.Ordinal);
        switch (operation)
        {
            case TaskQueryOperations.ModelsList: fields.Add("kind"); break;
            case TaskQueryOperations.DecisionReferences: fields.Add("modelId"); break;
            case TaskQueryOperations.ContractConsumers:
            case TaskQueryOperations.EvidenceList: fields.Add("key"); break;
            case TaskQueryOperations.SourceExcerpt:
                fields.Add("repo"); fields.Add("commit"); fields.Add("path"); fields.Add("start"); fields.Add("end");
                break;
        }

        return fields;
    }

    private static JsonObject InputSchemaFor(string operation)
    {
        var properties = new JsonObject
        {
            ["selection"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
            ["cap"] = new JsonObject { ["type"] = "string" },
            ["view"] = new JsonObject { ["type"] = "string" },
            ["snapshotId"] = new JsonObject { ["type"] = "string" },
            ["generation"] = new JsonObject { ["type"] = "integer" },
            ["cursor"] = new JsonObject { ["type"] = "string" },
            ["limit"] = new JsonObject { ["type"] = "integer" },
        };

        switch (operation)
        {
            case TaskQueryOperations.ModelsList:
                properties["kind"] = new JsonObject { ["type"] = "string" };
                break;
            case TaskQueryOperations.DecisionReferences:
                properties["modelId"] = new JsonObject { ["type"] = "string" };
                break;
            case TaskQueryOperations.ContractConsumers:
            case TaskQueryOperations.EvidenceList:
                properties["key"] = new JsonObject { ["type"] = "string" };
                break;
            case TaskQueryOperations.SourceExcerpt:
                properties["repo"] = new JsonObject { ["type"] = "string" };
                properties["commit"] = new JsonObject { ["type"] = "string" };
                properties["path"] = new JsonObject { ["type"] = "string" };
                properties["start"] = new JsonObject { ["type"] = "integer" };
                properties["end"] = new JsonObject { ["type"] = "integer" };
                break;
        }

        var required = new JsonArray();
        if (operation != TaskQueryOperations.SnapshotResolve) { required.Add("snapshotId"); required.Add("generation"); }
        if (operation == TaskQueryOperations.DecisionReferences) required.Add("modelId");
        if (operation is TaskQueryOperations.ContractConsumers or TaskQueryOperations.EvidenceList) required.Add("key");
        if (operation == TaskQueryOperations.SourceExcerpt)
            foreach (var name in new[] { "repo", "commit", "path", "start", "end" }) required.Add(name);
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false, ["required"] = required };
    }
}
