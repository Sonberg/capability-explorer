using System.Text.Json;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

/// <summary>Issue #6: absolute URLs in HTTP routes (gap 6) and package assets for older target frameworks (gap 3).</summary>
public sealed class MappingGapTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Directory.CreateTempSubdirectory("ce-gaps-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ------------------------------------------------------------ gap 6: absolute URLs

    [Theory]
    [InlineData("https://graph.facebook.com/v1/{}/events", "graph.facebook.com", "/v1/{}/events")]
    [InlineData("HTTP://User:pw@Api.Example.com:8080/a/b?x=1#frag", "api.example.com:8080", "/a/b")]
    [InlineData("https://host.test", "host.test", "/")]
    [InlineData("https://host.test?x=1", "host.test", "/")]
    [InlineData("/orders/{id}", null, "/orders/{}")]
    [InlineData("orders/", null, "/orders")]
    public void Absolute_urls_split_into_host_and_path(string url, string? host, string route)
    {
        Assert.Equal(host, Rules.SplitAbsoluteUrl(url).Host);
        Assert.Equal(route, Rules.NormaliseRoute(url));
        Assert.DoesNotContain("https:", Rules.NormaliseRoute(url), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dynamic_authority_gives_no_host_but_still_a_clean_path()
    {
        Assert.Null(Rules.SplitAbsoluteUrl("https://{}.example.com/x").Host);
        Assert.Equal("/x", Rules.NormaliseRoute("https://{}.example.com/x"));
    }

    [Fact]
    public void HttpClient_call_with_absolute_url_targets_the_host_not_a_path()
    {
        var records = ExtractSource("""
            using System.Net.Http;
            namespace Synthetic;
            public class Tracker
            {
                private readonly HttpClient _http = new();
                public async Task Purchase(string pixel, string version)
                {
                    await _http.PostAsync($"https://graph.facebook.com/{version}/{pixel}/events?access_token=x", null);
                }
            }
            """);
        var request = Assert.Single(records, r => r.GetProperty("kind").GetString() == "edge" && r.GetProperty("relation").GetString() == "REQUESTS");
        Assert.Equal("http|@unbound:synthetic/Synthetic:host=graph.facebook.com|POST /{}/{}/events", request.GetProperty("to").GetString());
        Assert.Equal("/{}/{}/events", request.GetProperty("props").GetProperty("normalizedRoute").GetString());
    }

    // ------------------------------------------------------------ gap 3: older frameworks

    [Fact]
    public void Frameworks_cover_net5_netcoreapp_and_netstandard1()
    {
        foreach (var tfm in new[] { "net5.0", "netcoreapp3.0", "netcoreapp2.1", "netstandard1.6", "netstandard1.1", "netstandard1.0" })
        {
            Assert.Contains(tfm, PackageResolver.Frameworks);
        }

        // Newer assets still win over older ones.
        Assert.True(Array.IndexOf(PackageResolver.Frameworks, "net8.0") < Array.IndexOf(PackageResolver.Frameworks, "net5.0"));
        Assert.True(Array.IndexOf(PackageResolver.Frameworks, "netstandard2.0") < Array.IndexOf(PackageResolver.Frameworks, "netstandard1.1"));
    }

    [Theory]
    [InlineData(".NETCoreApp3.1", "netcoreapp3.1")]
    [InlineData(".NETStandard1.1", "netstandard1.1")]
    [InlineData("net5.0", "net5.0")]
    [InlineData(".NETFramework4.8", ".netframework4.8")]
    public void Nuspec_framework_names_normalise(string nuspec, string expected) =>
        Assert.Equal(expected, PackageResolver.Normalise(nuspec));

    [Fact]
    public void Package_with_only_a_net5_asset_contributes_it_and_its_net5_dependency_group()
    {
        var packages = Path.Combine(_root, "packages");
        Package(packages, "easy.core", "1.0.0", ["lib/net5.0/Easy.Core.dll"], dependencies: ("net5.0", "easy.abstractions", "1.0.0"));
        Package(packages, "easy.abstractions", "1.0.0", ["lib/netstandard2.0/Easy.Abstractions.dll"]);

        var result = new PackageResolver(packages).Resolve([new PackageRef("Easy.Core", "1.0.0", 1)]);

        Assert.Equal(["Easy.Abstractions.dll", "Easy.Core.dll"], result.Assemblies.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["easy.abstractions", "easy.core"], result.Resolved.Select(r => r.Id.ToLowerInvariant()).Order(StringComparer.Ordinal));
        Assert.Empty(result.Unselected);

        var locked = new PackageResolver(packages).Resolve([], [new LockedPackage("easy.core", "1.0.0"), new LockedPackage("easy.abstractions", "1.0.0")]);
        Assert.Equal(2, locked.Assemblies.Count);
    }

    [Fact]
    public void Package_with_only_netstandard1_asset_is_used()
    {
        var packages = Path.Combine(_root, "packages");
        Package(packages, "xunit.extensibility.core", "2.9.0", ["lib/netstandard1.1/xunit.core.dll"]);

        var result = new PackageResolver(packages).Resolve([], [new LockedPackage("xunit.extensibility.core", "2.9.0")]);

        Assert.Equal(["xunit.core.dll"], result.Assemblies.Select(Path.GetFileName));
    }

    [Fact]
    public void Package_with_only_unsupported_assets_is_reported_not_silent()
    {
        var packages = Path.Combine(_root, "packages");
        Package(packages, "legacy.lib", "1.0.0", ["lib/net45/Legacy.dll", "lib/net461/Legacy.dll"]);
        Package(packages, "meta.only", "1.0.0", []);

        var result = new PackageResolver(packages).Resolve([], [new LockedPackage("legacy.lib", "1.0.0"), new LockedPackage("meta.only", "1.0.0")]);

        Assert.Empty(result.Assemblies);
        var unselected = Assert.Single(result.Unselected);
        Assert.Equal(("legacy.lib", "1.0.0"), (unselected.Id, unselected.Version));
        Assert.Equal(["lib/net45", "lib/net461"], unselected.Folders);
        Assert.Equal(2, result.Resolved.Count); // still resolved: this is observability, not a failure
    }

    [Fact]
    public void Unselected_package_surfaces_as_an_info_diagnostic_without_affecting_coverage()
    {
        var packages = Path.Combine(_root, "packages");
        Package(packages, "legacy.lib", "1.0.0", ["lib/net45/Legacy.dll"]);
        var source = NewSource("""<ItemGroup><PackageReference Include="Legacy.Lib" Version="1.0.0" /></ItemGroup>""");
        File.WriteAllText(Path.Combine(source, "A.cs"), "namespace Synthetic; public class A { }\n");

        var packageLock = new PackageLock();
        packageLock.Set("Synthetic.csproj", [new LockedPackage("legacy.lib", "1.0.0")]);
        var result = Assert.Single(new CSharpExtractor(new ExtractorOptions(
            source, "synthetic", Sha, PackagesRoot: packages, ReferencePacks: Packs(), Lock: packageLock)).Extract());
        var diagnostic = Assert.Single(Records(result.Jsonl), r => r.GetProperty("kind").GetString() == "diagnostic" && r.GetProperty("code").GetString() == "PACKAGE_ASSEMBLIES_NOT_SELECTED");
        Assert.Equal("info", diagnostic.GetProperty("severity").GetString());
        Assert.Contains("lib/net45", diagnostic.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(diagnostic.TryGetProperty("affectsCoverage", out var affects) && affects.GetBoolean());
    }

    // ------------------------------------------------------------ helpers

    private static void Package(string root, string id, string version, string[] files, (string Tfm, string Id, string Version)? dependencies = null)
    {
        var dir = Path.Combine(root, id, version);
        foreach (var f in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, f))!);
            File.WriteAllBytes(Path.Combine(dir, f), [0]);
        }

        Directory.CreateDirectory(dir);
        var deps = dependencies is { } d
            ? $"""<dependencies><group targetFramework="{d.Tfm}"><dependency id="{d.Id}" version="{d.Version}" /></group></dependencies>"""
            : "";
        File.WriteAllText(Path.Combine(dir, $"{id}.nuspec"), $"""<package><metadata><id>{id}</id><version>{version}</version>{deps}</metadata></package>""");
    }

    private string NewSource(string projectItems = "")
    {
        var source = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Synthetic.csproj"),
            $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>{projectItems}</Project>""");
        return source;
    }

    private static string Packs() => Path.GetFullPath(Path.Combine(
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));

    private static JsonElement[] Records(string jsonl) => jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l =>
    {
        using var doc = JsonDocument.Parse(l);
        return doc.RootElement.Clone();
    }).ToArray();

    private JsonElement[] ExtractSource(string code)
    {
        var source = NewSource();
        File.WriteAllText(Path.Combine(source, "Tracker.cs"), code);
        var result = Assert.Single(new CSharpExtractor(new ExtractorOptions(source, "synthetic", Sha, PackagesRoot: null, ReferencePacks: Packs())).Extract());
        return Records(result.Jsonl);
    }
}
