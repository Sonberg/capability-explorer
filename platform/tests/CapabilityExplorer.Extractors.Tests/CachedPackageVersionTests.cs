using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class CachedPackageVersionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ce-cached-version-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("2.0.0", "1.0.0", null)]
    [InlineData("(1.0.0,)", "1.0.0;1.1.0", "1.1.0")]
    [InlineData("[1.0.0,2.0.0)", "2.0.0", null)]
    [InlineData("(,2.0.0)", "1.0.0;1.5.0;2.0.0", "1.0.0")]
    [InlineData("[1.0.0]", "1.1.0", null)]
    [InlineData("[1.0.0]", "1.0.0;1.1.0", "1.0.0")]
    [InlineData("[1.0.0,2.0.0]", "2.0.0", "2.0.0")]
    [InlineData("1.0.0", "1.1.0;1.2.0", "1.1.0")]
    [InlineData("not-a-version", "1.0.0", null)]
    [InlineData("1.*", "1.0.0;1.9.0", null)]
    [InlineData("$(PackageVersion)", "1.0.0", null)]
    [InlineData("[1.0.0,0.9.0]", "1.0.0", null)]
    [InlineData("1.0.0", "2.0.0-beta.1", null)]
    [InlineData("1.0.0", "1.1.0-beta.1;1.1.0", "1.1.0")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10;1.0.0-beta.3;1.0.0", "1.0.0-beta.3")]
    [InlineData("[1.0.0-beta.2]", "1.0.0-beta.1;1.0.0-beta.2;1.0.0", "1.0.0-beta.2")]
    public void Cached_versions_must_satisfy_the_complete_supported_request(string request, string available, string? expected)
    {
        foreach (var version in available.Split(';')) Directory.CreateDirectory(Path.Combine(_root, "example.package", version));
        var result = new PackageResolver(_root).Resolve([new("Example.Package", request, 1)], targetFramework: "net10.0");
        if (expected is null)
        {
            Assert.Empty(result.Resolved);
            Assert.Single(result.Missing);
            Assert.Empty(result.Assemblies);
        }
        else
        {
            Assert.Empty(result.Missing);
            Assert.Equal(expected, Assert.Single(result.Resolved).Version);
        }
    }

    [Fact]
    public void Repeated_incompatible_requests_omit_the_chosen_package_instead_of_ignoring_the_later_bound()
    {
        Directory.CreateDirectory(Path.Combine(_root, "example.package", "1.0.0"));
        Directory.CreateDirectory(Path.Combine(_root, "example.package", "2.0.0"));
        var result = new PackageResolver(_root).Resolve([new("Example.Package", "[1.0.0]", 1), new("Example.Package", "2.0.0", 2)], targetFramework: "net10.0");
        Assert.Empty(result.Resolved);
        Assert.Empty(result.Assemblies);
        Assert.Contains(result.Issues, i => i.Code == "PACKAGE_VERSION_CONFLICT");
    }

    [Theory]
    [InlineData("[2.0.0]", "1.0.0")]
    [InlineData("[1.0.0,2.0.0)", "2.0.0")]
    [InlineData("1.*", "2.0.0")]
    [InlineData("not-a-version", "1.0.0")]
    [InlineData("1.0.0", "2.0.0-beta.1")]
    public void Incompatible_locked_requests_fail_instead_of_admitting_stale_versions(string request, string pinned)
    {
        Directory.CreateDirectory(Path.Combine(_root, "example.package", pinned));
        var error = Assert.Throws<InvalidOperationException>(() => new PackageResolver(_root).Resolve(
            [new("Example.Package", request, 1)], [new("Example.Package", pinned)]));
        Assert.Contains("Example.Package", error.Message);
        Assert.Contains(request, error.Message);
    }

    [Theory]
    [InlineData("[1.0.0]", "1.0.0")]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("[1.0.0,2.0.0)", "1.5.0")]
    [InlineData("1.*", "1.5.0")]
    [InlineData("1.0.0-*", "1.0.0-beta.1")]
    public void Compatible_locked_requests_preserve_the_exact_pin(string request, string pinned)
    {
        Directory.CreateDirectory(Path.Combine(_root, "example.package", pinned));
        Directory.CreateDirectory(Path.Combine(_root, "example.package", "9.0.0"));
        var result = new PackageResolver(_root).Resolve(
            [new("Example.Package", request, 1)], [new("Example.Package", pinned)]);
        Assert.Equal(pinned, Assert.Single(result.Resolved).Version);
        Assert.Empty(result.Missing);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("[2.0.0]", false)]
    [InlineData("[1.0.0,2.0.0)", true)]
    public void Locked_transitive_requests_are_checked_without_selecting_another_cached_version(string request, bool compatible)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "root", "1.0.0")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "child", "1.0.0"));
        Directory.CreateDirectory(Path.Combine(_root, "child", "2.0.0"));
        File.WriteAllText(Path.Combine(root, "root.nuspec"),
            $"<package><metadata><dependencies><dependency id=\"Child\" version=\"{request}\" /></dependencies></metadata></package>");
        PackageResolution Resolve() => new PackageResolver(_root).Resolve(
            [new("Root", "1.0.0", 1)], [new("Root", "1.0.0"), new("Child", "1.0.0")]);
        if (compatible)
        {
            var result = Resolve();
            Assert.Equal(new[] { "Child", "Root" }, result.Resolved.Select(p => p.Id));
            Assert.All(result.Resolved, p => Assert.Equal("1.0.0", p.Version));
            Assert.Empty(result.Issues);
        }
        else
        {
            var error = Assert.Throws<InvalidOperationException>(Resolve);
            Assert.Contains("Child", error.Message);
            Assert.Contains(request, error.Message);
        }
    }
}
