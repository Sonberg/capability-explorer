using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CapabilityExplorer.Capabilities;

namespace CapabilityExplorer.Cli;

/// <summary>The loaded, digest-verified C# adapter manifest/recipe identity for one analysis attempt.</summary>
internal sealed record AdapterShadowContext(NativeAdapterManifest Manifest, string ManifestSha256, string RecipeSha256, string HostRuntime);

/// <summary>The shadow (non-filtering) plan outcome for one repository: never fails the attempt by itself.</summary>
internal sealed record AdapterShadowOutcome(string Status, IReadOnlyList<string> Codes, string? PlanSha256, string? Census);

/// <summary>
/// Shadow adapter planning: runs the existing native C# planner over an already-exported commit tree and records
/// its identity/outcome as new receipt keys. Never filters, blocks, or changes dispatch — a non-"ok" outcome is
/// recorded and logged, never thrown, except for genuine configuration/invariant violations which fail the
/// attempt before any package or extraction work starts.
/// </summary>
internal static partial class AnalysisAdapterShadow
{
    private const string AdapterDirectory = "config/adapters/csharp.roslyn";
    private const long MaxRecipeBytes = 64 * 1024;
    // Must match NativeAdapterPlanner's own (private) manifest bound: the "before"/"after" tamper check below
    // reads the manifest a second time outside that reader and must never allocate past the same limit.
    private const long MaxManifestBytes = 1_048_576;

    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);

    // The closed set of diagnostic codes the native planner may emit. Anything else is an invariant violation.
    private static readonly HashSet<string> FaultCodes = new(StringComparer.Ordinal)
    {
        "depth-limit-exceeded", "directory-inaccessible", "entry-inaccessible", "entry-limit-exceeded",
        "export-skipped", "file-byte-limit", "input-inaccessible", "linked-input", "time-limit",
        "total-byte-limit", "unit-limit-exceeded",
    };
    private static readonly HashSet<string> NonFaultCodes = new(StringComparer.Ordinal)
    {
        "ambiguous-project-directory", "unsupported-embedded-source", "unsupported-input-profile",
        "source-outside-project", "exclusion-not-enforceable",
    };
    private static readonly HashSet<string> LimitCodes = new(StringComparer.Ordinal)
    {
        "depth-limit-exceeded", "entry-limit-exceeded", "file-byte-limit", "time-limit",
        "total-byte-limit", "unit-limit-exceeded",
    };

    [GeneratedRegex("^[A-Za-z0-9 .+-]{1,128}$")]
    private static partial Regex HostRuntimePattern();
    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex TreePattern();

    /// <summary>Loads and digest-verifies the tracked adapter manifest/recipe once per attempt.</summary>
    public static async Task<AdapterShadowContext> LoadAsync(string root, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(root, AdapterDirectory);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var recipePath = Path.Combine(directory, "recipe.json");

        // Read bounded, not File.ReadAllBytesAsync: an unbounded read here would allocate past the declared
        // limit for a hostile oversized file before the inner reader's own size check ever runs, and a plain
        // FileInfo.Length pre-check alone would still race a file that grows between the check and the read.
        var before = await ReadBoundedAsync(manifestPath, MaxManifestBytes, static message => new IOException(message), cancellationToken);
        var manifest = await NativeAdapterPlanner.ReadManifestAsync(manifestPath, cancellationToken);
        var after = await ReadBoundedAsync(manifestPath, MaxManifestBytes, static message => new IOException(message), cancellationToken);
        if (!before.AsSpan().SequenceEqual(after))
        {
            throw new IOException("adapter manifest changed while loading");
        }

        AnalysisPreparation.RejectLinks(recipePath);
        var recipeBytes = await ReadBoundedAsync(
            recipePath, MaxRecipeBytes, static message => new InvalidDataException(message), cancellationToken);
        if (recipeBytes.Length < 1)
        {
            throw new InvalidDataException("adapter recipe is missing, empty, or exceeds the size limit");
        }
        var recipeSha256 = Digest(recipeBytes);
        if (recipeSha256 != manifest.RecipeSha256)
        {
            throw new InvalidDataException("adapter recipe digest does not match the manifest's declared identity");
        }

        var host = RuntimeInformation.FrameworkDescription;
        if (!HostRuntimePattern().IsMatch(host))
        {
            throw new InvalidDataException("adapter host runtime description is malformed");
        }

        return new AdapterShadowContext(manifest, Digest(before), recipeSha256, host);
    }

    /// <summary>
    /// Runs the existing native planner over one already-exported repository and computes its shadow status.
    /// Content/limit/skip outcomes are returned, never thrown; only the one bounded planning deadline is caught.
    /// </summary>
    public static async Task<AdapterShadowOutcome> PlanAsync(
        string sourceRoot, string repositoryId, string commitSha, string treeSha, int exportSkipped,
        AdapterShadowContext shadow, TimeSpan budget, CancellationToken stop)
    {
        if (!TreePattern().IsMatch(treeSha))
        {
            throw new InvalidDataException("adapter tree identity is invalid");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(budget);
        NativeAdapterPlan plan;
        try
        {
            plan = await NativeAdapterPlanner.PlanAsync(sourceRoot, repositoryId, commitSha, treeSha, shadow.Manifest, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !stop.IsCancellationRequested)
        {
            return new AdapterShadowOutcome("limit-exceeded", ["time-limit"], null, null);
        }

        var codes = plan.Diagnostics.Select(diagnostic => diagnostic.Code).Distinct().ToArray();
        foreach (var code in codes)
        {
            if (!FaultCodes.Contains(code) && !NonFaultCodes.Contains(code))
            {
                throw new InvalidDataException($"unrecognized adapter diagnostic code: {code}");
            }
        }

        var faults = new SortedSet<string>(codes.Where(FaultCodes.Contains), StringComparer.Ordinal);
        if (exportSkipped > 0)
        {
            faults.Add("export-skipped");
        }

        var status = faults.Any(LimitCodes.Contains) ? "limit-exceeded"
            : faults.Count > 0 || !plan.EnumerationComplete ? "incomplete"
            : plan.UnknownCount > 0 ? "unknown"
            : "ok";

        var planSha256 = Digest(JsonSerializer.SerializeToUtf8Bytes(plan, PlanJson));
        var census =
            $"complete={(plan.EnumerationComplete ? 1 : 0)} unknown={plan.UnknownCount} skipped={exportSkipped} " +
            $"units={plan.UnitCounts.Total}/{plan.UnitCounts.Eligible}/{plan.UnitCounts.Excluded}/{plan.UnitCounts.Unsupported}/{plan.UnitCounts.Unknown} " +
            $"inputs={plan.InputCounts.Total}/{plan.InputCounts.Eligible}/{plan.InputCounts.Excluded}/{plan.InputCounts.Unsupported}/{plan.InputCounts.Unknown}";

        return new AdapterShadowOutcome(status, faults.ToArray(), planSha256, census);
    }

    /// <summary>Adds the per-repository shadow receipt keys: tree identity, status, plan digest, and census.</summary>
    public static void AddInputs(IDictionary<string, string> inputs, string repositoryId, string treeSha, AdapterShadowOutcome outcome)
    {
        inputs["tree:" + repositoryId] = treeSha;
        inputs["adapter-shadow-status:" + repositoryId] = outcome.Codes.Count == 0
            ? outcome.Status
            : outcome.Status + ":" + string.Join(',', outcome.Codes);
        if (outcome.PlanSha256 is not null)
        {
            inputs["adapter-plan:" + repositoryId] = outcome.PlanSha256;
        }
        if (outcome.Census is not null)
        {
            inputs["adapter-census:" + repositoryId] = outcome.Census;
        }
    }

    /// <summary>Adds the once-per-attempt global adapter identity receipt keys.</summary>
    public static void AddGlobalInputs(IDictionary<string, string> inputs, AdapterShadowContext shadow)
    {
        inputs["adapter-manifest"] = shadow.ManifestSha256;
        inputs["adapter-recipe"] = shadow.RecipeSha256;
        inputs["adapter-host-runtime"] = shadow.HostRuntime;
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Streams a file in fixed chunks, never buffering past <paramref name="maximumBytes"/> + 1 regardless of the
    /// file's reported length: this defends both a hostile oversized file (no unbounded single-shot allocation)
    /// and a file that grows after being stat'd (no length-then-read race). Mirrors the bounded-stream shape of
    /// NativeAdapterPlanner's own manifest reader and SourceSafety.ReadBoundedAsync; not a plain ReadAllBytes call.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(
        string path, long maximumBytes, Func<string, Exception> onExceeded, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var remaining = maximumBytes + 1 - buffer.Length;
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
            {
                throw onExceeded("adapter config file is missing, empty, or exceeds its declared size limit");
            }
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
