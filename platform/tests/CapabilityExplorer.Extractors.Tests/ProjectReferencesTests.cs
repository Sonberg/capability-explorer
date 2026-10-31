using System.Text.Json;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class ProjectReferencesTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Directory.CreateTempSubdirectory("ce-projects-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Chain_resolves_a_type_returned_by_B_and_a_direct_call_to_C_without_a_direct_reference()
    {
        Project("C", """
            namespace C;
            public sealed class Value { public int Read() => 7; }
            public static class Api { public static int Read() => 9; }
            """);
        Project("B", "namespace B; public static class Bridge { public static C.Value Create() => new C.Value(); }", "C");
        Project("A", """
            namespace A;
            public static class Caller
            {
                public static int ThroughB() => B.Bridge.Create().Read();
                public static int Directly() => C.Api.Read();
            }
            """, "B");

        var units = Extract();
        var a = Records(Unit(units, "A"));
        AssertDependencies(a, "A", "B");
        AssertDependencies(Records(Unit(units, "B")), "B", "C");
        AssertDependencies(Records(Unit(units, "C")), "C");
        AssertCalls(a, "cs|projects|A|M:A.Caller.ThroughB",
            "cs|projects|B|M:B.Bridge.Create", "cs|projects|C|M:C.Value.Read");
        AssertCalls(a, "cs|projects|A|M:A.Caller.Directly", "cs|projects|C|M:C.Api.Read");
        AssertAllFull(units);
    }

    [Fact]
    public void Diamond_shares_C_without_duplicate_diagnostics_and_repeats_byte_for_byte()
    {
        Project("C", "namespace C; public sealed class Value { public int Read() => 7; }");
        Project("B", "namespace B; public static class Bridge { public static C.Value Create() => new C.Value(); }", "C");
        Project("D", "namespace D; public static class Bridge { public static C.Value Create() => new C.Value(); }", "C");
        Project("A", """
            namespace A;
            public static class Caller
            {
                public static int Left() => B.Bridge.Create().Read();
                public static int Right() => D.Bridge.Create().Read();
            }
            """, "B", "D");

        var first = Extract();
        var second = Extract();
        Assert.Equal(first.Select(u => u.UnitPath), second.Select(u => u.UnitPath));
        Assert.Equal(first.Select(u => u.Jsonl), second.Select(u => u.Jsonl));
        var a = Records(Unit(first, "A"));
        AssertDependencies(a, "A", "B", "D");
        AssertDependencies(Records(Unit(first, "B")), "B", "C");
        AssertDependencies(Records(Unit(first, "D")), "D", "C");
        AssertCalls(a, "cs|projects|A|M:A.Caller.Left",
            "cs|projects|B|M:B.Bridge.Create", "cs|projects|C|M:C.Value.Read");
        AssertCalls(a, "cs|projects|A|M:A.Caller.Right",
            "cs|projects|D|M:D.Bridge.Create", "cs|projects|C|M:C.Value.Read");
        AssertAllFull(first);
    }

    [Fact]
    public void Missing_project_preserves_diagnostic_partial_status_and_resolvable_neighbor()
    {
        Project("B", "namespace B; public static class Api { public static int Read() => 7; }");
        Project("A", "namespace A; public static class Caller { public static int Read() => B.Api.Read(); }", "B", "Missing");

        var units = Extract();
        var a = Records(Unit(units, "A"));
        var diagnostic = Assert.Single(OfKind(a, "diagnostic"));
        Assert.Equal("MISSING_PROJECT_REFERENCE", diagnostic.GetProperty("code").GetString());
        Assert.Equal("A/A.csproj", diagnostic.GetProperty("path").GetString());
        Assert.True(diagnostic.GetProperty("affectsCoverage").GetBoolean());
        Assert.Contains("Missing", diagnostic.GetProperty("message").GetString());
        Assert.Equal("partial", Complete(a).GetProperty("status").GetString());
        AssertCalls(a, "cs|projects|A|M:A.Caller.Read", "cs|projects|B|M:B.Api.Read");
        AssertAllFull([Unit(units, "B")]);
    }

    [Fact]
    public void Unrelated_discovered_project_does_not_make_its_symbols_visible_to_A()
    {
        Project("B", "namespace B; public static class Api { public static int Read() => 7; }");
        Project("Unrelated", "namespace Unrelated; public static class Api { public static int Read() => 9; }");
        Project("A", """
            namespace A;
            public static class Caller
            {
                public static int Visible() => B.Api.Read();
                public static int Hidden() => Unrelated.Api.Read();
            }
            """, "B");

        var units = Extract();
        Assert.Equal(3, units.Count);
        var a = Records(Unit(units, "A"));
        AssertDependencies(a, "A", "B");
        AssertCalls(a, "cs|projects|A|M:A.Caller.Visible", "cs|projects|B|M:B.Api.Read");
        var hidden = Assert.Single(Calls(a, "cs|projects|A|M:A.Caller.Hidden"));
        Assert.Equal("unresolved", hidden.GetProperty("resolution").GetString());
        Assert.Equal("missing-reference", hidden.GetProperty("unresolvedReason").GetString());
        Assert.False(hidden.TryGetProperty("to", out _));
        var diagnostic = Assert.Single(OfKind(a, "diagnostic"));
        Assert.Equal("COMPILER_CS0103", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Unrelated", diagnostic.GetProperty("message").GetString());
        Assert.Equal("partial", Complete(a).GetProperty("status").GetString());
        AssertAllFull(units.Where(u => u.UnitName != "A"));
    }

    private void Project(string name, string source, params string[] references)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var items = string.Concat(references.Select(r => $"<ProjectReference Include=\"../{r}/{r}.csproj\" />"));
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>{items}</ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, name + ".cs"), source);
    }

    private IReadOnlyList<UnitResult> Extract()
    {
        var packs = Path.GetFullPath(Path.Combine(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
        return new CSharpExtractor(new ExtractorOptions(_root, "projects", Sha, PackagesRoot: null, ReferencePacks: packs)).Extract();
    }

    private static UnitResult Unit(IEnumerable<UnitResult> units, string name) =>
        Assert.Single(units, u => u.UnitPath == $"{name}/{name}.csproj");

    private static JsonElement[] Records(UnitResult unit) =>
        unit.Jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    private static IEnumerable<JsonElement> OfKind(IEnumerable<JsonElement> records, string kind) =>
        records.Where(r => r.GetProperty("kind").GetString() == kind);

    private static JsonElement Complete(IEnumerable<JsonElement> records) => Assert.Single(OfKind(records, "complete"));

    private static IEnumerable<JsonElement> Calls(IEnumerable<JsonElement> records, string caller) =>
        OfKind(records, "edge").Where(r => r.GetProperty("relation").GetString() == "CALLS"
            && r.GetProperty("from").GetString() == caller);

    private static void AssertCalls(JsonElement[] records, string caller, params string[] targets)
    {
        var calls = Calls(records, caller).ToArray();
        var resolved = calls.Where(c => c.GetProperty("resolution").GetString() == "resolved")
            .Select(c => c.GetProperty("to").GetString()).Order(StringComparer.Ordinal).ToArray();
        Assert.True(targets.Order(StringComparer.Ordinal).SequenceEqual(resolved) && calls.Length == targets.Length,
            $"Expected resolved repository CALLS from {caller} to [{string.Join(", ", targets)}]. " +
            $"Actual calls: {string.Join("\n", calls.Select(c => c.GetRawText()))}. " +
            $"Diagnostics: {string.Join("\n", OfKind(records, "diagnostic").Select(d => d.GetRawText()))}");
    }

    private static void AssertDependencies(JsonElement[] records, string project, params string[] targets)
    {
        var dependencies = OfKind(records, "edge").Where(r => r.GetProperty("relation").GetString() == "DEPENDS_ON").ToArray();
        Assert.All(dependencies, d => Assert.Equal($"proj|projects|{project}/{project}.csproj", d.GetProperty("from").GetString()));
        Assert.Equal(targets.Select(t => $"proj|projects|{t}/{t}.csproj").Order(StringComparer.Ordinal),
            dependencies.Select(d => d.GetProperty("to").GetString()).Order(StringComparer.Ordinal));
    }

    private static void AssertAllFull(IEnumerable<UnitResult> units)
    {
        foreach (var unit in units)
        {
            var records = Records(unit);
            Assert.Empty(OfKind(records, "diagnostic"));
            var complete = Complete(records);
            Assert.Equal("full", complete.GetProperty("status").GetString());
            Assert.Equal(0, complete.GetProperty("coverage").GetProperty("unresolvedEdges").GetInt32());
        }
    }
}
