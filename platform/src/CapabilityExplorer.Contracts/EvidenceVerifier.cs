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
        var resolved = (await GitAsync(["rev-parse", "--verify", "--quiet", $"{commit}^{{commit}}"], cancellationToken)).Output.Trim();
        if (resolved != commit)
        {
            return [new Problem(0, $"commit {commit} not found in {repositoryDirectory}")];
        }

        foreach (var group in artifact.Evidence.GroupBy(e => e.Path, StringComparer.Ordinal))
        {
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
                    problems.Add(new Problem(0, $"{evidence.Id}: snippet hash mismatch at {group.Key}:{evidence.Span.StartLine} (text there: {Truncate(snippet)})"));
                }
            }
        }

        return problems;
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..57] + "...";

    private async Task<(int ExitCode, string Output)> GitAsync(string[] args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repositoryDirectory);
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start git");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output);
    }
}
