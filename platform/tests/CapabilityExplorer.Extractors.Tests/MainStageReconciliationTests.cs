using System.Text.Json;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

/// <summary>Regression evidence for retaining constructor facts with target-aware, fenced package resolution.</summary>
public sealed class MainStageReconciliationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ce-reconciliation-").FullName;
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Constructor_chaining_and_repository_parameter_references_survive_reconciliation()
    {
        File.WriteAllText(Path.Combine(root, "Model.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(root, "Model.cs"), """
            namespace Model;
            public class Payload { }
            public class Handler
            {
                public Handler() : this(new Payload()) { }
                public Handler(Payload payload) { }
            }
            """);
        var packs = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
        var result = Assert.Single(new CSharpExtractor(new ExtractorOptions(root, "reconciled", new string('a', 40), null, packs)).Extract());
        var records = result.Jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }).ToArray();
        var edges = records.Where(r => r.GetProperty("kind").GetString() == "edge").ToArray();
        const string ns = "cs|reconciled|Model|";
        Assert.Contains(edges, e => e.GetProperty("relation").GetString() == "CALLS" && e.GetProperty("from").GetString() == ns + "M:Model.Handler.#ctor" && e.GetProperty("to").GetString() == ns + "M:Model.Handler.#ctor(Model.Payload)");
        Assert.Contains(edges, e => e.GetProperty("relation").GetString() == "REFERENCES" && e.GetProperty("from").GetString() == ns + "M:Model.Handler.#ctor(Model.Payload)" && e.GetProperty("to").GetString() == ns + "T:Model.Payload");
    }

    [Fact]
    public void Incompatible_assets_keep_unselected_observability_without_fabricating_references()
    {
        Package("future.lib", "net10.0");
        var result = new PackageResolver(root).Resolve([new PackageRef("future.lib", "1.0.0", 1)], [new LockedPackage("future.lib", "1.0.0")], "net8.0");
        Assert.Empty(result.Assemblies);
        Assert.Equal("future.lib", Assert.Single(result.Unselected).Id);
        Assert.Contains(result.Issues, i => i.Code == "PACKAGE_NO_COMPATIBLE_ASSETS");
    }

    [Fact]
    public void Unreferenced_lock_entries_never_expand_the_compile_surface()
    {
        Package("selected.lib", "net8.0");
        Package("unrelated.lib", "net10.0");
        var result = new PackageResolver(root).Resolve([new PackageRef("selected.lib", "1.0.0", 1)],
            [new LockedPackage("selected.lib", "1.0.0"), new LockedPackage("unrelated.lib", "1.0.0")], "net8.0");
        Assert.Equal("selected.lib", Assert.Single(result.Resolved).Id);
        Assert.Equal("selected.lib.dll", Path.GetFileName(Assert.Single(result.Assemblies)));
        Assert.Empty(result.Unselected);
    }

    [Fact]
    public void Explicit_empty_reference_group_is_not_misreported_as_unsupported_assets()
    {
        Package("empty.lib", "net8.0");
        var reference = Path.Combine(root, "empty.lib", "1.0.0", "ref", "net8.0");
        Directory.CreateDirectory(reference);
        File.WriteAllText(Path.Combine(reference, "_._"), "");
        var result = new PackageResolver(root).Resolve([new PackageRef("empty.lib", "1.0.0", 1)], [new LockedPackage("empty.lib", "1.0.0")], "net8.0");
        Assert.Empty(result.Assemblies);
        Assert.Empty(result.Unselected);
        Assert.DoesNotContain(result.Issues, i => i.Code == "PACKAGE_NO_COMPATIBLE_ASSETS");
    }

    private void Package(string id, string target)
    {
        var directory = Path.Combine(root, id, "1.0.0", "lib", target);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, id + ".dll"), [0]);
    }
}
