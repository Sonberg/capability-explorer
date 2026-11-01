using System.IO.Compression;
using System.Net;
using System.Text;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class PackageTargetFetchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ce-target-fetch-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("net8.0", "Old")]
    [InlineData("net10.0", "New")]
    [InlineData("net9.0", "Old")]
    public async Task Fetch_and_offline_resolution_use_the_same_nearest_dependency_group(string target, string expected)
    {
        using var handler = new DataFeed();
        handler.Add("Root", """
            <dependencies>
              <group targetFramework="net8.0"><dependency id="Old" version="1.0.0" /></group>
              <group targetFramework="net10.0"><dependency id="New" version="1.0.0" /></group>
            </dependencies>
            """);
        handler.Add("Old");
        handler.Add("New");
        using var http = new HttpClient(handler);
        var model = Project(target);
        var cache = Path.Combine(_root, "cache");
        var fetched = await new PackageFetcher(cache, http, [new("data", "https://feed.test/index.json", BaseAddress: "https://feed.test/flat/")]).FetchAsync([model]);
        Assert.Empty(fetched.Failed);
        Assert.Equal(new[] { expected, "Root" }.Order(StringComparer.OrdinalIgnoreCase), fetched.Lock.For("App.csproj").Select(p => p.Id));
        var resolved = new PackageResolver(cache).Resolve(model.PackageRoots(), fetched.Lock.For("App.csproj"), model.TargetFramework);
        Assert.Empty(resolved.Issues);
        Assert.Equal(fetched.Lock.For("App.csproj"), resolved.Resolved);
        Assert.DoesNotContain(handler.Requests, path => path.Contains(expected == "Old" ? "/new/" : "/old/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_compile_markers_survive_the_package_data_boundary()
    {
        using var handler = new DataFeed();
        handler.Add("Root", files: ["ref/net8.0/_._", "lib/net8.0/Runtime.dll"]);
        using var http = new HttpClient(handler);
        var cache = Path.Combine(_root, "cache");
        var model = Project("net8.0");
        var fetched = await new PackageFetcher(cache, http, [new("data", "https://feed.test/index.json", BaseAddress: "https://feed.test/flat/")]).FetchAsync([model]);
        Assert.Empty(fetched.Failed);
        Assert.True(File.Exists(Path.Combine(cache, "root", "1.0.0", "ref", "net8.0", "_._")));
        var resolved = new PackageResolver(cache).Resolve(model.PackageRoots(), fetched.Lock.For("App.csproj"), "net8.0");
        Assert.Empty(resolved.Assemblies);
        Assert.Empty(resolved.Issues);
    }

    [Fact]
    public async Task Long_nuspec_framework_names_use_the_same_compatibility_rules()
    {
        using var handler = new DataFeed();
        handler.Add("Root", "<dependencies><group targetFramework=\".NETStandard2.0\"><dependency id=\"Portable\" version=\"1.0.0\" /></group></dependencies>");
        handler.Add("Portable");
        using var http = new HttpClient(handler);
        var model = Project("net8.0");
        var fetched = await new PackageFetcher(Path.Combine(_root, "cache"), http, [new("data", "https://feed.test/index.json", BaseAddress: "https://feed.test/flat/")]).FetchAsync([model]);
        Assert.Empty(fetched.Failed);
        Assert.Contains(fetched.Lock.For("App.csproj"), p => p.Id == "Portable");
    }

    [Fact]
    public async Task A_dependency_only_package_with_no_compatible_group_is_not_silently_a_meta_package()
    {
        using var handler = new DataFeed();
        handler.Add("Root", "<dependencies><group targetFramework=\"net11.0\"><dependency id=\"Future\" version=\"1.0.0\" /></group></dependencies>");
        using var http = new HttpClient(handler);
        var cache = Path.Combine(_root, "cache");
        var model = Project("net8.0");
        var fetched = await new PackageFetcher(cache, http, [new("data", "https://feed.test/index.json", BaseAddress: "https://feed.test/flat/")]).FetchAsync([model]);
        Assert.Contains(fetched.Failed, f => f.Contains("no compatible dependency group", StringComparison.Ordinal));
        var resolved = new PackageResolver(cache).Resolve(model.PackageRoots(), fetched.Lock.For("App.csproj"), model.TargetFramework);
        Assert.Contains(resolved.Issues, i => i.Code == "PACKAGE_NO_COMPATIBLE_DEPENDENCIES");
    }

    [Fact]
    public async Task Target_specific_lock_does_not_make_another_targets_transitive_package_visible()
    {
        using var handler = new DataFeed();
        handler.Add("Root", "<dependencies><group targetFramework=\"net8.0\"><dependency id=\"Old\" version=\"1.0.0\" /></group><group targetFramework=\"net10.0\"><dependency id=\"New\" version=\"1.0.0\" /></group></dependencies>");
        handler.Add("New");
        using var http = new HttpClient(handler);
        var cache = Path.Combine(_root, "cache");
        var modern = Project("net10.0");
        var fetched = await new PackageFetcher(cache, http, [new("data", "https://feed.test/index.json", BaseAddress: "https://feed.test/flat/")]).FetchAsync([modern]);
        var old = Project("net8.0");
        var result = new PackageResolver(cache).Resolve(old.PackageRoots(), fetched.Lock.For("App.csproj"), old.TargetFramework);
        Assert.Equal("Root", Assert.Single(result.Resolved).Id);
        Assert.Contains(result.Issues, i => i.Code == "UNRESOLVED_TRANSITIVE_PACKAGE" && i.Message.Contains("Old", StringComparison.Ordinal));
    }

    private ProjectModel Project(string target)
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{target}</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Root\" Version=\"1.0.0\" /></ItemGroup></Project>");
        return Assert.Single(ProjectModel.Discover(_root));
    }

    private sealed class DataFeed : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _packages = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];

        public void Add(string id, string dependencies = "", string[]? files = null)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry(id + ".nuspec").Open()))
                {
                    writer.Write($"<package><metadata><id>{id}</id><version>1.0.0</version>{dependencies}</metadata></package>");
                }
                foreach (var file in files ?? []) zip.CreateEntry(file);
            }
            _packages[id.ToLowerInvariant()] = buffer.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var found = parts.Length == 4 && _packages.TryGetValue(parts[1], out var bytes);
            return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new ByteArrayContent(found ? _packages[parts[1]] : Encoding.UTF8.GetBytes("missing")),
            });
        }
    }
}
