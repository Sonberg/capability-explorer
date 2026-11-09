using System.Security.Cryptography;
using System.Text.Json;
using CapabilityExplorer.Explorer;

namespace CapabilityExplorer.Tests;

internal static class OwnershipTestInputs
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-09T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static OwnershipInputs Create()
    {
        var snapshots = OwnershipSystems.All.ToDictionary(system => system, system => Loaded(new OwnershipSnapshot(
            "ce.ownership-snapshot.v1", system, "snapshot-" + system, Now.ToString("O"), "full",
            [new("record-" + system, "subject-" + system, "native-owner", new("team", "team-" + system))])), StringComparer.Ordinal);
        var crosswalk = new OwnershipCrosswalk("ce.ownership-crosswalk.v1", "v1", "synthetic-review", Now.ToString("O"),
            OwnershipSystems.All.Select(system => new OwnershipSubject("svc-a", system, "subject-" + system, "reviewed", "application-a")).ToArray(),
            [], [new("party-p", OwnershipSystems.All.Select(system => new OwnershipPartyMember(system, "team", "team-" + system)).ToArray(), "reviewed")]);
        var rules = new OwnershipRules("ce.ownership-rules.v1", "v1", "synthetic-review", Now.ToString("O"),
            OwnershipSystems.All.ToDictionary(system => system, _ => "P7D", StringComparer.Ordinal),
            OwnershipSystems.All.Select(system => new OwnershipRequirement("required-" + system, system, "native-owner", 1, 2)).ToArray(),
            [new("relation-owner", new("github", "native-owner"), new("leanix", "native-owner"), "same-party-set")], [], [], []);
        return new(["svc-a"], snapshots, Loaded(crosswalk), Loaded(rules));
    }

    internal static OwnershipInput<T> Loaded<T>(T value) where T : class =>
        new("loaded", Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json))), value);

    internal static OwnershipInputs Snapshot(OwnershipInputs inputs, string system, Func<OwnershipSnapshot, OwnershipSnapshot> change) =>
        inputs with
        {
            Snapshots = inputs.Snapshots.ToDictionary(row => row.Key,
                row => row.Key == system ? Loaded(change(row.Value.Value!)) : row.Value, StringComparer.Ordinal)
        };

    internal static OwnershipInputs Slot(OwnershipInputs inputs, string system, string state) =>
        inputs with
        {
            Snapshots = inputs.Snapshots.ToDictionary(row => row.Key,
                row => row.Key == system ? new OwnershipInput<OwnershipSnapshot>(state) : row.Value, StringComparer.Ordinal)
        };

    internal static OwnershipFinding Find(OwnershipReport report, string type, string? system = null, string? rule = null) =>
        report.Findings.Single(finding => finding.CheckKey.Type == type &&
            (system is null || finding.CheckKey.System == system) && (rule is null || finding.RuleId == rule));
}
