using System.Text.Json;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Extractors.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class PackageConstructorTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string PackageReference = """<PackageReference Include="Surface" Version="1.0.0" />""";
    private const string Metadata = """
        namespace Library;
        public sealed class Widget
        {
            public Widget(int value) { }
            public Widget(string value) { }
        }
        public sealed class Box<T> { public Box(T value) { } }
        public sealed class Choice
        {
            public Choice(string value) { }
            public Choice(System.Uri value) { }
        }
        """;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "ce-package-constructors-" + Guid.NewGuid().ToString("N"));
    private string SourceRoot => Path.Combine(_root, "src");
    private string Cache => Path.Combine(_root, "packages");
    private static string Packs => Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Resolved_package_overloads_and_generic_constructors_have_exact_method_and_property_calls()
    {
        Package();
        const string source = """
            namespace Repo;
            public sealed class Host
            {
                public object Build() => new Library.Widget(7);
                public object Text() => new Library.Widget("ok");
                public Library.Widget Current { get; } = new("ready");
                public object Generic() => new Library.Box<int>(9);
                private readonly Library.Widget field = new(0);
                public object Framework() => new System.Collections.Generic.List<int>();
                public object Root() => new object();
            }
            """;
        Project("App", source, PackageReference);

        var first = Extract();
        var records = Records(Assert.Single(first).Jsonl);
        AssertFull(records);
        Assert.Equal(first.Select(unit => unit.Jsonl), Extract().Select(unit => unit.Jsonl));
        Assert.Equal(4, Calls(records).Count());
        AssertCall(records, "cs|packages|App|M:Repo.Host.Build",
            "cs|@ext|Surface|M:Library.Widget.#ctor(System.Int32)", source, 4, 30, 51, "new Library.Widget(7)");
        AssertCall(records, "cs|packages|App|M:Repo.Host.Text",
            "cs|@ext|Surface|M:Library.Widget.#ctor(System.String)", source, 5, 29, 53, "new Library.Widget(\"ok\")");
        AssertCall(records, "cs|packages|App|P:Repo.Host.Current",
            "cs|@ext|Surface|M:Library.Widget.#ctor(System.String)", source, 6, 46, 58, "new(\"ready\")");
        AssertCall(records, "cs|packages|App|M:Repo.Host.Generic",
            "cs|@ext|Surface|M:Library.Box`1.#ctor(`0)", source, 7, 32, 55, "new Library.Box<int>(9)");

        Assert.Contains(records, record => Is(record, "node") && record.GetProperty("symbolKey").GetString() == "cs|packages|App|F:Repo.Host.field");
        Assert.DoesNotContain(records, record => Is(record, "edge") && record.GetProperty("from").GetString() == "cs|packages|App|F:Repo.Host.field");
        Assert.DoesNotContain(records, record => Is(record, "node") && record.GetProperty("symbolKey").GetString()!.StartsWith("cs|@ext|", StringComparison.Ordinal));
        Assert.DoesNotContain(records, record => Is(record, "edge") && record.TryGetProperty("to", out var to)
            && (to.GetString()!.Contains("System.Collections.Generic.List", StringComparison.Ordinal)
                || to.GetString()!.Contains("System.Object", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Disabled_or_missing_package_cache_does_not_fabricate_constructor_calls(bool disabled)
    {
        Package();
        Project("App", "public class Host { public object Build() => new Library.Widget(7); }", PackageReference);
        var cache = disabled ? null : Path.Combine(_root, "missing-packages");

        var records = Records(Assert.Single(Extract(cache)).Jsonl);
        Assert.Empty(Calls(records));
        Assert.Contains(records, record => Is(record, "diagnostic") && record.GetProperty("code").GetString() == "UNRESOLVED_PACKAGE");
        Assert.Contains(records, record => Is(record, "diagnostic") && record.GetProperty("code").GetString() == "COMPILER_CS0246"
            && record.GetProperty("affectsCoverage").GetBoolean());
        Assert.Equal("partial", records[^1].GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("new Missing()", "CS0246")]
    [InlineData("new Library.Widget(true)", "CS1503")]
    [InlineData("new Library.Choice(null)", "CS0121")]
    public void Missing_candidate_and_ambiguous_creations_remain_compiler_diagnostics_without_calls(string expression, string diagnostic)
    {
        var metadata = Package();
        var source = $"public class Host {{ public object Build() => {expression}; }}";
        Project("App", source, PackageReference);
        var oracle = Compilation("Oracle", source, FrameworkReferences().Append(MetadataReference.CreateFromFile(metadata)));
        Assert.Contains(oracle.GetDiagnostics(), item => item.Severity == DiagnosticSeverity.Error && item.Id == diagnostic);

        var records = Records(Assert.Single(Extract()).Jsonl);
        Assert.Empty(Calls(records));
        Assert.Contains(records, record => Is(record, "diagnostic") && record.GetProperty("code").GetString() == $"COMPILER_{diagnostic}"
            && record.GetProperty("affectsCoverage").GetBoolean());
        Assert.Equal("partial", records[^1].GetProperty("status").GetString());
    }

    [Fact]
    public void System_namespace_and_repository_assembly_name_collision_do_not_hide_package_provenance()
    {
        Package("namespace System.PackageSurface; public sealed class Item { public Item(int value) { } }", "Shadow");
        Project("Shadow", "namespace Repo; public sealed class Marker { }");
        const string source = """
            public class Host
            {
                public object Build() => new System.PackageSurface.Item(5);
            }
            """;
        Project("App", source, PackageReference);

        var units = Extract();
        Assert.All(units, unit => AssertFull(Records(unit.Jsonl)));
        var records = Records(Assert.Single(units, unit => unit.UnitName == "App").Jsonl);
        Assert.Single(Calls(records));
        AssertCall(records, "cs|packages|App|M:Host.Build", "cs|@ext|Shadow|M:System.PackageSurface.Item.#ctor(System.Int32)",
            source, 3, 30, 63, "new System.PackageSurface.Item(5)");
        Assert.DoesNotContain(records, record => Is(record, "node") && record.GetProperty("name").GetString() == "Item");
    }

    [Fact]
    public void Package_metadata_selected_transitively_through_a_project_reference_is_callable()
    {
        Package();
        Project("Dependency", "public class Bridge { }", PackageReference);
        const string source = """
            public class Host
            {
                public object Build() => new Library.Widget(7);
            }
            """;
        Project("App", source, """<ProjectReference Include="../Dependency/Dependency.csproj" />""");

        var units = Extract();
        Assert.All(units, unit => AssertFull(Records(unit.Jsonl)));
        var records = Records(Assert.Single(units, unit => unit.UnitName == "App").Jsonl);
        Assert.Single(Calls(records));
        AssertCall(records, "cs|packages|App|M:Host.Build", "cs|@ext|Surface|M:Library.Widget.#ctor(System.Int32)",
            source, 3, 30, 51, "new Library.Widget(7)");
    }

    private static bool Is(JsonElement record, string kind) => record.GetProperty("kind").GetString() == kind;

    private static IEnumerable<JsonElement> Calls(JsonElement[] records) =>
        records.Where(record => Is(record, "edge") && record.GetProperty("relation").GetString() == "CALLS");

    private static void AssertCall(JsonElement[] records, string from, string to, string source, int line, int start, int end, string text)
    {
        var call = Assert.Single(Calls(records), record => record.GetProperty("from").GetString() == from);
        Assert.Equal((from, "CALLS", to, "compiler", "resolved"),
            (call.GetProperty("from").GetString(), call.GetProperty("relation").GetString(), call.GetProperty("to").GetString(),
                call.GetProperty("derivation").GetString(), call.GetProperty("resolution").GetString()));
        var reference = Assert.Single(call.GetProperty("evidence").EnumerateArray()).GetString();
        var evidence = Assert.Single(records, record => Is(record, "evidence") && record.GetProperty("id").GetString() == reference);
        Assert.Equal("App/Code.cs", evidence.GetProperty("path").GetString());
        var span = evidence.GetProperty("span");
        Assert.Equal((line, start, line, end),
            (span.GetProperty("startLine").GetInt32(), span.GetProperty("startColumn").GetInt32(),
                span.GetProperty("endLine").GetInt32(), span.GetProperty("endColumn").GetInt32()));
        Assert.Equal(text, source.Split('\n')[line - 1][(start - 1)..(end - 1)]);
        Assert.Equal(Snippet.Sha256(text), evidence.GetProperty("snippetSha256").GetString());
    }

    private static JsonElement[] Records(string jsonl)
    {
        var report = ArtifactValidator.Validate(jsonl);
        Assert.True(report.IsValid, string.Join("; ", report.Problems));
        return jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
    }

    private static void AssertFull(JsonElement[] records)
    {
        Assert.DoesNotContain(records, record => Is(record, "diagnostic") && record.GetProperty("code").GetString()!.StartsWith("COMPILER_", StringComparison.Ordinal));
        Assert.Equal("full", records[^1].GetProperty("status").GetString());
    }

    private string Package(string source = Metadata, string assembly = "Surface")
    {
        var directory = Directory.CreateDirectory(Path.Combine(Cache, "surface", "1.0.0", "lib", "net10.0")).FullName;
        var path = Path.Combine(directory, assembly + ".dll");
        var emit = Compilation(assembly, source, FrameworkReferences()).Emit(path);
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
        return path;
    }

    private static CSharpCompilation Compilation(string assembly, string source, IEnumerable<MetadataReference> references) =>
        CSharpCompilation.Create(assembly, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static IEnumerable<MetadataReference> FrameworkReferences()
    {
        var framework = Directory.EnumerateDirectories(Path.Combine(Packs, "Microsoft.NETCore.App.Ref"))
            .Order(StringComparer.Ordinal).Select(directory => Path.Combine(directory, "ref", "net10.0")).Last(Directory.Exists);
        return Directory.EnumerateFiles(framework, "*.dll").Order(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path));
    }

    private void Project(string name, string source, string references = "")
    {
        var directory = Directory.CreateDirectory(Path.Combine(SourceRoot, name)).FullName;
        File.WriteAllText(Path.Combine(directory, name + ".csproj"),
            $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>{references}</ItemGroup></Project>""");
        File.WriteAllText(Path.Combine(directory, "Code.cs"), source);
    }

    private IReadOnlyList<UnitResult> Extract() => Extract(Cache);

    private IReadOnlyList<UnitResult> Extract(string? cache) =>
        new CSharpExtractor(new ExtractorOptions(SourceRoot, "packages", Sha, cache, Packs)).Extract();
}
