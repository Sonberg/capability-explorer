using System.Net;
using System.Net.Http.Json;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GlossaryConceptsApiTests(ExplorerFactory factory) : IClassFixture<ExplorerFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // Labels written by reading the fixture sources (fixtures/src), each a concept a reader of these repositories would
    // expect to find in a glossary. Every entry is a stem key. See MADR-0005, Validation.
    private static readonly string[] Domain =
    [
        "sale",                  // SaleRow / SaleEntity / SaleRecord and dbo.Sales: one sale line
        "draft sale",            // web-ts weeklyStats.ts: an unsaved sale feeding the client-side average
        "article stats",         // dbo.ArticleStats: the stored weekly average per article
        "article stats updated", // worker event and the forecast service's V1 copy
        "weekly sale",           // WeeklySalesResponse: the published result (see KnownDisagreements)
        "iso week",              // IsoWeeks: the calendar the calendar-week average depends on
        "forecast",              // ForecastEntry / ForecastResponse
        "forecast strategy",     // IForecastStrategy with two implementations, selectable by users
        "weekly history",        // WeeklyHistory: weekly units per article
        "weekly series",         // WeeklySeries: ordered weekly units
        "price", "sales ledger", "demand signal", // pricing tables
    ];

    // Wiring, hosting and clients: present in the artifacts, not vocabulary. A key missing from the response was
    // excluded earlier (controllers and DbContexts) and counts as hidden.
    private static readonly string[] Plumbing =
    [
        "weekly sales calculator", "sales repository", "articles controller", "sales db context", "stats repository",
        "article stats calculator", "currency normalizer", "nightly stats job", "stats publisher", "strategy loader",
        "sales api client", "repository", "forecast endpoint", "stats updated consumer", "forecast db context", "sales api",
    ];

    // A response-only type is hidden by the representationOnly prior, which the labelled samples disagree on
    // (MADR-0005). If this changes, update the list and the ADR together.
    private static readonly string[] KnownDisagreements = ["weekly sale"];

    private async Task<GlossaryConceptView> Get(string query = "")
    {
        var response = await _client.GetAsync(new Uri("/api/glossary/concepts" + query, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GlossaryConceptView>(GlossaryApiTests.JsonOptions))!;
    }

    private static bool Shown(GlossaryConceptView view, string key) =>
        view.Concepts.FirstOrDefault(c => c.Key == key) is { Tier: not "plumbing" };

    [Fact]
    public async Task Fixture_gold_set_is_a_regression_gate_for_features_and_weights()
    {
        var view = await Get();
        var missed = Domain.Where(k => !Shown(view, k)).ToList();
        var leaked = Plumbing.Where(k => Shown(view, k)).ToList();
        Assert.Empty(leaked);
        Assert.Equal(KnownDisagreements, missed);
    }

    [Fact]
    public async Task Concepts_carry_reasons_that_add_up_and_a_tier_that_follows_the_checked_in_thresholds()
    {
        var view = await Get();
        var weights = GlossaryWeights.Default;
        Assert.Equal(weights.Version, view.WeightsVersion);
        Assert.NotEmpty(view.Concepts);
        Assert.All(view.Concepts, c =>
        {
            Assert.Equal(weights.Bias + c.Why.Sum(r => r.Points), c.Score);
            Assert.Equal(weights.TierOf(c.Score), c.Tier);
            Assert.NotEmpty(c.Members);
            Assert.All(c.Members.Concat(c.Variants), m => Assert.NotEmpty(m.Evidence));
        });
        // Ordered core, then supporting, then plumbing; within a tier by descending score.
        var order = new[] { "core", "supporting", "plumbing" };
        Assert.Equal(view.Concepts.Select(c => (Array.IndexOf(order, c.Tier), -c.Score)), view.Concepts.Select(c => (Array.IndexOf(order, c.Tier), -c.Score)).Order());
    }

    [Fact]
    public async Task Calculations_are_metrics_not_concepts_and_keep_their_comparisons()
    {
        var view = await Get();
        var active = view.Metrics.Single(m => m.Term == "average per active week");
        Assert.Contains(active.Occurrences, o => o.Role == "calculation" && o.RepositoryId == "worker-cs");
        Assert.DoesNotContain(view.Concepts, c => c.Key.Contains("average per", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Selected_revision_keeps_member_evidence_at_that_revision()
    {
        var selection = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/view?view=api-cs:v1-base");
        var view = await Get("?view=api-cs:v1-base");
        Assert.All(view.Concepts.SelectMany(c => c.Members.Concat(c.Variants)), m =>
        {
            Assert.Equal(selection![m.RepositoryId], m.CommitSha);
            Assert.All(m.Evidence, e => Assert.Equal(m.CommitSha, e.CommitSha));
        });
    }

    [Fact]
    public async Task The_candidate_endpoint_is_unchanged_by_the_concept_layer()
    {
        var response = await _client.GetAsync(new Uri("/api/glossary", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = (await response.Content.ReadFromJsonAsync<List<GlossaryCandidate>>(GlossaryApiTests.JsonOptions))!;
        Assert.Contains(candidates, c => c.Term == "average per active week");
        Assert.Contains(candidates, c => c.Term == "sale row");
    }

}

public sealed class GlossaryConceptsCapabilityApiTests(CapabilityExplorerFactory factory) : IClassFixture<CapabilityExplorerFactory>
{
    [Fact]
    public async Task Capability_scope_limits_members_to_its_repositories_and_the_response_is_repeatable()
    {
        var client = factory.CreateClient();
        var scoped = (await client.GetFromJsonAsync<GlossaryConceptView>("/api/glossary/concepts?cap=demand-pricing", GlossaryApiTests.JsonOptions))!;
        var members = scoped.Concepts.SelectMany(c => c.Members.Concat(c.Variants)).ToList();
        Assert.NotEmpty(members);
        Assert.All(members, m => Assert.Contains(m.RepositoryId, new[] { "forecast-cs", "pricing-node" }));
        Assert.All(members.Where(m => m.RepositoryId == "forecast-cs"), m => Assert.Equal(Fixtures.Lock["forecast-cs"]["v1-base"], m.CommitSha));
        Assert.All(scoped.Concepts, c => Assert.Contains("demand-pricing", c.Capabilities));
        Assert.Contains(scoped.Concepts, c => c.Key == "demand signal");
        Assert.DoesNotContain(scoped.Concepts, c => c.Key == "draft sale"); // web-ts is not in this capability
        var again = (await client.GetFromJsonAsync<GlossaryConceptView>("/api/glossary/concepts?cap=demand-pricing", GlossaryApiTests.JsonOptions))!;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(scoped, GlossaryApiTests.JsonOptions),
            System.Text.Json.JsonSerializer.Serialize(again, GlossaryApiTests.JsonOptions));
    }

    [Theory]
    [InlineData("?cap=does-not-exist")]
    [InlineData("?view=nope")]
    public async Task Invalid_selections_behave_like_the_candidate_endpoint(string query)
    {
        var client = factory.CreateClient();
        var candidates = await client.GetAsync(new Uri("/api/glossary" + query, UriKind.Relative));
        var concepts = await client.GetAsync(new Uri("/api/glossary/concepts" + query, UriKind.Relative));
        Assert.Equal(candidates.StatusCode, concepts.StatusCode);
    }
}
