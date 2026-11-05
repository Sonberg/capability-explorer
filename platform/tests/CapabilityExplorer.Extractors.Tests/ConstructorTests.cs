using System.Text.Json;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

/// <summary>
/// Constructors are where data is shaped: <c>new KlarnaCheckoutOrderInput(cart, quote, ...)</c> is the one place that says a Shopify cart and
/// a Shopify quote become a Klarna order. They used to be invisible: no node, no call edge into them, and code inside them attributed to nothing.
/// </summary>
public sealed class ConstructorTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Ns = "cs|synthetic|Synthetic|";
    private const string InputCtor = Ns + "M:Synthetic.OrderInput.#ctor(Synthetic.Cart,Synthetic.Quote,System.Threading.CancellationToken)";
    private const string LineCtor = Ns + "M:Synthetic.OrderLine.#ctor(Synthetic.Line,System.Decimal)";
    private readonly string _root = Directory.CreateTempSubdirectory("ce-ctor-").FullName;
    private readonly JsonElement[] _records;

    public ConstructorTests() => _records = Extract("""
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        namespace Synthetic;

        public class Line { }
        public class Cart { public List<Line> Lines { get; } = new(); }
        public class Quote
        {
            public decimal TaxRate => 0.25m;
            public void ApplyTo(List<OrderLine> lines) { }
        }
        public class OrderLine
        {
            public OrderLine(Line line, decimal tax) { }
        }
        public class DerivedLine : OrderLine
        {
            public DerivedLine(Line line) : base(line, 0m) { }
        }
        public class OrderInput
        {
            public OrderInput(Cart cart, Quote quote, CancellationToken cancellationToken = default)
            {
                Lines = cart.Lines.Select(l => new OrderLine(l, quote.TaxRate)).ToList();
                quote.ApplyTo(Lines);
            }

            public List<OrderLine> Lines { get; }
        }
        public record Money(decimal Amount);
        public class Handler
        {
            public OrderInput Build(Cart cart, Quote quote) => new OrderInput(cart, quote);
            public Money Zero() => new Money(0m);
            public object Framework() => new List<int>();
        }
        """);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private IEnumerable<JsonElement> Edges(string relation) => _records.Where(r => r.GetProperty("kind").GetString() == "edge" && r.GetProperty("relation").GetString() == relation);

    private static string? To(JsonElement e) => e.TryGetProperty("to", out var to) ? to.GetString() : null;

    private bool Has(string from, string relation, string to) => Edges(relation).Any(e => e.GetProperty("from").GetString() == from && To(e) == to);

    [Fact]
    public void A_constructor_is_a_method_named_after_its_type_with_its_parameters_as_signature()
    {
        var node = Assert.Single(_records, r => r.GetProperty("kind").GetString() == "node" && r.GetProperty("symbolKey").GetString() == InputCtor);
        Assert.Equal("Method", node.GetProperty("nodeKind").GetString());
        Assert.Equal("OrderInput", node.GetProperty("name").GetString());
        Assert.Contains("Cart cart, Quote quote", node.GetProperty("signature").GetString(), StringComparison.Ordinal);
        Assert.True(node.GetProperty("props").GetProperty("constructor").GetBoolean());
        Assert.True(Has(Ns + "T:Synthetic.OrderInput", "DECLARES", InputCtor));
    }

    [Fact]
    public void Constructor_parameters_reference_repository_types_and_not_framework_types()
    {
        var referenced = Edges("REFERENCES").Where(e => e.GetProperty("from").GetString() == InputCtor).Select(To).ToList();
        Assert.Contains(Ns + "T:Synthetic.Cart", referenced);
        Assert.Contains(Ns + "T:Synthetic.Quote", referenced);
        Assert.DoesNotContain(referenced, t => t!.Contains("CancellationToken", StringComparison.Ordinal));
    }

    [Fact]
    public void Creating_an_object_calls_its_declared_constructor()
    {
        Assert.True(Has(Ns + "M:Synthetic.Handler.Build(Synthetic.Cart,Synthetic.Quote)", "CALLS", InputCtor));
    }

    [Fact]
    public void Code_inside_a_constructor_belongs_to_it_including_lambdas()
    {
        Assert.True(Has(InputCtor, "CALLS", LineCtor)); // new OrderLine(...) inside a lambda in the constructor body
        Assert.True(Has(InputCtor, "CALLS", Ns + "M:Synthetic.Quote.ApplyTo(System.Collections.Generic.List{Synthetic.OrderLine})"));
    }

    [Fact]
    public void Constructor_initializers_call_the_base_constructor()
    {
        Assert.True(Has(Ns + "M:Synthetic.DerivedLine.#ctor(Synthetic.Line)", "CALLS", LineCtor));
    }

    [Fact]
    public void A_type_without_a_declared_constructor_is_referenced_instead()
    {
        Assert.True(Has(Ns + "M:Synthetic.Handler.Zero", "REFERENCES", Ns + "T:Synthetic.Money")); // record primary constructor: nothing to call
    }

    [Fact]
    public void Framework_constructors_are_not_recorded()
    {
        Assert.DoesNotContain(_records, r => r.GetProperty("kind").GetString() == "edge" && To(r) is { } to && to.StartsWith("cs|@ext|", StringComparison.Ordinal) && to.Contains("#ctor", StringComparison.Ordinal));
    }

    [Fact]
    public void Given_a_long_constructor_signature_when_extracted_then_keys_are_bounded_consistent_and_deterministic()
    {
        var payload = "Payload" + new string('P', 100);
        var parameters = string.Join(", ", Enumerable.Range(0, 24).Select(index => $"{payload} parameter{index}"));
        var arguments = string.Join(", ", Enumerable.Repeat("value", 24));
        var code = $$"""
            namespace Synthetic;
            public class {{payload}} { }
            public class Wide
            {
                public Wide({{parameters}}) { Touch(); }
                public void Touch() { }
            }
            public class Handler
            {
                public Wide Build({{payload}} value) => new Wide({{arguments}});
            }
            """;
        var records = Extract(code);
        var constructor = Assert.Single(records, record => record.GetProperty("kind").GetString() == "node"
            && record.TryGetProperty("name", out var name) && name.GetString() == "Wide"
            && record.TryGetProperty("props", out var props) && props.TryGetProperty("constructor", out _));
        var key = constructor.GetProperty("symbolKey").GetString()!;

        Assert.True(key.Length <= 2048, $"Constructor symbol key has {key.Length} characters; maximum is 2048.");
        Assert.StartsWith(Ns + "@sha256:", key);
        Assert.Contains(records, record => record.GetProperty("kind").GetString() == "edge"
            && record.GetProperty("relation").GetString() == "CALLS" && To(record) == key);
        Assert.Contains(records, record => record.GetProperty("kind").GetString() == "edge"
            && record.GetProperty("from").GetString() == key && To(record) == Ns + "M:Synthetic.Wide.Touch");
        foreach (var record in records)
            foreach (var field in new[] { "symbolKey", "from", "to" })
                if (record.TryGetProperty(field, out var value)) Assert.InRange(value.GetString()!.Length, 1, 2048);

        var report = ArtifactValidator.Validate(string.Join('\n', records.Select(record => record.GetRawText())) + "\n");
        Assert.True(report.IsValid, string.Join("; ", report.Problems));
        Assert.Equal(records.Select(record => record.GetRawText()), Extract(code).Select(record => record.GetRawText()));
    }

    [Fact]
    public void Given_long_overloads_when_extracted_then_each_identity_and_call_remains_distinct()
    {
        var payload = "Payload" + new string('P', 100);
        var parameters = string.Join(", ", Enumerable.Range(0, 24).Select(index => $"{payload} parameter{index}"));
        var arguments = string.Join(", ", Enumerable.Repeat("value", 24));
        var records = Extract($$"""
            namespace Synthetic;
            public class {{payload}} { }
            public class Wide
            {
                public Wide({{parameters}}) { }
                public Wide({{parameters}}, int marker) { }
            }
            public class Handler
            {
                public Wide First({{payload}} value) => new Wide({{arguments}});
                public Wide Second({{payload}} value) => new Wide({{arguments}}, 1);
            }
            """);
        var keys = records.Where(record => record.GetProperty("kind").GetString() == "node"
            && record.TryGetProperty("props", out var props) && props.TryGetProperty("constructor", out _))
            .Select(record => record.GetProperty("symbolKey").GetString()!).ToArray();
        Assert.Equal(2, keys.Length);
        Assert.Equal(2, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.Matches(@"^cs\|synthetic\|Synthetic\|@sha256:[a-f0-9]{64}$", key));
        foreach (var key in keys)
            Assert.Single(records, record => record.GetProperty("kind").GetString() == "edge"
                && record.GetProperty("relation").GetString() == "CALLS" && To(record) == key);
        var report = ArtifactValidator.Validate(string.Join('\n', records.Select(record => record.GetRawText())) + "\n");
        Assert.True(report.IsValid, string.Join("; ", report.Problems));
    }

    private JsonElement[] Extract(string code)
    {
        var source = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Synthetic.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(source, "Code.cs"), code);
        var packs = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
        var result = Assert.Single(new CSharpExtractor(new ExtractorOptions(source, "synthetic", Sha, PackagesRoot: null, ReferencePacks: packs)).Extract());
        return result.Jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l =>
        {
            using var doc = JsonDocument.Parse(l);
            return doc.RootElement.Clone();
        }).ToArray();
    }
}
