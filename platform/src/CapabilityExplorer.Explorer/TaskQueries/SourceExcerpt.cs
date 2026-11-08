namespace CapabilityExplorer.Explorer;

/// <summary>Pure line-windowing over already-read, already-authorized source text: never touches git, the file
/// system or any permission decision itself.</summary>
public static class SourceExcerpt
{
    public const int MaxSpan = 200;
    public const int ContextLines = 4;

    /// <summary>Throws <see cref="ArgumentException"/> for a span outside the file or wider than <see cref="MaxSpan"/>
    /// lines; the caller is expected to have validated bounds before reading, this only re-asserts them against
    /// the actual line count.</summary>
    public static TaskQuerySourceExcerpt Build(string repo, string commit, string path, string text, int start, int end)
    {
        var lines = text.Split('\n');
        if (start < 1 || end < start || start > lines.Length || end - start + 1 > MaxSpan)
            throw new ArgumentException("excerpt span is outside the file or exceeds the bounded span length");
        var clampedEnd = Math.Min(end, lines.Length);
        var from = Math.Max(1, start - ContextLines);
        var to = Math.Min(lines.Length, clampedEnd + ContextLines);
        var windowed = Enumerable.Range(from, to - from + 1).Select(n => new TaskQuerySourceLine(n, lines[n - 1])).ToList();
        return new TaskQuerySourceExcerpt(repo, commit, path, start, clampedEnd, lines.Length, windowed);
    }
}
