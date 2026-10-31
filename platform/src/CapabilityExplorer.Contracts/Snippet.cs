using System.Security.Cryptography;
using System.Text;

namespace CapabilityExplorer.Contracts;

/// <summary>Span text extraction and hashing exactly as specified by the schema's evidence definition.</summary>
public static class Snippet
{
    /// <summary>Returns the span text, or null when the span lies outside the file.</summary>
    public static string? Extract(string fileText, Span span)
    {
        var lines = fileText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (span.StartLine < 1 || span.EndLine > lines.Length || span.EndLine < span.StartLine)
        {
            return null;
        }

        var first = lines[span.StartLine - 1];
        var last = lines[span.EndLine - 1];
        if (span.StartColumn - 1 > first.Length || span.EndColumn - 1 > last.Length)
        {
            return null;
        }

        if (span.StartLine == span.EndLine)
        {
            return span.EndColumn < span.StartColumn ? null : first[(span.StartColumn - 1)..(span.EndColumn - 1)];
        }

        var builder = new StringBuilder(first[(span.StartColumn - 1)..]);
        for (var line = span.StartLine + 1; line < span.EndLine; line++)
        {
            builder.Append('\n').Append(lines[line - 1]);
        }

        builder.Append('\n').Append(last[..(span.EndColumn - 1)]);
        return builder.ToString();
    }

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
