using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record ExtractorOptions(string SourceRoot, string RepositoryId, string CommitSha, string? PackagesRoot, string ReferencePacks);

public sealed record UnitResult(string UnitPath, string UnitName, string Jsonl);

/// <summary>
/// Extracts ce.v1 artifacts from C# projects. Semantics come from Roslyn compilations built from the project's
/// sources, the SDK reference packs and package assemblies found in a packages folder. No restore, build or
/// analysed code runs. Missing references become diagnostics and a partial status, never guesses.
/// </summary>
public sealed class CSharpExtractor(ExtractorOptions options)
{
    public static readonly Tool Tool = new("roslyn", "0.2.0");

    /// <summary>Errors that only mean "a source generator would have produced this".</summary>
    private static readonly HashSet<string> GeneratorDependent = ["CS8795", "CS8796", "CS8797"];

    internal sealed record Unit(ProjectModel Project, CSharpCompilation Compilation, IReadOnlyList<PackageRef> MissingPackages, IReadOnlyList<ProjectRef> MissingProjects);

    public IReadOnlyList<UnitResult> Extract()
    {
        var projects = ProjectModel.Discover(options.SourceRoot);
        var resolver = new PackageResolver(options.PackagesRoot);
        var compilations = new Dictionary<string, Unit>(StringComparer.Ordinal);

        Unit Build(ProjectModel p, HashSet<string> visiting)
        {
            if (compilations.TryGetValue(p.FullPath, out var done))
            {
                return done;
            }

            visiting.Add(p.FullPath);
            var references = new List<MetadataReference>();
            references.AddRange(FrameworkReferences(p));
            var (packageAssemblies, missingPackages) = resolver.Resolve(p.Packages.Concat(p.IsWorker ? [new PackageRef("Microsoft.Extensions.Hosting", null, 0)] : []));
            references.AddRange(packageAssemblies.Select(a => MetadataReference.CreateFromFile(a)));
            var missingProjects = new List<ProjectRef>();
            foreach (var r in p.ProjectReferences)
            {
                var target = r.ResolvedPath is null ? null : projects.FirstOrDefault(x => x.FullPath == r.ResolvedPath);
                if (target is null || visiting.Contains(target.FullPath))
                {
                    missingProjects.Add(r);
                    continue;
                }

                var dep = Build(target, visiting);
                references.Add(dep.Compilation.ToMetadataReference());
                references.AddRange(dep.Compilation.References.Where(x => x is PortableExecutableReference pe && packageAssemblies.All(a => a != pe.FilePath)));
            }

            var parse = new CSharpParseOptions(LanguageVersion.Latest);
            var trees = p.SourceFiles.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, path: f)).ToList();
            var usings = p.ImplicitNamespaces();
            if (usings.Count > 0)
            {
                trees.Add(CSharpSyntaxTree.ParseText(string.Concat(usings.Select(u => $"global using global::{u};\n")), parse, path: "<implicit-usings>"));
            }

            var compilation = CSharpCompilation.Create(p.AssemblyName, trees, references.DistinctBy(r => r.Display),
                new CSharpCompilationOptions(p.IsExecutable ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: p.Nullable ? NullableContextOptions.Enable : NullableContextOptions.Disable, checkOverflow: p.CheckedArithmetic));
            var unit = new Unit(p, compilation, missingPackages, missingProjects);
            compilations[p.FullPath] = unit;
            visiting.Remove(p.FullPath);
            return unit;
        }

        var units = projects.Select(p => Build(p, [])).ToList();
        var repoAssemblies = units.Select(u => u.Project.AssemblyName).ToHashSet(StringComparer.Ordinal);
        var context = new RuleContext(options, units, repoAssemblies);
        return units.Select(u => ExtractUnit(u, context)).ToList();
    }

    private IEnumerable<MetadataReference> FrameworkReferences(ProjectModel p)
    {
        IEnumerable<string> Pack(string name)
        {
            var root = Path.Combine(options.ReferencePacks, name);
            if (!Directory.Exists(root))
            {
                return [];
            }

            var version = Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal).Last();
            var refDir = Directory.EnumerateDirectories(Path.Combine(version, "ref")).OrderBy(d => d, StringComparer.Ordinal).Last();
            return Directory.EnumerateFiles(refDir, "*.dll");
        }

        var files = Pack("Microsoft.NETCore.App.Ref");
        if (p.IsWeb)
        {
            files = files.Concat(Pack("Microsoft.AspNetCore.App.Ref"));
        }

        return files.Select(f => MetadataReference.CreateFromFile(f));
    }

    private UnitResult ExtractUnit(Unit unit, RuleContext context)
    {
        var p = unit.Project;
        var b = new ArtifactBuilder(options.RepositoryId, options.CommitSha, Tool);
        var keys = new SymbolKeys(options.RepositoryId, context.RepoAssemblies);
        var projectText = File.ReadAllText(p.FullPath);
        var projectKey = $"proj|{options.RepositoryId}|{p.RelativePath}";
        b.Node(projectKey, "Project", p.AssemblyName, [b.LineEvidence(p.RelativePath, p.SdkLine, projectText)]);

        foreach (var r in unit.MissingProjects)
        {
            b.Diagnostic("error", "MISSING_PROJECT_REFERENCE", $"Referenced project {r.Include} does not exist in the repository", p.RelativePath, true, b.LineEvidence(p.RelativePath, r.Line, projectText));
        }

        foreach (var r in unit.MissingPackages.Where(m => m.Line > 0))
        {
            b.Diagnostic("warning", "UNRESOLVED_PACKAGE", $"Package {r.Id} {r.Version} is not in the packages folder; symbols from it are unresolved", p.RelativePath, true, b.LineEvidence(p.RelativePath, r.Line, projectText));
        }

        foreach (var r in p.ProjectReferences.Where(r => r.ResolvedPath is not null))
        {
            var target = Path.GetRelativePath(options.SourceRoot, r.ResolvedPath!).Replace('\\', '/');
            b.Edge(projectKey, "DEPENDS_ON", $"proj|{options.RepositoryId}|{target}", [b.LineEvidence(p.RelativePath, r.Line, projectText)]);
        }

        // Source generators are not run (that would execute package code), so code that relies on them
        // (e.g. [LoggerMessage] partial methods) reports missing implementations. That is not a gap in the
        // analysed code: note it once, and do not count it against coverage.
        var errors = unit.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.IsInSource && d.Location.SourceTree!.FilePath != "<implicit-usings>").ToList();
        var generated = errors.Where(d => GeneratorDependent.Contains(d.Id)).ToList();
        if (generated.Count > 0)
        {
            b.Diagnostic("info", "SOURCE_GENERATORS_NOT_RUN", $"{generated.Count} member(s) are completed by source generators, which the extractor does not run", null, false);
        }

        // Compiler errors: the file is incomplete; a coverage diagnostic names the first error per file.
        foreach (var group in errors.Except(generated).GroupBy(d => d.Location.SourceTree!.FilePath))
        {
            var rel = Relative(group.Key);
            var first = group.OrderBy(d => d.Location.SourceSpan.Start).First();
            b.Incomplete(rel);
            b.Diagnostic("error", $"COMPILER_{first.Id}", first.GetMessage(System.Globalization.CultureInfo.InvariantCulture), rel, true,
                b.Evidence(rel, first.Location.GetLineSpan(), group.First().Location.SourceTree!.GetText().ToString()));
        }

        // Files by syntax tree, across the units of the repository (calculations follow calls into other projects).
        var files = new Dictionary<SyntaxTree, SourceFile?>();
        SourceFile? FileOf(SyntaxTree tree)
        {
            if (!files.TryGetValue(tree, out var known))
            {
                var owner = context.Units.FirstOrDefault(u => u.Compilation.ContainsSyntaxTree(tree));
                known = owner is null || tree.FilePath == "<implicit-usings>"
                    ? null
                    : new SourceFile(Relative(tree.FilePath), tree.GetText().ToString(), tree, owner.Compilation.GetSemanticModel(tree));
                files[tree] = known;
            }

            return known;
        }

        var calculations = new CalculationExtractor(b, keys, FileOf, () => context.Trees().Select(t => (t.Tree, t.Model)), p.CheckedArithmetic);
        foreach (var tree in unit.Compilation.SyntaxTrees.Where(t => t.FilePath != "<implicit-usings>"))
        {
            var file = FileOf(tree)!;
            Declarations(b, keys, file, projectKey);
            Calls(b, keys, file);
            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (file.Model.GetDeclaredSymbol(decl) is IMethodSymbol method && b.HasNode(keys.Of(method)))
                {
                    calculations.Extract(file, decl, method);
                }
            }
        }

        var rules = new Rules(context, unit, b, keys, Relative);
        rules.Apply();

        var inputHash = ArtifactBuilder.Sha256(string.Concat(p.SourceFiles.Append(p.FullPath).Order(StringComparer.Ordinal)
            .Select(f => $"{Relative(f)}\0{ArtifactBuilder.Sha256(File.ReadAllText(f))}\n")));
        var configHash = ArtifactBuilder.Sha256($"{Tool.Name}/{Tool.Version}|packages={(options.PackagesRoot is null ? "none" : "folder")}");
        var jsonl = b.ToJsonl(p.RelativePath, p.AssemblyName, "csharp", Rules.Tools, configHash, inputHash, p.SourceFiles.Count);
        return new UnitResult(p.RelativePath, p.AssemblyName, jsonl);
    }

    private string Relative(string path) => Path.GetRelativePath(options.SourceRoot, path).Replace('\\', '/');

    internal sealed record SourceFile(string Path, string Text, SyntaxTree Tree, SemanticModel Model)
    {
        public string Evidence(ArtifactBuilder b, SyntaxNode node) => b.Evidence(Path, node.GetLocation().GetLineSpan(), Text);

        public string Evidence(ArtifactBuilder b, Microsoft.CodeAnalysis.Text.TextSpan span) => b.Evidence(Path, Tree.GetLineSpan(span), Text);

        public bool Generated => Path.EndsWith(".g.cs", StringComparison.Ordinal) || Path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || Text.TrimStart().StartsWith("// <auto-generated", StringComparison.Ordinal);
    }

    /// <summary>Types and their members. Evidence covers the declaration header up to the name or parameter list.</summary>
    private static void Declarations(ArtifactBuilder b, SymbolKeys keys, SourceFile file, string projectKey)
    {
        foreach (var decl in file.Tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (file.Model.GetDeclaredSymbol(decl) is not INamedTypeSymbol type)
            {
                continue;
            }

            var key = keys.Of(type);
            var header = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(HeaderStart(decl.Modifiers, decl.Identifier), decl.Identifier.Span.End);
            b.Node(key, "Type", type.Name, [file.Evidence(b, header)], generated: file.Generated, props: new JsonObject
            {
                ["typeKind"] = type.TypeKind switch { TypeKind.Interface => "interface", TypeKind.Enum => "enum", TypeKind.Struct => type.IsRecord ? "record struct" : "struct", _ => type.IsRecord ? "record" : "class" },
                ["static"] = type.IsStatic ? true : null,
                ["partial"] = decl.Modifiers.Any(SyntaxKind.PartialKeyword) ? true : null,
                ["genericArity"] = type.Arity > 0 ? type.Arity : null,
            }.WithoutNulls());
            if (type.ContainingType is null)
            {
                b.Edge(projectKey, "DECLARES", key, [file.Evidence(b, header)]);
            }

            foreach (var iface in type.Interfaces.Where(i => i.TypeKind == TypeKind.Interface))
            {
                b.Edge(key, "IMPLEMENTS", keys.Of(iface), [file.Evidence(b, decl.BaseList is { } bl ? Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(header.Start, bl.Span.End) : header)]);
            }

            if (type.BaseType is { SpecialType: not SpecialType.System_Object and not SpecialType.System_ValueType } baseType && type.TypeKind == TypeKind.Class && !baseType.IsImplicitlyDeclared)
            {
                b.Edge(key, "INHERITS", keys.Of(baseType), [file.Evidence(b, decl.BaseList is { } bl ? bl : (SyntaxNode)decl.Identifier.Parent!)]);
            }
        }

        foreach (var decl in file.Tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (file.Model.GetDeclaredSymbol(decl) is not IMethodSymbol method)
            {
                continue;
            }

            var key = keys.Of(method);
            var header = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(HeaderStart(decl.Modifiers, decl.ReturnType.GetFirstToken()), decl.ParameterList.OpenParenToken.Span.End);
            b.Node(key, "Method", method.Name, [file.Evidence(b, header)], generated: file.Generated);
            b.Edge(keys.Of(method.ContainingType), "DECLARES", key, [file.Evidence(b, header)]);
            foreach (var iface in method.ContainingType.AllInterfaces)
            {
                foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    if (SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(member), method))
                    {
                        b.Edge(key, "IMPLEMENTS", keys.Of(member), [file.Evidence(b, header)]);
                    }
                }
            }
        }

        foreach (var decl in file.Tree.GetRoot().DescendantNodes().Where(n => n is PropertyDeclarationSyntax or ParameterSyntax { Parent.Parent: RecordDeclarationSyntax }))
        {
            var property = decl switch
            {
                PropertyDeclarationSyntax pd => file.Model.GetDeclaredSymbol(pd),
                ParameterSyntax ps when file.Model.GetDeclaredSymbol(ps) is IParameterSymbol parameter => parameter.ContainingType?.GetMembers(parameter.Name).OfType<IPropertySymbol>().FirstOrDefault()
                    ?? (parameter.ContainingSymbol as IMethodSymbol)?.ContainingType.GetMembers(parameter.Name).OfType<IPropertySymbol>().FirstOrDefault(),
                _ => null,
            };
            if (property is null)
            {
                continue;
            }

            var key = keys.Of(property);
            var span = decl is PropertyDeclarationSyntax p2 ? Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(HeaderStart(p2.Modifiers, p2.Type.GetFirstToken()), p2.Identifier.Span.End) : decl.Span;
            b.Node(key, "Property", property.Name, [file.Evidence(b, span)], signature: decl is ParameterSyntax ? decl.ToString() : null, generated: file.Generated);
            b.Edge(keys.Of(property.ContainingType), "DECLARES", key, [file.Evidence(b, span)]);
        }

        // Top-level statements: the synthesized entry point owns the code, as in the compiler.
        if (file.Tree.GetRoot() is CompilationUnitSyntax cu && cu.Members.OfType<GlobalStatementSyntax>().FirstOrDefault() is { } first
            && file.Model.GetEnclosingSymbol(first.SpanStart) is IMethodSymbol entry)
        {
            b.Node(keys.Of(entry), "Method", entry.Name, [file.Evidence(b, first)], props: new JsonObject { ["topLevelStatements"] = true });
        }
    }

    /// <summary>Where a declaration's header starts, skipping attributes.</summary>
    internal static int HeaderStart(SyntaxTokenList modifiers, SyntaxToken fallback) => modifiers.Count > 0 ? modifiers[0].SpanStart : fallback.SpanStart;

    /// <summary>Invocations, attributed to the nearest named member (lambdas and local functions fold into it).</summary>
    private static void Calls(ArtifactBuilder b, SymbolKeys keys, SourceFile file)
    {
        foreach (var inv in file.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (Owner(file.Model, inv.SpanStart) is not { } owner || !b.HasNode(keys.Of(owner)))
            {
                continue;
            }

            var from = keys.Of(owner);
            var info = file.Model.GetSymbolInfo(inv);
            if (info.Symbol is IMethodSymbol target)
            {
                var method = (target.ReducedFrom ?? target).OriginalDefinition;
                if (method.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction || method.Name == "nameof")
                {
                    continue;
                }

                var receiverIsBase = inv.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax };
                var dispatch = !receiverIsBase && !target.IsStatic && (method.ContainingType.TypeKind == TypeKind.Interface || method.IsAbstract || method.IsVirtual || method.IsOverride)
                    && method.ContainingType.TypeKind != TypeKind.Struct;
                var props = target.ContainingType is { IsGenericType: true } ct && !SymbolEqualityComparer.Default.Equals(ct, ct.OriginalDefinition)
                    ? new JsonObject { ["typeArguments"] = new JsonArray(ct.TypeArguments.Select(t => (JsonNode)JsonValue.Create(keys.Of(t))!).ToArray()) }
                    : null;
                b.Edge(from, "CALLS", keys.Of(method), [file.Evidence(b, inv)], resolution: dispatch ? "static-target" : "resolved", props: props);
            }
            else if (info.CandidateSymbols.Length > 0)
            {
                foreach (var candidate in info.CandidateSymbols.OfType<IMethodSymbol>())
                {
                    b.Edge(from, "CALLS", keys.Of(candidate.OriginalDefinition), [file.Evidence(b, inv)], resolution: "candidate", props: new JsonObject { ["reason"] = info.CandidateReason.ToString() });
                }
            }
            else
            {
                var missing = file.Model.GetDiagnostics(inv.Span).Any(d => d.Severity == DiagnosticSeverity.Error);
                b.Edge(from, "CALLS", null, [file.Evidence(b, inv)], resolution: "unresolved", reason: missing ? "missing-reference" : "dynamic-dispatch",
                    props: new JsonObject { ["expression"] = inv.Expression.ToString() });
            }
        }
    }

    internal static ISymbol? Owner(SemanticModel model, int position)
    {
        var symbol = model.GetEnclosingSymbol(position);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction or MethodKind.LambdaMethod })
        {
            symbol = symbol.ContainingSymbol;
        }

        return symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet } accessor => accessor.AssociatedSymbol,
            IMethodSymbol { MethodKind: MethodKind.Ordinary } m => m,
            IPropertySymbol p => p,
            _ => null,
        };
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject WithoutNulls(this JsonObject o)
    {
        foreach (var k in o.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
        {
            o.Remove(k);
        }

        return o;
    }
}

/// <summary>ce.v1 symbol keys for Roslyn symbols: documentation comment IDs, scoped to repository and assembly.</summary>
internal sealed class SymbolKeys(string repositoryId, IReadOnlySet<string> repoAssemblies)
{
    public string Of(ISymbol symbol)
    {
        var original = symbol.OriginalDefinition;
        var id = original.GetDocumentationCommentId() ?? $"?:{original.ToDisplayString()}";
        var assembly = original.ContainingAssembly?.Name ?? "unknown";
        return repoAssemblies.Contains(assembly) && original.Locations.Any(l => l.IsInSource)
            ? $"cs|{repositoryId}|{assembly}|{id}"
            : $"cs|@ext|{assembly}|{id}";
    }
}
