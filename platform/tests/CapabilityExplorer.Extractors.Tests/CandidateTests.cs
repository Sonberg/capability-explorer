using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class CandidateTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string Root = FindRoot();
    private readonly string scratch = Path.Combine(Root, "platform/tests/CapabilityExplorer.Extractors.Tests/Fixtures/CandidateScratch", Guid.NewGuid().ToString("N"));

    public CandidateTests() => Directory.CreateDirectory(scratch);
    public void Dispose() => Directory.Delete(scratch, true);

    [Fact]
    public void Given_reviewed_oracles_when_extracting_then_process_and_classifier_preserve_behavior()
    {
        var result = Reviewed();
        Assert.Equal("full", result.Coverage.Status);
        Assert.Equal(2, result.Candidates.Count);
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures/candidates/oracle.json")));
        foreach (var row in oracle.RootElement.GetProperty("process").EnumerateArray())
        {
            var candidate = result.Candidates.Single(c => c.Kind == "process");
            var simulator = new CandidateOracle(candidate, new()
            {
                ["input"] = row.GetProperty("input").ValueKind == JsonValueKind.Null ? null : row.GetProperty("input").GetString(),
                ["eligible"] = row.GetProperty("eligible").GetBoolean(), ["member"] = row.GetProperty("member").GetBoolean()
            }, row.TryGetProperty("callFailure", out var failure) && failure.GetBoolean());
            Assert.Equal(Scalar(row.GetProperty("outcome")), simulator.Run());
            Assert.Equal(row.GetProperty("reads").EnumerateArray().Select(e => e.GetString()), simulator.Reads);
            Assert.Equal(row.GetProperty("effects").EnumerateArray().Select(e => e.GetString()), simulator.Effects);
        }
        foreach (var row in oracle.RootElement.GetProperty("decision").EnumerateArray())
        {
            var simulator = new CandidateOracle(result.Candidates.Single(c => c.Kind == "decision"),
                new() { ["amount"] = row.GetProperty("amount").GetInt32(), ["member"] = row.GetProperty("member").GetBoolean() }, false);
            Assert.Equal(Scalar(row.GetProperty("outcome")), simulator.Run());
        }
    }

    [Fact]
    public void Given_source_revision_when_extracting_then_spans_and_semantic_dataflow_are_exact()
    {
        var result = Reviewed();
        var source = File.ReadAllText(Path.Combine(Root, "fixtures/candidates/reviewed.cs"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), result.SourceSha256);
        Assert.Equal(Sha, result.CommitSha);
        Assert.Equal("reviewed.cs", result.UnitPath);
        Assert.Empty(result.Diagnostics);
        var process = result.Candidates.Single(c => c.Kind == "process");
        Assert.Equal(["eligible", "input", "member"], process.InputsRead.Select(s => s.Split(':').Last()).Order());
        Assert.Empty(process.InputsWritten);
        foreach (var node in Walk(process.Body))
        {
            Assert.Equal(result.SourceSha256, node.Source.SourceSha256);
            Assert.Equal(Sha, node.Source.CommitSha);
            Assert.Equal(result.UnitPath, node.Source.UnitPath);
            var span = source.Substring(node.Source.Start, node.Source.Length);
            Assert.NotEmpty(span);
            var before = source[..node.Source.Start];
            Assert.Equal(before.Count(c => c == '\n') + 1, node.Source.StartLine);
            Assert.Equal(node.Source.Start - before.LastIndexOf('\n'), node.Source.StartColumn);
            if (node.RawPredicate is not null) Assert.Equal(span, node.RawPredicate);
        }
        Assert.All(result.Candidates, c =>
        {
            Assert.Equal("UNREVIEWED", c.ReviewStatus);
            Assert.False(c.Executable);
            Assert.Equal("candidate-content", c.ContentProfile);
        });
        Assert.Equal("propagate", Walk(process.Body).Single(n => n.Kind == "call").Failure);
        Assert.Equal(["System.ArgumentNullException", "System.ArgumentException"], Walk(process.Body).Where(n => n.Kind == "throw").Select(n => n.Type));
    }

    [Theory]
    [InlineData("1234567890", true, true, "True", "input,input,input,eligible,member,input", "Record")]
    [InlineData("1234567890", false, true, "False", "input,input,input,eligible", "")]
    [InlineData("12345678901", true, true, "ArgumentException", "input,input", "")]
    [InlineData("12345678901", false, false, "ArgumentException", "input,input", "")]
    [InlineData(null, false, false, "ArgumentNullException", "input", "")]
    [InlineData("", false, false, "False", "input,input,input", "")]
    public void Given_exact_length_boundaries_or_ineligible_input_then_validation_precedes_eligibility(
        string? input, bool eligible, bool member, string outcome, string reads, string effects)
    {
        var process = Reviewed().Candidates.Single(c => c.Kind == "process");
        var simulator = new CandidateOracle(process,
            new() { ["input"] = input, ["eligible"] = eligible, ["member"] = member }, false);

        Assert.Equal(outcome, simulator.Run()?.ToString());
        Assert.Equal(reads, string.Join(",", simulator.Reads));
        Assert.Equal(effects, string.Join(",", simulator.Effects));
    }

    [Fact]
    public void Given_validation_exceptions_then_parameter_name_and_message_remain_constructor_arguments()
    {
        var process = Reviewed().Candidates.Single(c => c.Kind == "process");
        var exceptions = Walk(process.Body).Where(n => n.Kind == "throw").ToArray();
        foreach (var (type, argument) in new[]
        {
            ("System.ArgumentNullException", "input"),
            ("System.ArgumentException", "invalid input")
        })
        {
            var constructorArgument = Assert.Single(exceptions.Single(n => n.Type == type).Children);
            Assert.Equal("constant", constructorArgument.Kind);
            Assert.Equal("string", constructorArgument.Type);
            Assert.Equal(argument, constructorArgument.Value);
        }
    }

    [Theory]
    [InlineData("public static async System.Threading.Tasks.Task<int> M(int x) { await System.Threading.Tasks.Task.Yield(); return x; }")]
    [InlineData("public static int M(dynamic x) { return x; }")]
    [InlineData("public static int M(int x) { return M(x); }")]
    [InlineData("public static int M(int x) { while(x > 0) x--; return x; }")]
    [InlineData("public static int M(int x) { x = 1; return x; }")]
    [InlineData("public static int M(int x) { if(x > 0) return 1; if(x > 10) return 2; return 0; }")]
    [InlineData("public static int M(int x) { return Missing(x); }")]
    [InlineData("public static int M(int x) { try { return x; } catch { return 0; } }")]
    public void Given_unsupported_semantics_when_extracting_then_no_approximate_candidate(string method)
    {
        var result = Extract("public static class Unsupported { " + method + " }");
        Assert.Equal("partial", result.Coverage.Status);
        Assert.Empty(result.Candidates);
        Assert.NotEmpty(result.Diagnostics);
        Assert.True(result.Coverage.UnsupportedMethods > 0);
    }

    [Fact]
    public void Given_unsupported_neighbor_then_supported_method_survives_without_whole_journey_claim()
    {
        var result = Extract("public static class C { public static string Ok(int x) { if(x < 0) return \"Invalid\"; return \"None\"; } public static int Bad(int x) { while(x > 0) x--; return x; } }");
        Assert.Equal("partial", result.Coverage.Status);
        Assert.Single(result.Candidates);
        Assert.Equal(1, result.Coverage.UnsupportedMethods);
    }

    [Theory]
    [InlineData("", Sha)]
    [InlineData("../unsafe", Sha)]
    [InlineData("repo", "bad")]
    public void Given_malformed_identity_then_reject(string repository, string commit)
        => Assert.Throws<ArgumentException>(() => CandidateExtractor.Extract(Path.Combine(Root, "fixtures/candidates/reviewed.cs"), repository, commit));

    [Fact]
    public void Given_oversized_source_or_directory_or_traversal_then_reject()
    {
        var path = Path.Combine(scratch, "large.cs");
        File.WriteAllText(path, new string(' ', 262145));
        Assert.Throws<ArgumentException>(() => CandidateExtractor.Extract(path, "repo", Sha));
        Assert.Throws<ArgumentException>(() => CandidateExtractor.Extract(scratch, "repo", Sha));
        Assert.Throws<ArgumentException>(() => CandidateExtractor.Extract(Path.Combine(scratch, "../reviewed.cs"), "repo", Sha));
    }

    [Fact]
    public void Given_symlink_then_reject_without_reading_target()
    {
        var path = Path.Combine(scratch, "link.cs");
        File.CreateSymbolicLink(path, Path.Combine(Root, "fixtures/candidates/reviewed.cs"));
        Assert.Throws<ArgumentException>(() => CandidateExtractor.Extract(path, "repo", Sha));
    }

    [Fact]
    public void Given_real_ce_cs_command_then_output_is_deterministic_candidate_profile()
    {
        var first = Path.Combine(scratch, "first.json");
        var second = Path.Combine(scratch, "second.json");
        Assert.Equal(0, Command(first));
        Assert.Equal(0, Command(second));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        using var json = JsonDocument.Parse(File.ReadAllText(first));
        Assert.Equal("ce.candidates.v1", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("candidates").GetArrayLength());
        Assert.Equal(2, Command(Path.Combine(scratch, "bad.json"), "not-a-sha"));
        Assert.False(File.Exists(Path.Combine(scratch, "bad.json")));
    }

    private static int Command(string output, string commit = Sha)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(Root, "platform/src/CapabilityExplorer.Extractors.CSharp/bin", configuration, "net10.0/ce-cs.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { dll, "extract-candidates", "--source", Path.Combine(Root, "fixtures/candidates/reviewed.cs"), "--repository", "synthetic", "--commit", commit, "--out", output })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000), stdout + stderr);
        return process.ExitCode;
    }

    private CandidateArtifact Extract(string source)
    {
        var path = Path.Combine(scratch, "input.cs");
        File.WriteAllText(path, source);
        return CandidateExtractor.Extract(path, "synthetic", Sha);
    }

    private static CandidateArtifact Reviewed() => CandidateExtractor.Extract(Path.Combine(Root, "fixtures/candidates/reviewed.cs"), "synthetic", Sha);
    private static object? Scalar(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetBoolean();
    private static IEnumerable<CandidateNode> Walk(CandidateNode node) => new[] { node }.Concat(node.Children.SelectMany(Walk));
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "fixtures/candidates"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("fixture root not found");
    }
}
