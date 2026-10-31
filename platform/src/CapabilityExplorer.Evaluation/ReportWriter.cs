using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapabilityExplorer.Evaluation;

public static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new RatioConverter() },
    };

    public static string ToJson(EvaluationReport report) => JsonSerializer.Serialize(report, JsonOptions);

    /// <param name="maxFindings">Per artifact; findings beyond this are summarised by kind.</param>
    public static void WriteText(EvaluationReport report, TextWriter output, int maxFindings = 40)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        foreach (var e in report.Artifacts)
        {
            var tag = e.Status switch
            {
                PairingStatus.Evaluated when e.Findings.Count == 0 => "PASS",
                PairingStatus.Evaluated => "DIFF",
                PairingStatus.MissingActual => "MISSING",
                _ => "INVALID",
            };
            output.WriteLine($"{tag,-8} {e.Label}  ({e.RepositoryId}@{e.CommitSha[..12]} {e.UnitPath}{(e.ActualExtractor is null ? "" : $", {e.ActualExtractor}")})");
            foreach (var p in e.Problems.Take(maxFindings))
            {
                output.WriteLine($"           {p}");
            }

            if (e.Status != PairingStatus.Evaluated || e.Findings.Count == 0)
            {
                continue;
            }

            WriteMetrics(output, e.Metrics, "           ");
            foreach (var f in e.Findings.Take(maxFindings))
            {
                output.WriteLine($"           - {f}");
            }

            if (e.Findings.Count > maxFindings)
            {
                var rest = e.Findings.Skip(maxFindings).GroupBy(f => f.Kind).Select(g => $"{g.Count()} {g.Key}");
                output.WriteLine($"           … and {e.Findings.Count - maxFindings} more: {string.Join(", ", rest)}");
            }
        }

        foreach (var u in report.UnpairedActual)
        {
            output.WriteLine($"EXTRA    {u} — no golden counterpart, not scored");
        }

        foreach (var u in report.InvalidUnpairedActual)
        {
            output.WriteLine($"INVALID  {u}");
        }

        output.WriteLine();
        output.WriteLine($"TOTAL    {report.Artifacts.Count(a => a.Status == PairingStatus.Evaluated)}/{report.Artifacts.Count} golden artifacts evaluated");
        WriteMetrics(output, report.Totals, "         ");
        output.WriteLine();
        if (report.Passed)
        {
            output.WriteLine("thresholds: passed");
        }
        else
        {
            output.WriteLine("thresholds: FAILED");
            foreach (var f in report.ThresholdFailures)
            {
                output.WriteLine($"  - {f}");
            }
        }
    }

    private static void WriteMetrics(TextWriter output, Metrics m, string indent)
    {
        output.WriteLine($"{indent}nodes {m.NodeRecall}  edges {m.EdgeRecall}  edge accuracy {m.EdgeAccuracy}  evidence located {m.EvidenceLocated}");
        output.WriteLine($"{indent}calculations {m.CalculationRecall}  attributes {m.AttributeAccuracy}  irHash {m.IrHashMatch}");
        output.WriteLine($"{indent}precision (exhaustive categories) {m.Precision}  coverage {m.Coverage}  evidence errors {m.EvidenceErrors}");
    }

    private sealed class RatioConverter : JsonConverter<Ratio>
    {
        public override Ratio Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Ratio value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("hit", value.Hit);
            writer.WriteNumber("total", value.Total);
            writer.WriteNumber("value", Math.Round(value.Value, 4));
            writer.WriteEndObject();
        }
    }
}
