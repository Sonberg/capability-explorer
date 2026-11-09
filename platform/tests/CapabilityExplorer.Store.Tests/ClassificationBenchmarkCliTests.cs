using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class ClassificationBenchmarkCliTests
{
    [Fact]
    public async Task Actual_cli_rejects_invalid_inputs_without_creating_or_overwriting_reports()
    {
        var directory = Path.Combine(Path.GetTempPath(), "classification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "report.json");
        var corpus = Path.Combine(directory, "corpus.json");
        try
        {
            var root = Fixtures.RepositoryRoot;
            var artifact = Path.Combine(root, "fixtures", "classification", "synthetic-artifact.v1.json");
            var originalCorpus = Path.Combine(root, "fixtures", "classification", "synthetic-corpus.v1.json");
            string[] options =
            [
                "classification-benchmark", "--artifact", artifact,
                "--sha256", "a48a617509060866ee2da35e0eaadc51f9685640b9f9841139dc4f1079a6ab6b",
                "--corpus", corpus, "--source", "86f23939bbc9200214be17c7bafa38f5e39312c4",
                "--samples", "1000", "--concurrency", "1", "--json", output
            ];
            await File.WriteAllTextAsync(corpus, "{\"schema\":\"ce.classification-corpus.v1\",\"purpose\":\"synthetic\",\"cases\":[]}");
            var empty = await Ce(options);
            Assert.Equal(1, empty.Exit);
            Assert.Contains("1..256 cases", empty.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
            await File.WriteAllTextAsync(corpus, "{\"schema\":\"ce.classification-corpus.v1\",\"schema\":\"duplicate\",\"purpose\":\"synthetic\",\"cases\":[]}");
            var duplicate = await Ce(options);
            Assert.Equal(1, duplicate.Exit);
            Assert.Contains("duplicate", duplicate.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
            await File.WriteAllTextAsync(corpus, await File.ReadAllTextAsync(originalCorpus));
            var wrongHashOptions = options.ToArray();
            wrongHashOptions[4] = new string('0', 64);
            var mismatch = await Ce(wrongHashOptions);
            Assert.Equal(1, mismatch.Exit);
            Assert.Contains("IdentityMismatch", mismatch.Output, StringComparison.Ordinal);
            var duplicateOption = await Ce([.. options, "--samples", "1000"]);
            Assert.Equal(2, duplicateOption.Exit);
            Assert.False(File.Exists(output));
            await File.WriteAllTextAsync(output, "retained-report");
            var noOverwrite = await Ce(options);
            Assert.Equal(1, noOverwrite.Exit);
            Assert.Equal("retained-report", await File.ReadAllTextAsync(output));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Actual_cli_records_all_outcomes_and_distinct_concurrent_workers()
    {
        var output = Path.Combine(Path.GetTempPath(), "classification-report-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var result = await Ce(
                "classification-benchmark",
                "--artifact", Path.Combine(Fixtures.RepositoryRoot, "fixtures", "classification", "synthetic-artifact.v1.json"),
                "--sha256", "a48a617509060866ee2da35e0eaadc51f9685640b9f9841139dc4f1079a6ab6b",
                "--corpus", Path.Combine(Fixtures.RepositoryRoot, "fixtures", "classification", "synthetic-corpus.v1.json"),
                "--source", "86f23939bbc9200214be17c7bafa38f5e39312c4",
                "--samples", "1000", "--concurrency", "4", "--json", output);
            Assert.True(File.Exists(output), result.Output);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var root = report.RootElement;
            Assert.Equal("ce.classification-benchmark.v1", root.GetProperty("schema").GetString());
            Assert.Equal(4, root.GetProperty("workload").GetProperty("workerThreads").GetInt32());
            Assert.Equal(1000, root.GetProperty("rawSamples").GetArrayLength());
            Assert.Equal(0, root.GetProperty("metrics").GetProperty("errors").GetInt32());
            Assert.Equal(375, root.GetProperty("metrics").GetProperty("abstentions").GetInt32());
            Assert.Equal(8, root.GetProperty("quality").GetProperty("contractCases").GetInt32());
            Assert.All(root.GetProperty("rawSamples").EnumerateArray(),
                sample => Assert.True(sample.GetProperty("ticks").GetInt64() >= 0));
            Assert.Equal(root.GetProperty("warmedTargetPassed").GetBoolean() ? 0 : 1, result.Exit);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    private static async Task<(int Exit, string Output)> Ce(params string[] arguments)
    {
        var dll = Path.Combine(Fixtures.RepositoryRoot, "platform", "src", "CapabilityExplorer.Cli", "bin", "Debug", "net10.0", "ce.dll");
        Assert.True(File.Exists(dll), "Build the CLI before its subprocess tests");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
}
