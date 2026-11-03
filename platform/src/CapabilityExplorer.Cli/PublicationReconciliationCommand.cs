using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Capabilities.Analysis;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Cli;

internal static class PublicationReconciliationCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions PrettyJson = new(Json) { WriteIndented = true };

    internal static async Task<int> RunAsync(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("run", out var runId) || !Id(runId) ||
            !options.TryGetValue("attempt", out var attemptId) || !Id(attemptId))
        {
            Console.Error.WriteLine("reconcile-publication requires lowercase 32-character --run and --attempt ids");
            return 2;
        }

        PublicationReconciliationReport? databaseReport = null;
        string? databaseError = null;
        try
        {
            var connectionString = options.GetValueOrDefault("connection") ?? Environment.GetEnvironmentVariable("CE_DATABASE");
            if (string.IsNullOrEmpty(connectionString)) databaseError = "database-configuration-unavailable";
            else
            {
                await using var database = NpgsqlDataSource.Create(connectionString);
                databaseReport = await new SnapshotStore(database).ReconcilePublicationAttemptAsync(runId, attemptId);
            }
        }
        catch (NpgsqlException) { databaseError = "database-unavailable"; }
        catch (TimeoutException) { databaseError = "database-unavailable"; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
        { databaseError = "database-unavailable"; }

        AnalysisRun? run = null;
        string? hostJournal = null;
        try { run = ReadRun(runId); }
        catch (FileNotFoundException) { hostJournal = "missing"; }
        catch (DirectoryNotFoundException) { hostJournal = "missing"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { hostJournal = "unreadable-or-conflicting"; }

        var hostIdentityMatches = run is not null && run.RunId == runId && run.AttemptId == attemptId &&
            databaseReport is not null && run.CapabilityId == databaseReport.CapabilityId &&
            run.AcceptedInputsHash == databaseReport.AcceptedInputsHash &&
            run.ManifestHash == databaseReport.ManifestHash;
        var postgresEffects = run?.Effects?.Where(effect => effect.Surface == "postgres" &&
            effect.Destination == run.CapabilityId).ToArray() ?? [];
        var hostConfirmation = "missing";
        if (hostJournal == "unreadable-or-conflicting" || run is not null && !hostIdentityMatches ||
            postgresEffects.Length > 1)
            hostConfirmation = "conflicting";
        else if (hostIdentityMatches && postgresEffects.Length == 1)
        {
            var effect = postgresEffects[0];
            var expectedReceipt = databaseReport!.CandidateSnapshotId is { } candidate &&
                databaseReport.CandidateInputIdentity is { } identity
                ? HashText(run!.AcceptedInputsHash + ":" + run.ManifestHash + ":" + candidate + ":" + identity)
                : null;
            hostConfirmation = effect.State switch
            {
                "confirmed" when databaseReport.Outcome == "confirmed-published" &&
                    effect.EvidenceHash == expectedReceipt => "confirmed",
                "pending" => "pending",
                _ => "conflicting"
            };
        }

        var outcome = databaseReport?.Outcome ?? "unresolved";
        if (hostConfirmation == "conflicting") outcome = "unresolved";
        var localEffects = run?.Effects?.Where(effect => effect.Surface == "local-artifacts")
            .Select(effect => new { effect.State, effect.EvidenceHash }).ToArray() ?? [];
        var report = new
        {
            outcome,
            authorization = "none; this report cannot retry, repair, archive, or clear journal evidence",
            databaseEvidence = databaseReport,
            databaseAvailability = databaseReport is null ? databaseError ?? "unavailable" : "available",
            hostJournal = new
            {
                state = run?.State,
                status = hostJournal ?? (run is null ? "missing" : hostIdentityMatches ? "matched" : "conflicting"),
                postgresConfirmation = hostConfirmation,
                localArtifactEffects = localEffects
            }
        };
        Console.WriteLine(JsonSerializer.Serialize(report, PrettyJson));
        return outcome == "unresolved" ? 1 : 0;
    }

    private static AnalysisRun ReadRun(string runId)
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".sandbox-cache", "requests"));
        var runDirectory = Path.Combine(root, "runs", runId);
        var path = Path.Combine(runDirectory, "run.json");
        RejectLinks(root);
        RejectLinks(Path.Combine(root, "runs"));
        RejectLinks(runDirectory);
        RejectLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("run record unavailable");
        if (info.Length is < 1 or > 1024 * 1024) throw new InvalidDataException("run record exceeds its read bound");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is < 1 or > 1024 * 1024) throw new InvalidDataException("run record exceeds its read bound");
        var run = JsonSerializer.Deserialize<AnalysisRun>(bytes, Json) ?? throw new InvalidDataException("empty run record");
        if (run.RunId != runId || run.AttemptId is null && run.State is ("publishing" or "unknown" or "done") ||
            run.AttemptId is not null && !Id(run.AttemptId) ||
            run.AcceptedInputsHash.Length != 64 || run.AcceptedInputsHash.Any(c => !Uri.IsHexDigit(c)) ||
            run.ManifestHash is not null && (run.ManifestHash.Length != 64 || run.ManifestHash.Any(c => !Uri.IsHexDigit(c))))
            throw new InvalidDataException("run receipt identity is invalid");
        return run;
    }

    private static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("host receipt path contains a symbolic link");
        }
    }

    private static string HashText(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool Id(string? value) => value is { Length: 32 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
