using CapabilityExplorer.Contracts;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Stage 1 exit criterion: identity and evidence in the golden artifacts are consistent.</summary>
public sealed class GoldenArtifactTests
{
    public static TheoryData<string> Golden => Fixtures.GoldenFileData();

    [Fact]
    public void Golden_set_covers_every_locked_revision()
    {
        var revisions = Fixtures.GoldenFiles.Select(f => string.Join('/', f.Split('/')[..2])).ToHashSet();
        var locked = Fixtures.Lock.SelectMany(r => r.Value.Keys.Select(tag => $"{r.Key}/{tag}")).ToHashSet();
        Assert.Equal(locked.Order(), revisions.Order());
    }

    [Theory]
    [MemberData(nameof(Golden))]
    public void Golden_artifact_satisfies_the_contract(string file)
    {
        var report = ArtifactValidator.Validate(Fixtures.Golden(file));
        Assert.Empty(report.Problems);
    }

    [Theory]
    [MemberData(nameof(Golden))]
    public void Golden_artifact_points_at_the_locked_commit(string file)
    {
        var parts = file.Split('/');
        var artifact = Valid(file);
        Assert.Equal(parts[0], artifact.Header.RepositoryId);
        Assert.Equal(Fixtures.Lock[parts[0]][parts[1]], artifact.Header.CommitSha);
    }

    [Theory]
    [MemberData(nameof(Golden))]
    public async Task Every_evidence_span_matches_the_source_at_that_commit(string file)
    {
        var artifact = Valid(file);
        var problems = await new EvidenceVerifier(Fixtures.RepoDirectory(artifact.Header.RepositoryId)).VerifyAsync(artifact);
        Assert.Empty(problems);
    }

    internal static Artifact Valid(string file)
    {
        var report = ArtifactValidator.Validate(Fixtures.Golden(file));
        Assert.True(report.IsValid, string.Join("\n", report.Problems));
        return report.Artifact!;
    }
}
