using System.Globalization;
using System.Text.Json;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Evaluation;

/// <summary>One JSONL artifact as read from disk; <see cref="Path"/> is only used for messages.</summary>
public sealed record ArtifactFile(string Path, string Content)
{
    public static IReadOnlyList<ArtifactFile> LoadDirectory(string directory) =>
        Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(f => new ArtifactFile(System.IO.Path.GetRelativePath(directory, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToList();
}

/// <summary>
/// Scores extractor output against golden artifacts. Artifacts are paired by header identity
/// (repository, commit, unit path), never by file name. Golden artifacts must be valid; actual artifacts
/// that fail the contract are reported and score zero.
/// </summary>
public static class Evaluator
{
    public static async Task<EvaluationReport> EvaluateAsync(
        IReadOnlyList<ArtifactFile> expected,
        IReadOnlyList<ArtifactFile> actual,
        EvaluationConfig config,
        string? repositoriesDirectory = null,
        IReadOnlyCollection<string>? onlyRepositories = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(config);

        var golden = new Dictionary<(string, string, string), (string Label, Artifact Artifact)>();
        foreach (var file in expected)
        {
            var report = ArtifactValidator.Validate(file.Content);
            if (!report.IsValid)
            {
                throw new InvalidOperationException($"golden artifact {file.Path} is invalid: {string.Join("; ", report.Problems.Take(3))}");
            }

            var a = report.Artifact!;
            if (onlyRepositories is { Count: > 0 } && !onlyRepositories.Contains(a.Header.RepositoryId))
            {
                continue;
            }

            if (!golden.TryAdd(Key(a.Header), (file.Path, a)))
            {
                throw new InvalidOperationException($"two golden artifacts for {Describe(a.Header)}");
            }
        }

        var valid = new Dictionary<(string, string, string), (string Path, Artifact Artifact)>();
        var invalid = new Dictionary<(string, string, string), (string Path, IReadOnlyList<Problem> Problems)>();
        var invalidUnpaired = new List<string>();
        foreach (var file in actual)
        {
            var report = ArtifactValidator.Validate(file.Content);
            if (report.IsValid)
            {
                var a = report.Artifact!;
                if (!valid.TryAdd(Key(a.Header), (file.Path, a)))
                {
                    throw new InvalidOperationException($"two actual artifacts for {Describe(a.Header)} ({valid[Key(a.Header)].Path}, {file.Path})");
                }
            }
            else if (TryReadHeaderKey(file.Content) is { } key)
            {
                invalid[key] = (file.Path, report.Problems);
            }
            else
            {
                invalidUnpaired.Add($"{file.Path}: {string.Join("; ", report.Problems.Take(3))}");
            }
        }

        var evaluations = new List<ArtifactEvaluation>();
        foreach (var (key, (label, g)) in golden.OrderBy(kv => kv.Value.Label, StringComparer.Ordinal))
        {
            var h = g.Header;
            if (valid.TryGetValue(key, out var match))
            {
                var evaluation = Compare(label, g, match.Artifact, config.Scope);
                if (repositoriesDirectory is not null)
                {
                    evaluation = await WithEvidenceCheckAsync(evaluation, match.Artifact, repositoriesDirectory, cancellationToken);
                }

                evaluations.Add(evaluation);
            }
            else
            {
                var status = invalid.TryGetValue(key, out var bad) ? PairingStatus.InvalidActual : PairingStatus.MissingActual;
                var problems = status == PairingStatus.InvalidActual
                    ? bad.Problems.Select(p => $"{bad.Path}: {p}").ToList()
                    : [$"no actual artifact for {Describe(h)}"];
                evaluations.Add(new ArtifactEvaluation(label, h.RepositoryId, h.CommitSha, h.Unit.Path, status, null, Unmatched(g), [], problems));
            }
        }

        var unpaired = valid.Where(kv => !golden.ContainsKey(kv.Key)).Select(kv => $"{kv.Value.Path} ({Describe(kv.Value.Artifact.Header)})").Order(StringComparer.Ordinal).ToList();
        invalidUnpaired.AddRange(invalid.Where(kv => !golden.ContainsKey(kv.Key)).Select(kv => $"{kv.Value.Path}: {string.Join("; ", kv.Value.Problems.Take(3))}"));

        var totals = evaluations.Aggregate(Metrics.Empty, (sum, e) => sum + e.Metrics);
        return new EvaluationReport(evaluations, unpaired, invalidUnpaired, totals, CheckThresholds(totals, evaluations, config.Thresholds));
    }

    private static (string, string, string) Key(HeaderRecord h) => (h.RepositoryId, h.CommitSha, h.Unit.Path);

    private static string Describe(HeaderRecord h) => $"{h.RepositoryId}@{h.CommitSha[..12]} {h.Unit.Path}";

    private static (string, string, string)? TryReadHeaderKey(string jsonl)
    {
        try
        {
            var end = jsonl.IndexOf('\n', StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(jsonl.AsMemory(0, end >= 0 ? end : jsonl.Length));
            var r = doc.RootElement;
            return r.GetProperty("kind").GetString() == "header"
                ? (r.GetProperty("repositoryId").GetString()!, r.GetProperty("commitSha").GetString()!, r.GetProperty("unit").GetProperty("path").GetString()!)
                : null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Metrics for a golden artifact with no usable counterpart: every golden fact is a miss.</summary>
    private static Metrics Unmatched(Artifact g)
    {
        var attributes = g.Calculations.Sum(c => c.Raw.GetProperty("attributes").EnumerateObject().Count());
        return new Metrics
        {
            NodeRecall = new(0, g.Nodes.Count),
            EdgeRecall = new(0, g.Edges.Count),
            EdgeAccuracy = new(0, g.Edges.Count),
            EvidenceLocated = new(0, g.Nodes.Count + g.Edges.Count),
            CalculationRecall = new(0, g.Calculations.Count),
            AttributeAccuracy = new(0, attributes),
            IrHashMatch = new(0, g.Calculations.Count),
            Coverage = new(0, 1),
        };
    }

    internal static ArtifactEvaluation Compare(string label, Artifact g, Artifact a, EvaluationScope scope)
    {
        var findings = new List<Finding>();
        var gEvidence = g.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var aEvidence = a.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal);

        bool Located(IEnumerable<string> goldenRefs, IEnumerable<string> actualRefs)
        {
            var actualSpans = actualRefs.Select(r => aEvidence[r]).ToList();
            return goldenRefs.Select(r => gEvidence[r]).Any(ge => actualSpans.Any(ae =>
                ae.Path == ge.Path && ae.Span.StartLine <= ge.Span.EndLine && ge.Span.StartLine <= ae.Span.EndLine));
        }

        // ---- nodes ----
        var actualNodes = a.Nodes.ToDictionary(n => n.SymbolKey, StringComparer.Ordinal);
        int nodeHits = 0, located = 0;
        foreach (var gn in g.Nodes)
        {
            if (!actualNodes.TryGetValue(gn.SymbolKey, out var an))
            {
                findings.Add(new(FindingKind.MissingNode, gn.SymbolKey, gn.NodeKind));
                continue;
            }

            if (an.NodeKind != gn.NodeKind)
            {
                findings.Add(new(FindingKind.NodeKindMismatch, gn.SymbolKey, $"expected {gn.NodeKind}, got {an.NodeKind}"));
                continue;
            }

            nodeHits++;
            if (Located(gn.Evidence, an.Evidence))
            {
                located++;
            }
            else
            {
                findings.Add(new(FindingKind.EvidenceNotLocated, gn.SymbolKey, ExpectedAt(gn.Evidence, gEvidence)));
            }
        }

        // ---- edges ----
        static string Describe(EdgeRecord e) => $"{e.From} -{e.Relation}-> {e.To ?? $"<unresolved:{e.UnresolvedReason}>"}";
        var byTarget = a.Edges.Where(e => e.To is not null).ToLookup(e => (e.From, e.Relation, e.To!));
        var byUnknownTarget = a.Edges.Where(e => e.To is null).ToLookup(e => (e.From, e.Relation));
        int edgeHits = 0, edgeAccurate = 0;
        foreach (var ge in g.Edges)
        {
            var candidates = ge.To is not null ? byTarget[(ge.From, ge.Relation, ge.To)].ToList() : byUnknownTarget[(ge.From, ge.Relation)].ToList();
            if (candidates.Count == 0)
            {
                findings.Add(new(FindingKind.MissingEdge, Describe(ge), ExpectedAt(ge.Evidence, gEvidence)));
                continue;
            }

            // Several actual edges may share a key (one per call site): prefer the one at the golden location, then the most accurate.
            var best = candidates
                .OrderByDescending(c => Located(ge.Evidence, c.Evidence))
                .ThenByDescending(c => (c.Resolution == ge.Resolution ? 4 : 0) + (c.UnresolvedReason == ge.UnresolvedReason ? 2 : 0) + (c.Derivation == ge.Derivation ? 1 : 0))
                .First();
            edgeHits++;
            var accurate = true;
            if (best.Resolution != ge.Resolution)
            {
                accurate = false;
                findings.Add(new(FindingKind.ResolutionMismatch, Describe(ge), $"expected {ge.Resolution}, got {best.Resolution}"));
            }

            if (best.UnresolvedReason != ge.UnresolvedReason)
            {
                accurate = false;
                findings.Add(new(FindingKind.UnresolvedReasonMismatch, Describe(ge), $"expected {ge.UnresolvedReason ?? "none"}, got {best.UnresolvedReason ?? "none"}"));
            }

            if (best.Derivation != ge.Derivation)
            {
                accurate = false;
                findings.Add(new(FindingKind.DerivationMismatch, Describe(ge), $"expected {ge.Derivation}, got {best.Derivation}"));
            }

            edgeAccurate += accurate ? 1 : 0;
            if (Located(ge.Evidence, best.Evidence))
            {
                located++;
            }
            else
            {
                findings.Add(new(FindingKind.EvidenceNotLocated, Describe(ge), ExpectedAt(ge.Evidence, gEvidence)));
            }
        }

        // ---- precision, only where the golden set is exhaustive ----
        var goldenEdgeKeys = g.Edges.Select(e => (e.From, e.Relation, e.To ?? "?")).ToHashSet();
        var goldenNodeKeys = g.Nodes.Select(n => n.SymbolKey).ToHashSet(StringComparer.Ordinal);
        int inScope = 0, inScopeHits = 0;
        foreach (var ae in a.Edges)
        {
            var fromKind = actualNodes.TryGetValue(ae.From, out var fromNode) ? fromNode.NodeKind : null;
            if (!scope.ExhaustiveRelations.Any(r => r.Covers(ae.Relation, fromKind)))
            {
                continue;
            }

            inScope++;
            if (goldenEdgeKeys.Contains((ae.From, ae.Relation, ae.To ?? "?")))
            {
                inScopeHits++;
            }
            else
            {
                findings.Add(new(FindingKind.UnexpectedFact, Describe(ae), "not in golden set (category is exhaustive)"));
            }
        }

        foreach (var an in a.Nodes.Where(n => scope.ExhaustiveNodeKinds.Contains(n.NodeKind)))
        {
            inScope++;
            if (goldenNodeKeys.Contains(an.SymbolKey))
            {
                inScopeHits++;
            }
            else
            {
                findings.Add(new(FindingKind.UnexpectedFact, an.SymbolKey, $"{an.NodeKind} not in golden set (category is exhaustive)"));
            }
        }

        // ---- calculations ----
        var actualCalcs = a.Calculations.ToDictionary(c => c.SymbolKey, StringComparer.Ordinal);
        int calcHits = 0, attrHits = 0, attrTotal = 0, irHits = 0;
        foreach (var gc in g.Calculations)
        {
            var gAttrs = gc.Raw.GetProperty("attributes").EnumerateObject().ToList();
            attrTotal += gAttrs.Count;
            if (!actualCalcs.TryGetValue(gc.SymbolKey, out var ac))
            {
                findings.Add(new(FindingKind.MissingCalculation, gc.SymbolKey, $"{gAttrs.Count} attributes not evaluated"));
                continue;
            }

            calcHits++;
            var aAttrs = ac.Raw.GetProperty("attributes");
            foreach (var attr in gAttrs)
            {
                var finding = CompareAttribute(gc.SymbolKey, attr.Name, attr.Value, aAttrs.TryGetProperty(attr.Name, out var actualAttr) ? actualAttr : null);
                if (finding is null)
                {
                    attrHits++;
                }
                else
                {
                    findings.Add(finding);
                }
            }

            var gHash = gc.Raw.TryGetProperty("irHash", out var gh) ? gh.GetString() : null;
            var aHash = ac.Raw.TryGetProperty("irHash", out var ah) ? ah.GetString() : null;
            if (gHash is not null && gHash == aHash)
            {
                irHits++;
            }
            else
            {
                findings.Add(new(FindingKind.IrHashMismatch, gc.SymbolKey, aHash is null ? "actual has no irHash" : "normalised expression differs"));
            }
        }

        // ---- coverage and diagnostics ----
        var coverageOk = true;
        if (g.Complete.Status != a.Complete.Status)
        {
            coverageOk = false;
            findings.Add(new(FindingKind.CoverageMismatch, "complete.status", $"expected {g.Complete.Status}, got {a.Complete.Status}"));
        }

        var gIncomplete = (g.Complete.Coverage.IncompletePaths ?? []).Order(StringComparer.Ordinal).ToList();
        var aIncomplete = (a.Complete.Coverage.IncompletePaths ?? []).Order(StringComparer.Ordinal).ToList();
        if (!gIncomplete.SequenceEqual(aIncomplete))
        {
            coverageOk = false;
            findings.Add(new(FindingKind.CoverageMismatch, "coverage.incompletePaths", $"expected [{string.Join(", ", gIncomplete)}], got [{string.Join(", ", aIncomplete)}]"));
        }

        foreach (var gd in g.Diagnostics.Where(d => !a.Diagnostics.Any(x => x.Code == d.Code && x.Path == d.Path)))
        {
            coverageOk = false;
            findings.Add(new(FindingKind.MissingDiagnostic, $"{gd.Code} {gd.Path}", gd.Message));
        }

        var metrics = new Metrics
        {
            NodeRecall = new(nodeHits, g.Nodes.Count),
            EdgeRecall = new(edgeHits, g.Edges.Count),
            EdgeAccuracy = new(edgeAccurate, g.Edges.Count),
            EvidenceLocated = new(located, g.Nodes.Count + g.Edges.Count),
            CalculationRecall = new(calcHits, g.Calculations.Count),
            AttributeAccuracy = new(attrHits, attrTotal),
            IrHashMatch = new(irHits, g.Calculations.Count),
            Precision = new(inScopeHits, inScope),
            Coverage = new(coverageOk ? 1 : 0, 1),
        };
        var h = g.Header;
        return new ArtifactEvaluation(label, h.RepositoryId, h.CommitSha, h.Unit.Path, PairingStatus.Evaluated, $"{a.Header.Extractor.Name} {a.Header.Extractor.Version}", metrics, findings, []);
    }

    /// <summary>
    /// Golden known → actual must be known with the same value. Golden partial → partial or known, same value.
    /// Golden unknown → actual must not claim a value: the reference says the code holds no evidence for one.
    /// </summary>
    private static Finding? CompareAttribute(string symbol, string name, JsonElement golden, JsonElement? actual)
    {
        var subject = $"{symbol} :: {name}";
        var gStatus = golden.GetProperty("status").GetString();
        var aStatus = actual?.GetProperty("status").GetString() ?? "unknown";
        var gValue = golden.TryGetProperty("value", out var gv) ? Scalar(gv) : null;
        var aValue = actual is { } act && act.TryGetProperty("value", out var av) ? Scalar(av) : null;

        if (gStatus == "unknown")
        {
            return aStatus == "unknown" ? null : new(FindingKind.AttributeFabricated, subject, $"golden has no evidence; actual claims {aStatus} '{aValue}'");
        }

        if (aStatus == "unknown")
        {
            return new(FindingKind.AttributeMissed, subject, $"expected {gStatus} '{gValue}'");
        }

        if (gStatus == "known" && aStatus != "known")
        {
            return new(FindingKind.AttributeWrong, subject, $"expected known '{gValue}', got {aStatus} '{aValue}'");
        }

        return gValue == aValue ? null : new(FindingKind.AttributeWrong, subject, $"expected '{gValue}', got '{aValue}'");
    }

    private static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        JsonValueKind.Null => null,
        _ => e.GetRawText(),
    };

    private static string ExpectedAt(IReadOnlyList<string> refs, Dictionary<string, EvidenceRecord> evidence) =>
        "expected at " + string.Join(", ", refs.Select(r => $"{evidence[r].Path}:{evidence[r].Span.StartLine}"));

    private static async Task<ArtifactEvaluation> WithEvidenceCheckAsync(ArtifactEvaluation evaluation, Artifact actual, string repositoriesDirectory, CancellationToken cancellationToken)
    {
        var repo = Path.Combine(repositoriesDirectory, actual.Header.RepositoryId);
        IReadOnlyList<Problem> problems = Directory.Exists(repo)
            ? await new EvidenceVerifier(repo).VerifyAsync(actual, cancellationToken)
            : [new Problem(0, $"repository directory {repo} not found")];
        if (problems.Count == 0)
        {
            return evaluation;
        }

        return evaluation with
        {
            Metrics = evaluation.Metrics with { EvidenceErrors = problems.Count },
            Findings = [.. evaluation.Findings, .. problems.Select(p => new Finding(FindingKind.EvidenceError, p.Message, ""))],
        };
    }

    private static List<string> CheckThresholds(Metrics totals, IReadOnlyList<ArtifactEvaluation> evaluations, Thresholds t)
    {
        var failures = new List<string>();
        void Check(string name, double? min, Ratio actual)
        {
            if (min is { } m && actual.Value < m)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {actual} is below {m * 100:0.0}%"));
            }
        }

        Check("nodeRecall", t.NodeRecall, totals.NodeRecall);
        Check("edgeRecall", t.EdgeRecall, totals.EdgeRecall);
        Check("edgeAccuracy", t.EdgeAccuracy, totals.EdgeAccuracy);
        Check("evidenceLocated", t.EvidenceLocated, totals.EvidenceLocated);
        Check("calculationRecall", t.CalculationRecall, totals.CalculationRecall);
        Check("attributeAccuracy", t.AttributeAccuracy, totals.AttributeAccuracy);
        Check("irHashMatch", t.IrHashMatch, totals.IrHashMatch);
        Check("precision", t.Precision, totals.Precision);
        Check("coverage", t.Coverage, totals.Coverage);
        if (t.MaxEvidenceErrors is { } max && totals.EvidenceErrors > max)
        {
            failures.Add($"evidenceErrors {totals.EvidenceErrors} exceeds {max}");
        }

        if (t.RequireAllArtifacts && evaluations.Any(e => e.Status != PairingStatus.Evaluated))
        {
            failures.Add($"{evaluations.Count(e => e.Status != PairingStatus.Evaluated)} golden artifact(s) have no valid actual artifact");
        }

        return failures;
    }
}
