using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GlossaryConceptsTests
{
    [Theory]
    // Representation suffixes and plurals fold into the stem; nothing else does.
    [InlineData("Order", "class", "order", false, false)]
    [InlineData("OrderDto", "class", "order", true, false)]
    [InlineData("OrderRow", "record", "order", true, false)]
    [InlineData("CreateOrderRequest", "class", "create order", true, false)]
    [InlineData("OrderItems", "class", "order item", false, false)]
    [InlineData("SubscriptionV2", "record", "subscription", false, false)]
    [InlineData("SubscriptionDtoV2", "record", "subscription", true, false)]
    [InlineData("IForecastStrategy", "interface", "forecast strategy", false, false)]
    [InlineData("Stats", "class", "stats", false, false)]
    [InlineData("Status", "enum", "status", false, false)]
    // A role suffix marks plumbing, but a bare "Client" or "Job" may be the domain word itself.
    [InlineData("PaymentProvider", "class", "payment provider", false, true)]
    [InlineData("OrderRepository", "class", "order repository", false, true)]
    [InlineData("Client", "class", "client", false, false)]
    [InlineData("Job", "class", "job", false, false)]
    [InlineData("JobPosting", "class", "job posting", false, false)]
    public void Stem_folds_only_listed_representation_suffixes_and_marks_role_suffixes(
        string name, string typeKind, string key, bool folded, bool role)
    {
        var node = TypeNode(name, typeKind);
        var stem = GlossaryVocabulary.StemOf(node);
        Assert.Equal(key, stem.Key);
        Assert.Equal(folded, stem.Folded);
        Assert.Equal(role, stem.Role);
    }

    [Fact]
    public void Stem_does_not_change_MADR_0004_term_candidate_normalization()
    {
        Assert.Equal("invoice dto factory", GlossaryNormalization.Normalize("InvoiceDtoFactory"));
        Assert.Equal("invoice", GlossaryNormalization.Normalize("InvoiceDto"));
        Assert.Equal("order row", GlossaryNormalization.Normalize("OrderRow"));
        Assert.Equal("order items", GlossaryNormalization.Normalize("OrderItems"));
    }

    [Theory]
    [InlineData("src/App/Orders/Order.cs", "src/App/App.csproj", false)]
    [InlineData("tests/App.Tests/OrderTests.cs", "tests/App.Tests/App.Tests.csproj", true)]
    [InlineData("src/App/Order.test.ts", "tsconfig.json", true)]
    [InlineData("src/__tests__/order.ts", "tsconfig.json", true)]
    [InlineData("src/Contest/Order.cs", "src/Contest/Contest.csproj", false)]
    public void Test_paths_are_recognised_without_matching_ordinary_words(string path, string unit, bool isTest) =>
        Assert.Equal(isTest, GlossaryVocabulary.IsTestPath(unit, path));

    [Fact]
    public void Weights_are_embedded_versioned_integers_and_tiers_change_exactly_at_the_thresholds()
    {
        var weights = GlossaryWeights.Default;
        Assert.True(weights.Version >= 1);
        Assert.True(weights.SupportingAt < weights.CoreAt);
        Assert.Equal("plumbing", weights.TierOf(weights.SupportingAt - 1));
        Assert.Equal("supporting", weights.TierOf(weights.SupportingAt));
        Assert.Equal("supporting", weights.TierOf(weights.CoreAt - 1));
        Assert.Equal("core", weights.TierOf(weights.CoreAt));
        Assert.All(weights.Weights, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value.Basis), kv.Key));
        Assert.Equal(0, weights.Points("noSuchFeature"));
    }

    [Fact]
    public void Representations_fold_into_one_concept_and_attributes_are_nested_not_top_level()
    {
        var app = new Unit("repo", "src/App/App.csproj", "App");
        var order = app.Type("Order", "src/App/Orders/Models/Order.cs");
        var dto = app.Type("OrderDto", "src/App/Orders/Models/OrderDto.cs");
        app.Type("OrderRow", "src/App/Data/OrderRow.cs");
        foreach (var (type, member) in new[] { (order, "Id"), (order, "CustomerName"), (order, "Total"), (dto, "Total") })
        {
            app.Edge(type.Key, "DECLARES", app.Property(type, member, "src/App/Orders/Models/Order.cs").Key);
        }

        var view = Build(app);
        var concept = Assert.Single(view.Concepts);
        Assert.Equal("order", concept.Key);
        Assert.Equal(["Order", "OrderDto", "OrderRow"], concept.Members.Select(m => m.Name).Order());
        Assert.Equal(["declaration", "representation", "representation"],
            concept.Members.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => m.Fold));
        Assert.DoesNotContain(view.Concepts, c => c.Key is "total" or "id" or "customer name");
        Assert.Equal(["customer name", "id", "total"], concept.Attributes.Select(a => a.Label));
        Assert.True(concept.Attributes.Single(a => a.Label == "id").Generic);
        Assert.All(concept.Members, m => Assert.NotEmpty(m.Evidence));
    }

    [Fact]
    public void Plumbing_stays_in_the_response_with_its_reasons_and_is_not_deleted()
    {
        var app = new Unit("repo", "src/App/App.csproj", "App");
        app.Type("OrderRepository", "src/App/Data/OrderRepository.cs");
        var concept = Assert.Single(Build(app).Concepts);
        Assert.Equal("plumbing", concept.Tier);
        var reason = Assert.Single(concept.Why, r => r.Feature == "roleSuffix");
        Assert.Equal("repository", reason.Detail);
        Assert.Equal(GlossaryWeights.Default.Bias + reason.Points, concept.Score);
    }

    [Fact]
    public void Test_generated_and_external_declarations_are_not_concepts_and_tests_are_counted()
    {
        var app = new Unit("repo", "src/App/App.csproj", "App");
        app.Type("Invoice", "src/App/Invoice.cs");
        var generated = app.Type("GeneratedInvoice", "src/App/Generated/GeneratedInvoice.cs");
        generated.Node["generated"] = true;
        var tests = new Unit("repo", "tests/App.Tests/App.Tests.csproj", "App.Tests");
        tests.Type("InvoiceTests", "tests/App.Tests/InvoiceTests.cs");
        tests.Type("Receipt", "tests/App.Tests/Receipt.cs");

        var view = Build(app, tests);
        Assert.Equal(["invoice"], view.Concepts.Select(c => c.Key));
        Assert.Equal(2, view.ExcludedTestDeclarations);
    }

    [Fact]
    public void A_model_in_an_integration_folder_never_folds_into_the_domain_concept_of_the_same_name()
    {
        var infra = new Unit("repo", "src/App.Infrastructure/App.Infrastructure.csproj", "App.Infrastructure");
        infra.Type("KlaviyoClient", "src/App.Infrastructure/Klaviyo/KlaviyoClient.cs");
        var domain = new Unit("repo", "src/App.Interfaces/App.Interfaces.csproj", "App.Interfaces");
        domain.Type("Snoozed", "src/App.Interfaces/Subscriptions/Scheduling/SchedulingEvent.cs");
        domain.Type("SnoozedRequest", "src/App.Interfaces/Klaviyo/Models/SnoozedRequest.cs");

        var view = Build(infra, domain);
        var plain = view.Concepts.Single(c => c.Key == "snoozed");
        var vendor = view.Concepts.Single(c => c.Key == "snoozed @klaviyo");
        Assert.Equal(["Snoozed"], plain.Members.Select(m => m.Name));
        Assert.Equal(["SnoozedRequest"], vendor.Members.Select(m => m.Name));
        Assert.Equal("snoozed (klaviyo)", vendor.Term);
        Assert.Equal("klaviyo", Assert.Single(vendor.Why, r => r.Feature == "integrationModel").Detail);
        Assert.DoesNotContain(plain.Why, r => r.Feature == "integrationModel");
    }

    [Fact]
    public void An_enum_is_a_value_set_not_an_operation_but_a_verb_first_class_is()
    {
        var app = new Unit("repo", "src/App/App.csproj", "App");
        app.Type("ScheduleMode", "src/App/Subscriptions/ScheduleMode.cs", "enum");
        app.Type("UpdateOrder", "src/App/Orders/UpdateOrder.cs");
        var view = Build(app);
        Assert.DoesNotContain(view.Concepts.Single(c => c.Key == "schedule mode").Why, r => r.Feature == "operationName");
        Assert.Contains(view.Concepts.Single(c => c.Key == "update order").Why, r => r.Feature == "operationName");
    }

    [Fact]
    public void An_interface_with_several_implementations_lists_variants_and_a_single_one_is_a_seam()
    {
        var app = new Unit("repo", "src/App/App.csproj", "App");
        var strategy = app.Type("IForecastStrategy", "src/App/Forecasting/IForecastStrategy.cs", "interface");
        var moving = app.Type("MovingAverageStrategy", "src/App/Forecasting/MovingAverageStrategy.cs");
        var smoothing = app.Type("ExponentialSmoothingStrategy", "src/App/Forecasting/ExponentialSmoothingStrategy.cs");
        var gateway = app.Type("IClock", "src/App/Time/IClock.cs", "interface");
        var system = app.Type("SystemClock", "src/App/Time/SystemClock.cs");
        app.Edge(moving.Key, "IMPLEMENTS", strategy.Key);
        app.Edge(smoothing.Key, "IMPLEMENTS", strategy.Key);
        app.Edge(system.Key, "IMPLEMENTS", gateway.Key);

        var view = Build(app);
        var forecast = view.Concepts.Single(c => c.Key == "forecast strategy");
        Assert.Equal(["ExponentialSmoothingStrategy", "MovingAverageStrategy"], forecast.Variants.Select(v => v.Name));
        Assert.All(forecast.Variants, v => Assert.Equal("variant", v.Fold));
        Assert.Contains(forecast.Why, r => r.Feature == "variants");
        Assert.DoesNotContain(view.Concepts, c => c.Key is "moving average strategy" or "exponential smoothing strategy");
        Assert.Contains(view.Concepts.Single(c => c.Key == "clock").Why, r => r.Feature == "singleImplementationInterface");
    }

    [Fact]
    public void Identifier_attributes_do_not_invent_concepts()
    {
        var app = new Unit("repo", "src/App/App.csproj", "App");
        foreach (var name in new[] { "Invoice", "Receipt" })
        {
            var type = app.Type(name, $"src/App/{name}.cs");
            app.Edge(type.Key, "DECLARES", app.Property(type, "CustomerId", $"src/App/{name}.cs").Key);
        }

        Assert.DoesNotContain(Build(app).Concepts, c => c.Key.Contains("customer", StringComparison.Ordinal));
    }

    [Fact]
    public void Score_is_the_bias_plus_the_reported_points_and_custom_weights_are_respected()
    {
        var app = new Unit("repo", "src/App/App.csproj", "App");
        app.Type("OrderRepository", "src/App/Data/OrderRepository.cs");
        var custom = GlossaryWeights.Parse("""
            {"version":9,"bias":10,"supportingAt":-5,"coreAt":50,"genericLabelShare":0.05,
             "weights":{"roleSuffix":{"points":-100,"basis":"test"}}}
            """);
        var view = Build(custom, app);
        Assert.Equal(9, view.WeightsVersion);
        var concept = Assert.Single(view.Concepts);
        Assert.Equal(-90, concept.Score);
        Assert.Equal("plumbing", concept.Tier);
        Assert.Equal(concept.Score, custom.Bias + concept.Why.Sum(r => r.Points));
    }

    [Fact]
    public void Output_is_identical_for_shuffled_input_other_cultures_and_repeated_builds()
    {
        Unit[] Units()
        {
            var app = new Unit("repo", "src/App/App.csproj", "App");
            var order = app.Type("Order", "src/App/Orders/Models/Order.cs");
            app.Type("OrderDto", "src/App/Orders/Models/OrderDto.cs");
            app.Edge(order.Key, "DECLARES", app.Property(order, "Total", "src/App/Orders/Models/Order.cs").Key);
            app.Type("OrderRepository", "src/App/Data/OrderRepository.cs");
            var other = new Unit("repo", "src/Other/Other.csproj", "Other");
            other.Type("Invoice", "src/Other/Invoice.cs");
            return [app, other];
        }

        var first = Serialize(Build(Units()));
        Assert.Equal(first, Serialize(Build(Units())));
        Assert.Equal(first, Serialize(Build(Units().Reverse().ToArray())));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(first, Serialize(Build(Units())));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Serialized_contract_keeps_reasons_evidence_identity_and_disclosed_limits()
    {
        var app = new Unit("repo", "src/App/App.csproj", "App") { Language = "typescript" };
        app.Type("Invoice", "src/App/Invoice.ts");
        var view = Build(app);
        var json = JsonSerializer.Deserialize<JsonElement>(Serialize(view));
        var concept = json.GetProperty("concepts")[0];
        Assert.Equal("invoice", concept.GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Array, concept.GetProperty("why").ValueKind);
        var member = concept.GetProperty("members")[0];
        Assert.StartsWith("glossary-member:", member.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal("repo", member.GetProperty("evidence")[0].GetProperty("repositoryId").GetString());
        Assert.Contains(json.GetProperty("limits").EnumerateArray(), l => l.GetString()!.Contains("TypeScript", StringComparison.Ordinal));
        var roundTrip = JsonSerializer.Deserialize<GlossaryConceptView>(Serialize(view), JsonSerializerOptions.Web)!;
        Assert.Equal(Serialize(view), Serialize(roundTrip));
    }

    private static byte[] Serialize(GlossaryConceptView view) => JsonSerializer.SerializeToUtf8Bytes(view, JsonSerializerOptions.Web);

    private static GlossaryConceptView Build(params Unit[] units) => Build(GlossaryWeights.Default, units);

    private static GlossaryConceptView Build(GlossaryWeights weights, params Unit[] units) =>
        GlossaryConcepts.Build(Load(units).View(null), null, weights);

    private static NodeRecord TypeNode(string name, string typeKind) => new(
        "n:1", "cs|repo|App|T:App." + name, "Type", name, null, null,
        JsonDocument.Parse($$"""{"typeKind":"{{typeKind}}"}""").RootElement.Clone(), []);

    // ---------------------------------------------------------------- synthetic ce.v1 artifacts

    private sealed record Declared(string Key, JsonObject Node);

    /// <summary>One analysis unit. Every node gets its own evidence record at the stated file path.</summary>
    private sealed class Unit(string repository, string path, string name)
    {
        private readonly List<JsonObject> _records = [];
        private int _next;

        public string Language { get; init; } = "csharp";

        public IReadOnlyList<JsonObject> Records => _records;

        public string Repository => repository;

        public string UnitPath => path;

        public string UnitName => name;

        public Declared Type(string typeName, string file, string typeKind = "class") =>
            Declare($"T:{typeName}", typeName, "Type", file, new JsonObject { ["typeKind"] = typeKind });

        public Declared Property(Declared owner, string member, string file) =>
            Declare($"P:{owner.Node["name"]}.{member}", member, "Property", file, null);

        private Declared Declare(string local, string display, string nodeKind, string file, JsonObject? props)
        {
            var id = _next++;
            var key = $"cs|{repository}|{name}|{local}";
            _records.Add(new JsonObject
            {
                ["schema"] = "ce.v1", ["kind"] = "evidence", ["id"] = $"ev:{id}", ["repositoryId"] = repository,
                ["commitSha"] = new string('a', 40), ["path"] = file,
                ["span"] = JsonNode.Parse("""{"startLine":1,"startColumn":1,"endLine":2,"endColumn":2}"""),
                ["snippetSha256"] = new string('b', 64),
                ["extractor"] = JsonNode.Parse("""{"name":"test","version":"1"}"""),
            });
            var node = new JsonObject
            {
                ["schema"] = "ce.v1", ["kind"] = "node", ["id"] = $"n:{id}", ["symbolKey"] = key,
                ["nodeKind"] = nodeKind, ["name"] = display, ["evidence"] = new JsonArray($"ev:{id}"),
            };
            if (props is not null)
            {
                node["props"] = props;
            }

            _records.Add(node);
            return new Declared(key, node);
        }

        public void Edge(string from, string relation, string to)
        {
            var id = _next++;
            var evidence = _records.Last(r => (string?)r["kind"] == "evidence")["id"]!.GetValue<string>();
            _records.Add(new JsonObject
            {
                ["schema"] = "ce.v1", ["kind"] = "edge", ["id"] = $"e:{id}", ["from"] = from, ["relation"] = relation,
                ["to"] = to, ["derivation"] = "compiler", ["resolution"] = "resolved", ["evidence"] = new JsonArray(evidence),
            });
        }
    }

    private static ExplorerIndex Load(IReadOnlyList<Unit> units)
    {
        var directory = Directory.CreateTempSubdirectory("ce-glossary-concepts").FullName;
        try
        {
            var golden = JsonNode.Parse(Fixtures.Golden("worker-cs/v1-base/Sales.Worker.jsonl").Split('\n')[0])!.AsObject();
            for (var i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                var header = (JsonObject)golden.DeepClone();
                header["repositoryId"] = unit.Repository;
                header["commitSha"] = new string('a', 40);
                header["unit"] = new JsonObject { ["path"] = unit.UnitPath, ["name"] = unit.UnitName };
                header["language"] = unit.Language;
                var records = unit.Records;
                int Count(string kind) => records.Count(r => (string?)r["kind"] == kind);
                var complete = new JsonObject
                {
                    ["schema"] = "ce.v1", ["kind"] = "complete", ["status"] = "full",
                    ["counts"] = new JsonObject
                    {
                        ["node"] = Count("node"), ["edge"] = Count("edge"), ["evidence"] = Count("evidence"),
                        ["calculation"] = 0, ["diagnostic"] = 0,
                    },
                    ["coverage"] = JsonNode.Parse("""{"filesTotal":1,"filesAnalyzed":1,"unresolvedEdges":0}"""),
                };
                var text = string.Join('\n', new[] { header }.Concat(records).Append(complete).Select(r => r.ToJsonString())) + "\n";
                var validated = ArtifactValidator.Validate(text);
                Assert.True(validated.IsValid, string.Join("; ", validated.Problems));
                File.WriteAllText(Path.Combine(directory, $"{i:D3}.jsonl"), text);
            }

            var index = ExplorerIndex.Load([directory], null);
            Assert.Empty(index.Problems);
            return index;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
