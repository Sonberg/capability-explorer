using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class CachedPackageGraphTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ce-package-graph-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Compatible_diamond_converges_to_lowest_common_cached_version_and_rewalks_its_dependencies()
    {
        Package("A", "1.0.0", ("Shared", "1.0.0"));
        Package("B", "1.0.0", ("Shared", "2.0.0"));
        Package("Shared", "1.0.0", ("Old", "1.0.0"));
        Package("Shared", "2.0.0", ("New", "1.0.0"));
        Package("Shared", "3.0.0", ("Future", "1.0.0"));
        Package("Old", "1.0.0");
        Package("New", "1.0.0");
        var resolver = new PackageResolver(_root);
        PackageRef[] roots = [new("A", "1.0.0", 1), new("B", "1.0.0", 2)];
        var first = resolver.Resolve(roots, targetFramework: "net10.0");
        var reversed = resolver.Resolve(roots.Reverse(), targetFramework: "net10.0");
        Assert.Empty(first.Issues);
        Assert.Empty(first.Missing);
        Assert.Equal("2.0.0", Assert.Single(first.Resolved, p => p.Id == "Shared").Version);
        Assert.Contains(first.Resolved, p => p.Id == "New");
        Assert.DoesNotContain(first.Resolved, p => p.Id is "Old" or "Future");
        Assert.Equal(first.Resolved, reversed.Resolved);
        Assert.Equal(first.InputFingerprint, reversed.InputFingerprint);
    }

    [Fact]
    public void Genuinely_incompatible_diamond_omits_the_package_and_its_stale_dependency_branch()
    {
        Package("A", "1.0.0", ("Shared", "[1.0.0]"));
        Package("B", "1.0.0", ("Shared", "[2.0.0]"));
        Package("Shared", "1.0.0", ("Old", "1.0.0"));
        Package("Shared", "2.0.0");
        Package("Old", "1.0.0");
        var result = new PackageResolver(_root).Resolve([new("A", "1.0.0", 1), new("B", "1.0.0", 2)], targetFramework: "net10.0");
        Assert.Contains(result.Issues, i => i.Code == "PACKAGE_VERSION_CONFLICT");
        Assert.DoesNotContain(result.Resolved, p => p.Id is "Shared" or "Old");
    }

    [Theory]
    [InlineData("not-a-version", false)]
    [InlineData("not-a-version", true)]
    [InlineData("1.*", false)]
    [InlineData("1.*", true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public void Any_unknown_request_blocks_an_otherwise_valid_package_in_either_order(string? invalid, bool reverse)
    {
        Package("Shared", "1.0.0");
        PackageRef[] roots = [new("Shared", "1.0.0", 1), new("Shared", invalid, 2)];
        var result = new PackageResolver(_root).Resolve(reverse ? roots.Reverse() : roots, targetFramework: "net10.0");
        Assert.Empty(result.Resolved);
        Assert.Empty(result.Assemblies);
        Assert.Contains(result.Issues, i => i.Code == "UNSUPPORTED_PACKAGE_VERSION_RANGE");
    }

    [Fact]
    public void Cyclic_dependencies_with_compatible_constraints_reach_a_stable_graph()
    {
        Package("A", "1.0.0", ("B", "1.0.0"));
        Package("B", "1.0.0", ("A", "1.0.0"));
        var result = new PackageResolver(_root).Resolve([new("A", "1.0.0", 1)], targetFramework: "net10.0");
        Assert.Empty(result.Issues);
        Assert.Equal(2, result.Resolved.Count);
    }

    [Fact]
    public void Oscillating_version_dependent_constraints_fail_closed_with_no_package_assets()
    {
        Package("A", "1.0.0", ("B", "[2.0.0]"));
        Package("A", "2.0.0", ("B", "[1.0.0]"));
        Package("B", "1.0.0", ("A", "[1.0.0]"));
        Package("B", "2.0.0", ("A", "[2.0.0]"));
        var result = new PackageResolver(_root).Resolve([new("A", "1.0.0", 1), new("B", "1.0.0", 2)], targetFramework: "net10.0");
        Assert.Empty(result.Resolved);
        Assert.Empty(result.Assemblies);
        Assert.Contains(result.Issues, i => i.Code == "PACKAGE_GRAPH_NON_CONVERGENCE");
    }

    private void Package(string id, string version, params (string Id, string Range)[] dependencies)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, id.ToLowerInvariant(), version)).FullName;
        var deps = string.Concat(dependencies.Select(d => $"<dependency id=\"{d.Id}\" version=\"{d.Range}\" />"));
        File.WriteAllText(Path.Combine(dir, id + ".nuspec"), $"<package><metadata><dependencies>{deps}</dependencies></metadata></package>");
    }
}
