using System.Diagnostics;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GitSourceSecurityTests
{
    [Fact]
    public void Replacement_refs_cannot_change_source_at_the_requested_commit()
    {
        var root = Directory.CreateTempSubdirectory("ce-source-replacement-").FullName;
        try
        {
            var repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo);
            string Git(params string[] args)
            {
                var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("-C"); start.ArgumentList.Add(repo);
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd(); process.WaitForExit();
                Assert.True(process.ExitCode == 0, error); return output.Trim();
            }
            Git("init"); Git("config", "user.name", "Synthetic test"); Git("config", "user.email", "test@example.invalid");
            File.WriteAllText(Path.Combine(repo, "file.txt"), "original source");
            Git("add", "file.txt"); Git("commit", "-m", "original");
            var original = Git("rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(repo, "file.txt"), "replacement source");
            Git("add", "file.txt"); Git("commit", "-m", "replacement");
            var replacement = Git("rev-parse", "HEAD");
            Git("replace", original, replacement);
            var source = new GitSource(root);
            Assert.Equal("original source", source.ReadFile("repo", original, "file.txt"));
            Assert.Equal("original", source.Subject("repo", original));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Missing_blob_does_not_execute_a_repository_configured_promisor_helper()
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
            Assert.Null(new GitSource(root).ReadFile("repo", commit, "file.txt"));
            Assert.False(File.Exists(marker), "Reading source executed the repository's configured transport helper");
        }
        finally { Directory.Delete(root, true); }
    }
}
