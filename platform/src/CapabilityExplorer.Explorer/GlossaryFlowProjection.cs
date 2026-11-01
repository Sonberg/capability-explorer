using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

public static partial class GlossaryFlow
{
    private sealed partial class Builder
    {
        private readonly Dictionary<(string Repo, string Sha, string Unit, string Key), string> sourceIds = [];
        private readonly Dictionary<string, List<(LoadedArtifact Artifact, NodeRecord Node, string Id)>> declarations = new(StringComparer.Ordinal);

        public Builder(ViewIndex view)
        {
            this.view = view;
            foreach (var artifact in view.Artifacts.OrderBy(a => a.RepositoryId, StringComparer.Ordinal)
                         .ThenBy(a => a.CommitSha, StringComparer.Ordinal).ThenBy(a => a.Artifact.Header.Unit.Path, StringComparer.Ordinal))
            {
                foreach (var group in artifact.Artifact.Nodes.GroupBy(n => n.SymbolKey, StringComparer.Ordinal))
                {
                    var node = group.OrderBy(n => n.NodeKind, StringComparer.Ordinal).ThenBy(n => n.Name, StringComparer.Ordinal)
                        .ThenBy(n => n.Id, StringComparer.Ordinal).First();
                    var id = SourceId(artifact, node.SymbolKey);
                    sourceIds[(artifact.RepositoryId, artifact.CommitSha, artifact.Artifact.Header.Unit.Path, node.SymbolKey)] = id;
                    if (!declarations.TryGetValue(node.SymbolKey, out var declared)) declarations[node.SymbolKey] = declared = [];
                    declared.Add((artifact, node, id));
                    var evidence = Evidence(artifact, group.SelectMany(n => n.Evidence));
                    nodes[id] = new(id, artifact.RepositoryId, artifact.CommitSha, artifact.Artifact.Header.Unit.Name,
                        artifact.Artifact.Header.Unit.Path, node.SymbolKey, [], evidence);
                    canvas[id] = new(id, node.NodeKind.ToLowerInvariant(), node.Name,
                        $"{artifact.RepositoryId} · {artifact.CommitSha} · {artifact.Artifact.Header.Unit.Path}",
                        artifact.RepositoryId, node.SymbolKey, artifact.RepositoryId, null, ["source context"]);
                }
                if (artifact.Artifact.Complete.Status != "full" ||
                    artifact.Artifact.Complete.Coverage.FilesAnalyzed < artifact.Artifact.Complete.Coverage.FilesTotal)
                    globalReasons.Add(new("partial-analysis", $"{artifact.RepositoryId}@{artifact.CommitSha}/{artifact.Artifact.Header.Unit.Path} has incomplete analysis."));
                if (artifact.Artifact.Complete.Coverage.UnresolvedEdges > 0)
                    globalReasons.Add(new("unresolved-analysis", $"{artifact.RepositoryId}@{artifact.CommitSha}/{artifact.Artifact.Header.Unit.Path} reports unresolved edges."));
                foreach (var diagnostic in artifact.Artifact.Diagnostics.Where(d => d.AffectsCoverage == true))
                    globalReasons.Add(new("analysis-diagnostic", $"{artifact.RepositoryId}@{artifact.CommitSha}: {diagnostic.Code}: {diagnostic.Message}"));
            }
            foreach (var problem in view.Index.Problems.Order(StringComparer.Ordinal))
                globalReasons.Add(new("unavailable-artifact", problem));
            foreach (var occurrence in Glossary.Build(view).SelectMany(c => c.Occurrences))
            {
                var key = (occurrence.RepositoryId, occurrence.CommitSha, occurrence.UnitPath, occurrence.Key);
                if (!sourceIds.TryGetValue(key, out var id)) continue;
                occurrenceNodes[occurrence.Id] = id;
                nodes[id] = nodes[id] with { OccurrenceIds = nodes[id].OccurrenceIds.Append(occurrence.Id).Order(StringComparer.Ordinal).ToList() };
            }
        }

        public void AddSourceEdges()
        {
            foreach (var source in view.Edges)
            {
                var original = source.Record;
                Add(source.Artifact, original, "source-navigation");
                if (view.BoundFor(source) is { } bound)
                    Add(source.Artifact, bound.Edge, "service-binding", bound.Binding.Hint, bound.Binding.Target, bound.Binding.Note,
                        originalRecordId: original.Id);
            }
        }

        private void Add(LoadedArtifact artifact, EdgeRecord record, string category,
            string? hint = null, string? target = null, string? note = null, string? sourceRecordId = null, string? inputRef = null,
            string? originalRecordId = null)
        {
            var from = Resolve(artifact, record.From, false, out var fromGap);
            // Static or unresolved targets remain visible boundary nodes. They cannot bridge declared symbols.
            var to = Resolve(artifact, record.To ?? $"dynamic:{record.Id}", category == "service-binding", out var toGap,
                record.Resolution is "resolved" or "static-target");
            var id = Identity("flow-edge:", artifact.RepositoryId, artifact.CommitSha, artifact.Artifact.Header.Unit.Path, category, record.Id);
            edges[id] = new(id, from, to, category, record.Relation, artifact.RepositoryId, artifact.CommitSha,
                artifact.Artifact.Header.Unit.Path, sourceRecordId ?? record.Id, record.Resolution, record.Derivation,
                record.Rule is null ? null : $"{record.Rule.Name} {record.Rule.Version}", record.UnresolvedReason,
                Evidence(artifact, record.Evidence), hint, target, note, InputRef: inputRef, OriginalRecordId: originalRecordId);
            var gaps = new List<GlossaryFlowReason>();
            if (fromGap is not null) gaps.Add(new(fromGap, "The relation source has no unambiguous declaration in the selected source unit.", from, id));
            if (toGap is not null) gaps.Add(new(toGap, "The relation target is a boundary: no unambiguous selected declaration is established.", to, id));
            if (record.Resolution != "resolved")
                gaps.Add(new("unresolved-relation", $"{record.Relation} has {record.Resolution} resolution ({record.UnresolvedReason ?? "no stronger resolution"}).", to, id));
            edgeReasons[id] = gaps;
        }

        private string Resolve(LoadedArtifact artifact, string key, bool binding, out string? gap, bool allowTarget = true)
        {
            gap = null;
            var coordinate = (artifact.RepositoryId, artifact.CommitSha, artifact.Artifact.Header.Unit.Path, key);
            if (allowTarget && sourceIds.TryGetValue(coordinate, out var local)) return local;
            var candidates = new List<(LoadedArtifact Artifact, NodeRecord Node, string Id)>();
            if (allowTarget && declarations.TryGetValue(key, out var declared))
            {
                // Repository-aware exact keys permit explicit cross-repository references. Unbound/shared
                // resource names never establish cross-repository identity on their own.
                var owner = SymbolKey.OwningRepository(key);
                if (owner is null && key.StartsWith("http|", StringComparison.Ordinal))
                    owner = key.Split('|')[1].Split('/')[0];
                candidates.AddRange(declared.Where(d => binding || d.Artifact.RepositoryId == artifact.RepositoryId ||
                    (owner is not null && owner == d.Artifact.RepositoryId)));
            }
            if (binding && allowTarget && candidates.Count == 0)
            {
                // A declared unbound resource may be named by the same explicit service-map mapping.
                foreach (var group in declarations)
                {
                    if (Rewrite(group.Key) == key) candidates.AddRange(group.Value);
                }
            }
            candidates = candidates.DistinctBy(c => c.Id).ToList();
            if (candidates.Count == 1) return candidates[0].Id;
            gap = candidates.Count > 1 ? "ambiguous-target" : binding ? "binding-boundary" : "unsupported-boundary";
            var id = Identity("flow-boundary:", artifact.RepositoryId, artifact.CommitSha, artifact.Artifact.Header.Unit.Path, key,
                binding ? "binding" : "source");
            if (!nodes.ContainsKey(id))
            {
                nodes[id] = new(id, artifact.RepositoryId, artifact.CommitSha, artifact.Artifact.Header.Unit.Name,
                    artifact.Artifact.Header.Unit.Path, key, [], []);
                canvas[id] = new(id, "unmatched", Map.DisplayName(key), $"{gap} · {artifact.RepositoryId} · {artifact.CommitSha}",
                    artifact.RepositoryId, key, artifact.RepositoryId, null, ["boundary", gap]);
            }
            return id;
        }

        private string? Rewrite(string key)
        {
            var parts = key.Split('|');
            for (var i = 1; i < parts.Length; i++)
            {
                if (view.Index.Bindings.Entries.FirstOrDefault(b => b.Hint == parts[i]) is not { } entry) continue;
                parts[i] = entry.Target;
                return string.Join('|', parts);
            }
            return null;
        }

        public void AddCalculations()
        {
            foreach (var artifact in view.Artifacts)
            {
                foreach (var calculation in artifact.Artifact.Calculations)
                {
                    foreach (var input in calculation.Raw.GetProperty("inputs").EnumerateArray())
                    {
                        if (!input.TryGetProperty("origin", out var origin) || origin.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                        var key = origin.GetString()!;
                        var evidence = calculation.Raw.TryGetProperty("evidence", out var cited)
                            ? cited.EnumerateArray().Select(e => e.GetString()!).ToList() : [];
                        Add(artifact, new EdgeRecord(Identity("calculation-input:", calculation.Id, input.GetProperty("ref").GetString()!),
                            key, "CALCULATION_INPUT", calculation.SymbolKey, "calculation-record", "resolved", null, null, null, evidence),
                            "calculation-input", sourceRecordId: calculation.Id, inputRef: input.GetProperty("ref").GetString());
                    }
                }
            }
        }

        public void AddHumanAssertions(IReadOnlyList<GlossaryFlowHumanAssertion> assertions)
        {
            foreach (var assertion in assertions.OrderBy(a => a.TermId, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal))
            {
                if (assertion.Kind is not ("same" or "alias" or "derived_from") || string.IsNullOrWhiteSpace(assertion.TermId) ||
                    string.IsNullOrWhiteSpace(assertion.Id) || string.IsNullOrWhiteSpace(assertion.TermName))
                    throw new ArgumentException("Human assertions require IDs, a term name and same, alias or derived_from kind.", nameof(assertions));
                if (!occurrenceNodes.TryGetValue(assertion.OccurrenceId, out var occurrenceNode))
                {
                    globalReasons.Add(new("unavailable-human-occurrence", $"Review {assertion.Id} names an occurrence outside the selected revisions."));
                    continue;
                }
                var hub = Identity("flow-term:", assertion.TermId);
                if (canvas.TryGetValue(hub, out var existing) && existing.Label != assertion.TermName)
                    throw new ArgumentException("A reviewed term ID has conflicting names.", nameof(assertions));
                nodes[hub] = new(hub, null, null, null, null, null, [], []);
                canvas[hub] = new(hub, "reviewed-term", assertion.TermName, "Human reviewed term", "Reviewed terms", null, null, null, ["human assertion"]);
                var from = assertion.Kind == "derived_from" ? occurrenceNode : hub;
                var to = assertion.Kind == "derived_from" ? hub : occurrenceNode;
                var id = Identity("flow-human:", assertion.Id);
                var edge = new GlossaryFlowEdge(id, from, to, "human-assertion", assertion.Kind, null, null, null, null,
                    "human-asserted", "human", null, null, [], AssertionId: assertion.Id, TermId: assertion.TermId,
                    Reviewer: assertion.Reviewer, Rationale: assertion.Rationale, OccurrenceId: assertion.OccurrenceId);
                if (edges.TryGetValue(id, out var prior) && prior != edge)
                    throw new ArgumentException("Duplicate human assertion IDs conflict.", nameof(assertions));
                edges[id] = edge;
            }
        }

        private static string SourceId(LoadedArtifact artifact, string key) => Identity("flow-source:",
            artifact.RepositoryId, artifact.CommitSha, artifact.Artifact.Header.Unit.Path, key);

        private static List<EvidenceDto> Evidence(LoadedArtifact artifact, IEnumerable<string> ids) => Map.Evidence(artifact, ids)
            .Distinct().OrderBy(e => e.RepositoryId, StringComparer.Ordinal).ThenBy(e => e.CommitSha, StringComparer.Ordinal)
            .ThenBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.StartLine).ThenBy(e => e.StartColumn)
            .ThenBy(e => e.EndLine).ThenBy(e => e.EndColumn).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }
}
