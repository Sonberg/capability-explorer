using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Interpretation.BoundedClassification;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class BoundedClassificationTests
{
    [Fact]
    public void Load_binds_raw_bytes_and_classifies_with_stable_scalar_softmax()
    {
        var firstBytes = ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[1d], [-1d]], [0d, 0d]);
        var artifact = Load<Label>(firstBytes);
        var result = artifact.Classify(
            ClassificationArtifact<Label>.InputSchema, artifact.ArtifactId, [8d]);

        Assert.Equal(ClassificationOutcome.Decision, result.Outcome);
        Assert.Equal(ClassificationReason.None, result.Reason);
        Assert.Equal(Label.Accepted, result.SelectedClass);
        Assert.Equal(artifact.ArtifactId, result.ArtifactId);
        Assert.Equal("ce.classification-result.v1", result.Schema);
        Assert.Equal("normalized-score", result.ScoreKind);
        Assert.Equal(1d, result.Scores.Sum(), 1e-12);
        Assert.Equal(1d / (1d + Math.Exp(-16d)), result.Scores[0], 1e-12);

        var changedBytes = ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[-1d], [1d]], [0d, 0d]);
        var changed = Load<Label>(changedBytes);
        Assert.NotEqual(artifact.ArtifactId, changed.ArtifactId);
        Assert.Equal(
            Label.Rejected,
            changed.Classify(ClassificationArtifact<Label>.InputSchema, changed.ArtifactId, [8d]).SelectedClass);
        var whitespaceBytes = Mutate(firstBytes, json => json.Replace("{\"schema\"", "{ \"schema\"", StringComparison.Ordinal));
        var whitespaceVariant = Load<Label>(whitespaceBytes);
        Assert.NotEqual(artifact.ArtifactId, whitespaceVariant.ArtifactId);
        Assert.Equal(
            result.Scores,
            whitespaceVariant.Classify(
                ClassificationArtifact<Label>.InputSchema, whitespaceVariant.ArtifactId, [8d]).Scores);
    }

    [Fact]
    public void Ties_keep_class_order_and_thresholds_pass_inclusively_or_abstain()
    {
        var atThreshold = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[0d], [0d]], [0d, 0d],
            minimumScore: 0.5d, minimumMargin: 0d));
        var tied = atThreshold.Classify(
            ClassificationArtifact<Label>.InputSchema, atThreshold.ArtifactId, [5d]);
        Assert.Equal(ClassificationOutcome.Decision, tied.Outcome);
        Assert.Equal(Label.Accepted, tied.SelectedClass);
        Assert.Equal(0.5d, tied.Scores[0]);
        Assert.Equal(0.5d, tied.Scores[1]);

        var lowScore = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[0d], [0d]], [0d, 0d],
            minimumScore: 0.5001d));
        AssertAbstains(lowScore.Classify(
            ClassificationArtifact<Label>.InputSchema, lowScore.ArtifactId, [5d]),
            ClassificationReason.LowScore);

        var lowMargin = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[0d], [0d]], [0d, 0d],
            minimumMargin: 0.01d));
        AssertAbstains(lowMargin.Classify(
            ClassificationArtifact<Label>.InputSchema, lowMargin.ArtifactId, [5d]),
            ClassificationReason.LowMargin);
    }

    [Fact]
    public void Underflowed_loser_is_permitted_and_out_of_domain_input_abstains_without_scores()
    {
        var artifact = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[0d], [0d]], [0d, -1000d],
            minimumScore: 0d, minimumMargin: 1d));
        var result = artifact.Classify(
            ClassificationArtifact<Label>.InputSchema, artifact.ArtifactId, [5d]);

        Assert.Equal(ClassificationOutcome.Decision, result.Outcome);
        Assert.Equal(Label.Accepted, result.SelectedClass);
        Assert.Equal(1d, result.Scores[0]);
        Assert.Equal(0d, result.Scores[1]);

        var outside = artifact.Classify(
            ClassificationArtifact<Label>.InputSchema, artifact.ArtifactId, [11d]);
        AssertAbstains(outside, ClassificationReason.OutOfDistribution);
        Assert.Empty(outside.Scores);
    }

    [Fact]
    public void Input_validation_precedes_scoring_and_json_is_strict()
    {
        var artifact = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[1d], [-1d]], [0d, 0d]));
        var schemaFailure = artifact.Classify("wrong", artifact.ArtifactId, [double.NaN]);
        AssertFailure(schemaFailure, ClassificationReason.UnsupportedSchema);
        AssertFailure(artifact.Classify(
            ClassificationArtifact<Label>.InputSchema, "wrong", [double.NaN]),
            ClassificationReason.IdentityMismatch);
        AssertFailure(artifact.Classify(
            ClassificationArtifact<Label>.InputSchema, artifact.ArtifactId, [1d, 2d]),
            ClassificationReason.ShapeMismatch);
        AssertFailure(artifact.Classify(
            ClassificationArtifact<Label>.InputSchema, artifact.ArtifactId, [double.NaN]),
            ClassificationReason.NonFinite);
        var mixedInput = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length", "risk"], [[1d, 0d], [-1d, 0d]], [0d, 0d]));
        AssertFailure(mixedInput.Classify(
            ClassificationArtifact<Label>.InputSchema, mixedInput.ArtifactId, [11d, double.NaN]),
            ClassificationReason.NonFinite);

        var validJson = InputJson(artifact.ArtifactId, [8d]);
        Assert.Equal(ClassificationOutcome.Decision, artifact.ClassifyJson(validJson).Outcome);
        AssertFailure(artifact.ClassifyJson(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(validJson).Replace("\"features\":[8", "\"extra\":0,\"features\":[8", StringComparison.Ordinal))),
            ClassificationReason.InvalidInput);
        AssertFailure(artifact.ClassifyJson(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(validJson).Replace(
                "\"features\":[8]", "\"features\":[8],\"features\":[8]", StringComparison.Ordinal))),
            ClassificationReason.InvalidInput);
        AssertFailure(artifact.ClassifyJson(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(validJson).Replace(
                "\"artifactId\":\"" + artifact.ArtifactId + "\"",
                "\"artifactId\":null", StringComparison.Ordinal))),
            ClassificationReason.InvalidInput);
        AssertFailure(artifact.ClassifyJson(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(validJson).Replace("\"features\":[8]", "\"features\":[1e999]", StringComparison.Ordinal))),
            ClassificationReason.NonFinite);
        AssertFailure(artifact.ClassifyJson([0x7B, 0xFF, 0x7D]), ClassificationReason.InvalidInput);
        AssertFailure(artifact.ClassifyJson(new byte[16 * 1024 + 1]), ClassificationReason.PayloadTooLarge);
    }

    [Fact]
    public void Artifact_load_rejects_bad_hash_unknown_duplicate_shape_and_enum_aliases()
    {
        var valid = ArtifactBytes(["Accepted", "Rejected"], ["length"], [[1d], [-1d]], [0d, 0d]);
        Assert.Equal(ClassificationReason.IdentityMismatch,
            ClassificationArtifact<Label>.Load(valid, new string('0', 64)).Reason);

        Assert.Equal(ClassificationReason.InvalidArtifact,
            LoadFailure<Label>(Mutate(valid, json => json.Replace(
                "\"taxonomy\":\"synthetic\"", "\"extra\":0,\"taxonomy\":\"synthetic\"", StringComparison.Ordinal))));
        Assert.Equal(ClassificationReason.InvalidArtifact,
            LoadFailure<Label>(Mutate(valid, json => json.Replace(
                "\"taxonomy\":\"synthetic\"", "\"taxonomy\":\"synthetic\",\"taxonomy\":\"synthetic\"", StringComparison.Ordinal))));
        Assert.Equal(ClassificationReason.InvalidArtifact,
            LoadFailure<Label>(Mutate(valid, json => json.Replace(
                "\"units\":[\"count\"]", "\"units\":null", StringComparison.Ordinal))));
        var deeplyNested = Mutate(valid, json => json.Insert(
            1, "\"extra\":{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":{\"i\":1}}}}}}}}},"));
        Assert.Equal(ClassificationReason.InvalidArtifact, LoadFailure<Label>(deeplyNested));
        var tooManyFeatures = Enumerable.Range(0, 129).Select(static index => $"feature{index}").ToArray();
        var oversizedShape = ArtifactBytes(
            ["Accepted", "Rejected"],
            tooManyFeatures,
            [new double[tooManyFeatures.Length], new double[tooManyFeatures.Length]],
            [0d, 0d]);
        Assert.Equal(ClassificationReason.InvalidArtifact, LoadFailure<Label>(oversizedShape));
        Assert.Equal(
            ClassificationReason.IdentityMismatch,
            ClassificationArtifact<Label>.Load([0x7B, 0xFF, 0x7D], new string('0', 64)).Reason);
        Assert.Equal(ClassificationReason.PayloadTooLarge,
            ClassificationArtifact<Label>.Load(new byte[128 * 1024 + 1], new string('0', 64)).Reason);

        var numericClass = ArtifactBytes(["0", "Rejected"], ["length"], [[1d], [-1d]], [0d, 0d]);
        Assert.Equal(ClassificationReason.InvalidArtifact, LoadFailure<Label>(numericClass));
        var unknownClass = ArtifactBytes(["Missing", "Rejected"], ["length"], [[1d], [-1d]], [0d, 0d]);
        Assert.Equal(ClassificationReason.InvalidArtifact, LoadFailure<Label>(unknownClass));
        var aliasedClass = ArtifactBytes(
            ["First", "Second"], ["length"], [[1d], [-1d]], [0d, 0d]);
        Assert.Equal(
            ClassificationReason.InvalidArtifact,
            ClassificationArtifact<AliasedLabel>.Load(aliasedClass, Sha256(aliasedClass)).Reason);
    }

    [Fact]
    public void Finite_input_that_overflows_a_logit_returns_typed_failure_without_a_decision()
    {
        var artifact = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[2d], [-2d]], [0d, 0d],
            minimums: [-1e308], maximums: [1e308]));
        var result = artifact.Classify(
            ClassificationArtifact<Label>.InputSchema, artifact.ArtifactId, [1e308]);

        AssertFailure(result, ClassificationReason.NumericalFailure);
        Assert.Equal(ClassificationOutcome.ValidationFailure, result.Outcome);
    }

    [Fact]
    public void Slot_uses_explicit_fenced_activation_and_atomic_rollback()
    {
        var first = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[1d], [-1d]], [0d, 0d]));
        var second = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[-1d], [1d]], [0d, 0d]));
        var slot = new ClassificationSlot<Label>();

        Assert.Equal(ClassificationOutcome.Unavailable, slot.Classify(
            ClassificationArtifact<Label>.InputSchema, first.ArtifactId, [2d]).Outcome);
        Assert.Equal(ClassificationReason.None, slot.TryActivate(first, expectedCurrentId: null));
        Assert.Equal(ClassificationReason.ArtifactUnavailable, slot.Rollback(first.ArtifactId));
        Assert.Equal(ClassificationReason.UpdateConflict, slot.TryActivate(second, expectedCurrentId: null));
        Assert.Equal(ClassificationReason.None, slot.TryActivate(second, first.ArtifactId));
        Assert.Equal(second.ArtifactId, slot.CurrentArtifactId);
        Assert.Equal(Label.Rejected, slot.Classify(
            ClassificationArtifact<Label>.InputSchema, second.ArtifactId, [2d]).SelectedClass);
        Assert.Equal(ClassificationReason.UpdateConflict, slot.Rollback(first.ArtifactId));
        Assert.Equal(ClassificationReason.None, slot.Rollback(second.ArtifactId));
        Assert.Equal(first.ArtifactId, slot.CurrentArtifactId);
        Assert.Equal(Label.Accepted, slot.Classify(
            ClassificationArtifact<Label>.InputSchema, first.ArtifactId, [2d]).SelectedClass);
    }

    [Fact]
    public async Task Competing_activations_have_one_winner_and_readers_observe_whole_snapshots()
    {
        var first = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[1d], [-1d]], [0d, 0d]));
        var second = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[-1d], [1d]], [0d, 0d]));
        var third = Load<Label>(ArtifactBytes(
            ["Accepted", "Rejected"], ["length"], [[0.5d], [-0.5d]], [0d, 0d]));
        var slot = new ClassificationSlot<Label>();
        Assert.Equal(ClassificationReason.None, slot.TryActivate(first, null));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = Enumerable.Range(0, 4).Select(async _ =>
        {
            await start.Task;
            for (var index = 0; index < 500; index++)
            {
                var result = slot.Classify(
                    ClassificationArtifact<Label>.InputSchema, "not-current", [1d]);
                Assert.Equal(ClassificationReason.IdentityMismatch, result.Reason);
                Assert.NotNull(result.ArtifactId);
                Assert.Contains(result.ArtifactId, new[] { first.ArtifactId, second.ArtifactId, third.ArtifactId });
                Assert.Empty(result.Scores);
            }
        });

        start.SetResult();
        var contenders = await Task.WhenAll(
            Task.Run(() => slot.TryActivate(second, first.ArtifactId)),
            Task.Run(() => slot.TryActivate(third, first.ArtifactId)));
        Assert.Equal(1, contenders.Count(static result => result == ClassificationReason.None));
        Assert.Equal(1, contenders.Count(static result => result == ClassificationReason.UpdateConflict));
        await Task.WhenAll(readers);
    }

    private static byte[] ArtifactBytes(
        string[] classes,
        string[] features,
        double[][] weights,
        double[] biases,
        double minimumScore = 0d,
        double minimumMargin = 0d,
        double[]? minimums = null,
        double[]? maximums = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = ClassificationArtifact<Label>.Schema,
            taxonomy = "synthetic",
            features,
            units = features.Select(_ => "count").ToArray(),
            preprocessing = "identity-f64-v1",
            minimums = minimums ?? features.Select(_ => 0d).ToArray(),
            maximums = maximums ?? features.Select(_ => 10d).ToArray(),
            classes,
            weights,
            biases,
            minimumScore,
            minimumMargin,
        });

    private static byte[] InputJson(string artifactId, double[] features) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = ClassificationArtifact<Label>.InputSchema,
            artifactId,
            features,
        });

    private static ClassificationArtifact<TLabel> Load<TLabel>(byte[] bytes)
        where TLabel : struct, Enum
    {
        var result = ClassificationArtifact<TLabel>.Load(bytes, Sha256(bytes));
        Assert.Equal(ClassificationReason.None, result.Reason);
        return Assert.IsType<ClassificationArtifact<TLabel>>(result.Artifact);
    }

    private static ClassificationReason LoadFailure<TLabel>(byte[] bytes)
        where TLabel : struct, Enum =>
        ClassificationArtifact<TLabel>.Load(bytes, Sha256(bytes)).Reason;

    private static byte[] Mutate(byte[] bytes, Func<string, string> mutation) =>
        Encoding.UTF8.GetBytes(mutation(Encoding.UTF8.GetString(bytes)));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AssertFailure(ClassificationResult<Label> result, ClassificationReason reason)
    {
        Assert.Equal(ClassificationOutcome.ValidationFailure, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.SelectedClass);
        Assert.Empty(result.Scores);
    }

    private static void AssertAbstains(ClassificationResult<Label> result, ClassificationReason reason)
    {
        Assert.Equal(ClassificationOutcome.Abstain, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.SelectedClass);
    }

    private enum Label
    {
        Accepted,
        Rejected,
        Unknown,
    }

    private enum AliasedLabel
    {
        First,
        FirstAlias = First,
        Second,
    }
}
