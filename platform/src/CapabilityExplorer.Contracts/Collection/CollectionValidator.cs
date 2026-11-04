using System.Security.Cryptography;
using System.Text.Json;

namespace CapabilityExplorer.Contracts.Collection;

internal static class CollectionValidator
{
    public static async Task<CollectionOperationResult> ValidateAsync(
        string bundleDirectory,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(bundleDirectory) || !Directory.Exists(bundleDirectory) ||
                !CollectionExporter.HasSafeExistingPath(Path.GetFullPath(bundleDirectory), includeLeaf: true))
            {
                return Failure("bundle directory is missing or uses a symbolic link");
            }

            var root = Path.GetFullPath(bundleDirectory);
            var descriptorPath = Path.Combine(root, "collection.json");
            var attemptPath = Path.Combine(root, "attempt.json");
            if (!File.Exists(descriptorPath) || !File.Exists(attemptPath) ||
                !CollectionExporter.HasSafeExistingPath(descriptorPath, includeLeaf: true) ||
                !CollectionExporter.HasSafeExistingPath(attemptPath, includeLeaf: true))
            {
                return Failure("bundle descriptor or attempt metadata is missing or unsafe");
            }

            var envelope = await ReadJsonAsync<CollectionEnvelope>(descriptorPath, maxBytes: 1_048_576,
                cancellationToken).ConfigureAwait(false);
            var attempt = await ReadJsonAsync<CollectionAttempt>(attemptPath, maxBytes: 16_384,
                cancellationToken).ConfigureAwait(false);
            if (envelope is null || attempt is null || envelope.Content is null)
            {
                return Failure("bundle metadata is invalid");
            }

            if (envelope.Schema != CollectionRules.BundleSchema || envelope.Content.Schema != CollectionRules.BundleSchema ||
                !CollectionRules.IsSha256(envelope.ContentSha256) ||
                !Guid.TryParseExact(attempt.AttemptId, "N", out _) || attempt.ObservedAt == default)
            {
                return Failure("bundle schema, content digest, or attempt identity is invalid");
            }

            var contentDigest = CollectionCanonicalization.Digest(envelope.Content);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(envelope.ContentSha256), Convert.FromHexString(contentDigest)))
            {
                return Failure("collection content digest does not match canonical content");
            }

            ValidateContentShape(envelope.Content, problems);
            if (problems.Count > 0)
            {
                return new CollectionOperationResult(false, null, problems, false);
            }

            var repositories = envelope.Content.Repositories.ToDictionary(r => r.RepositoryId, StringComparer.Ordinal);
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            long totalBytes = 0;
            foreach (var input in envelope.Content.Inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (input.Status == "optional-failed")
                {
                    continue;
                }

                var remainingBytes = CollectionRules.MaxTotalBytes - totalBytes;
                if (remainingBytes <= 0 || input.ByteLength > remainingBytes)
                {
                    problems.Add("aggregate read budget exhausted");
                    continue;
                }

                var repo = repositories[input.RepositoryId];
                var artifactPath = ResolveArtifact(root, input.ArtifactPath!);
                if (artifactPath is null || !File.Exists(artifactPath) ||
                    !CollectionExporter.HasSafeExistingPath(artifactPath, includeLeaf: true))
                {
                    problems.Add("referenced artifact is missing or unsafe");
                    continue;
                }

                var bytes = await CollectionRules.ReadBoundedAsync(artifactPath,
                    Math.Min(CollectionRules.MaxInputBytes, remainingBytes),
                    cancellationToken).ConfigureAwait(false);
                totalBytes += bytes.LongLength;
                if (bytes.LongLength != input.ByteLength || bytes.LongLength > CollectionRules.MaxInputBytes ||
                    totalBytes > CollectionRules.MaxTotalBytes || CollectionRules.Digest(bytes) != input.Sha256)
                {
                    problems.Add("artifact bytes do not match descriptor length or SHA-256");
                    continue;
                }

                var profileProblems = CollectionRules.ValidateProfile(input.Profile, bytes, repo.RepositoryId,
                    repo.CommitSha, input.UnitPath, input.ContentProfile, repo.PermittedContentProfiles);
                problems.AddRange(profileProblems.Select(p => $"{input.Profile}: {p}"));
                referenced.Add(input.ArtifactPath!);
            }

            foreach (var file in EnumerateFiles(root))
            {
                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative is "collection.json" or "attempt.json")
                {
                    continue;
                }

                if (!referenced.Contains(relative))
                {
                    problems.Add("bundle contains an unreferenced file");
                }
            }

            return problems.Count == 0
                ? new CollectionOperationResult(true, contentDigest, [], false)
                : new CollectionOperationResult(false, null, problems.Take(32).ToArray(), false);
        }
        catch (OperationCanceledException)
        {
            return Failure("validation canceled");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                           ArgumentException or FormatException or NotSupportedException)
        {
            return Failure(exception is UnauthorizedAccessException ? "bundle access denied" : "bundle validation failed");
        }
    }

    private static void ValidateContentShape(CollectionContent content, List<string> problems)
    {
        if (!CollectionRules.IsSha256(content.RecipeSha256) || !CollectionRules.IsSha256(content.ConfigSha256) ||
            !CollectionRules.IsSha256(content.PolicySha256) || content.Repositories is null || content.Inputs is null ||
            content.Repositories.Count is < 1 or > CollectionRules.MaxRepositories ||
            content.Inputs.Count is < 1 or > CollectionRules.MaxInputs)
        {
            problems.Add("collection identity or count is outside profile bounds");
            return;
        }

        var repositories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repository in content.Repositories)
        {
            if (!CollectionRules.IsRepositoryId(repository.RepositoryId) || repository.PermittedContentProfiles is null ||
                !CollectionRules.IsId(repository.ProducerId) ||
                !CollectionRules.IsId(repository.TenantRef) || !CollectionRules.IsId(repository.RightsRef) ||
                !CollectionRules.IsCommitSha(repository.CommitSha) || !CollectionRules.IsCommitSha(repository.TreeSha) ||
                repository.PermittedContentProfiles.Any(p => !CollectionRules.IsId(p)) ||
                !repositories.Add(repository.RepositoryId))
            {
                problems.Add("repository metadata is invalid or duplicated");
            }
        }

        var uniqueInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in content.Inputs)
        {
            var key = $"{input.RepositoryId}\n{input.Profile}\n{input.UnitPath}";
            var knownInput = input.Status == "included" ? CollectionRules.IsProfile(input.Profile) : CollectionRules.IsId(input.Profile);
            if (!repositories.Contains(input.RepositoryId) || !knownInput ||
                !CollectionRules.IsLogicalPath(input.UnitPath) ||
                (input.ContentProfile is not null && !CollectionRules.IsId(input.ContentProfile)) ||
                !uniqueInputs.Add(key))
            {
                problems.Add("input identity is invalid or duplicated");
            }

            if (input.Status == "included")
            {
                if (!IsArtifactPath(input.ArtifactPath) || !CollectionRules.IsSha256(input.Sha256) ||
                    input.ByteLength is < 1 or > CollectionRules.MaxInputBytes)
                {
                    problems.Add("included artifact descriptor is invalid");
                }
            }
            else if (input.Status == "optional-failed")
            {
                if (input.Required || input.ArtifactPath is not null || !CollectionRules.IsSha256(input.Sha256) ||
                    input.ByteLength is < 1 or > CollectionRules.MaxInputBytes)
                {
                    problems.Add("optional failure disposition is invalid");
                }
            }
            else
            {
                problems.Add("unknown input disposition");
            }
        }

        if (!content.Repositories.SequenceEqual(content.Repositories.OrderBy(r => r.RepositoryId, StringComparer.Ordinal)) ||
            !content.Inputs.SequenceEqual(content.Inputs.OrderBy(i => i.RepositoryId, StringComparer.Ordinal)
                .ThenBy(i => i.Profile, StringComparer.Ordinal).ThenBy(i => i.UnitPath, StringComparer.Ordinal)))
        {
            problems.Add("repositories and inputs must be deterministically sorted");
        }

        for (var index = 0; index < content.Inputs.Count; index++)
        {
            var input = content.Inputs[index];
            if (input.Status != "included")
            {
                continue;
            }

            var expected = $"artifacts/{input.RepositoryId}/{input.Profile}/{index:D4}.json";
            if (input.ArtifactPath != expected)
            {
                problems.Add("artifact references are not deterministic");
            }
        }
    }

    private static string? ResolveArtifact(string root, string relative)
    {
        if (!IsArtifactPath(relative))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var path = Path.GetRelativePath(root, full);
        return path.StartsWith($"artifacts{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(path) ? full : null;
    }

    private static bool IsArtifactPath(string? path) =>
        path is not null && path.StartsWith("artifacts/", StringComparison.Ordinal) &&
        CollectionRules.IsLogicalPath(path) && path.EndsWith(".json", StringComparison.Ordinal);

    private static List<string> EnumerateFiles(string root)
    {
        var files = new List<string>();
        var directories = new Stack<string>();
        var entries = 0;
        directories.Push(root);
        while (directories.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (++entries > CollectionRules.MaxInputs * 4 + CollectionRules.MaxRepositories + 4)
                {
                    throw new IOException("bundle contains too many entries");
                }
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("symbolic link in bundle");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                }
                else
                {
                    files.Add(entry);
                    if (files.Count > CollectionRules.MaxInputs + 2)
                    {
                        throw new IOException("bundle contains too many files");
                    }
                }
            }
        }

        return files;
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await CollectionRules.ReadBoundedAsync(path, maxBytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (!EvidenceProfileSchema.HasUnambiguousStructure(document.RootElement)) return default;
            if (typeof(T) == typeof(CollectionEnvelope) &&
                EvidenceProfileSchema.Validate(CollectionRules.BundleSchema, document.RootElement).Count > 0)
            {
                return default;
            }
            return document.RootElement.Deserialize<T>(CollectionJson.Options);
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static CollectionOperationResult Failure(string message) =>
        new(false, null, [message], false);
}
