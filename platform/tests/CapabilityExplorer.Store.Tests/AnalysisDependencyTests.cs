extern alias analysiscli;
using analysiscli::CapabilityExplorer.Cli;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisDependencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath().Replace("/var/","/private/var/",StringComparison.Ordinal),"ce-dependency-" + Guid.NewGuid().ToString("N"));
    public AnalysisDependencyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Installing_dependencies_preserves_export_identity_and_binds_dependency_bytes()
    {
        Write("package-lock.json","locked source");
        var source = AnalysisPackagePreparation.SourceIdentity(_root);
        Write("node_modules/package/bin.js","first");
        Directory.CreateDirectory(Path.Combine(_root,"node_modules/.bin"));
        File.CreateSymbolicLink(Path.Combine(_root,"node_modules/.bin/tool"),"../package/bin.js");
        Assert.Equal(source,AnalysisPackagePreparation.SourceIdentity(_root));
        var first = AnalysisPackagePreparation.DependencyIdentity(_root);
        Write("node_modules/package/bin.js","second");
        Assert.NotEqual(first,AnalysisPackagePreparation.DependencyIdentity(_root));
        Assert.Equal(source,AnalysisPackagePreparation.SourceIdentity(_root));
    }

    [Fact]
    public void Normal_workspace_and_bin_links_resolve_within_the_export()
    {
        Write("packages/workspace/bin.js","source workspace");
        Directory.CreateDirectory(Path.Combine(_root,"node_modules/.bin"));
        Directory.CreateSymbolicLink(Path.Combine(_root,"node_modules/workspace"),"../packages/workspace");
        File.CreateSymbolicLink(Path.Combine(_root,"node_modules/.bin/workspace"),"../workspace/bin.js");
        var identity = AnalysisPackagePreparation.DependencyIdentity(_root);
        Assert.Equal(64,identity.Length);
    }

    [Theory]
    [InlineData("../../outside")]
    [InlineData("../missing")]
    public void Escaping_or_dangling_dependency_links_are_refused(string target)
    {
        Directory.CreateDirectory(Path.Combine(_root,"node_modules"));
        File.CreateSymbolicLink(Path.Combine(_root,"node_modules/link"),target);
        Assert.ThrowsAny<IOException>(() => AnalysisPackagePreparation.DependencyIdentity(_root));
    }

    [Fact]
    public void Absolute_dependency_links_are_refused_even_when_the_target_is_inside()
    {
        Write("target","data");
        Directory.CreateDirectory(Path.Combine(_root,"node_modules"));
        File.CreateSymbolicLink(Path.Combine(_root,"node_modules/link"),Path.Combine(_root,"target"));
        Assert.ThrowsAny<IOException>(() => AnalysisPackagePreparation.DependencyIdentity(_root));
    }

    private void Write(string relative,string contents)
    {
        var path = Path.Combine(_root,relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,contents);
    }
    public void Dispose() => Directory.Delete(_root,true);
}
