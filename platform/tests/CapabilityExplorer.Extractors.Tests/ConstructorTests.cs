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
    private const string MoneyCtor = Ns + "M:Synthetic.Money.#ctor(System.Decimal)";
    private readonly string _root = Directory.CreateTempSubdirectory("ce-ctor-").FullName;
    private readonly JsonElement[] _records;

    public ConstructorTests() => _records = Extract("""
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        namespace Synthetic;

        public class Line { }
        public class Box<T> { }
        public record LineBundle(Line[] Lines);
        public class Cart { public List<Line> Lines { get; } = new(); }
        public class Primary(Box<Line[]> lines, Line[,] matrix, CancellationToken cancellationToken) { }
        public partial class Split(int count) { }
        public partial class Split { }
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
            public Primary CreatePrimary() => new Primary(new Box<Line[]>(), new Line[1, 1], default);
            public LineBundle CreateBundle() => new LineBundle(new Line[0]);
            public Split CreateSplit() => new Split(1);
            public Cart EmptyCart() => new Cart();
            public Box<OrderLine[]> Wrap(IReadOnlyList<OrderLine> lines, Cart cart) => new();
            public Money Zero() => new Money(0m);
            public object Framework() => new List<int>();
            public void FrameworkOnly(CancellationToken cancellationToken) { }
        }
        """);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private IEnumerable<JsonElement> Edges(string relation) => _records.Where(r => r.GetProperty("kind").GetString() == "edge" && r.GetProperty("relation").GetString() == relation);

    private static string? To(JsonElement e) => e.TryGetProperty("to", out var to) ? to.GetString() : null;

    private bool Has(string from, string relation, string to) => Edges(relation).Any(e => e.GetProperty("from").GetString() == from && To(e) == to);

    private string EvidenceText(JsonElement reference)
    {
        var evidence = Assert.Single(_records, record => record.GetProperty("kind").GetString() == "evidence"
            && record.GetProperty("id").GetString() == reference.GetString());
        var path = evidence.GetProperty("path").GetString()!;
        var file = Assert.Single(Directory.GetFiles(_root, Path.GetFileName(path), SearchOption.AllDirectories));
        var span = evidence.GetProperty("span");
        var startLine = span.GetProperty("startLine").GetInt32();
        var endLine = span.GetProperty("endLine").GetInt32();
        Assert.Equal(startLine, endLine);
        var line = File.ReadAllLines(file)[startLine - 1];
        var startColumn = span.GetProperty("startColumn").GetInt32();
        var endColumn = span.GetProperty("endColumn").GetInt32();
        return line[(startColumn - 1)..(endColumn - 1)];
    }

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
    public void Class_primary_constructor_is_a_method_with_repository_parameter_references_and_exact_evidence()
    {
        var constructor = Assert.Single(_records, record => record.GetProperty("kind").GetString() == "node"
            && record.GetProperty("name").GetString() == "Primary"
            && record.TryGetProperty("props", out var props) && props.TryGetProperty("constructor", out _));
        var key = constructor.GetProperty("symbolKey").GetString()!;
        Assert.Equal("Method", constructor.GetProperty("nodeKind").GetString());
        Assert.Contains("Box<Line[]> lines, Line[,] matrix", constructor.GetProperty("signature").GetString(), StringComparison.Ordinal);
        Assert.Equal("public class Primary(Box<Line[]> lines, Line[,] matrix, CancellationToken cancellationToken)",
            EvidenceText(constructor.GetProperty("evidence")[0]));
        Assert.True(Has(Ns + "T:Synthetic.Primary", "DECLARES", key));

        var references = Edges("REFERENCES").Where(edge => edge.GetProperty("from").GetString() == key).ToList();
        Assert.Contains(references, edge => To(edge) == Ns + "T:Synthetic.Box`1" && EvidenceText(edge.GetProperty("evidence")[0]) == "Box<Line[]> lines");
        Assert.Contains(references, edge => To(edge) == Ns + "T:Synthetic.Line" && EvidenceText(edge.GetProperty("evidence")[0]) == "Box<Line[]> lines");
        Assert.Contains(references, edge => To(edge) == Ns + "T:Synthetic.Line" && EvidenceText(edge.GetProperty("evidence")[0]) == "Line[,] matrix");
        Assert.DoesNotContain(references, edge => To(edge)!.Contains("CancellationToken", StringComparison.Ordinal));
    }

    [Fact]
    public void Record_primary_constructor_is_a_method_and_object_creation_calls_primary_nodes()
    {
        var recordCtor = Assert.Single(_records, record => record.GetProperty("kind").GetString() == "node"
            && record.GetProperty("symbolKey").GetString() == MoneyCtor);
        Assert.Equal("Method", recordCtor.GetProperty("nodeKind").GetString());
        Assert.True(recordCtor.GetProperty("props").GetProperty("constructor").GetBoolean());
        Assert.Equal("Money", recordCtor.GetProperty("name").GetString());
        Assert.Contains("decimal Amount", recordCtor.GetProperty("signature").GetString(), StringComparison.Ordinal);
        Assert.Contains("public record Money(decimal Amount)", EvidenceText(recordCtor.GetProperty("evidence")[0]));
        Assert.True(Has(Ns + "M:Synthetic.Handler.Zero", "CALLS", MoneyCtor));

        var bundleCtor = Assert.Single(_records, record => record.GetProperty("kind").GetString() == "node"
            && record.GetProperty("name").GetString() == "LineBundle"
            && record.TryGetProperty("props", out var props) && props.TryGetProperty("constructor", out _));
        var bundleKey = bundleCtor.GetProperty("symbolKey").GetString()!;
        var lineReference = Assert.Single(Edges("REFERENCES"), edge => edge.GetProperty("from").GetString() == bundleKey
            && To(edge) == Ns + "T:Synthetic.Line");
        Assert.Equal("Line[] Lines", EvidenceText(lineReference.GetProperty("evidence")[0]));
        Assert.True(Has(Ns + "M:Synthetic.Handler.CreateBundle", "CALLS", bundleKey));

        var primary = Assert.Single(_records, record => record.GetProperty("kind").GetString() == "node"
            && record.GetProperty("name").GetString() == "Primary"
            && record.TryGetProperty("props", out var props) && props.TryGetProperty("constructor", out _));
        Assert.True(Has(Ns + "M:Synthetic.Handler.CreatePrimary", "CALLS", primary.GetProperty("symbolKey").GetString()!));
    }

    [Fact]
    public void Partial_primary_constructor_is_emitted_once_and_implicit_constructor_keeps_type_reference_behavior()
    {
        var constructor = Assert.Single(_records, record => record.GetProperty("kind").GetString() == "node"
            && record.GetProperty("name").GetString() == "Split"
            && record.TryGetProperty("props", out var props) && props.TryGetProperty("constructor", out _));
        var key = constructor.GetProperty("symbolKey").GetString()!;
        Assert.True(Has(Ns + "M:Synthetic.Handler.CreateSplit", "CALLS", key));
        Assert.True(Has(Ns + "M:Synthetic.Handler.EmptyCart", "REFERENCES", Ns + "T:Synthetic.Cart"));
    }

    [Fact]
    public void Primary_constructor_artifacts_are_deterministic()
    {
        const string code = """
            using System.Threading;
            namespace Synthetic;
            public class Leaf { }
            public class Box<T> { }
            public class Primary<T>(Box<Leaf[]> payload, Leaf[] values, CancellationToken token) { }
            public record Result(Leaf Value);
            public class Handler
            {
                public Primary<int> Create() => new Primary<int>(new Box<Leaf[]>(), new Leaf[1], default);
                public Result Make() => new Result(new Leaf());
            }
            """;

        var first = Extract(code).Select(record => record.GetRawText()).ToArray();
        Assert.Equal(first, Extract(code).Select(record => record.GetRawText()).ToArray());
    }

    [Fact]
    public void Ordinary_method_parameters_and_return_types_reference_repository_types_with_evidence()
    {
        var method = Ns + "M:Synthetic.Handler.Build(Synthetic.Cart,Synthetic.Quote)";
        var references = Edges("REFERENCES").Where(edge => edge.GetProperty("from").GetString() == method).ToList();

        Assert.Contains(references, edge => To(edge) == Ns + "T:Synthetic.Cart");
        Assert.Contains(references, edge => To(edge) == Ns + "T:Synthetic.Quote");
        Assert.Contains(references, edge => To(edge) == Ns + "T:Synthetic.OrderInput");
        Assert.All(references, edge => Assert.NotEmpty(edge.GetProperty("evidence").EnumerateArray()));
    }

    [Fact]
    public void Ordinary_method_signatures_include_nested_repository_types_but_not_framework_types()
    {
        var method = Ns + "M:Synthetic.Handler.Wrap(System.Collections.Generic.IReadOnlyList{Synthetic.OrderLine},Synthetic.Cart)";
        var referenced = Edges("REFERENCES").Where(edge => edge.GetProperty("from").GetString() == method).Select(To).ToList();

        Assert.Contains(Ns + "T:Synthetic.Box`1", referenced);
        Assert.Contains(Ns + "T:Synthetic.OrderLine", referenced);
        Assert.Contains(Ns + "T:Synthetic.Cart", referenced);
        Assert.DoesNotContain(referenced, target => target!.Contains("IReadOnlyList", StringComparison.Ordinal));

        var frameworkOnly = Ns + "M:Synthetic.Handler.FrameworkOnly(System.Threading.CancellationToken)";
        Assert.DoesNotContain(Edges("REFERENCES"), edge => edge.GetProperty("from").GetString() == frameworkOnly);
        Assert.DoesNotContain(Edges("REFERENCES"), edge => edge.GetProperty("from").GetString() == Ns + "M:Synthetic.Handler.Framework");
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
    public void Creating_a_type_with_an_implicit_default_constructor_references_its_type()
    {
        Assert.True(Has(Ns + "M:Synthetic.Handler.EmptyCart", "REFERENCES", Ns + "T:Synthetic.Cart"));
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

    private const string FieldSource = """
        using System;
        namespace Synthetic;
        public class Explicit { public Explicit() { } }
        public class Implicit { }
        public class Fields
        {
            [Obsolete] private readonly Explicit instance = new Explicit();
            public static Explicit shared = new();
            private Explicit first = new Explicit(), second = new();
            private Implicit implicitValue = new();
            private object framework = new object();
            private Explicit empty;
            private Explicit viaCall = Make();
            private Func<Explicit> deferred = () => new Explicit();
            private Func<Explicit> anonymous = delegate { return new Explicit(); };
            public event Action Changed = delegate { _ = new Explicit(); };
            public Explicit ExplicitProperty { get; } = new Explicit();
            public static Implicit ImplicitProperty { get; } = new();
            public Fields() : this(0) { }
            public Fields(int marker) { }
            static Fields() { }
            private static Explicit Make() => new Explicit();
        }
        """;

    [Fact]
    public void Field_initializers_reference_repository_types_without_inventing_constructor_execution()
    {
        var records = Extract(FieldSource);
        var fields = records.Where(r => r.GetProperty("kind").GetString() == "node"
            && r.GetProperty("nodeKind").GetString() == "Field").ToArray();
        Assert.Equal(new[] { "anonymous", "deferred", "empty", "first", "framework", "implicitValue", "instance", "second", "shared", "viaCall" },
            fields.Select(r => r.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
        foreach (var field in fields)
        {
            var name = field.GetProperty("name").GetString()!;
            var key = Ns + "F:Synthetic.Fields." + name;
            Assert.Equal(key, field.GetProperty("symbolKey").GetString());
            Assert.Equal(name, SourceEvidence(records, field.GetProperty("evidence")[0], FieldSource));
            Assert.Contains(records, r => r.GetProperty("kind").GetString() == "edge"
                && r.GetProperty("from").GetString() == Ns + "T:Synthetic.Fields"
                && r.GetProperty("relation").GetString() == "DECLARES" && To(r) == key);
        }
        var edges = records.Where(r => r.GetProperty("kind").GetString() == "edge").ToArray();
        var references = edges.Where(r => r.GetProperty("from").GetString()!.StartsWith(Ns + "F:", StringComparison.Ordinal))
            .Select(r => (r.GetProperty("from").GetString()!, r.GetProperty("relation").GetString()!, To(r)!,
                SourceEvidence(records, r.GetProperty("evidence")[0], FieldSource))).OrderBy(r => r.Item1, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            (Ns + "F:Synthetic.Fields.first", "REFERENCES", Ns + "T:Synthetic.Explicit", "new Explicit()"),
            (Ns + "F:Synthetic.Fields.implicitValue", "REFERENCES", Ns + "T:Synthetic.Implicit", "new()"),
            (Ns + "F:Synthetic.Fields.instance", "REFERENCES", Ns + "T:Synthetic.Explicit", "new Explicit()"),
            (Ns + "F:Synthetic.Fields.second", "REFERENCES", Ns + "T:Synthetic.Explicit", "new()"),
            (Ns + "F:Synthetic.Fields.shared", "REFERENCES", Ns + "T:Synthetic.Explicit", "new()"),
        }, references);
        Assert.DoesNotContain(edges, r => r.GetProperty("from").GetString()!.StartsWith(Ns + "M:Synthetic.Fields.#", StringComparison.Ordinal)
            && (To(r) == Ns + "T:Synthetic.Explicit" || To(r) == Ns + "M:Synthetic.Explicit.#ctor"));
        Assert.DoesNotContain(records, r => r.GetProperty("kind").GetString() == "node"
            && r.GetProperty("symbolKey").GetString() == Ns + "E:Synthetic.Fields.Changed");
    }

    [Fact]
    public void Property_initializers_and_constructor_chaining_keep_existing_owners_and_relations()
    {
        var records = Extract(FieldSource);
        var edges = records.Where(r => r.GetProperty("kind").GetString() == "edge").ToArray();
        foreach (var expected in new[]
        {
            (Ns + "P:Synthetic.Fields.ExplicitProperty", "CALLS", Ns + "M:Synthetic.Explicit.#ctor", "new Explicit()"),
            (Ns + "P:Synthetic.Fields.ImplicitProperty", "REFERENCES", Ns + "T:Synthetic.Implicit", "new()"),
            (Ns + "M:Synthetic.Fields.#ctor", "CALLS", Ns + "M:Synthetic.Fields.#ctor(System.Int32)", ": this(0)"),
            (Ns + "M:Synthetic.Fields.Make", "CALLS", Ns + "M:Synthetic.Explicit.#ctor", "new Explicit()"),
        })
        {
            var edge = Assert.Single(edges, r => r.GetProperty("from").GetString() == expected.Item1
                && r.GetProperty("relation").GetString() == expected.Item2 && To(r) == expected.Item3);
            Assert.Equal(expected.Item4, SourceEvidence(records, edge.GetProperty("evidence")[0], FieldSource));
        }
    }

    [Fact]
    public void Unresolved_field_creation_retains_diagnostic_without_a_fabricated_reference()
    {
        var records = Extract("namespace Synthetic; public class Broken { private object missing = new Missing(); }");
        Assert.Contains(records, r => r.GetProperty("kind").GetString() == "node"
            && r.GetProperty("nodeKind").GetString() == "Field"
            && r.GetProperty("symbolKey").GetString() == Ns + "F:Synthetic.Broken.missing");
        Assert.DoesNotContain(records, r => r.GetProperty("kind").GetString() == "edge"
            && r.GetProperty("from").GetString() == Ns + "F:Synthetic.Broken.missing");
        Assert.Contains(records, r => r.GetProperty("kind").GetString() == "diagnostic"
            && r.GetProperty("code").GetString() == "COMPILER_CS0246" && r.GetProperty("affectsCoverage").GetBoolean());
        Assert.Equal("partial", records[^1].GetProperty("status").GetString());
    }

    [Fact]
    public void Field_initializer_artifacts_are_contract_valid_and_byte_deterministic()
    {
        var records = Extract(FieldSource);
        Assert.Equal(records.Select(r => r.GetRawText()), Extract(FieldSource).Select(r => r.GetRawText()));
        var report = ArtifactValidator.Validate(string.Join('\n', records.Select(r => r.GetRawText())) + "\n");
        Assert.True(report.IsValid, string.Join("; ", report.Problems));
        Assert.Equal("full", records[^1].GetProperty("status").GetString());
    }

    private static string SourceEvidence(JsonElement[] records, JsonElement reference, string source)
    {
        var evidence = Assert.Single(records, r => r.GetProperty("kind").GetString() == "evidence"
            && r.GetProperty("id").GetString() == reference.GetString());
        Assert.Equal("Code.cs", evidence.GetProperty("path").GetString());
        var span = evidence.GetProperty("span");
        Assert.Equal(span.GetProperty("startLine").GetInt32(), span.GetProperty("endLine").GetInt32());
        var line = source.Split('\n')[span.GetProperty("startLine").GetInt32() - 1];
        return line[(span.GetProperty("startColumn").GetInt32() - 1)..(span.GetProperty("endColumn").GetInt32() - 1)];
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
