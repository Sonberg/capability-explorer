using System.Diagnostics;

namespace CapabilityExplorer.Contracts;

/// <summary>
/// Checks every evidence record against the repository content at the artifact's commit: the path exists,
/// the span lies inside the file and the snippet hash matches. Reads git objects only; never checks out code.
/// </summary>
public sealed class EvidenceVerifier(string repositoryDirectory)
{
    public async Task<IReadOnlyList<Problem>> VerifyAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var problems = new List<Problem>();
        var commit = artifact.Header.CommitSha;
        if (commit.Length is not (40 or 64) || !commit.All(char.IsAsciiHexDigit))
            return [new Problem(0, "invalid commit SHA")];
        var resolved = (await GitAsync(["rev-parse", "--verify", "--quiet", "--end-of-options", $"{commit}^{{commit}}"], cancellationToken)).Output.Trim();
        if (resolved != commit)
        {
            return [new Problem(0, $"commit {commit} not found in {repositoryDirectory}")];
        }

        foreach (var group in artifact.Evidence.GroupBy(e => e.Path, StringComparer.Ordinal))
        {
            if (group.Key.Length is 0 or > 4096 || group.Key.StartsWith('/') || group.Key.Contains('\\')
                || group.Key.Any(char.IsControl) || group.Key.Split('/').Any(part => part is "" or "." or ".."))
            {
                problems.Add(new Problem(0, "invalid evidence path"));
                continue;
            }
            var (exitCode, text) = await GitAsync(["show", $"{commit}:{group.Key}"], cancellationToken);
            if (exitCode != 0)
            {
                problems.AddRange(group.Select(e => new Problem(0, $"{e.Id}: {group.Key} does not exist at {commit[..12]}")));
                continue;
            }

            foreach (var evidence in group)
            {
                var snippet = Snippet.Extract(text, evidence.Span);
                if (snippet is null)
                {
                    problems.Add(new Problem(0, $"{evidence.Id}: span {evidence.Span} lies outside {group.Key}"));
                }
                else if (Snippet.Sha256(snippet) != evidence.SnippetSha256)
                {
                    problems.Add(new Problem(0, $"{evidence.Id}: snippet hash mismatch at {group.Key}:{evidence.Span.StartLine}"));
                }
            }
        }

        return problems;
    }

    private async Task<(int ExitCode, string Output)> GitAsync(string[] args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var setting in new[] { "core.hooksPath=/dev/null", "core.fsmonitor=false", "credential.helper=",
            "protocol.allow=never", "protocol.file.allow=never", "protocol.ext.allow=never", "protocol.https.allow=never",
            "http.followRedirects=false", "http.sslVerify=true", "core.sshCommand=/bin/false" })
        {
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(setting);
        }
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ASKPASS"] = "/bin/false";
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repositoryDirectory);
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start git");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output = ReadBounded(process.StandardOutput, process, timeout.Token);
            var error = ReadBounded(process.StandardError, process, timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            return (process.ExitCode, await output);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            return (1, "");
        }
    }
    private static async Task<string> ReadBounded(StreamReader reader, Process process, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (text.Length + count > 8 * 1024 * 1024)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw new IOException("Git evidence exceeds bounded response limit");
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
