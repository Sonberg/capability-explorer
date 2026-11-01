using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

/// <summary>
/// Read-only concept projection over the selected artifacts (MADR-0005): fold representations, nest attributes,
/// score with explainable features. Deterministic: no model, no database, ordinal ordering throughout.
/// </summary>
public static class GlossaryConcepts
{
    private static readonly Regex CsOwner = new(@"^(cs\|[^|]*\|[^|]*\|)[PFM]:([^(]*)\.\w+(\(.*)?$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ConceptKinds = new(StringComparer.Ordinal) { "Type", "Table", "MessageContract" };
    private static readonly HashSet<string> AttributeKinds = new(StringComparer.Ordinal) { "Property", "Field", "Column" };
    private static readonly string[] TierOrder = ["core", "supporting", "plumbing"];

    private sealed record Decl(LoadedArtifact Artifact, NodeRecord Node, string EvidencePath)
    {
        public string Repo => Artifact.RepositoryId;
        public string UnitPath => Artifact.Artifact.Header.Unit.Path;
        public string Key => Node.SymbolKey;
    }

    private sealed class Group
    {
        public List<Decl> Members { get; } = [];
        public List<Decl> Variants { get; } = [];
    }

    private sealed class AttributeAcc(string name, IReadOnlyList<EvidenceDto> evidence)
    {
        public string Name { get; } = name;
        public IReadOnlyList<EvidenceDto> Evidence { get; } = evidence;
        public int Declarations { get; set; }
    }

    public static GlossaryConceptView Build(
        ViewIndex view, IReadOnlyDictionary<string, IReadOnlyList<string>>? membership = null, GlossaryWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        weights ??= GlossaryWeights.Default;
        var artifacts = view.Artifacts.OrderBy(a => a.RepositoryId, StringComparer.Ordinal)
            .ThenBy(a => a.CommitSha, StringComparer.Ordinal)
            .ThenBy(a => a.Artifact.Header.Unit.Path, StringComparer.Ordinal).ToList();

        // Every node, first occurrence by (repository, revision, unit path). Used for lookups; eligibility is applied below.
        var all = new Dictionary<string, Decl>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            foreach (var node in artifact.Artifact.Nodes.OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal))
            {
                all.TryAdd(node.SymbolKey, new Decl(artifact, node, FirstEvidencePath(artifact, node)));
            }
        }

        var declaredBy = new Dictionary<string, string>(StringComparer.Ordinal);
        var implementers = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var registered = new HashSet<string>(StringComparer.Ordinal);
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            foreach (var edge in artifact.Artifact.Edges.Where(e => e.To is not null))
            {
                var to = edge.To!;
                switch (edge.Relation)
                {
                    case "DECLARES":
                        if (!declaredBy.TryGetValue(to, out var current) || string.CompareOrdinal(edge.From, current) < 0)
                        {
                            declaredBy[to] = edge.From;
                        }
                        break;
                    case "IMPLEMENTS" when all.TryGetValue(to, out var target) && target.Node.NodeKind == "Type":
                        if (!implementers.TryGetValue(to, out var set))
                        {
                            implementers[to] = set = new SortedSet<string>(StringComparer.Ordinal);
                        }
                        set.Add(edge.From);
                        break;
                    case "REGISTERS":
                        registered.Add(to);
                        break;
                    case "MAPS_TO":
                        mapped.Add(edge.From);
                        break;
                }
            }
        }

        var (calcInputs, calcIncludes, calcOwners) = Calculations(artifacts, declaredBy);
        var integration = IntegrationFolders(all.Values);

        var excludedTests = 0;
        var conceptDecls = new List<Decl>();
        foreach (var decl in all.Values.Where(d => ConceptKinds.Contains(d.Node.NodeKind) && Glossary.Eligible(d.Node)))
        {
            if (GlossaryVocabulary.IsTestPath(decl.UnitPath, decl.EvidencePath))
            {
                excludedTests++;
            }
            else
            {
                conceptDecls.Add(decl);
            }
        }

        // Variants: implementations of an interface that has two or more implementations fold under the interface.
        var variantOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (iface, impls) in implementers.Where(kv => kv.Value.Count >= 2))
        {
            foreach (var impl in impls)
            {
                if (!variantOf.TryGetValue(impl, out var chosen) || string.CompareOrdinal(iface, chosen) < 0)
                {
                    variantOf[impl] = iface;
                }
            }
        }

        // A model that lives in an integration's folder describes that vendor's shape, not the domain concept of the
        // same name ("Klaviyo/SnoozedRequest" is not the "Snoozed" scheduling event), so it never folds into one.
        string GroupKey(Decl d) => GlossaryVocabulary.StemOf(d.Node).Key +
            (IntegrationOf(d, integration) is { } vendor ? $" @{vendor}" : "");
        var groups = new SortedDictionary<string, Group>(StringComparer.Ordinal);
        foreach (var decl in conceptDecls.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (variantOf.TryGetValue(decl.Key, out var iface) && all.TryGetValue(iface, out var ifaceDecl))
            {
                GroupFor(groups, GroupKey(ifaceDecl)).Variants.Add(decl);
                continue;
            }

            GroupFor(groups, GroupKey(decl)).Members.Add(decl);
        }

        foreach (var key in groups.Where(g => g.Value.Members.Count == 0).Select(g => g.Key).ToList())
        {
            groups.Remove(key);
        }

        var attributes = Attributes(all, declaredBy);
        var memberAttributes = groups.ToDictionary(g => g.Key, g => MergeAttributes(g.Value.Members, attributes), StringComparer.Ordinal);
        var bearing = memberAttributes.Count(g => g.Value.Count > 0);
        var threshold = Math.Max(3, weights.GenericLabelShare * bearing);
        var labelConcepts = memberAttributes.SelectMany(g => g.Value.Keys.Select(label => (label, concept: g.Key)))
            .GroupBy(x => x.label, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.concept).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        bool IsGeneric(string label) => label == "id" || (!label.EndsWith(" id", StringComparison.Ordinal) && labelConcepts[label] >= threshold);

        var concepts = new List<GlossaryConcept>();
        foreach (var (key, group) in groups)
        {
            var members = group.Members;
            var stems = members.Select(m => GlossaryVocabulary.StemOf(m.Node)).ToList();
            var attrs = memberAttributes[key];
            var shape = attrs.Keys.Count(l => !IsGeneric(l));
            var reasons = new List<GlossaryReason>();
            void Feature(string name, string? detail = null) => reasons.Add(new(name, weights.Points(name), detail));
            var keys = members.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
            var persisted = members.Any(m => m.Node.NodeKind == "Table" || mapped.Contains(m.Key));

            if (persisted) Feature("persisted");
            if (members.Any(m => m.Node.NodeKind == "MessageContract" || GlossaryVocabulary.PropIsTrue(m.Node, "messageContract"))) Feature("messageContract");
            var isInput = keys.Overlaps(calcInputs);
            if (isInput) Feature("calculationInput");
            else if (keys.Overlaps(calcIncludes)) Feature("usedByCalculation");
            if (CrossRepo(members, stems, stems[0].Key, persisted) is { } repos) Feature("crossRepo", $"{repos} repositories");
            if (shape >= 2) Feature("dataShape", $"{shape} attributes");
            if (shape >= 4) Feature("wideShape");
            if (group.Variants.Count > 0) Feature("variants", $"{group.Variants.Count} variants");
            if (stems.All(s => s.Role)) Feature("roleSuffix", stems[0].RoleWord);
            if (members.All(m => GlossaryVocabulary.TypeKind(m.Node) == "interface" && implementers.TryGetValue(m.Key, out var impl) && impl.Count == 1))
                Feature("singleImplementationInterface");
            if (group.Variants.Count == 0 && members.All(m => registered.Contains(m.Key) && !attributes.ContainsKey(m.Key)))
                Feature("diRegistered");
            if (members.All(m => m.Node.NodeKind == "Type" && !attributes.ContainsKey(m.Key) &&
                                 calcOwners.Contains(m.Node.Name.Split('.')[^1])))
                Feature("hostsCalculation");

            var hits = members.Select(m => IntegrationOf(m, integration)).ToList();
            var isIntegration = hits.All(h => h is not null);
            if (isIntegration) Feature("integrationModel", hits[0]);
            var isEvent = !isIntegration && members.Any(m => m.Node.NodeKind == "Type" && GlossaryNormalization.Words(m.Node.Name) is { Count: > 0 } w && w[^1] == "event");
            if (isEvent) Feature("domainEvent");
            // An enum is a value set ("ScheduleMode"), not an operation, whatever its first word.
            if (!isEvent && members.All(m => GlossaryVocabulary.TypeKind(m.Node) != "enum" &&
                                             GlossaryVocabulary.BaseWords(m.Node) is { Count: >= 2 } w && GlossaryVocabulary.Verbs.Contains(w[0])))
                Feature("operationName");
            if (stems.All(st => st.Folded)) Feature("representationOnly");
            if (!isIntegration && members.Select(m => FeatureFolder(m, key, integration)).FirstOrDefault(f => f is not null) is { } folder)
                Feature("featureFolder", folder);
            if (!isIntegration && members.All(m => m.UnitPath.Contains("Infrastructure", StringComparison.Ordinal)))
                Feature("infrastructureLayer");
            if (members.All(m => declaredBy.TryGetValue(m.Key, out var owner) && all.TryGetValue(owner, out var o) && o.Node.NodeKind == "Type"))
                Feature("nestedType");
            if (members.All(m => m.Key.StartsWith("ts|", StringComparison.Ordinal) && GlossaryVocabulary.TypeKind(m.Node).Length > 0 &&
                                 !GlossaryVocabulary.PropIsTrue(m.Node, "exported")))
                Feature("moduleLocalType");

            var score = weights.Bias + reasons.Sum(r => r.Points);
            var term = members.Select((m, i) => m.Node.NodeKind == "Table" ? key.Split(" @")[0] : stems[i].Display)
                .OrderBy(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).First() +
                (isIntegration ? $" ({hits[0]})" : "");
            var repositories = Glossary.Sorted(members.Concat(group.Variants).Select(m => m.Repo));
            var capabilities = Glossary.Sorted(repositories.SelectMany(r =>
                membership is not null && membership.TryGetValue(r, out var caps) ? caps : []));
            concepts.Add(new GlossaryConcept(key, term, weights.TierOf(score), score,
                reasons.OrderByDescending(r => Math.Abs(r.Points)).ThenBy(r => r.Feature, StringComparer.Ordinal).ToList(),
                MemberDtos(members, stems), MemberDtos(group.Variants, null),
                attrs.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new GlossaryField(
                    a.Key, a.Value.Name, IsGeneric(a.Key), a.Value.Declarations, a.Value.Evidence)).ToList(),
                repositories, capabilities));
        }

        var metrics = Glossary.Build(view, membership).Where(c => c.Occurrences.Any(o => o.Role == "calculation")).ToList();
        var partial = artifacts.Where(a => a.Artifact.Complete.Status != "full")
            .Select(a => $"{a.RepositoryId}/{a.Artifact.Header.Unit.Path}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var limits = new List<string>();
        if (artifacts.Any(a => a.Artifact.Header.Language is "typescript" or "javascript"))
        {
            limits.Add("TypeScript and JavaScript type members are not part of ce.v1: those concepts have no attributes and no data-shape signal.");
        }

        if (partial.Count > 0)
        {
            limits.Add($"{partial.Count} analysis unit(s) are partial: declarations they could not resolve are missing.");
        }

        return new GlossaryConceptView(weights.Version,
            concepts.OrderBy(c => Array.IndexOf(TierOrder, c.Tier)).ThenByDescending(c => c.Score)
                .ThenBy(c => c.Term, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal).ToList(),
            metrics, excludedTests, partial, limits);
    }

    private static Group GroupFor(SortedDictionary<string, Group> groups, string key)
    {
        if (!groups.TryGetValue(key, out var group))
        {
            groups[key] = group = new Group();
        }

        return group;
    }

    private static string FirstEvidencePath(LoadedArtifact artifact, NodeRecord node) =>
        node.Evidence.Select(id => artifact.EvidenceById.TryGetValue(id, out var e) ? e.Path : null).FirstOrDefault(p => p is not null) ?? "";

    private static (HashSet<string> Inputs, HashSet<string> Includes, HashSet<string> Owners) Calculations(
        IEnumerable<LoadedArtifact> artifacts, Dictionary<string, string> declaredBy)
    {
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        var includes = new HashSet<string>(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        void AddWithOwner(HashSet<string> into, string key)
        {
            into.Add(key);
            if (declaredBy.TryGetValue(key, out var owner))
            {
                into.Add(owner);
            }
            else if (CsOwner.Match(key) is { Success: true } m)
            {
                into.Add($"{m.Groups[1].Value}T:{m.Groups[2].Value}");
            }
        }

        foreach (var calculation in artifacts.SelectMany(a => a.Artifact.Calculations))
        {
            var raw = calculation.Raw;
            if (raw.TryGetProperty("inputs", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var input in list.EnumerateArray())
                {
                    if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.String)
                    {
                        AddWithOwner(inputs, origin.GetString()!);
                    }
                }
            }

            if (raw.TryGetProperty("includes", out var included) && included.ValueKind == JsonValueKind.Array)
            {
                foreach (var key in included.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String))
                {
                    AddWithOwner(includes, key.GetString()!);
                }
            }

            if (OwnerTypeName(calculation.SymbolKey) is { } owner)
            {
                owners.Add(owner);
            }
        }

        return (inputs, includes, owners);
    }

    /// <summary>The simple name of the type declaring a C# method key: cs|repo|unit|M:Ns.Type.Method(args) gives Type.</summary>
    internal static string? OwnerTypeName(string symbolKey)
    {
        var start = symbolKey.IndexOf("|M:", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var text = symbolKey[(start + 3)..];
        if (text.IndexOf('(') is var paren and >= 0)
        {
            text = text[..paren];
        }

        var method = text.LastIndexOf('.');
        if (method < 0)
        {
            return null;
        }

        text = text[..method];
        return text[(text.LastIndexOf('.') + 1)..];
    }

    /// <summary>
    /// Integration = the outermost folder under a project root that holds a "*Client" type
    /// (Shopify/Orders/OrderRestClient.cs gives "shopify"). Types named after a table are not clients.
    /// </summary>
    private static HashSet<string> IntegrationFolders(IEnumerable<Decl> decls)
    {
        var list = decls.ToList();
        var tableStems = list.Where(d => d.Node.NodeKind == "Table").Select(d => GlossaryVocabulary.StemOf(d.Node).Key)
            .ToHashSet(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decl in list.Where(d => d.Node.NodeKind == "Type" && d.EvidencePath.Length > 0))
        {
            if (GlossaryNormalization.Words(decl.Node.Name) is not { Count: > 0 } words || words[^1] != "client" ||
                tableStems.Contains(GlossaryVocabulary.StemOf(decl.Node).Key))
            {
                continue;
            }

            var slash = decl.UnitPath.LastIndexOf('/');
            var projectDir = slash >= 0 ? decl.UnitPath[..slash] : "";
            var relative = projectDir.Length > 0 && decl.EvidencePath.StartsWith(projectDir + "/", StringComparison.Ordinal)
                ? decl.EvidencePath[(projectDir.Length + 1)..] : decl.EvidencePath;
            var segments = relative.Split('/')[..^1].Where(s => !GlossaryVocabulary.StructuralFolders.Contains(s)).ToList();
            if (segments.Count > 0)
            {
                folders.Add(segments[0].ToLowerInvariant());
            }
        }

        return folders;
    }

    private static string? IntegrationOf(Decl decl, HashSet<string> integration) =>
        decl.EvidencePath.Split('/')[..^1].Select(s => s.ToLowerInvariant()).FirstOrDefault(integration.Contains) ??
        GlossaryVocabulary.BaseWords(decl.Node).FirstOrDefault(integration.Contains);

    /// <summary>The folder a declaration is named after (Carts/Models/Cart.cs for "cart"), which marks a feature's own model.</summary>
    private static string? FeatureFolder(Decl decl, string conceptKey, HashSet<string> integration)
    {
        // A convention of one-model-per-feature-folder layouts (C#). TypeScript modules sit in feature folders by
        // default, so there the folder says nothing, and operation folders hold requests, not models.
        var folders = decl.EvidencePath.Split('/')[..^1];
        if (!decl.Key.StartsWith("cs|", StringComparison.Ordinal) || folders.Any(GlossaryVocabulary.OperationFolders.Contains) ||
            decl.UnitPath.Contains("Jobs", StringComparison.Ordinal))
        {
            return null;
        }

        var key = conceptKey.Split(' ');
        foreach (var segment in folders)
        {
            var words = GlossaryNormalization.Words(segment).Select(GlossaryVocabulary.Singular).ToList();
            if (words.Count > 0 && !integration.Contains(segment.ToLowerInvariant()) && !GlossaryVocabulary.Verbs.Contains(words[0]) &&
                !GlossaryVocabulary.GenericFeatureFolders.Contains(words[0]) && words.Count <= key.Length &&
                key.Take(words.Count).Select(GlossaryVocabulary.Singular).SequenceEqual(words))
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>
    /// Repositories that independently declare the concept, or null when it is not a cross-repository signal.
    /// A repository that only holds suffixed copies (a web DTO mirroring an API type) does not count, and neither
    /// does a one-word name that is not persisted ("Data", "Page"): both are name collisions.
    /// </summary>
    private static int? CrossRepo(IReadOnlyList<Decl> members, List<GlossaryVocabulary.Stem> stems, string key, bool persisted)
    {
        var all = members.Select(m => m.Repo).Distinct(StringComparer.Ordinal).ToList();
        var independent = members.Where((_, i) => !stems[i].Folded).Select(m => m.Repo).Distinct(StringComparer.Ordinal).ToList();
        var repos = independent.Count > 0 ? independent : all;
        return repos.Count >= 2 && (key.Contains(' ') || persisted) ? repos.Count : null;
    }

    private static Dictionary<string, Dictionary<string, AttributeAcc>> Attributes(
        IReadOnlyDictionary<string, Decl> all, Dictionary<string, string> declaredBy)
    {
        var result = new Dictionary<string, Dictionary<string, AttributeAcc>>(StringComparer.Ordinal);
        foreach (var decl in all.Values.Where(d => AttributeKinds.Contains(d.Node.NodeKind) && Glossary.Eligible(d.Node)))
        {
            var owner = declaredBy.TryGetValue(decl.Key, out var declared) ? declared
                : decl.Key.LastIndexOf('#') is var hash and > 0 && all.ContainsKey(decl.Key[..hash]) ? decl.Key[..hash] : null;
            var label = GlossaryNormalization.Normalize(decl.Node.Name);
            if (owner is null || label.Length == 0)
            {
                continue;
            }

            if (!result.TryGetValue(owner, out var byLabel))
            {
                result[owner] = byLabel = new Dictionary<string, AttributeAcc>(StringComparer.Ordinal);
            }

            if (!byLabel.TryGetValue(label, out var acc))
            {
                byLabel[label] = acc = new AttributeAcc(decl.Node.Name, Glossary.SortEvidence(Map.Evidence(decl.Artifact, decl.Node.Evidence)));
            }

            acc.Declarations++;
        }

        return result;
    }

    private static Dictionary<string, AttributeAcc> MergeAttributes(
        IEnumerable<Decl> members, Dictionary<string, Dictionary<string, AttributeAcc>> attributes)
    {
        var merged = new Dictionary<string, AttributeAcc>(StringComparer.Ordinal);
        foreach (var member in members.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!attributes.TryGetValue(member.Key, out var own))
            {
                continue;
            }

            foreach (var (label, acc) in own.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (merged.TryGetValue(label, out var existing))
                {
                    existing.Declarations += acc.Declarations;
                }
                else
                {
                    merged[label] = new AttributeAcc(acc.Name, acc.Evidence) { Declarations = acc.Declarations };
                }
            }
        }

        return merged;
    }

    private static List<GlossaryMember> MemberDtos(IEnumerable<Decl> decls, List<GlossaryVocabulary.Stem>? stems) =>
        decls.Select((d, i) => new GlossaryMember(
                "glossary-member:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                    new[] { d.Repo, d.Artifact.CommitSha, d.UnitPath, d.Key }))),
                d.Key, d.Node.Name, d.Node.NodeKind, stems is null ? "variant" : stems[i].Folded ? "representation" : "declaration",
                d.Repo, d.Artifact.CommitSha, d.Artifact.Artifact.Header.Unit.Name, d.UnitPath,
                Glossary.SortEvidence(Map.Evidence(d.Artifact, d.Node.Evidence))))
            .OrderBy(m => m.RepositoryId, StringComparer.Ordinal).ThenBy(m => m.UnitPath, StringComparer.Ordinal)
            .ThenBy(m => m.Key, StringComparer.Ordinal).ToList();
}
