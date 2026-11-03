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
    private string[] _lines = [];
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
    public void Ef_context_shared_from_two_projects_has_one_store_when_connection_is_unknown()
    {
        UseRuntimeCosmosRegistrations();
        AddSiblingContext();
        var edges = _edges.Value;
        var read = edges.Single(e => e.From == "cs|shop|Shop.Application|M:Shop.Application.GetAuditLogsQuery.Run" && e.Relation == "READS").To;
        var write = edges.Single(e => e.From == "cs|shop|Shop.Infrastructure|M:Shop.Infrastructure.AuditLogsRepository.Append(Shop.Data.AuditLog)" && e.Relation == "WRITES").To;

        Assert.Equal(read, write);
        Assert.EndsWith(":conn=unknown;context=Shop.Data.ShopDb|audit-logs", read, StringComparison.Ordinal);
        Assert.Single(edges, e => e.From == "cs|shop|Shop.Data|T:Shop.Data.AuditLog" && e.Relation == "MAPS_TO" && e.To == read);
        Assert.Equal(2, edges.Where(e => e.To.Contains("@unbound:shop/Shop.Data:", StringComparison.Ordinal) && e.To.EndsWith("|audit-logs", StringComparison.Ordinal))
            .Select(e => e.To).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Same_container_in_distinct_ef_contexts_stays_separate_when_connections_are_unknown()
    {
        UseRuntimeCosmosRegistrations();
        AddSiblingContext();
        var edges = _edges.Value;
        var shopStore = edges.Single(e => e.From == "cs|shop|Shop.Application|M:Shop.Application.GetAuditLogsQuery.Run" && e.Relation == "READS").To;
        var siblingStore = edges.Single(e => e.From == "cs|shop|Shop.Application|M:Shop.Application.OtherAuditLogReader.Read" && e.Relation == "READS").To;

        Assert.NotEqual(shopStore, siblingStore);
    }

    [Fact]
    public void Same_entity_in_two_unknown_contexts_stays_with_the_context_it_is_read_through()
    {
        UseRuntimeCosmosRegistrations();
        File.WriteAllText(Path.Combine(_root, "Shop.Data", "SecondaryDb.cs"), """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            public class SecondaryDb : DbContext
            {
                public DbSet<AuditLog> AuditLogs { get; } = new();

                public void OnModelCreating(ModelBuilder model) => model.Entity<AuditLog>().ToContainer("audit-logs");
            }
            """);
        File.WriteAllText(Path.Combine(_root, "Shop.Application", "SecondaryAuditLogReader.cs"), """
            using Shop.Data;

            namespace Shop.Application;

            public class SecondaryAuditLogReader(SecondaryDb db)
            {
                public object Read() => db.AuditLogs;
            }
            """);
        var edges = _edges.Value;
        var shopStore = edges.Single(e => e.From == "cs|shop|Shop.Application|M:Shop.Application.GetAuditLogsQuery.Run" && e.Relation == "READS").To;
        var secondaryStore = edges.Single(e => e.From == "cs|shop|Shop.Application|M:Shop.Application.SecondaryAuditLogReader.Read" && e.Relation == "READS").To;

        Assert.NotEqual(shopStore, secondaryStore);
        Assert.EndsWith("context=Shop.Data.ShopDb|audit-logs", shopStore, StringComparison.Ordinal);
        Assert.EndsWith("context=Shop.Data.SecondaryDb|audit-logs", secondaryStore, StringComparison.Ordinal);
        Assert.Equal([secondaryStore, shopStore], edges.Where(e => e.From == "cs|shop|Shop.Data|T:Shop.Data.AuditLog" && e.Relation == "MAPS_TO").Select(e => e.To).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Unknown_context_store_identity_remains_repository_scoped()
    {
        UseRuntimeCosmosRegistrations();
        var shopStore = _edges.Value.Single(e => e.From == "cs|shop|Shop.Application|M:Shop.Application.GetAuditLogsQuery.Run" && e.Relation == "READS").To;
        var otherStore = Extract("other").Single(e => e.From == "cs|other|Shop.Application|M:Shop.Application.GetAuditLogsQuery.Run" && e.Relation == "READS").To;

        Assert.NotEqual(shopStore, otherStore);
        Assert.Contains("@unbound:shop/", shopStore, StringComparison.Ordinal);
        Assert.Contains("@unbound:other/", otherStore, StringComparison.Ordinal);
    }

    [Fact]
    public void Ef_model_builders_and_their_generic_extensions_do_not_query_tables()
    {
        File.WriteAllText(Path.Combine(_root, "Shop.Stubs", "ModelConfigurationExtensions.cs"), """
            namespace Microsoft.EntityFrameworkCore;
            public static class ModelConfigurationExtensions
            {
                public static EntityTypeBuilder<T> ToStorage<T>(this EntityTypeBuilder<T> builder) where T : class => builder;
            }
            """);
        var configuration = Path.Combine(_root, "Shop.Data", "ShopDb.cs");
        File.WriteAllText(configuration, File.ReadAllText(configuration)
            .Replace(".ToTable(\"Orders\")", ".ToStorage().ToTable(\"Orders\")", StringComparison.Ordinal));
        Assert.DoesNotContain(_edges.Value, edge => edge.From.Contains(".OnModelCreating(", StringComparison.Ordinal)
            && edge.Relation is "READS" or "WRITES");
        Assert.Contains(_edges.Value, edge => edge.Relation == "MAPS_TO" && edge.From == "cs|shop|Shop.Data|T:Shop.Data.Order");
    }

    [Fact]
    public void Supabase_table_model_is_read_and_written_on_the_node_its_mapping_points_at()
    {
        var table = Targets("sql|", "reviews").Single();
        Assert.Equal(["READS"], Relations("cs|shop|Shop.Infrastructure|M:Shop.Infrastructure.ReviewsRepository.Get", table));
        Assert.Equal(["WRITES"], Relations("cs|shop|Shop.Infrastructure|M:Shop.Infrastructure.ReviewsRepository.Add(Shop.Data.Review)", table));
        Assert.Equal(["MAPS_TO"], Relations("cs|shop|Shop.Data|T:Shop.Data.Review", table));
        Assert.Empty(Relations("cs|shop|Shop.Infrastructure|P:Shop.Infrastructure.ReviewsRepository.Pending", table));
        Assert.Contains(_lines, l => l.Contains("\"client\":\"Supabase.Client\"", StringComparison.Ordinal) && l.Contains("\"clientNamespaces\":[\"Supabase\"]", StringComparison.Ordinal));
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

    private (string From, string Relation, string To)[] Extract()
        => Extract("shop");

    private (string From, string Relation, string To)[] Extract(string repositoryId)
    {
        var packs = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
        var results = new CSharpExtractor(new ExtractorOptions(_root, repositoryId, new string('a', 40), null, packs)).Extract();
        _lines = results.SelectMany(r => r.Jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        return Edges(_lines);
    }

    private static (string From, string Relation, string To)[] Edges(IEnumerable<string> lines) =>
        lines
            .Select(l => { using var d = JsonDocument.Parse(l); return d.RootElement.Clone(); })
            .Where(r => r.GetProperty("kind").GetString() == "edge" && r.TryGetProperty("to", out _))
            .Select(r => (r.GetProperty("from").GetString()!, r.GetProperty("relation").GetString()!, r.GetProperty("to").GetString()!))
            .ToArray();

    // Cosmos options name the account only at runtime, so neither context has a statically known connection.
    private void UseRuntimeCosmosRegistrations()
    {
        File.WriteAllText(Path.Combine(_root, "Shop.Stubs", "CosmosOptions.cs"), """
            namespace Microsoft.EntityFrameworkCore;
            public static class CosmosOptions
            {
                public static object UseCosmos(this object options, string endpoint, string key, string database) => options;
            }
            """);
        var path = Path.Combine(_root, "Shop.Host", "Program.cs");
        var text = File.ReadAllText(path)
            .Replace("using System;", "using System;\nusing Microsoft.EntityFrameworkCore;", StringComparison.Ordinal)
            .Replace("_ => config.GetConnectionString(\"Shop\")", "o => o.UseCosmos(config[\"Cosmos:Endpoint\"]!, config[\"Cosmos:Key\"]!, \"shop\")", StringComparison.Ordinal)
            .Replace("_ => config.GetConnectionString(\"Archive\")", "o => o.UseCosmos(config[\"Cosmos:Endpoint\"]!, config[\"Cosmos:Key\"]!, \"archive\")", StringComparison.Ordinal);
        Assert.Contains("UseCosmos", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GetConnectionString(\"Shop\")", text, StringComparison.Ordinal);
        File.WriteAllText(path, text);
    }

    private void AddSiblingContext()
    {
        File.WriteAllText(Path.Combine(_root, "Shop.Data", "OtherDb.cs"), """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            public class OtherAuditLog { public int Id { get; set; } }

            public class OtherDb : DbContext
            {
                public DbSet<OtherAuditLog> AuditLogs { get; } = new();

                public void OnModelCreating(ModelBuilder model) => model.Entity<OtherAuditLog>().ToContainer("audit-logs");
            }
            """);
        File.WriteAllText(Path.Combine(_root, "Shop.Application", "OtherAuditLogReader.cs"), """
            using Shop.Data;

            namespace Shop.Application;

            public class OtherAuditLogReader(OtherDb db)
            {
                public object Read() => db.AuditLogs;
            }
            """);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
    }
}
