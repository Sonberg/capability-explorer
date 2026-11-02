namespace CapabilityExplorer.Capabilities.Analysis;

public sealed record SourceCheck(
    string RequestId,
    string CapabilityId,
    string State,
    DateTimeOffset AcceptedAt,
    string ConfigurationPath,
    string ConfigurationHash,
    string GitHubAuth,
    string? AttemptId = null,
    CapabilitySnapshot? Snapshot = null,
    bool CancellationRequested = false,
    string? Detail = null);

public sealed record SourceCheckAdmission(bool Accepted, SourceCheck Check);
public sealed record SourceCheckCancellation(string Outcome, SourceCheck Check);
