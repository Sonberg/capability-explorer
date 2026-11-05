using System.Text.Json;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class HttpTargetTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ce-http-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("\"HTTPS://user:secret@Graph.Example:443/v1/../v2/%2F?q=secret#fragment\"", "host=graph.example", "/v2/%2f")]
    [InlineData("$\"https://graph.example/v1/{id}/events?token=secret#fragment\"", "host=graph.example", "/v1/{}/events")]
    [InlineData("\"https://graph.example:8443/events\"", "host=graph.example:8443", "/events")]
    [InlineData("\"/relative?token=secret#fragment\"", "client=HttpFixture", "/relative")]
    [InlineData("\"/a/../relative\"", "client=HttpFixture", "/relative")]
    public void Separates_host_path_and_safe_metadata(string expression, string hint, string route)
    {
        var records = Extract(expression);
        var edge = Request(records);
        Assert.Equal($"http|@unbound:mapping/HttpFixture:{hint}|GET {route}", edge.GetProperty("to").GetString());
        Assert.Equal("unresolved", edge.GetProperty("resolution").GetString());
        Assert.Equal("configuration", edge.GetProperty("unresolvedReason").GetString());
        Assert.DoesNotContain("secret", edge.GetRawText());
        Assert.DoesNotContain("fragment", edge.GetRawText());
        Assert.DoesNotContain("user:", edge.GetRawText());
    }

    [Theory]
    [InlineData("$\"https://{id}/events\"")]
    [InlineData("\"//graph.example/events\"")]
    [InlineData("\"https:///events\"")]
    [InlineData("\"ftp://graph.example/events\"")]
    public void Unknown_or_invalid_authority_never_becomes_a_host(string expression)
    {
        var edge = Request(Extract(expression));
        Assert.False(edge.TryGetProperty("to", out _));
        Assert.Equal("dynamic-string", edge.GetProperty("unresolvedReason").GetString());
    }

    [Theory]
    [InlineData("\"events\"", "host=base.example", "/prefix/events")]
    [InlineData("\"/events\"", "host=base.example", "/events")]
    [InlineData("\"https://other.example/events\"", "host=other.example", "/events")]
    public void Static_typed_base_obeys_uri_resolution(string expression, string hint, string route)
    {
        var edge = Request(Extract(expression, true));
        Assert.Equal($"http|@unbound:mapping/HttpFixture:{hint}|GET {route}", edge.GetProperty("to").GetString());
    }

    [Fact]
    public void Multiple_base_registrations_are_ambiguous_but_absolute_request_overrides_them()
    {
        var unknown = Request(Extract("\"events\"", true, true));
        Assert.False(unknown.TryGetProperty("to", out _));
        Assert.Equal("dynamic-string", unknown.GetProperty("unresolvedReason").GetString());
        var absolute = Request(Extract("\"https://other.example/events\"", true, true));
        Assert.Equal("http|@unbound:mapping/HttpFixture:host=other.example|GET /events", absolute.GetProperty("to").GetString());
    }

    [Fact]
    public void Base_derived_target_cites_registration_evidence()
    {
        var records = Extract("\"events\"", true);
        var edge = Request(records);
        var evidence = edge.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        var registrationLine = File.ReadAllLines(Path.Combine(_root, "HttpFixture.cs"))
            .Select((line, index) => (line, index)).Single(x => x.line.Contains("static void Register", StringComparison.Ordinal)).index + 1;
        Assert.Contains(records, r => r.GetProperty("kind").GetString() == "evidence"
            && evidence.Contains(r.GetProperty("id").GetString())
            && r.GetProperty("span").GetProperty("startLine").GetInt32() == registrationLine);
    }

    [Fact]
    public void Given_a_cross_file_url_constant_when_requested_then_each_citation_uses_its_own_source_file()
    {
        File.WriteAllText(Path.Combine(_root, "Routes.cs"), new string('\n', 80)
            + "public static class Routes { public const string Endpoint = \"/cross-file\"; }");
        var records = Extract("Routes.Endpoint");
        var request = Request(records);
        var references = request.GetProperty("evidence").EnumerateArray().Select(value => value.GetString()).ToHashSet();
        var citations = records.Where(record => record.GetProperty("kind").GetString() == "evidence"
            && references.Contains(record.GetProperty("id").GetString())).ToArray();
        Assert.Contains(citations, citation => citation.GetProperty("path").GetString() == "Routes.cs");
        foreach (var citation in citations)
        {
            var file = citation.GetProperty("path").GetString()!;
            var span = citation.GetProperty("span");
            var content = Snippet.Extract(File.ReadAllText(Path.Combine(_root, file)), new Span(
                span.GetProperty("startLine").GetInt32(), span.GetProperty("startColumn").GetInt32(),
                span.GetProperty("endLine").GetInt32(), span.GetProperty("endColumn").GetInt32()));
            Assert.NotNull(content);
            Assert.Equal(citation.GetProperty("snippetSha256").GetString(), Snippet.Sha256(content));
        }
    }

    private JsonElement[] Extract(string expression, bool withBase = false, bool duplicate = false)
    {
        File.WriteAllText(Path.Combine(_root, "HttpFixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var registration = withBase ? "static void Register() => AddHttpClient<HttpFixture>(c => c.BaseAddress = new System.Uri(\"https://base.example/prefix/\")); static void AddHttpClient<T>(System.Action<System.Net.Http.HttpClient> configure) {}" : "";
        if (duplicate) registration += " static void Other() => AddHttpClient<HttpFixture>(c => c.BaseAddress = new System.Uri(\"https://other.example/\"));";
        File.WriteAllText(Path.Combine(_root, "HttpFixture.cs"), $$"""
            using System.Net.Http;
            public class HttpFixture {
                public void Send(HttpClient client, string id) { _ = client.GetAsync({{expression}}); }
                {{registration}}
            }
            """);
        var packs = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
        var options = new ExtractorOptions(_root, "mapping", "0123456789abcdef0123456789abcdef01234567", null, packs);
        var first = Assert.Single(new CSharpExtractor(options).Extract()).Jsonl;
        Assert.Equal(first, Assert.Single(new CSharpExtractor(options).Extract()).Jsonl);
        return first.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => { using var d = JsonDocument.Parse(l); return d.RootElement.Clone(); }).ToArray();
    }

    private static JsonElement Request(JsonElement[] records) => Assert.Single(records, r => r.GetProperty("kind").GetString() == "edge" && r.GetProperty("relation").GetString() == "REQUESTS");
}
