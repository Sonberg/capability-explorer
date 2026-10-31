using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CapabilityExplorer.Extractors.Tests;

/// <summary>An in-memory NuGet v3 feed (service index + flat container) that records what it was asked.</summary>
public sealed class FakeFeed : HttpMessageHandler
{
    private readonly Dictionary<(string Id, string Version), byte[]> _packages = new();

    public FakeFeed(string host = "feed.test", string? requiredBasic = null, string? redirectNupkgsTo = null)
    {
        Host = host;
        RequiredBasic = requiredBasic;
        RedirectNupkgsTo = redirectNupkgsTo;
    }

    public string Host { get; }

    public string? RequiredBasic { get; }

    public string? RedirectNupkgsTo { get; }

    public string IndexUrl => $"https://{Host}/v3/index.json";

    public List<(Uri Url, string? Authorization)> Requests { get; } = [];

    /// <summary>Adds a package whose single assembly (lib/net8.0) is compiled from <paramref name="source"/>.</summary>
    public FakeFeed Add(string id, string version, string source)
    {
        _packages[(id.ToLowerInvariant(), version)] = Pack(id, version, source);
        return this;
    }

    public static byte[] Pack(string id, string version, string source)
    {
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(id, [CSharpSyntaxTree.ParseText(source)], refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var dll = new MemoryStream();
        var emit = compilation.Emit(dll);
        if (!emit.Success)
        {
            throw new InvalidOperationException(string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        using var zipped = new MemoryStream();
        using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspec = zip.CreateEntry($"{id}.nuspec");
            using (var w = new StreamWriter(nuspec.Open()))
            {
                w.Write($"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>");
            }

            var entry = zip.CreateEntry($"lib/net8.0/{id}.dll");
            using var s = entry.Open();
            s.Write(dll.ToArray());
        }

        return zipped.ToArray();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        var auth = request.Headers.Authorization is { } a ? $"{a.Scheme} {a.Parameter}" : null;
        lock (Requests)
        {
            Requests.Add((url, auth));
        }

        HttpResponseMessage Respond(HttpStatusCode code, byte[]? body = null) => new(code) { Content = new ByteArrayContent(body ?? []) };

        if (url.Host == Host)
        {
            if (RequiredBasic is not null && auth != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(RequiredBasic)))
            {
                return Task.FromResult(Respond(HttpStatusCode.Unauthorized));
            }

            if (url.AbsolutePath == "/v3/index.json")
            {
                return Task.FromResult(Respond(HttpStatusCode.OK, Encoding.UTF8.GetBytes($$"""{"version":"3.0.0","resources":[{"@id":"https://{{Host}}/flat/","@type":"PackageBaseAddress/3.0.0"}]}""")));
            }

            var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["flat", var id, "index.json"])
            {
                var versions = _packages.Keys.Where(k => k.Id == id).Select(k => k.Version).ToList();
                versions.Sort(StringComparer.Ordinal);
                return Task.FromResult(versions.Count == 0
                    ? Respond(HttpStatusCode.NotFound)
                    : Respond(HttpStatusCode.OK, Encoding.UTF8.GetBytes($$"""{"versions":[{{string.Join(",", versions.Select(v => $"\"{v}\""))}}]}""")));
            }

            if (parts is ["flat", var pid, var version, _])
            {
                if (!_packages.ContainsKey((pid, version)))
                {
                    return Task.FromResult(Respond(HttpStatusCode.NotFound));
                }

                if (RedirectNupkgsTo is { } cdn)
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redirect.Headers.Location = new Uri($"https://{cdn}/blob/{pid}/{version}");
                    return Task.FromResult(redirect);
                }

                return Task.FromResult(Respond(HttpStatusCode.OK, _packages[(pid, version)]));
            }
        }
        else if (url.Host == RedirectNupkgsTo && url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["blob", var bid, var bver] && _packages.TryGetValue((bid, bver), out var bytes))
        {
            return Task.FromResult(Respond(HttpStatusCode.OK, bytes));
        }

        return Task.FromResult(Respond(HttpStatusCode.NotFound));
    }
}

/// <summary>Routes requests to the fake feed that owns the host.</summary>
public sealed class FeedRouter(params FakeFeed[] feeds) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var feed = feeds.FirstOrDefault(f => f.Host == request.RequestUri!.Host || f.RedirectNupkgsTo == request.RequestUri.Host);
        return feed is null
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            : new HttpMessageInvoker(feed, disposeHandler: false).SendAsync(request, cancellationToken);
    }
}
