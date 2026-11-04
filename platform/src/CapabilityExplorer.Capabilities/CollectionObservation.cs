namespace CapabilityExplorer.Capabilities;

public sealed record LastSuccessfulReceipt(
    string AttemptId, DateTimeOffset CollectedAt, string InputVectorSha256, string BundleSha256);

/// <summary>
/// Caller-asserted local observation, not a trusted clock or authenticated authority.
/// ObservedAt precedes collection completion; publication, if present, follows it.
/// The reserved policy ID "rights" binds PolicySha256 to the vector's RightsSha256.
/// Other policy IDs identify separate caller-asserted policies, not central authorization.
/// </summary>
public sealed record CollectionObservationContext(
    DateTimeOffset AsOf, DateTimeOffset ObservedAt, DateTimeOffset CollectedAt,
    string AttemptId, DateTimeOffset? SourceEffectiveAt, DateTimeOffset? PublishedAt,
    string? PolicyId, string? PolicySha256, TimeSpan? MaxAge, LastSuccessfulReceipt? LastSuccessful);

/// <summary>
/// ObservationAge means AsOf minus ObservedAt, never a policy creation age.
/// LastSuccessful is carried through unchanged; this evaluation cannot issue a success receipt.
/// </summary>
public sealed record CollectionObservationResult(
    string Status, IReadOnlyList<string> Reasons, string? InputVectorSha256,
    DateTimeOffset? ValidUntil, TimeSpan? ObservationAge, LastSuccessfulReceipt? LastSuccessful,
    CollectionObservationContext Context);

public static partial class CollectionFreshness
{
    public static CollectionObservationResult Evaluate(
        CollectionInputVector previous, CollectionInputVector observed, CollectionObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var vector = Evaluate(previous, observed);
        var reasons = new List<string>(vector.Reasons);
        var clockValid = context.ObservedAt <= context.CollectedAt && context.CollectedAt <= context.AsOf &&
            (context.PublishedAt is null ||
                context.PublishedAt >= context.CollectedAt && context.PublishedAt <= context.AsOf);
        if (!clockValid) reasons.Add("clock-inconsistent");

        var attemptValid = IsObservationIdentity(context.AttemptId);
        if (!attemptValid) reasons.Add("invalid-attempt-id");
        var receiptValid = IsSuccessfulReceiptValid(context);
        if (!receiptValid) reasons.Add("invalid-last-successful-receipt");

        var lastSuccessfulMismatch = receiptValid && context.LastSuccessful is { } receipt &&
            vector.InputVectorSha256 is { } digest && receipt.InputVectorSha256 != digest;
        if (lastSuccessfulMismatch) reasons.Add("last-successful-vector-mismatch");

        var policyValid = ValidateObservationPolicy(context, observed, reasons);
        DateTimeOffset? validUntil = null;
        TimeSpan? observationAge = null;
        if (clockValid && attemptValid && receiptValid && policyValid)
        {
            try
            {
                validUntil = context.ObservedAt.Add(context.MaxAge!.Value);
                observationAge = context.AsOf - context.ObservedAt;
            }
            catch (ArgumentOutOfRangeException)
            {
                reasons.Add("freshness-expiry-overflow");
                policyValid = false;
            }
        }

        var expired = validUntil is { } deadline && context.AsOf >= deadline;
        if (expired) reasons.Add("observation-expired");
        var status = vector.Status == "unknown" || !clockValid || !attemptValid || !receiptValid ? "unknown" :
            vector.Status == "stale" || lastSuccessfulMismatch ? "stale" :
            !policyValid ? "unknown" : expired ? "stale" : "current";
        return new(status, reasons.Order(StringComparer.Ordinal).ToArray(), vector.InputVectorSha256,
            validUntil, observationAge, context.LastSuccessful, context);
    }

    private static bool ValidateObservationPolicy(
        CollectionObservationContext context, CollectionInputVector observed, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(context.PolicyId) ||
            string.IsNullOrWhiteSpace(context.PolicySha256) || context.MaxAge is null)
        {
            reasons.Add("no-freshness-policy");
            return false;
        }
        if (!IsObservationIdentity(context.PolicyId) ||
            !IsHash(context.PolicySha256, 64) || context.MaxAge <= TimeSpan.Zero)
        {
            reasons.Add("invalid-freshness-policy");
            return false;
        }
        if (context.PolicyId == "rights" && context.PolicySha256 != observed.RightsSha256)
        {
            reasons.Add("freshness-policy-rights-mismatch");
            return false;
        }
        return true;
    }

    private static bool IsSuccessfulReceiptValid(CollectionObservationContext context) =>
        context.LastSuccessful is not { } receipt ||
        IsObservationIdentity(receipt.AttemptId) &&
        IsHash(receipt.InputVectorSha256, 64) && IsHash(receipt.BundleSha256, 64) &&
        receipt.CollectedAt <= context.CollectedAt && receipt.CollectedAt <= context.AsOf;

    private static bool IsObservationIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
}
