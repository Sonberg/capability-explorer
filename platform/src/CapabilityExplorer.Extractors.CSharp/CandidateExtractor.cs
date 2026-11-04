using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>Offline, single-file semantic observations; never builds, emits or executes target code.</summary>
public static class CandidateExtractor
{
    public static CandidateArtifact Extract(string source, string repositoryId, string commitSha)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(commitSha);
        if (repositoryId.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(repositoryId[0]) ||
            repositoryId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.') ||
            repositoryId.Contains("..", StringComparison.Ordinal) ||
            commitSha.Length != 40 || commitSha.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Invalid repository or revision identity.");
        var path = CandidatePaths.Validate(source);
        if (!path.EndsWith(".cs", StringComparison.Ordinal) || !File.Exists(path) || new FileInfo(path).Length > 262144)
            throw new ArgumentException("Expected a bounded C# source file.");
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bytes = new byte[262145];
            var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count > 262144) throw new ArgumentException("Source exceeds byte limit.");
            Array.Resize(ref bytes, count);
        }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException error) { throw new ArgumentException("Source must be UTF-8.", error); }
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var unit = Path.GetFileName(path);
        if (unit.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new ArgumentException("Unsafe unit filename.");
        var revision = commitSha.ToLowerInvariant();
        CheckLexicalLimits(text);
        var tree = CSharpSyntaxTree.ParseText(text, path: unit);
        var root = tree.GetRoot();
        var nodes = root.DescendantNodesAndSelf().ToArray();
        if (nodes.Length > 16384 || nodes.Any(n => n.Ancestors().Take(65).Count() > 64))
            throw new ArgumentException("Source exceeds structural limits.");
        var methods = nodes.OfType<MethodDeclarationSyntax>().Where(m => m.Body is not null || m.ExpressionBody is not null).ToArray();
        if (methods.Length > 64) throw new ArgumentException("Too many methods.");
        var references = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var compilation = CSharpCompilation.Create("candidate-analysis", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: false));
        var model = compilation.GetSemanticModel(tree);
        List<StaticCandidate> candidates = [];
        List<CandidateDiagnostic> diagnostics = [];
        CandidateSource Source(SyntaxNode syntax)
        {
            var line = tree.GetLineSpan(syntax.Span);
            return new(unit, revision, hash, syntax.SpanStart, syntax.Span.Length,
                line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1,
                line.EndLinePosition.Line + 1, line.EndLinePosition.Character + 1);
        }
        foreach (var method in methods)
        {
            try
            {
                if (model.GetDiagnostics(method.Span).Any(d => d.Severity == DiagnosticSeverity.Error))
                    throw new CandidateUnsupportedException("unresolved-semantics");
                candidates.Add(new CandidateRecipe(model, Source).Read(method));
            }
            catch (CandidateUnsupportedException error)
            {
                diagnostics.Add(new(error.Message, "Method is outside the bounded intra-method recipe.", Source(method)));
            }
        }
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            diagnostics.Add(new("invalid-syntax", "Source contains syntax errors."));
        if (methods.Length == 0) diagnostics.Add(new("no-supported-methods", "No method bodies in this file."));
        if (root.DescendantNodes().Any(n => n is GlobalStatementSyntax or AccessorDeclarationSyntax or ConstructorDeclarationSyntax or DestructorDeclarationSyntax))
            diagnostics.Add(new("outside-method-scope", "Executable declarations outside method bodies are not covered."));
        if (root.DescendantNodes().OfType<EqualsValueClauseSyntax>().Any(n => n.Parent is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } or PropertyDeclarationSyntax))
            diagnostics.Add(new("outside-method-scope", "Type and property initialization is outside intra-method coverage."));
        if (root.DescendantTrivia(descendIntoTrivia: true).Any(t => t.GetStructure() is ConditionalDirectiveTriviaSyntax or ElseDirectiveTriviaSyntax or ElifDirectiveTriviaSyntax))
            diagnostics.Add(new("conditional-compilation", "Conditional source is not fully covered by this single-file recipe."));
        return new("ce.candidates.v1", repositoryId, revision, unit, hash,
            new(diagnostics.Count == 0 ? "full" : "partial", candidates.Count, methods.Length - candidates.Count),
            diagnostics, candidates);
    }

    private static void CheckLexicalLimits(string text)
    {
        var depth = 0;
        var count = 0;
        foreach (var token in SyntaxFactory.ParseTokens(text))
        {
            if (++count > 32768) throw new ArgumentException("Source exceeds token limit.");
            if (token.Kind() is SyntaxKind.OpenParenToken or SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken)
            {
                if (++depth > 64) throw new ArgumentException("Source exceeds nesting limit.");
            }
            else if (token.Kind() is SyntaxKind.CloseParenToken or SyntaxKind.CloseBraceToken or SyntaxKind.CloseBracketToken)
            {
                depth--;
            }
        }
    }
}

internal sealed class CandidateUnsupportedException(string reason) : Exception(reason);
