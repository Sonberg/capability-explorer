using System.Text.Json;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

/// <summary>
/// Fixtures/shop-cs: one resource used from several projects. Its key must name the project that declares or
/// configures it, not the project that happens to touch it, or the resource splits into one node per caller.
/// </summary>
public sealed class SharedResourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ce-shop-").FullName;
    private readonly Lazy<(string From, string Relation, string To)[]> _edges;

    public SharedResourceTests()
    {
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "shop-cs"), _root);
        _edges = new(Extract);
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Ef_entity_used_from_two_projects_is_one_node_with_a_read_and_a_write()
    {
        const string key = "sql|@unbound:shop/Shop.Host:conn=Shop|audit-logs";
        Assert.Equal(["READS"], Relations("cs|shop|Shop.Application|M:Shop.Application.GetAuditLogsQuery.Run", key));
        Assert.Equal(["WRITES"], Relations("cs|shop|Shop.Infrastructure|M:Shop.Infrastructure.AuditLogsRepository.Append(Shop.Data.AuditLog)", key));
    }

    [Fact]
    public void Ef_contexts_with_different_connections_stay_apart_even_when_the_container_name_matches()
    {
        Assert.Contains("sql|@unbound:shop/Shop.Host:conn=Archive|audit-logs", Targets("sql|", "audit-logs"));
        Assert.Contains("sql|@unbound:shop/Shop.Host:conn=Shop|audit-logs", Targets("sql|", "audit-logs"));
        Assert.Equal(2, Targets("sql|", "audit-logs").Count);
    }

    [Fact]
    public void Service_bus_client_created_in_one_project_names_the_namespace_for_senders_in_another()
    {
        Assert.Equal(["chan|servicebus|@unbound:shop/Shop.Infrastructure:config=ServiceBus:Connection|entity/orders"], Targets("chan|", "orders"));
    }

    [Fact]
    public void Typed_client_base_configured_once_is_one_target_for_every_client_that_uses_it()
    {
        Assert.Equal(["http|@unbound:shop/Shop.Host:config=Payments:Url|GET /v1/status"], Targets("http|", "/v1/status"));
    }

    [Fact]
    public void Dapper_connection_read_in_the_host_names_the_database_for_every_project_that_queries_it()
    {
        Assert.Equal(["sql|@unbound:shop/Shop.Host:conn=Reports|dbo.Reports"], Targets("sql|", "dbo.Reports"));
    }

    private List<string> Targets(string prefix, string contains) =>
        _edges.Value.Select(e => e.To).Where(t => t.StartsWith(prefix, StringComparison.Ordinal) && t.Contains(contains, StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal).ToList();

    private string[] Relations(string from, string to) =>
        _edges.Value.Where(e => e.From == from && e.To == to).Select(e => e.Relation).Distinct().ToArray();

    private (string, string, string)[] Extract()
    {
        var packs = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
        var results = new CSharpExtractor(new ExtractorOptions(_root, "shop", new string('a', 40), null, packs)).Extract();
        return results.SelectMany(r => r.Jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(l => { using var d = JsonDocument.Parse(l); return d.RootElement.Clone(); })
            .Where(r => r.GetProperty("kind").GetString() == "edge" && r.TryGetProperty("to", out _))
            .Select(r => (r.GetProperty("from").GetString()!, r.GetProperty("relation").GetString()!, r.GetProperty("to").GetString()!))
            .ToArray();
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
    }
}
