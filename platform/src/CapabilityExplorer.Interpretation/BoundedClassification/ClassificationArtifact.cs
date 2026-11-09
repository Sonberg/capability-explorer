using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace CapabilityExplorer.Interpretation.BoundedClassification;

public sealed class ClassificationArtifact<TLabel>
    where TLabel : struct, Enum
{
    public const string Schema = "ce.classification-artifact.v1";
    public const string InputSchema = "ce.classification-input.v1";

    private const int MaximumArtifactBytes = 128 * 1024;
    private const int MaximumInputBytes = 16 * 1024;
    private const string Preprocessing = "identity-f64-v1";

    private static readonly string[] ArtifactPropertyNames =
    [
        "schema", "taxonomy", "features", "units", "preprocessing", "minimums", "maximums",
        "classes", "weights", "biases", "minimumScore", "minimumMargin",
    ];

    private static readonly string[] InputPropertyNames = ["schema", "artifactId", "features"];

    private readonly ImmutableArray<string> _units;
    private readonly ImmutableArray<double> _minimums;
    private readonly ImmutableArray<double> _maximums;
    private readonly ImmutableArray<ImmutableArray<double>> _weights;
    private readonly ImmutableArray<double> _biases;
    private readonly double _minimumScore;
    private readonly double _minimumMargin;

    private ClassificationArtifact(
        string artifactId,
        ImmutableArray<string> features,
        ImmutableArray<string> units,
        ImmutableArray<TLabel> classes,
        ImmutableArray<double> minimums,
        ImmutableArray<double> maximums,
        ImmutableArray<ImmutableArray<double>> weights,
        ImmutableArray<double> biases,
        double minimumScore,
        double minimumMargin)
    {
        ArtifactId = artifactId;
        Features = features;
        _units = units;
        Classes = classes;
        _minimums = minimums;
        _maximums = maximums;
        _weights = weights;
        _biases = biases;
        _minimumScore = minimumScore;
        _minimumMargin = minimumMargin;
    }

    public string ArtifactId { get; }
    public ImmutableArray<string> Features { get; }
    public ImmutableArray<TLabel> Classes { get; }

    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "The frozen API loads artifacts for a specific enum label type.")]
    public static ClassificationArtifactLoad<TLabel> Load(ReadOnlySpan<byte> utf8, string expectedSha256)
    {
        if (utf8.Length > MaximumArtifactBytes)
        {
            return FailedLoad(ClassificationReason.PayloadTooLarge);
        }

        if (!IsSha256(expectedSha256))
        {
            return FailedLoad(ClassificationReason.IdentityMismatch);
        }

        var actualSha256 = Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            return FailedLoad(ClassificationReason.IdentityMismatch);
        }

        if (!ClassificationJson.TryParse(utf8, MaximumArtifactBytes, out var document, out var parseReason))
        {
            return FailedLoad(parseReason == ClassificationReason.PayloadTooLarge
                ? parseReason
                : ClassificationReason.InvalidArtifact);
        }

        using (document)
        {
            if (!ClassificationJson.TryGetExactProperties(
                    document!.RootElement, ArtifactPropertyNames, out var properties))
            {
                return FailedLoad(ClassificationReason.InvalidArtifact);
            }

            if (!ClassificationJson.TryGetString(properties["schema"], 64, out var schema))
            {
                return FailedLoad(ClassificationReason.InvalidArtifact);
            }

            if (!string.Equals(schema, Schema, StringComparison.Ordinal))
            {
                return FailedLoad(ClassificationReason.UnsupportedSchema);
            }

            if (!ClassificationJson.TryGetString(properties["taxonomy"], 64, out var taxonomy) ||
                !ClassificationJson.IsIdentifier(taxonomy) ||
                !ClassificationJson.TryGetString(properties["preprocessing"], 64, out var preprocessing) ||
                !string.Equals(preprocessing, Preprocessing, StringComparison.Ordinal) ||
                !TryReadFeatures(properties["features"], out var features) ||
                !TryReadUnits(properties["units"], features.Length, out var units) ||
                !ClassificationJson.TryGetFiniteDoubleArray(properties["minimums"], features.Length, out var minimums) ||
                !ClassificationJson.TryGetFiniteDoubleArray(properties["maximums"], features.Length, out var maximums) ||
                !TryReadClasses(properties["classes"], out var classes) ||
                !TryReadWeights(properties["weights"], classes.Length, features.Length, out var weights) ||
                !ClassificationJson.TryGetFiniteDoubleArray(properties["biases"], classes.Length, out var biases) ||
                !TryReadThreshold(properties["minimumScore"], out var minimumScore) ||
                !TryReadThreshold(properties["minimumMargin"], out var minimumMargin))
            {
                return FailedLoad(ClassificationReason.InvalidArtifact);
            }

            for (var index = 0; index < minimums.Length; index++)
            {
                if (minimums[index] > maximums[index])
                {
                    return FailedLoad(ClassificationReason.InvalidArtifact);
                }
            }

            return new ClassificationArtifactLoad<TLabel>(
                new ClassificationArtifact<TLabel>(
                    actualSha256, features, units, classes, minimums, maximums, weights, biases,
                    minimumScore, minimumMargin),
                ClassificationReason.None);
        }
    }

    public ClassificationResult<TLabel> Classify(
        string schema,
        string artifactId,
        ReadOnlySpan<double> features)
    {
        if (!string.Equals(schema, InputSchema, StringComparison.Ordinal))
        {
            return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.UnsupportedSchema);
        }

        if (!string.Equals(artifactId, ArtifactId, StringComparison.Ordinal))
        {
            return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.IdentityMismatch);
        }

        if (features.Length != Features.Length)
        {
            return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.ShapeMismatch);
        }

        for (var index = 0; index < features.Length; index++)
        {
            if (!double.IsFinite(features[index]))
            {
                return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.NonFinite);
            }
        }

        for (var index = 0; index < features.Length; index++)
        {
            if (features[index] < _minimums[index] || features[index] > _maximums[index])
            {
                return Failure(ClassificationOutcome.Abstain, ClassificationReason.OutOfDistribution);
            }
        }

        return Score(features);
    }

    public ClassificationResult<TLabel> ClassifyJson(ReadOnlySpan<byte> utf8)
    {
        if (!ClassificationJson.TryParse(utf8, MaximumInputBytes, out var document, out var parseReason))
        {
            return Failure(ClassificationOutcome.ValidationFailure, parseReason);
        }

        using (document)
        {
            if (!ClassificationJson.TryGetExactProperties(
                    document!.RootElement, InputPropertyNames, out var properties) ||
                !ClassificationJson.TryGetString(properties["schema"], 64, out var schema) ||
                !ClassificationJson.TryGetString(properties["artifactId"], 64, out var artifactId) ||
                properties["features"].ValueKind != JsonValueKind.Array)
            {
                return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.InvalidInput);
            }

            if (!string.Equals(schema, InputSchema, StringComparison.Ordinal))
            {
                return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.UnsupportedSchema);
            }

            if (!string.Equals(artifactId, ArtifactId, StringComparison.Ordinal))
            {
                return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.IdentityMismatch);
            }

            var jsonFeatures = properties["features"];
            if (jsonFeatures.GetArrayLength() != Features.Length)
            {
                return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.ShapeMismatch);
            }

            var parsedFeatures = new double[Features.Length];
            var index = 0;
            foreach (var feature in jsonFeatures.EnumerateArray())
            {
                if (feature.ValueKind != JsonValueKind.Number)
                {
                    return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.InvalidInput);
                }

                if (!ClassificationJson.TryGetFiniteDouble(feature, out parsedFeatures[index]))
                {
                    return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.NonFinite);
                }

                index++;
            }

            return Classify(schema, artifactId, parsedFeatures);
        }
    }

    private static ClassificationArtifactLoad<TLabel> FailedLoad(ClassificationReason reason) => new(null, reason);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryReadFeatures(JsonElement element, out ImmutableArray<string> features)
    {
        features = ImmutableArray<string>.Empty;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 or > 128)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<string>(element.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (!ClassificationJson.TryGetString(item, 64, out var feature) ||
                !ClassificationJson.IsIdentifier(feature) ||
                !seen.Add(feature))
            {
                return false;
            }

            builder.Add(feature);
        }

        features = builder.MoveToImmutable();
        return true;
    }

    private static bool TryReadUnits(JsonElement element, int featureCount, out ImmutableArray<string> units)
    {
        units = ImmutableArray<string>.Empty;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != featureCount)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<string>(featureCount);
        foreach (var item in element.EnumerateArray())
        {
            if (!ClassificationJson.TryGetString(item, 64, out var unit) ||
                !ClassificationJson.IsSafeAscii(unit))
            {
                return false;
            }

            builder.Add(unit);
        }

        units = builder.MoveToImmutable();
        return true;
    }

    private static bool TryReadClasses(JsonElement element, out ImmutableArray<TLabel> classes)
    {
        classes = ImmutableArray<TLabel>.Empty;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 2 or > 32)
        {
            return false;
        }

        var enumNames = Enum.GetNames<TLabel>();
        var aliasedValues = enumNames
            .Select(static name => Enum.Parse<TLabel>(name, ignoreCase: false))
            .GroupBy(static value => value)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet();
        var allowedNames = enumNames.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<TLabel>();
        var builder = ImmutableArray.CreateBuilder<TLabel>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (!ClassificationJson.TryGetString(item, 64, out var name) ||
                !allowedNames.Contains(name) ||
                !Enum.TryParse<TLabel>(name, ignoreCase: false, out var label) ||
                aliasedValues.Contains(label) ||
                !seen.Add(label))
            {
                return false;
            }

            builder.Add(label);
        }

        classes = builder.MoveToImmutable();
        return true;
    }

    private static bool TryReadWeights(
        JsonElement element,
        int classCount,
        int featureCount,
        out ImmutableArray<ImmutableArray<double>> weights)
    {
        weights = ImmutableArray<ImmutableArray<double>>.Empty;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != classCount)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<ImmutableArray<double>>(classCount);
        foreach (var row in element.EnumerateArray())
        {
            if (!ClassificationJson.TryGetFiniteDoubleArray(row, featureCount, out var parsedRow))
            {
                return false;
            }

            builder.Add(parsedRow);
        }

        weights = builder.MoveToImmutable();
        return true;
    }

    private static bool TryReadThreshold(JsonElement element, out double threshold)
    {
        threshold = default;
        return ClassificationJson.TryGetFiniteDouble(element, out threshold) &&
            threshold is >= 0 and <= 1;
    }

    private ClassificationResult<TLabel> Score(ReadOnlySpan<double> features)
    {
        var logits = new double[Classes.Length];
        for (var classIndex = 0; classIndex < Classes.Length; classIndex++)
        {
            var logit = _biases[classIndex];
            for (var featureIndex = 0; featureIndex < features.Length; featureIndex++)
            {
                var product = features[featureIndex] * _weights[classIndex][featureIndex];
                if (!double.IsFinite(product))
                {
                    return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.NumericalFailure);
                }

                logit += product;
                if (!double.IsFinite(logit))
                {
                    return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.NumericalFailure);
                }
            }

            logits[classIndex] = logit;
        }

        var maximumLogit = logits.Max();
        var scores = new double[logits.Length];
        var denominator = 0d;
        for (var index = 0; index < logits.Length; index++)
        {
            var shifted = logits[index] - maximumLogit;
            if (!double.IsFinite(shifted))
            {
                return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.NumericalFailure);
            }

            var exponential = Math.Exp(shifted);
            if (!double.IsFinite(exponential))
            {
                return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.NumericalFailure);
            }

            scores[index] = exponential;
            denominator += exponential;
            if (!double.IsFinite(denominator))
            {
                return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.NumericalFailure);
            }
        }

        if (denominator <= 0)
        {
            return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.NumericalFailure);
        }

        var winner = 0;
        var secondScore = -1d;
        for (var index = 0; index < scores.Length; index++)
        {
            scores[index] /= denominator;
            if (!double.IsFinite(scores[index]))
            {
                return Failure(ClassificationOutcome.ValidationFailure, ClassificationReason.NumericalFailure);
            }

            if (index == 0)
            {
                continue;
            }

            if (scores[index] > scores[winner])
            {
                secondScore = scores[winner];
                winner = index;
            }
            else if (scores[index] > secondScore)
            {
                secondScore = scores[index];
            }
        }

        var immutableScores = ImmutableArray.CreateRange(scores);
        if (scores[winner] < _minimumScore)
        {
            return new ClassificationResult<TLabel>(
                ClassificationOutcome.Abstain, ClassificationReason.LowScore, null, immutableScores, ArtifactId);
        }

        if (scores[winner] - secondScore < _minimumMargin)
        {
            return new ClassificationResult<TLabel>(
                ClassificationOutcome.Abstain, ClassificationReason.LowMargin, null, immutableScores, ArtifactId);
        }

        return new ClassificationResult<TLabel>(
            ClassificationOutcome.Decision, ClassificationReason.None, Classes[winner], immutableScores, ArtifactId);
    }

    private ClassificationResult<TLabel> Failure(ClassificationOutcome outcome, ClassificationReason reason) =>
        new(outcome, reason, null, ImmutableArray<double>.Empty, ArtifactId);
}
