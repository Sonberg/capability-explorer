using System.Text.Json;
using CapabilityExplorer.Capabilities;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class CollectionObservationTests
{
    private static readonly DateTimeOffset Observed = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private static string Hash(char value) => new(value, 64);
    private static CollectionInputVector Vector() =>
        new([new("sales", new string('a', 40), new string('b', 40), Hash('c'), Hash('d'))],
            Hash('a'), Hash('b'), Hash('c'), Hash('d'), Hash('e'), true);

    private static CollectionObservationContext Context() =>
        new(Observed.AddHours(12), Observed, Observed.AddMinutes(1), "attempt-new",
            null, null, "local-24h", Hash('f'), TimeSpan.FromHours(24), null);

    private static LastSuccessfulReceipt Receipt(CollectionInputVector vector) =>
        new("attempt-old", Observed.AddHours(-1),
            CollectionFreshness.Evaluate(vector, vector).InputVectorSha256!, Hash('a'));

    [Fact]
    public void Given_complete_inputs_within_explicit_window_when_observed_then_current_with_bound_context()
    {
        var vector = Vector();
        var context = Context();
        var result = CollectionFreshness.Evaluate(vector, vector, context);

        Assert.Equal("current", result.Status);
        Assert.Empty(result.Reasons);
        Assert.Equal(Observed.AddHours(24), result.ValidUntil);
        Assert.Equal(TimeSpan.FromHours(12), result.ObservationAge);
        Assert.Equal(CollectionFreshness.Evaluate(vector, vector).InputVectorSha256, result.InputVectorSha256);
        Assert.Same(context, result.Context);
        Assert.Null(result.LastSuccessful);
    }

    [Theory]
    [InlineData(-1, "current")]
    [InlineData(0, "stale")]
    [InlineData(1, "stale")]
    public void Given_half_open_window_when_asof_reaches_expiry_then_stale(long offsetTicks, string expected)
    {
        var vector = Vector();
        var context = Context() with { AsOf = Observed.AddHours(24).AddTicks(offsetTicks) };
        var result = CollectionFreshness.Evaluate(vector, vector, context);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == "stale", result.Reasons.Contains("observation-expired", StringComparer.Ordinal));
    }

    [Fact]
    public void Given_changed_vector_and_no_policy_when_observed_then_staleness_is_not_hidden()
    {
        var previous = Vector();
        var observed = previous with { RecipeSha256 = Hash('f') };
        var result = CollectionFreshness.Evaluate(previous, observed,
            Context() with { PolicyId = null, PolicySha256 = null, MaxAge = null });

        Assert.Equal("stale", result.Status);
        Assert.Contains("Changed input: recipe", result.Reasons);
        Assert.Contains("no-freshness-policy", result.Reasons);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("hash")]
    [InlineData("age")]
    public void Given_current_vector_without_complete_policy_when_observed_then_unknown(string missing)
    {
        var vector = Vector();
        var context = Context();
        context = missing switch
        {
            "id" => context with { PolicyId = null },
            "hash" => context with { PolicySha256 = null },
            _ => context with { MaxAge = null }
        };
        var result = CollectionFreshness.Evaluate(vector, vector, context);

        Assert.Equal("unknown", result.Status);
        Assert.Contains("no-freshness-policy", result.Reasons);
        Assert.Null(result.ValidUntil);
    }

    [Fact]
    public void Given_incomplete_enumeration_with_expired_window_when_observed_then_still_unknown()
    {
        var previous = Vector();
        var observed = previous with { EnumerationComplete = false, RecipeSha256 = Hash('f') };
        var result = CollectionFreshness.Evaluate(previous, observed,
            Context() with { AsOf = Observed.AddDays(2) });

        Assert.Equal("unknown", result.Status);
        Assert.Contains("observed: input enumeration is incomplete", result.Reasons);
        Assert.Null(result.InputVectorSha256);
    }

    [Theory]
    [InlineData("asof")]
    [InlineData("collection")]
    [InlineData("collection-after-asof")]
    [InlineData("published-before-collection")]
    [InlineData("published-after-asof")]
    public void Given_inconsistent_clocks_when_observed_then_rejected_without_new_success(string fault)
    {
        var vector = Vector();
        var receipt = Receipt(vector);
        var context = Context() with { LastSuccessful = receipt };
        context = fault switch
        {
            "asof" => context with { AsOf = Observed.AddTicks(-1) },
            "collection" => context with { CollectedAt = Observed.AddTicks(-1) },
            "collection-after-asof" => context with { CollectedAt = context.AsOf.AddTicks(1) },
            "published-before-collection" => context with { PublishedAt = context.CollectedAt.AddTicks(-1) },
            _ => context with { PublishedAt = context.AsOf.AddTicks(1) }
        };
        var result = CollectionFreshness.Evaluate(vector, vector, context);

        Assert.Equal("unknown", result.Status);
        Assert.Contains("clock-inconsistent", result.Reasons);
        Assert.Null(result.ValidUntil);
        Assert.Null(result.ObservationAge);
        Assert.Same(receipt, result.LastSuccessful);
    }

    [Fact]
    public void Given_last_success_from_another_vector_when_observed_then_stale()
    {
        var vector = Vector();
        var result = CollectionFreshness.Evaluate(vector, vector,
            Context() with { LastSuccessful = Receipt(vector) with { InputVectorSha256 = Hash('f') } });

        Assert.Equal("stale", result.Status);
        Assert.Contains("last-successful-vector-mismatch", result.Reasons);
    }

    [Fact]
    public void Given_failed_new_attempt_when_inputs_are_incomplete_then_prior_success_survives_unchanged()
    {
        var vector = Vector();
        var receipt = Receipt(vector);
        var context = Context() with { LastSuccessful = receipt };
        var result = CollectionFreshness.Evaluate(vector, vector with { EnumerationComplete = false }, context);

        Assert.Equal("unknown", result.Status);
        Assert.Same(receipt, result.LastSuccessful);
        Assert.NotEqual(context.AttemptId, result.LastSuccessful!.AttemptId);
        Assert.Same(receipt, CollectionFreshness.Evaluate(vector, vector, context).LastSuccessful);
    }

    [Fact]
    public void Given_only_observation_metadata_changes_when_observed_then_original_input_digest_is_unchanged()
    {
        var vector = Vector();
        var first = CollectionFreshness.Evaluate(vector, vector, Context());
        var second = CollectionFreshness.Evaluate(vector, vector, Context() with
        {
            AttemptId = "different-attempt", AsOf = Observed.AddDays(2), PolicyId = "separate-policy",
            PolicySha256 = Hash('a'), MaxAge = TimeSpan.FromHours(1), SourceEffectiveAt = Observed.AddYears(10),
            PublishedAt = Observed.AddHours(2)
        });

        Assert.Equal("current", first.Status);
        Assert.Equal("stale", second.Status);
        Assert.Equal(first.InputVectorSha256, second.InputVectorSha256);
        Assert.Equal(CollectionFreshness.Evaluate(vector, vector).InputVectorSha256, second.InputVectorSha256);
    }

    [Theory]
    [InlineData(-87600)]
    [InlineData(0)]
    [InlineData(87600)]
    public void Given_author_controlled_effective_time_when_varied_then_freshness_is_unchanged(int hours)
    {
        var vector = Vector();
        var result = CollectionFreshness.Evaluate(vector, vector,
            Context() with { SourceEffectiveAt = Observed.AddHours(hours) });

        Assert.Equal("current", result.Status);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Given_identical_explicit_context_when_repeated_then_serialized_output_is_identical()
    {
        var vector = Vector();
        var context = Context() with { LastSuccessful = Receipt(vector), AsOf = Observed.AddDays(2) };
        var first = CollectionFreshness.Evaluate(vector, vector with { RecipeSha256 = Hash('f') }, context);
        var second = CollectionFreshness.Evaluate(vector, vector with { RecipeSha256 = Hash('f') }, context);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(first.Reasons.Order(StringComparer.Ordinal), first.Reasons);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("bad-id")]
    [InlineData("long-id")]
    [InlineData("bad-hash")]
    public void Given_malformed_policy_when_observed_then_unknown_with_diagnostic(string fault)
    {
        var vector = Vector();
        var context = Context();
        context = fault switch
        {
            "zero" => context with { MaxAge = TimeSpan.Zero },
            "negative" => context with { MaxAge = TimeSpan.FromTicks(-1) },
            "bad-id" => context with { PolicyId = "policy\ninjected" },
            "long-id" => context with { PolicyId = new string('a', 257) },
            _ => context with { PolicySha256 = "not-a-hash" }
        };
        var result = CollectionFreshness.Evaluate(vector, vector, context);

        Assert.Equal("unknown", result.Status);
        Assert.Contains("invalid-freshness-policy", result.Reasons);
        Assert.Null(result.ValidUntil);
    }

    [Fact]
    public void Given_expiry_addition_overflow_when_observed_then_explicitly_unknown_not_exception()
    {
        var vector = Vector();
        var result = CollectionFreshness.Evaluate(vector, vector,
            Context() with { MaxAge = TimeSpan.MaxValue });

        Assert.Equal("unknown", result.Status);
        Assert.Contains("freshness-expiry-overflow", result.Reasons);
        Assert.Null(result.ValidUntil);
    }

    [Theory]
    [InlineData(true, "current")]
    [InlineData(false, "unknown")]
    public void Given_policy_identified_as_rights_when_hash_differs_then_rejected(bool matches, string expected)
    {
        var vector = Vector();
        var result = CollectionFreshness.Evaluate(vector, vector, Context() with
        {
            PolicyId = "rights", PolicySha256 = matches ? vector.RightsSha256 : Hash('f')
        });

        Assert.Equal(expected, result.Status);
        Assert.Equal(!matches, result.Reasons.Contains("freshness-policy-rights-mismatch", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("hash")]
    [InlineData("bundle")]
    [InlineData("future")]
    public void Given_invalid_prior_receipt_when_observed_then_unknown_without_rewriting_receipt(string fault)
    {
        var vector = Vector();
        var receipt = Receipt(vector);
        receipt = fault switch
        {
            "attempt" => receipt with { AttemptId = "" },
            "hash" => receipt with { InputVectorSha256 = "invalid" },
            "bundle" => receipt with { BundleSha256 = "invalid" },
            _ => receipt with { CollectedAt = Context().AsOf.AddTicks(1) }
        };
        var result = CollectionFreshness.Evaluate(vector, vector, Context() with { LastSuccessful = receipt });

        Assert.Equal("unknown", result.Status);
        Assert.Contains("invalid-last-successful-receipt", result.Reasons);
        Assert.Same(receipt, result.LastSuccessful);
    }

    [Fact]
    public void Given_invalid_attempt_id_when_observed_then_unknown()
    {
        var vector = Vector();
        var result = CollectionFreshness.Evaluate(vector, vector, Context() with { AttemptId = " " });

        Assert.Equal("unknown", result.Status);
        Assert.Contains("invalid-attempt-id", result.Reasons);
    }
}
