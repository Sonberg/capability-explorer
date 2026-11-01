using System.Diagnostics;
using CapabilityExplorer.Contracts;
using System.Text.Json.Nodes;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class EvidenceVerifierSecurityTests
{
    [Fact]
    public async Task Replacement_refs_cannot_change_evidence_at_the_requested_commit()
    {
        var root = Directory.CreateTempSubdirectory("ce-evidence-replacement-").FullName;
        try
        {
            const string marker = "original-evidence";
            File.WriteAllText(Path.Combine(root, "file.txt"), marker);
            string Git(params string[] args)
            {
                var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("-C"); start.ArgumentList.Add(root);
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(); Assert.Equal(0, process.ExitCode);
                return output.Trim();
            }
            Git("init"); Git("add", "file.txt");
            Git("-c", "user.name=Synthetic test", "-c", "user.email=test@example.invalid", "commit", "-m", "synthetic");
            var commit = Git("rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(root, "file.txt"), "replaced-evidence");
            Git("add", "file.txt");
            Git("-c", "user.name=Synthetic test", "-c", "user.email=test@example.invalid", "commit", "-m", "replacement");
            var replacement = Git("rev-parse", "HEAD");
            Git("replace", commit, replacement);
            var records = Fixtures.Golden("api-cs/v1-base/Sales.Api.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
            foreach (var record in records)
            {
                if (record["commitSha"] is not null) record["commitSha"] = commit;
                if (record["kind"]!.GetValue<string>() == "evidence")
                {
                    record["path"] = "file.txt";
                    record["span"] = new JsonObject { ["startLine"] = 1, ["startColumn"] = 1, ["endLine"] = 1, ["endColumn"] = marker.Length + 1 };
                    record["snippetSha256"] = Snippet.Sha256(marker);
                }
            }
            var report = ArtifactValidator.Validate(string.Join('\n', records.Select(r => r.ToJsonString())) + "\n");
            Assert.True(report.IsValid, string.Join("; ", report.Problems));
            var problems = await new EvidenceVerifier(root).VerifyAsync(report.Artifact!);
            Assert.Empty(problems);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Snippet_mismatch_diagnostics_do_not_copy_source_contents()
    {
        var root = Directory.CreateTempSubdirectory("ce-evidence-redaction-").FullName;
        try
        {
            const string marker = "synthetic-sensitive-evidence";
            File.WriteAllText(Path.Combine(root, "file.txt"), marker);
            string Git(params string[] args)
            {
                var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("-C"); start.ArgumentList.Add(root);
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(); Assert.Equal(0, process.ExitCode);
                return output.Trim();
            }
            Git("init"); Git("add", "file.txt");
            Git("-c", "user.name=Synthetic test", "-c", "user.email=test@example.invalid", "commit", "-m", "synthetic");
            var commit = Git("rev-parse", "HEAD");
            var records = Fixtures.Golden("api-cs/v1-base/Sales.Api.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
            foreach (var record in records)
            {
                if (record["commitSha"] is not null) record["commitSha"] = commit;
                if (record["kind"]!.GetValue<string>() == "evidence")
                {
                    record["path"] = "file.txt";
                    record["span"] = new JsonObject { ["startLine"] = 1, ["startColumn"] = 1, ["endLine"] = 1, ["endColumn"] = marker.Length + 1 };
                    record["snippetSha256"] = new string('a', 64);
                }
            }
            var report = ArtifactValidator.Validate(string.Join('\n', records.Select(r => r.ToJsonString())) + "\n");
            Assert.True(report.IsValid, string.Join("; ", report.Problems));
            var problems = await new EvidenceVerifier(root).VerifyAsync(report.Artifact!);
            Assert.Contains(problems, p => p.Message.Contains("snippet hash mismatch", StringComparison.Ordinal));
            Assert.DoesNotContain(marker, string.Join("; ", problems));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Missing_blob_does_not_execute_a_repository_configured_promisor_helper()
    {
        var root = Directory.CreateTempSubdirectory("ce-git-source-security-").FullName;
        try
        {
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            string Git(params string[] args)
            {
                var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("-C"); start.ArgumentList.Add(repo);
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, error);
                return output.Trim();
            }
            Git("init"); Git("config", "user.name", "Synthetic test"); Git("config", "user.email", "test@example.invalid");
            File.WriteAllText(Path.Combine(repo, "file.txt"), "synthetic source");
            Git("add", "file.txt"); Git("commit", "-m", "synthetic");
            var commit = Git("rev-parse", "HEAD");
            var blob = Git("rev-parse", "HEAD:file.txt");
            File.Delete(Path.Combine(repo, ".git", "objects", blob[..2], blob[2..]));
            var marker = Path.Combine(root, "helper-executed");
            var helper = Path.Combine(root, "helper");
            File.WriteAllText(helper, $"#!/bin/sh\ntouch '{marker}'\nexit 1\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Git("config", "core.repositoryformatversion", "1");
            Git("config", "extensions.partialClone", "origin");
            Git("config", "remote.origin.promisor", "true");
            Git("config", "remote.origin.url", "ext::" + helper);
            Git("config", "protocol.ext.allow", "always");
            var records = Fixtures.Golden("api-cs/v1-base/Sales.Api.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonNode.Parse(s)!.AsObject()).ToList();
            foreach (var record in records)
            {
                if (record["commitSha"] is not null) record["commitSha"] = commit;
                if (record["kind"]!.GetValue<string>() == "evidence") record["path"] = "file.txt";
            }
            var report = ArtifactValidator.Validate(string.Join('\n', records.Select(r => r.ToJsonString())) + "\n");
            Assert.True(report.IsValid, string.Join("; ", report.Problems));
            var problems = await new EvidenceVerifier(repo).VerifyAsync(report.Artifact!);
            Assert.NotEmpty(problems);
            Assert.False(File.Exists(marker), "Reading source executed the repository's configured transport helper");
        }
        finally { Directory.Delete(root, true); }
    }
}
