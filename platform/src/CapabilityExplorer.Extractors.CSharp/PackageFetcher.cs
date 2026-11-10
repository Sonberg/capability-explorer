using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using NuGet.Versioning;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record FetchResult(int Fetched, int Present, IReadOnlyList<string> Failed, PackageLock Lock);

/// <summary>
/// Resolves one exact version set per project and downloads those packages (and their nuspec dependencies) from
/// NuGet v3 flat-container feeds into a global-packages layout, so the extractor can bind against their reference
/// assemblies. Packages are treated as data: only the nuspec and lib/ref assemblies are unpacked, nothing is
/// executed, and no MSBuild evaluation or restore of the analysed repository takes place.
/// The result is a <see cref="PackageLock"/>; it depends on the declared versions and the feeds, never on what the
/// packages folder already held. Feeds are tried in order. Credentials are sent only to their own feed's host.
/// </summary>
public sealed class PackageFetcher(string packagesRoot, HttpClient http, IReadOnlyList<PackageFeed>? feeds = null, PackageFeedPolicy? policy = null)
{
    private readonly PackageFeedPolicy _policy = policy ?? new PackageFeedPolicy();
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private const int MaxPackages = 600;
    private const int MaxArchiveEntries = 10000;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const long MaxMetadataBytes = 4L * 1024 * 1024;

    private readonly IReadOnlyList<PackageFeed> _feeds = feeds is { Count: > 0 } ? feeds : [PackageFeed.NuGetOrg];
    private readonly Dictionary<string, string?> _bases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _floating = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _failed = [];
    private int _fetched;
    private int _present;

    public async Task<FetchResult> FetchAsync(IEnumerable<(string Project, IReadOnlyList<PackageRef> Roots)> projects, CancellationToken cancellationToken = default)
        => await FetchAsync(projects.Select(p => (p.Project, p.Roots, Target: (string?)"net10.0")), cancellationToken);

    /// <summary>Target-aware production entrypoint. No MSBuild evaluation or analysed-code restore occurs.</summary>
    public Task<FetchResult> FetchAsync(IEnumerable<ProjectModel> projects, CancellationToken cancellationToken = default) =>
        FetchAsync(projects.Select(p => (p.RelativePath, p.PackageRoots(), p.TargetFramework)), cancellationToken);

    private async Task<FetchResult> FetchAsync(IEnumerable<(string Project, IReadOnlyList<PackageRef> Roots, string? Target)> projects, CancellationToken cancellationToken)
    {
        var result = new PackageLock();
        foreach (var (project, roots, target) in projects.OrderBy(p => p.Project, StringComparer.Ordinal))
        {
            if (FrameworkSelection.Parse(target) is null)
            {
                Fail($"{project}: a single supported literal target framework is required to select package dependencies");
                result.Set(project, []);
                continue;
            }

            result.Set(project, await ResolveProjectAsync(roots, target, cancellationToken));
        }

        return new FetchResult(_fetched, _present, _failed, result);
    }

    private async Task<List<LockedPackage>> ResolveProjectAsync(IReadOnlyList<PackageRef> roots, string? target, CancellationToken cancellationToken)
    {
        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, string? Range, int Depth)>(roots.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Select(r => (r.Id, r.Version, 0)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0 && seen.Count < MaxPackages)
        {
            var (id, range, depth) = queue.Dequeue();
            // Nearest wins, roughly like NuGet: the first version seen for an id is the one used.
            if (!seen.Add(id) || depth > 8)
            {
                continue;
            }

            if (!PackageSafety.Id(id)) { Fail("Invalid package ID; paths are prohibited"); continue; }
            var version = await ResolveVersionAsync(id, range, cancellationToken);
            if (version is null || !PackageSafety.Version(version))
            {
                Fail($"{id} {range ?? "(no version)"}: no matching version on the feeds");
                continue;
            }

            var dir = Path.Combine(packagesRoot, id.ToLowerInvariant(), version.ToLowerInvariant());
            PackageSafety.NoLinks(packagesRoot, dir);
            if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir, "*.nuspec").Any())
            {
                if (!await DownloadAsync(id, version, dir, cancellationToken))
                {
                    continue;
                }

                _fetched++;
                _done.Add($"{id}/{version}");
            }
            else if (_done.Add($"{id}/{version}"))
            {
                _present++;
            }

            PackageSafety.Tree(dir);
            chosen[id] = version;
            if (FrameworkSelection.IncompatibleDependencyGroups(dir, target))
            {
                Fail($"{id} {version}: no compatible dependency group for {target}");
            }

            foreach (var (depId, depRange) in Dependencies(dir, target))
            {
                queue.Enqueue((depId, depRange, depth + 1));
            }
        }

        return chosen.Select(kv => new LockedPackage(kv.Key, kv.Value)).ToList();
    }

    private void Fail(string message)
    {
        if (!_failed.Contains(message))
        {
            _failed.Add(message);
        }
    }

    /// <summary>Inclusive lower bounds are used as written; exclusive floors choose the lowest matching version above them. Other feed-index queries choose the newest match.</summary>
    private async Task<string?> ResolveVersionAsync(string id, string? range, CancellationToken cancellationToken)
    {
        if (range is not null && range.Contains('$', StringComparison.Ordinal))
        {
            return null; // an MSBuild property we do not evaluate
        }

        var min = range?.Trim().Trim('[', '(', ']', ')', ' ').Split(',')[0].Trim();
        var exclusiveFloor = range is not null && VersionRange.TryParse(range, out var constraint)
            && constraint.MinVersion is not null && !constraint.IsMinInclusive;
        if (!string.IsNullOrEmpty(min) && !min.Contains('*', StringComparison.Ordinal) && !exclusiveFloor)
        {
            return min;
        }

        var cacheKey = range is null ? $"{id}|missing" : $"{id}|range:{range}";
        if (_floating.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        foreach (var feed in _feeds)
        {
            try
            {
                if (await BaseAsync(feed, cancellationToken) is not { } baseUrl)
                {
                    continue;
                }

                using var response = await SendAsync(feed, new Uri($"{baseUrl}{id.ToLowerInvariant()}/index.json"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(await ReadMetadataAsync(response, cancellationToken));
                var versions = doc.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).ToList();
                var matching = versions.Where(v => PackageVersionSelection.AllowsLocked(range ?? "*", v))
                    .OrderBy(v => NuGetVersion.Parse(v), VersionComparer.VersionRelease)
                    .ThenBy(v => v, StringComparer.Ordinal);
                var pick = exclusiveFloor ? matching.FirstOrDefault() : matching.LastOrDefault();
                if (pick is not null)
                {
                    return _floating[cacheKey] = pick;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
            {
                // try the next feed
            }
        }

        return _floating[cacheKey] = null;
    }

    private async Task<string?> BaseAsync(PackageFeed feed, CancellationToken cancellationToken)
    {
        if (feed.BaseAddress is { } known)
        {
            if (Uri.TryCreate(known, UriKind.Absolute, out var uri) && PackageSafety.Https(uri)) return known.EndsWith('/') ? known : known + "/";
            Fail("Feed base must be HTTPS without user information or fragment");
            return null;
        }

        if (_bases.TryGetValue(feed.Url, out var cached))
        {
            return cached;
        }

        string? result = null;
        try
        {
            using var response = await SendAsync(feed, new Uri(feed.Url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await ReadMetadataAsync(response, cancellationToken));
                result = doc.RootElement.GetProperty("resources").EnumerateArray()
                    .Where(r => r.GetProperty("@type").GetString() is { } t && t.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal))
                    .Select(r => r.GetProperty("@id").GetString()).FirstOrDefault();
                if (result is not null && !result.EndsWith('/'))
                {
                    result += "/";
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            // reported as unavailable below
        }

        if (result is null)
        {
            Fail($"feed {feed.Name}: no service index (unreachable, unauthorised or not a v3 feed)");
        }

        return _bases[feed.Url] = result;
    }

    /// <summary>GET with manual redirects; the Authorization header is only ever attached for the feed's own host over https.</summary>
    private async Task<HttpResponseMessage> SendAsync(PackageFeed feed, Uri url, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var feedUri = new Uri(feed.Url);
        RequireOrigin(feedUri);
        for (var hop = 0; hop < 5; hop++)
        {
            RequireOrigin(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (feed.Credentials is { } c && url.Scheme == Uri.UriSchemeHttps && string.Equals(url.Authority, feedUri.Authority, StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{c.Username}:{c.Password}")));
            }

            var response = await http.SendAsync(request, completion, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                response.Dispose();
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (url.Scheme != Uri.UriSchemeHttps)
                {
                    throw new HttpRequestException("redirect to a non-https address refused");
                }

                continue;
            }

            return response;
        }

        throw new HttpRequestException("too many redirects");
    }

    private async Task<bool> DownloadAsync(string id, string version, string dir, CancellationToken cancellationToken)
    {
        var lower = id.ToLowerInvariant();
        var staging = dir + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            MemoryStream? buffer = null;
            foreach (var feed in _feeds)
            {
                if (await BaseAsync(feed, cancellationToken) is not { } baseUrl)
                {
                    continue;
                }

                var url = new Uri($"{baseUrl}{lower}/{version.ToLowerInvariant()}/{lower}.{version.ToLowerInvariant()}.nupkg");
                using var response = await SendAsync(feed, url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    continue; // another feed may have it
                }

                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxPackageBytes)
                {
                    Fail($"{id} {version}: larger than {MaxPackageBytes / 1024 / 1024} MB, skipped");
                    return false;
                }

                var received = new MemoryStream();
                await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
                {
                    var chunk = new byte[81920];
                    int read;
                    while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
                    {
                        if (received.Length + read > MaxPackageBytes)
                        {
                            Fail($"{id} {version}: larger than {MaxPackageBytes / 1024 / 1024} MB, skipped");
                            await received.DisposeAsync();
                            return false;
                        }

                        received.Write(chunk, 0, read);
                    }
                }

                buffer = received;
                break;
            }

            if (buffer is null)
            {
                Fail($"{id} {version}: not on any configured feed (private feed without credentials?)");
                return false;
            }

            using var owned = buffer;
            buffer.Position = 0;
            Directory.CreateDirectory(staging);
            var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > MaxArchiveEntries || zip.Entries.Sum(e => e.Length) > MaxExpandedBytes)
                    throw new InvalidDataException("Package expanded size or entry count exceeds limits");
                foreach (var entry in zip.Entries)
                {
                    var name = Uri.UnescapeDataString(entry.FullName).Replace('\\', '/');
                    var keep = (!name.Contains('/', StringComparison.Ordinal) && name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                        || ((name.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ref/", StringComparison.OrdinalIgnoreCase))
                            && (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name) == "_._"));
                    if (!keep)
                    {
                        continue;
                    }

                    var target = Path.GetFullPath(Path.Combine(staging, name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) ? $"{lower}.nuspec" : name));
                    if (!target.StartsWith(root, StringComparison.Ordinal))
                    {
                        continue; // path traversal in the archive
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            Directory.Move(staging, dir);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            Fail($"{id} {version}: package download or archive rejected ({e.GetType().Name})");
            return false;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private void RequireOrigin(Uri uri)
    {
        if (!_policy.Allows(uri)) Fail("Unauthorized package feed origin; configure CE_NUGET_ALLOWED_ORIGINS explicitly");
        _policy.Require(uri);
    }

    private static async Task<string> ReadMetadataAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > MaxMetadataBytes) throw new HttpRequestException("Feed metadata exceeds limit");
            buffer.Write(chunk, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static List<(string Id, string? Range)> Dependencies(string dir, string? target)
    {
        if (!Directory.Exists(dir) || Directory.EnumerateFiles(dir, "*.nuspec").FirstOrDefault() is not { } nuspec)
        {
            return [];
        }

        var deps = FrameworkSelection.DependencyElements(dir, target);
        return deps.Where(e => e.Name.LocalName == "dependency" && (string?)e.Attribute("id") is not null)
            .Select(e => ((string)e.Attribute("id")!, (string?)e.Attribute("version")))
            .ToList();
    }
}
