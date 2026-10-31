using System.Text.Json;
using System.Text.Json.Serialization;
using CapabilityExplorer.Explorer;

// ce-explorer --artifacts <dir>[,<dir>] [--repos <dir>] [--evaluation <report.json>] [--urls http://127.0.0.1:5080]
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});

// --artifacts/--repos/--evaluation arrive through configuration (command line, environment or test host).
var options = ExplorerOptions.From(builder.Configuration);
builder.Services.AddSingleton(_ => options.Build());
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
{
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
}

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapExplorerApi(options.EvaluationReport);
app.MapFallbackToFile("index.html");
app.Run();
