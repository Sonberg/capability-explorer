using System.Text.Json;
using System.Text.Json.Serialization;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;
using Npgsql;

// ce-explorer --artifacts <dir>[,<dir>] (later directories win for the same repository, commit and unit) [--repos <dir>] [--evaluation <report.json>] [--urls http://127.0.0.1:5080]
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});

// --artifacts/--repos/--evaluation arrive through configuration (command line, environment or test host).
var options = ExplorerOptions.From(builder.Configuration);
// The index and capabilities are reloaded when their files change (see ExplorerState); handlers get the current load.
builder.Services.AddSingleton(options);
var source = builder.Configuration["source"] ?? Environment.GetEnvironmentVariable("EXPLORER_SOURCE") ?? "files";
if (source is not ("files" or "postgres")) throw new InvalidOperationException("source must be files or postgres");
if (source == "files")
{
    var state = new ExplorerState(options);
    builder.Services.AddSingleton(state);
    builder.Services.AddHostedService(_ => state);
    builder.Services.AddTransient(_ => state.Current.Index);
    builder.Services.AddTransient(_ => state.Current.Capabilities);
}

// Optional intelligence layer: provider "none" unless configured (interpretation:provider = recorded | openai | foundry)
// or saved from the Settings page / `make ollama` (modelSettings). Similarity search also needs the database (CE_DATABASE).
var interpretation = builder.Configuration.GetSection("interpretation").Get<InterpretationOptions>() ?? new InterpretationOptions();
var database = builder.Configuration["database"] ?? Environment.GetEnvironmentVariable("CE_DATABASE");
var dataSource = database is { Length: > 0 } ? NpgsqlDataSource.Create(database) : null;
if (source == "postgres")
{
    if (dataSource is null) throw new InvalidOperationException("PostgreSQL source requires CE_DATABASE");
    if (!AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) || !invariant)
        throw new InvalidOperationException("PostgreSQL search requires the pinned invariant globalization runtime");
    builder.Services.AddSingleton(dataSource);
    builder.Services.AddSingleton<ExplorerQueryCursor>();
    builder.Services.AddSingleton<IExplorerQueries, PostgresExplorerQueries>();
    builder.Services.AddSingleton(new DatabaseExplorerConfiguration(options));
}
var intelligence = new Intelligence(interpretation, builder.Configuration["modelSettings"] ?? ModelSettings.DefaultPath, dataSource is null ? null : new EmbeddingStore(dataSource)) { DataSource = dataSource };
builder.Services.AddSingleton(intelligence);
builder.Services.AddSingleton(new PackageFeedSettingsStore(builder.Configuration["packageFeedCatalog"] ?? PackageFeedSettingsStore.DefaultPath));
builder.Services.AddSingleton(new GlossaryReviewService(dataSource));
builder.Services.AddTransient(_ => intelligence.Layer);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddResponseCompression();
if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
{
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
}

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (!LocalRequestGuard.Allowed(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Only trusted local requests are supported.");
        return;
    }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next(context);
});
app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();
if (source == "postgres") app.MapPostgresExplorerApi();
else
{
    app.MapExplorerApi(options.EvaluationReport);
    app.MapGlossaryReviewApi();
    app.MapBindingPreviewApi();
}
app.MapFallbackToFile("index.html");
app.Run();
