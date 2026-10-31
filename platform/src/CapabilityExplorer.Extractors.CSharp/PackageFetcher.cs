using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record FetchResult(int Fetched, int Present, IReadOnlyList<string> Failed);

/// <summary>
/// Downloads the packages a repository's projects reference (and their nuspec dependencies) from a NuGet v3
/// flat container into a global-packages layout, so the extractor can bind against their reference assemblies.
/// Packages are treated as data: only the nuspec and lib/ref assemblies are unpacked, nothing is executed, and
/// no MSBuild evaluation or restore of the analysed repository takes place. Private feeds are not consulted.
/// </summary>
public sealed class PackageFetcher(string packagesRoot, HttpClient http, string feed = "https://api.nuget.org/v3-flatcontainer/")
{
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private const int MaxPackages = 600;

    private readonly Dictionary<string, string?> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _failed = [];
    private int _fetched;
    private int _present;

    public async Task<FetchResult> FetchAsync(IEnumerable<PackageRef> roots, CancellationToken cancellationToken = default)
    {
        var queue = new Queue<(string Id, string? Range, int Depth)>(roots.Select(r => (r.Id, r.Version, 0)));
        while (queue.Count > 0 && _versions.Count < MaxPackages)
        {
            var (id, range, depth) = queue.Dequeue();
            // Nearest wins, roughly like NuGet: the first version seen for an id is the one used.
            if (_versions.ContainsKey(id) || depth > 8)
            {
                continue;
            }

            var version = await ResolveVersionAsync(id, range, cancellationToken);
            _versions[id] = version;
            if (version is null)
            {
                _failed.Add($"{id} {range ?? "(no version)"}: no matching version on the feed");
                continue;
            }

            var dir = Path.Combine(packagesRoot, id.ToLowerInvariant(), version.ToLowerInvariant());
            if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir, "*.nuspec").Any())
            {
                if (!await DownloadAsync(id, version, dir, cancellationToken))
                {
                    continue;
                }

                _fetched++;
            }
            else
            {
                _present++;
            }

            foreach (var (depId, depRange) in Dependencies(dir))
            {
                queue.Enqueue((depId, depRange, depth + 1));
            }
        }

        return new FetchResult(_fetched, _present, _failed);
    }

    /// <summary>Exact versions are used as written; ranges take their lower bound; floating or missing versions the newest stable.</summary>
    private async Task<string?> ResolveVersionAsync(string id, string? range, CancellationToken cancellationToken)
    {
        if (range is not null && range.Contains('$', StringComparison.Ordinal))
        {
            return null; // an MSBuild property we do not evaluate
        }

        var min = range?.Trim().Trim('[', '(', ']', ')', ' ').Split(',')[0].Trim();
        if (!string.IsNullOrEmpty(min) && !min.Contains('*', StringComparison.Ordinal))
        {
            return min;
        }

        try
        {
            using var response = await http.GetAsync(new Uri($"{feed}{id.ToLowerInvariant()}/index.json"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var versions = doc.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).ToList();
            var prefix = min?.TrimEnd('*') ?? "";
            return versions.LastOrDefault(v => !v.Contains('-', StringComparison.Ordinal) && v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ?? versions.LastOrDefault();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    private async Task<bool> DownloadAsync(string id, string version, string dir, CancellationToken cancellationToken)
    {
        var lower = id.ToLowerInvariant();
        var url = new Uri($"{feed}{lower}/{version.ToLowerInvariant()}/{lower}.{version.ToLowerInvariant()}.nupkg");
        var staging = dir + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _failed.Add($"{id} {version}: not on the feed (private feed?)");
                return false;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxPackageBytes)
            {
                _failed.Add($"{id} {version}: larger than {MaxPackageBytes / 1024 / 1024} MB, skipped");
                return false;
            }

            using var buffer = new MemoryStream();
            await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    if (buffer.Length + read > MaxPackageBytes)
                    {
                        _failed.Add($"{id} {version}: larger than {MaxPackageBytes / 1024 / 1024} MB, skipped");
                        return false;
                    }

                    buffer.Write(chunk, 0, read);
                }
            }

            buffer.Position = 0;
            Directory.CreateDirectory(staging);
            var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    var name = Uri.UnescapeDataString(entry.FullName).Replace('\\', '/');
                    var keep = (!name.Contains('/', StringComparison.Ordinal) && name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                        || ((name.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ref/", StringComparison.OrdinalIgnoreCase))
                            && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
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
            _failed.Add($"{id} {version}: {e.Message}");
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

    private static List<(string Id, string? Range)> Dependencies(string dir)
    {
        if (!Directory.Exists(dir) || Directory.EnumerateFiles(dir, "*.nuspec").FirstOrDefault() is not { } nuspec)
        {
            return [];
        }

        var doc = XDocument.Load(nuspec);
        var groups = doc.Descendants().Where(e => e.Name.LocalName == "group").ToList();
        var group = PackageResolver.Frameworks.Select(f => groups.FirstOrDefault(g => PackageResolver.Normalise((string?)g.Attribute("targetFramework")) == f)).FirstOrDefault(g => g is not null);
        var deps = group?.Elements() ?? doc.Descendants().Where(e => e.Name.LocalName == "dependencies").Elements();
        return deps.Where(e => e.Name.LocalName == "dependency" && (string?)e.Attribute("id") is not null)
            .Select(e => ((string)e.Attribute("id")!, (string?)e.Attribute("version")))
            .ToList();
    }
}
