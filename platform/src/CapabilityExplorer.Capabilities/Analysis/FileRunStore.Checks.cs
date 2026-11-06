using System.Text.Json;
using System.Text.RegularExpressions;

namespace CapabilityExplorer.Capabilities.Analysis;

public sealed partial class FileRunStore
{
    private string ChecksDirectory()
    {
        CoordinationDomain.Validate(_root);
        var path = Path.Combine(_root, "checks");
        AnalysisPaths.DirectoryPrivate(path);
        return path;
    }

    private string CheckPath(string id, string name)
    {
        AnalysisPaths.Id(id);
        var path = Path.Combine(ChecksDirectory(), id, name);
        AnalysisPaths.NoLinks(path);
        return path;
    }

    private static bool CheckTerminal(SourceCheck check) => check.State is "done" or "failed" or "cancelled";
    private void SaveCheck(SourceCheck check) => AnalysisPaths.Save(CheckPath(check.RequestId, "check.json"),
        check with { ConfigurationPath = "capabilities.json" });

    public SourceCheck ReadCheck(string requestId)
    {
        var path = CheckPath(requestId, "check.json");
        if (!File.Exists(path)) throw new KeyNotFoundException("source check does not exist");
        var check = AnalysisPaths.Read<SourceCheck>(path);
        if (check.RequestId != requestId || check.ConfigurationPath != "capabilities.json" ||
            check.ConfigurationHash is not { Length: 64 } || !check.ConfigurationHash.All(char.IsAsciiHexDigit) ||
            !GitHubAuth.IsValidMode(check.GitHubAuth) ||
            check.State is not ("queued" or "working" or "cancelling" or "done" or "failed" or "cancelled" or "unknown"))
            throw new InvalidDataException("invalid source check record");
        AnalysisPaths.Capability(check.CapabilityId);
        if (check.AttemptId is not null) AnalysisPaths.Id(check.AttemptId);
        if ((check.State is "working" or "cancelling" or "done" or "failed" or "unknown") && check.AttemptId is null)
            throw new InvalidDataException("source check attempt is missing");
        if ((check.State == "done") != (check.Snapshot is not null) ||
            (check.Snapshot is { } snapshot && (snapshot.CapabilityId != check.CapabilityId || snapshot.Repositories is null)))
            throw new InvalidDataException("source check result state mismatch");
        return check with { ConfigurationPath = CheckPath(requestId, "capabilities.json") };
    }

    private List<SourceCheck> AllChecks() => Directory.EnumerateDirectories(ChecksDirectory())
        .Select(path => ReadCheck(Path.GetFileName(path))).ToList();
    private bool ChecksBlock() => AllChecks().Any(check => !CheckTerminal(check) && check.State != "queued");

    public SourceCheck? ReadLatestCheck(string capId)
    {
        AnalysisPaths.Capability(capId);
        return AllChecks().Where(check => check.CapabilityId == capId).OrderByDescending(check => check.AcceptedAt)
            .ThenByDescending(check => check.RequestId, StringComparer.Ordinal).FirstOrDefault();
    }

    private static string CheckHash(byte[] configuration, string auth) =>
        AnalysisPaths.Hash(JsonSerializer.Serialize(new { configuration = AnalysisPaths.Hash(configuration), githubAuth = auth }, AnalysisPaths.Json));

    public bool CheckMatches(SourceCheck check, string configurationPath) =>
        CheckMatches(check, configurationPath, ReadWorkerDefaults()?.Settings.GitHubAuth ?? "auto");

    public static bool CheckMatches(SourceCheck check, string configurationPath, string gitHubAuth) =>
        CheckHash(CaptureConfiguration(configurationPath), gitHubAuth) == check.ConfigurationHash;

    public SourceCheckAdmission AdmitCheck(string capId, string configurationPath, string? gitHubAuth = null)
    {
        AnalysisPaths.Capability(capId);
        using var gate = Gate();
        var checks = AllChecks();
        var active = checks.Where(check => check.CapabilityId == capId && !CheckTerminal(check))
            .OrderByDescending(check => check.AcceptedAt).FirstOrDefault();
        if (active is not null) return new(false, active);
        if (Legacy() || checks.Any(check => check.State == "unknown") ||
            All().Any(run => run.State is "unknown" or "interrupted" or "partial"))
            throw new InvalidOperationException("unreconciled execution blocks source check admission");
        if (checks.Count >= Math.Min(1000, _maxRunRecords)) throw new InvalidOperationException("source check retention limit reached");
        var bytes = CaptureConfiguration(configurationPath);
        var auth = gitHubAuth ?? ReadWorkerDefaults()?.Settings.GitHubAuth ?? "auto";
        if (!GitHubAuth.IsValidMode(auth)) throw new ArgumentException("invalid GitHub authentication policy", nameof(gitHubAuth));
        var id = Guid.NewGuid().ToString("N");
        // Validate the effective definition before creating operational evidence.
        using (var document = JsonDocument.Parse(bytes))
            if (!document.RootElement.GetProperty("capabilities").EnumerateArray().Any(cap => cap.GetProperty("id").GetString() == capId))
                throw new InvalidDataException("capability is absent from accepted configuration");
        var time = checks.Count == 0 ? DateTimeOffset.UtcNow : new[] { DateTimeOffset.UtcNow, checks.Max(check => check.AcceptedAt).AddTicks(1) }.Max();
        var check = new SourceCheck(id, capId, "queued", time, CheckPath(id, "capabilities.json"), CheckHash(bytes, auth), auth);
        AnalysisPaths.DirectoryPrivate(Path.GetDirectoryName(check.ConfigurationPath)!);
        AnalysisPaths.Atomic(check.ConfigurationPath, bytes);
        SaveCheck(check);
        return new(true, check);
    }

    private static void VerifyCheck(SourceCheck check)
    {
        if (CheckHash(CaptureConfiguration(check.ConfigurationPath), check.GitHubAuth) != check.ConfigurationHash)
            throw new InvalidDataException("accepted source check inputs changed");
    }

    public SourceCheckCancellation CancelCheck(string requestId)
    {
        using var gate = Gate();
        var check = ReadCheck(requestId);
        if (check.State == "done") return new("too-late", check);
        if (CheckTerminal(check)) return new(check.State == "cancelled" ? "already-cancelled" : "terminal", check);
        check = check with { CancellationRequested = true, State = check.State == "queued" ? "cancelled" : check.State == "working" ? "cancelling" : check.State };
        SaveCheck(check);
        return new("accepted", check);
    }

    public SourceCheck? ClaimCheck(WorkerOwnership owner)
    {
        Owner(owner);
        using var gate = Gate();
        if (Legacy() || ChecksBlock() || All().Any(run => !Terminal(run) && run.State != "queued"))
            throw new InvalidOperationException("unreconciled execution blocks source acquisition");
        var check = AllChecks().Where(check => check.State == "queued").OrderBy(check => check.AcceptedAt)
            .ThenBy(check => check.RequestId, StringComparer.Ordinal).FirstOrDefault();
        if (check is null) return null;
        VerifyCheck(check);
        check = check with { State = "working", AttemptId = Guid.NewGuid().ToString("N") };
        SaveCheck(check);
        return check;
    }

    private SourceCheck CheckAttempt(AttemptIdentity attempt)
    {
        AnalysisPaths.Id(attempt.AttemptId);
        var check = ReadCheck(attempt.RunId);
        if (check.AttemptId != attempt.AttemptId || check.State is not ("working" or "cancelling"))
            throw new InvalidOperationException("stale or terminal source check attempt");
        VerifyCheck(check);
        return check;
    }

    public SourceCheck CompleteCheck(WorkerOwnership owner, AttemptIdentity attempt, CapabilitySnapshot snapshot)
    {
        Owner(owner);
        using var gate = Gate();
        var check = CheckAttempt(attempt);
        if (check.CancellationRequested || check.State != "working") throw new InvalidOperationException("source check cancellation prevents publication");
        using var definitions = CapabilityStore.Open(check.ConfigurationPath, CheckPath(check.RequestId, "unused-snapshots.json"), loadSnapshots: false);
        var definition = definitions.Find(check.CapabilityId) ?? throw new InvalidDataException("accepted capability is missing");
        if (snapshot.CapabilityId != check.CapabilityId || snapshot.Repositories is null || snapshot.Repositories.Count != definition.Repositories.Count ||
            snapshot.Repositories.Select(repo => repo.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Repositories.Count ||
            snapshot.Repositories.Any(repo => !definition.Repositories.Any(source => source.Id == repo.Id && source.Ref == repo.Ref) ||
                (repo.CommitSha is null ? repo.Error is null : !Regex.IsMatch(repo.CommitSha, @"\A[a-f0-9]{40}\z") || repo.Error is not null)))
            throw new InvalidDataException("source check revision or membership mismatch");
        // Remote subjects/errors are untrusted and may echo credentials. They never enter public records.
        var sanitized = new CapabilitySnapshot(check.CapabilityId, snapshot.CheckedAt,
            snapshot.Repositories.Select(repo => repo with { Subject = null, Error = repo.Error is null ? null : "source acquisition failed" }).ToArray());
        check = check with { State = "done", Snapshot = sanitized };
        SaveCheck(check);
        return check;
    }

    public SourceCheck FailCheck(WorkerOwnership owner, AttemptIdentity attempt, bool terminationConfirmed = false)
    {
        Owner(owner);
        using var gate = Gate();
        var check = CheckAttempt(attempt);
        check = check with { State = check.CancellationRequested || !terminationConfirmed ? "unknown" : "failed", Detail = "source acquisition did not complete", Snapshot = null };
        SaveCheck(check);
        return check;
    }

    private bool RecoverChecks()
    {
        var checks = AllChecks();
        foreach (var check in checks.Where(check => check.State is "working" or "cancelling"))
            SaveCheck(check with { State = "unknown", Snapshot = null, Detail = "prior source acquisition requires reconciliation" });
        return checks.Any(check => !CheckTerminal(check) && check.State != "queued");
    }
}
