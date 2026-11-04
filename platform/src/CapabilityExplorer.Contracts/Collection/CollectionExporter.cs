using System.Security.Cryptography;
using System.Text.Json;

namespace CapabilityExplorer.Contracts.Collection;

public static class CollectionExporter
{
    private const int MaxManifestBytes = 1_048_576;

    public static async Task<CollectionOperationResult> ExportAsync(
        string planFile,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var stage = string.Empty;
        try
        {
            var problems = new List<string>();
            var plan = await ReadPlanAsync(planFile, problems, cancellationToken).ConfigureAwait(false);
            if (plan is null || !ValidatePlan(plan, problems))
            {
                return Failure(problems);
            }

            var existing = await CollectionValidator.ValidateAsync(outputDirectory, cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(outputDirectory))
            {
                if (existing.IsValid && await MatchesPlanAsync(outputDirectory, plan, cancellationToken).ConfigureAwait(false))
                {
                    return existing with { Reused = true };
                }

                return Failure(["output directory already exists with different or invalid content"]);
            }

            var outputFullPath = Path.GetFullPath(outputDirectory);
            var parent = Path.GetDirectoryName(outputFullPath);
            if (parent is null)
            {
                return Failure(["output directory has no parent"]);
            }

            if (!HasSafeExistingPath(parent, includeLeaf: true))
            {
                return Failure(["output parent contains a symbolic link"]);
            }
            Directory.CreateDirectory(parent);

            stage = Path.Combine(parent, $".{Path.GetFileName(outputFullPath)}.collection-stage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stage);
            var descriptors = await ReadInputsAsync(plan, Path.GetDirectoryName(Path.GetFullPath(planFile))!,
                stage, problems, cancellationToken).ConfigureAwait(false);
            if (problems.Count > 0)
            {
                return Failure(problems);
            }

            var content = CreateContent(plan, descriptors);
            var contentSha256 = CollectionCanonicalization.Digest(content);
            var envelope = new CollectionEnvelope(CollectionRules.BundleSchema, contentSha256, content);
            await WriteJsonAsync(Path.Combine(stage, "collection.json"), envelope, cancellationToken).ConfigureAwait(false);
            var attempt = new CollectionAttempt(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
            await WriteJsonAsync(Path.Combine(stage, "attempt.json"), attempt, cancellationToken).ConfigureAwait(false);

            var stagedValidation = await CollectionValidator.ValidateAsync(stage, cancellationToken).ConfigureAwait(false);
            if (!stagedValidation.IsValid || stagedValidation.ContentSha256 != contentSha256)
            {
                return Failure(stagedValidation.Problems.Count == 0
                    ? ["staged bundle digest did not match"]
                    : stagedValidation.Problems);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stage, outputFullPath);
            stage = string.Empty;
            return new CollectionOperationResult(true, contentSha256, [], false);
        }
        catch (OperationCanceledException)
        {
            return Failure(["collection canceled"]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                           ArgumentException or NotSupportedException)
        {
            return Failure([$"collection failed: {SafeMessage(exception)}"]);
        }
        finally
        {
            if (stage.Length > 0 && Directory.Exists(stage))
            {
                Directory.Delete(stage, recursive: true);
            }
        }
    }

    public static Task<CollectionOperationResult> ValidateAsync(
        string bundleDirectory,
        CancellationToken cancellationToken = default) =>
        CollectionValidator.ValidateAsync(bundleDirectory, cancellationToken);

    private static async Task<CollectionPlan?> ReadPlanAsync(
        string planFile,
        List<string> problems,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(planFile))
        {
            problems.Add("plan file is required");
            return null;
        }

        var fullPath = Path.GetFullPath(planFile);
        if (!File.Exists(fullPath) || !HasSafeExistingPath(fullPath, includeLeaf: true))
        {
            problems.Add("plan file is missing or uses a symbolic link");
            return null;
        }

        var info = new FileInfo(fullPath);
        if (info.Length is < 1 or > CollectionRules.MaxPlanBytes)
        {
            problems.Add("plan file is empty or exceeds the size limit");
            return null;
        }

        var bytes = await CollectionRules.ReadBoundedAsync(fullPath, CollectionRules.MaxPlanBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var schemaProblems = EvidenceProfileSchema.Validate(CollectionRules.PlanSchema, document.RootElement);
            if (schemaProblems.Count > 0)
            {
                problems.AddRange(schemaProblems.Select(problem => $"plan schema: {problem}"));
                return null;
            }
            return document.RootElement.Deserialize<CollectionPlan>(CollectionJson.Options);
        }
        catch (JsonException)
        {
            problems.Add("plan JSON is invalid or contains unknown fields");
            return null;
        }
    }

    private static bool ValidatePlan(CollectionPlan plan, List<string> problems)
    {
        if (plan.Schema != CollectionRules.PlanSchema)
        {
            problems.Add("unsupported collection plan schema");
        }

        if (!CollectionRules.IsSha256(plan.RecipeSha256) || !CollectionRules.IsSha256(plan.ConfigSha256) ||
            !CollectionRules.IsSha256(plan.PolicySha256))
        {
            problems.Add("recipe, config, and policy identities must be lowercase SHA-256 digests");
        }

        if (plan.Repositories is null || plan.Inputs is null ||
            plan.Repositories.Count is < 1 or > CollectionRules.MaxRepositories ||
            plan.Inputs.Count is < 1 or > CollectionRules.MaxInputs)
        {
            problems.Add("repository or input count is outside collection bounds");
            return false;
        }

        var repositories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repository in plan.Repositories)
        {
            if (!CollectionRules.IsRepositoryId(repository.RepositoryId) || !IsSafeRef(repository.ProviderId) ||
                !CollectionRules.IsId(repository.ProducerId) || !CollectionRules.IsId(repository.TenantRef) ||
                !CollectionRules.IsId(repository.RightsRef) || !CollectionRules.IsCommitSha(repository.CommitSha) ||
                !CollectionRules.IsCommitSha(repository.TreeSha) ||
                repository.PermittedContentProfiles.Any(profile => !CollectionRules.IsId(profile)))
            {
                problems.Add("repository identity, enrollment references, or content profile is invalid");
            }

            if (!repositories.Add(repository.RepositoryId))
            {
                problems.Add($"duplicate repository identity {repository.RepositoryId}");
            }
        }

        var inputKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in plan.Inputs)
        {
            var repository = plan.Repositories.FirstOrDefault(r => r.RepositoryId == input.RepositoryId);
            if (!repositories.Contains(input.RepositoryId) || !CollectionRules.IsLogicalPath(input.Path) ||
                !CollectionRules.IsLogicalPath(input.UnitPath) || !CollectionRules.IsSha256(input.Sha256) ||
                input.ByteLength is < 1 or > CollectionRules.MaxInputBytes ||
                (input.ContentProfile is not null && (!CollectionRules.IsId(input.ContentProfile) ||
                    repository is null || !repository.PermittedContentProfiles.Contains(input.ContentProfile, StringComparer.Ordinal))) ||
                (!input.Required && !CollectionRules.IsProfile(input.Profile) && !CollectionRules.IsId(input.Profile)) ||
                (input.Required && !CollectionRules.IsProfile(input.Profile)))
            {
                problems.Add("input identity, relative path, digest, or length is invalid");
            }

            if (!inputKeys.Add($"{input.RepositoryId}\n{input.Profile}\n{input.UnitPath}"))
            {
                problems.Add("duplicate repository/profile/unitPath input");
            }
        }

        return problems.Count == 0;
    }

    private static async Task<List<CollectionInputDescriptor>> ReadInputsAsync(
        CollectionPlan plan,
        string inputRoot,
        string stage,
        List<string> problems,
        CancellationToken cancellationToken)
    {
        var descriptors = new List<CollectionInputDescriptor>();
        var repositoryMap = plan.Repositories.ToDictionary(r => r.RepositoryId, StringComparer.Ordinal);
        long totalBytes = 0;
        var sortedInputs = plan.Inputs
            .OrderBy(i => i.RepositoryId, StringComparer.Ordinal)
            .ThenBy(i => i.Profile, StringComparer.Ordinal)
            .ThenBy(i => i.UnitPath, StringComparer.Ordinal)
            .ToArray();

        for (var index = 0; index < sortedInputs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = sortedInputs[index];
            var repository = repositoryMap[input.RepositoryId];
            if (!CollectionRules.IsProfile(input.Profile))
            {
                HandleInputFailure(input, "unsupported profile", descriptors, problems);
                continue;
            }

            if (!TryResolveInput(inputRoot, input.Path, out var sourcePath))
            {
                HandleInputFailure(input, "input path is unsafe or missing", descriptors, problems);
                continue;
            }

            var bytes = await CollectionRules.ReadBoundedAsync(sourcePath, CollectionRules.MaxInputBytes, cancellationToken)
                .ConfigureAwait(false);
            totalBytes += bytes.LongLength;
            if (bytes.LongLength != input.ByteLength || bytes.LongLength > CollectionRules.MaxInputBytes ||
                totalBytes > CollectionRules.MaxTotalBytes || CollectionRules.Digest(bytes) != input.Sha256)
            {
                HandleInputFailure(input, "input bytes differ from the declared size or digest", descriptors, problems);
                continue;
            }

            var validationProblems = CollectionRules.ValidateProfile(input.Profile, bytes, repository.RepositoryId,
                repository.CommitSha, input.UnitPath, input.ContentProfile, repository.PermittedContentProfiles);
            if (validationProblems.Count > 0)
            {
                HandleInputFailure(input, string.Join("; ", validationProblems), descriptors, problems);
                continue;
            }

            var relativeArtifact = $"artifacts/{repository.RepositoryId}/{input.Profile}/{index:D4}.json";
            var artifactPath = Path.Combine(stage, relativeArtifact.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            await File.WriteAllBytesAsync(artifactPath, bytes, cancellationToken).ConfigureAwait(false);
            descriptors.Add(new CollectionInputDescriptor(repository.RepositoryId, input.Profile, input.UnitPath,
                input.Required, "included", relativeArtifact, input.Sha256, bytes.LongLength, input.ContentProfile));
        }

        return descriptors;
    }

    private static void HandleInputFailure(
        CollectionInputPlan input,
        string reason,
        List<CollectionInputDescriptor> descriptors,
        List<string> problems)
    {
        if (input.Required)
        {
            problems.Add($"required {input.Profile} input for {input.RepositoryId} failed: {reason}");
            return;
        }

        descriptors.Add(new CollectionInputDescriptor(input.RepositoryId, input.Profile, input.UnitPath,
            false, "optional-failed", null, input.Sha256, input.ByteLength, input.ContentProfile));
    }

    private static CollectionContent CreateContent(
        CollectionPlan plan,
        IReadOnlyList<CollectionInputDescriptor> inputs) =>
        new(CollectionRules.BundleSchema, CollectionCanonicalization.Recipe,
            plan.RecipeSha256, plan.ConfigSha256, plan.PolicySha256,
            plan.Repositories.OrderBy(r => r.RepositoryId, StringComparer.Ordinal)
                .Select(r => new CollectionRepositoryDescriptor(r.RepositoryId, r.ProviderId, r.ProducerId,
                    r.TenantRef, r.RightsRef, r.CommitSha, r.TreeSha,
                    r.PermittedContentProfiles.Order(StringComparer.Ordinal).ToArray())).ToArray(),
            inputs.OrderBy(i => i.RepositoryId, StringComparer.Ordinal)
                .ThenBy(i => i.Profile, StringComparer.Ordinal)
                .ThenBy(i => i.UnitPath, StringComparer.Ordinal).ToArray());

    private static async Task<bool> MatchesPlanAsync(
        string bundleDirectory,
        CollectionPlan plan,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(bundleDirectory, "collection.json");
        await using var stream = File.OpenRead(path);
        var envelope = await JsonSerializer.DeserializeAsync<CollectionEnvelope>(
            stream, CollectionJson.Options, cancellationToken).ConfigureAwait(false);
        if (envelope is null || envelope.Content.RecipeSha256 != plan.RecipeSha256 ||
            envelope.Content.ConfigSha256 != plan.ConfigSha256 || envelope.Content.PolicySha256 != plan.PolicySha256)
        {
            return false;
        }

        var repositories = plan.Repositories.OrderBy(r => r.RepositoryId, StringComparer.Ordinal)
            .Select(r => new CollectionRepositoryDescriptor(r.RepositoryId, r.ProviderId, r.ProducerId,
                r.TenantRef, r.RightsRef, r.CommitSha, r.TreeSha,
                r.PermittedContentProfiles.Order(StringComparer.Ordinal).ToArray())).ToArray();
        if (envelope.Content.Repositories.Count != repositories.Length ||
            envelope.Content.Repositories.Where((actual, index) => !SameRepository(actual, repositories[index])).Any())
        {
            return false;
        }

        var expectedInputs = plan.Inputs.OrderBy(i => i.RepositoryId, StringComparer.Ordinal)
            .ThenBy(i => i.Profile, StringComparer.Ordinal)
            .ThenBy(i => i.UnitPath, StringComparer.Ordinal)
            .ToArray();
        if (expectedInputs.Length != envelope.Content.Inputs.Count)
        {
            return false;
        }

        for (var index = 0; index < expectedInputs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = expectedInputs[index];
            var actual = envelope.Content.Inputs[index];
            if (actual.RepositoryId != expected.RepositoryId || actual.Profile != expected.Profile ||
                actual.UnitPath != expected.UnitPath || actual.Required != expected.Required ||
                actual.Sha256 != expected.Sha256 || actual.ByteLength != expected.ByteLength ||
                actual.ContentProfile != expected.ContentProfile)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameRepository(CollectionRepositoryDescriptor left, CollectionRepositoryDescriptor right) =>
        left.RepositoryId == right.RepositoryId && left.ProviderId == right.ProviderId &&
        left.ProducerId == right.ProducerId && left.TenantRef == right.TenantRef &&
        left.RightsRef == right.RightsRef && left.CommitSha == right.CommitSha && left.TreeSha == right.TreeSha &&
        left.PermittedContentProfiles.SequenceEqual(right.PermittedContentProfiles, StringComparer.Ordinal);

    private static bool TryResolveInput(string root, string relativePath, out string path)
    {
        path = string.Empty;
        if (!CollectionRules.IsLogicalPath(relativePath))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(fullRoot, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) || !File.Exists(candidate) || !HasSafeExistingPath(candidate, includeLeaf: true))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    internal static bool HasSafeExistingPath(string path, bool includeLeaf)
    {
        var full = Path.GetFullPath(path);
        var end = includeLeaf ? full : Path.GetDirectoryName(full)!;
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            if (string.Equals(current, end, StringComparison.Ordinal))
            {
                break;
            }
        }

        return true;
    }

    private static bool IsSafeRef(string value) =>
        value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CollectionJson.Options);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static CollectionOperationResult Failure(IEnumerable<string> problems) =>
        new(false, null, problems.Take(32).ToArray(), false);

    private static string SafeMessage(Exception exception) => exception switch
    {
        JsonException => "invalid JSON input",
        UnauthorizedAccessException => "access denied",
        _ => exception.Message.Length > 200 ? exception.Message[..200] : exception.Message,
    };
}
