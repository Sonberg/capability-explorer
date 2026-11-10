using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record ExtractorOptions(string SourceRoot, string RepositoryId, string CommitSha, string? PackagesRoot, string ReferencePacks, PackageLock? Lock = null, bool IncludeTests = false)
{
    public Action<ExtractionTiming>? Timing { get; init; }
    public ProjectSelectionContract? Selection { get; init; }
    internal ExtractionTimer Measure(string stage, int? unit = null) => new(Timing,stage,unit);
}

public sealed record UnitResult(string UnitPath, string UnitName, string Jsonl);

/// <summary>Extracts ce.v1 artifacts with Roslyn and SDK/package references; never restores, builds, or executes analysed code.</summary>
public sealed class CSharpExtractor(ExtractorOptions options)
{
    public static readonly Tool Tool = new("roslyn", "0.2.0");

    /// <summary>The deterministic extractor build and Roslyn version enter config hashes to prevent stale cache collisions.</summary>
    internal static readonly string Build = ArtifactBuilder.Sha256($"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(CSharpExtractor).Assembly.Location)))}|roslyn={typeof(CSharpCompilation).Assembly.GetName().Version}")[..16];

    /// <summary>Errors that only mean "a source generator would have produced this".</summary>
    private static readonly HashSet<string> GeneratorDependent = ["CS8795", "CS8796", "CS8797"];

    internal sealed record Unit(ProjectModel Project, CSharpCompilation Compilation, IReadOnlyList<PackageRef> MissingPackages, IReadOnlyList<ProjectRef> MissingProjects, IReadOnlyList<LockedPackage> ResolvedPackages, IReadOnlyList<ResolutionIssue> ResolutionIssues, IReadOnlySet<string> FrameworkPaths, string ReferenceFingerprint)
    {
        public IReadOnlyList<UnselectedPackage> UnselectedPackages { get; init; } = [];
    }

    public IReadOnlyList<UnitResult> Extract()
    {
        using var total = options.Measure("cs.total");
        using var discovery = options.Measure("cs.projects");
        var projects = ProjectModel.Discover(options.SourceRoot, options.IncludeTests, options.Selection);
        discovery.Complete();
        discovery.Dispose();
        var compilations = new Dictionary<string, Unit>(StringComparer.Ordinal);

        Unit Build(ProjectModel p, HashSet<string> visiting)
        {
            if (compilations.TryGetValue(p.FullPath, out var done))
            {
                return done;
            }

            visiting.Add(p.FullPath);
            using var referenceTiming = options.Measure("cs.references");
            var references = new List<MetadataReference>();
            var issues = new List<ResolutionIssue>(p.ResolutionIssues);
            var frameworkFiles = ReferenceSelection.FrameworkFiles(options.ReferencePacks, p, issues);
            var frameworkPaths = frameworkFiles.ToHashSet(StringComparer.Ordinal);
            var frameworkPackages = FrameworkPackageCoverage.Read(frameworkFiles);
            var resolver = new PackageResolver(options.PackagesRoot, frameworkPackages.Packages);
            references.AddRange(ReferenceSelection.FileReferences(frameworkFiles, issues));
            var resolution = resolver.Resolve(p.PackageRoots(), options.Lock?.For(p.RelativePath) ?? (options.Lock is null ? null : []), p.TargetFramework);
            issues.AddRange(resolution.Issues);
            var (packageAssemblies, missingPackages) = (resolution.Assemblies, resolution.Missing);
            references.AddRange(ReferenceSelection.FileReferences(packageAssemblies, issues));
            referenceTiming.Complete();
            referenceTiming.Dispose();
            var missingProjects = new List<ProjectRef>();
            var projectReferenceInputs = new List<string>();
            foreach (var r in p.ProjectReferences)
            {
                var target = r.ResolvedPath is null ? null : projects.FirstOrDefault(x => x.FullPath == r.ResolvedPath);
                projectReferenceInputs.Add(target is null ? $"{r.Include}:missing" : $"{target.RelativePath}:{ArtifactBuilder.Sha256(File.ReadAllText(target.FullPath))}");
                if (target is null || r.ReferenceOutputAssembly && visiting.Contains(target.FullPath))
                {
                    missingProjects.Add(r);
                    continue;
                }

                if (!r.ReferenceOutputAssembly) continue;
                if (p.TargetFramework is null || target.TargetFramework is null)
                {
                    issues.Add(new("UNSUPPORTED_PROJECT_TARGET", $"Project reference {target.RelativePath} cannot be checked because a target framework is unresolved; its symbols are omitted"));
                    continue;
                }

                if (FrameworkSelection.Nearest(p.TargetFramework, [target.TargetFramework]) is null)
                {
                    issues.Add(new("INCOMPATIBLE_PROJECT_TARGET", $"Project {target.RelativePath} targets {target.TargetFramework}, incompatible with {p.TargetFramework}; its symbols are omitted"));
                    continue;
                }

                var dep = Build(target, visiting);
                var sourceInputs = dep.Compilation.SyntaxTrees.Select(t => $"{(t.FilePath == "<implicit-usings>" ? t.FilePath : Relative(t.FilePath))}:{ArtifactBuilder.Sha256(t.GetText().ToString())}");
                projectReferenceInputs.Add($"{target.RelativePath}|{ArtifactBuilder.Sha256(File.ReadAllText(target.FullPath))}|{dep.ReferenceFingerprint}|{string.Join("|", sourceInputs.Order(StringComparer.Ordinal))}");
                references.Add(dep.Compilation.ToMetadataReference());
                references.AddRange(dep.Compilation.References.Where(x => x is CompilationReference
                    || x is PortableExecutableReference pe && !dep.FrameworkPaths.Contains(pe.FilePath!) && packageAssemblies.All(a => a != pe.FilePath)));
            }

            using var parseTiming = options.Measure("cs.parse");
            var parse = new CSharpParseOptions(LanguageVersion.Latest);
            var trees = p.SourceFiles.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, path: f)).ToList();
            var usings = p.ImplicitNamespaces();
            if (usings.Count > 0)
            {
                trees.Add(CSharpSyntaxTree.ParseText(string.Concat(usings.Select(u => $"global using global::{u};\n")), parse, path: "<implicit-usings>"));
            }

            parseTiming.Complete();
            parseTiming.Dispose();
            using var resolutionTiming = options.Measure("cs.reference-resolution");
            var distinctReferences = ReferenceSelection.Resolve(references, frameworkPaths, issues);
            resolutionTiming.Complete();
            resolutionTiming.Dispose();
            var compilation = CSharpCompilation.Create(p.AssemblyName, trees, distinctReferences,
                new CSharpCompilationOptions(p.IsExecutable ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: p.Nullable ? NullableContextOptions.Enable : NullableContextOptions.Disable, checkOverflow: p.CheckedArithmetic));
            var decisions = string.Join("\n", issues.Distinct().OrderBy(i => i.Code, StringComparer.Ordinal).ThenBy(i => i.Message, StringComparer.Ordinal).Select(i => $"{i.Code}:{i.Message}"));
            using var referenceFingerprintTiming = options.Measure("cs.reference-fingerprint");
            var referenceFingerprint = ArtifactBuilder.Sha256($"{ReferenceSelection.Fingerprint(references, frameworkFiles.Concat(packageAssemblies))}|frameworkPackages={frameworkPackages.Fingerprint}|packages={resolution.InputFingerprint}|{string.Join("\n", projectReferenceInputs.Order(StringComparer.Ordinal))}|{decisions}");
            referenceFingerprintTiming.Complete();
            var unit = new Unit(p, compilation, missingPackages, missingProjects, resolution.Resolved, issues, frameworkPaths, referenceFingerprint) { UnselectedPackages = resolution.Unselected };
            compilations[p.FullPath] = unit;
            visiting.Remove(p.FullPath);
            return unit;
        }

        using var compilationTiming = options.Measure("cs.compilations");
        var units = projects.Select(p => Build(p, [])).ToList();
        compilationTiming.Complete();
        compilationTiming.Dispose();
        var repoAssemblies = units.Select(u => u.Project.AssemblyName).ToHashSet(StringComparer.Ordinal);
        var context = new RuleContext(options, units, repoAssemblies);
        var results = units.Select((unit,index) => ExtractUnit(unit,context,index)).ToList();
        total.Complete();
        return results;
    }

    private UnitResult ExtractUnit(Unit unit, RuleContext context, int unitIndex)
    {
        var p = unit.Project;
        var b = new ArtifactBuilder(options.RepositoryId, options.CommitSha, Tool);
        var keys = context.Keys;
        var projectText = File.ReadAllText(p.FullPath);
        var projectKey = $"proj|{options.RepositoryId}|{p.RelativePath}";
        b.Node(projectKey, "Project", p.AssemblyName, [b.LineEvidence(p.RelativePath, p.SdkLine, projectText)]);

        foreach (var issue in unit.ResolutionIssues.Distinct().OrderBy(i => i.Code, StringComparer.Ordinal).ThenBy(i => i.Message, StringComparer.Ordinal))
        {
            b.Diagnostic("warning", issue.Code, issue.Message, p.RelativePath, true, b.LineEvidence(p.RelativePath, p.SdkLine, projectText));
        }

        foreach (var r in unit.MissingProjects)
        {
            b.Diagnostic("error", "MISSING_PROJECT_REFERENCE", $"Referenced project {r.Include} does not exist in the repository", p.RelativePath, true, b.LineEvidence(p.RelativePath, r.Line, projectText));
        }

        foreach (var r in unit.MissingPackages.Where(m => m.Line > 0))
        {
            b.Diagnostic("warning", "UNRESOLVED_PACKAGE", $"Package {r.Id} {r.Version} is not in the packages folder; symbols from it are unresolved", p.RelativePath, true, b.LineEvidence(p.RelativePath, r.Line, projectText));
        }

        foreach (var u in unit.UnselectedPackages)
        {
            // Additional observability; the target-aware resolution issue separately marks any compile coverage gap.
            b.Diagnostic("info", "PACKAGE_ASSEMBLIES_NOT_SELECTED", $"Package {u.Id} {u.Version} ships assemblies ({string.Join(", ", u.Folders)}) but no compatible compile group was selected for {p.TargetFramework ?? "the unresolved target"}; none was referenced", p.RelativePath, false);
        }

        foreach (var r in p.ProjectReferences.Where(r => r.ResolvedPath is not null))
        {
            var target = Path.GetRelativePath(options.SourceRoot, r.ResolvedPath!).Replace('\\', '/');
            b.Edge(projectKey, "DEPENDS_ON", $"proj|{options.RepositoryId}|{target}", [b.LineEvidence(p.RelativePath, r.Line, projectText)]);
        }

        // Source generators are not run because they execute package code; report dependent missing implementations without reducing coverage.
        using var diagnosticTiming = options.Measure("cs.diagnostics",unitIndex);
        var errors = unit.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.IsInSource && d.Location.SourceTree!.FilePath is { Length: > 0 } and not "<implicit-usings>").ToList();
        // Errors on Roslyn's pathless synthesized top-level entry point belong to the project, not a file.
        foreach (var d in unit.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.IsInSource && d.Location.SourceTree!.FilePath.Length == 0).DistinctBy(d => d.Id))
        {
            b.Diagnostic("warning", $"COMPILER_{d.Id}", d.GetMessage(System.Globalization.CultureInfo.InvariantCulture), null, false);
        }
        diagnosticTiming.Complete();
        diagnosticTiming.Dispose();

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
        using var walkTiming = options.Measure("cs.walk",unitIndex);
        var calculationTime = TimeSpan.Zero;
        foreach (var tree in unit.Compilation.SyntaxTrees.Where(t => t.FilePath != "<implicit-usings>"))
        {
            var file = FileOf(tree)!;
            Declarations(b, keys, file, projectKey);
            Calls(b, keys, file);
            Creations(b, keys, file);
            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (file.Model.GetDeclaredSymbol(decl) is IMethodSymbol method && b.HasNode(keys.Of(method)))
                {
                    var started = Stopwatch.GetTimestamp();
                    calculations.Extract(file, decl, method);
                    calculationTime += Stopwatch.GetElapsedTime(started);
                }
            }
        }

        walkTiming.Complete();
        walkTiming.Dispose();
        options.Timing?.Invoke(new("cs.calculations",unitIndex,Math.Round(calculationTime.TotalMilliseconds,3),"completed"));
        var rules = new Rules(context, unit, b, keys, Relative,unitIndex);
        rules.Apply();

        using var fingerprintTiming = options.Measure("cs.fingerprint",unitIndex);
        var inputHash = ArtifactBuilder.Sha256(string.Concat(p.SourceFiles.Append(p.FullPath).Order(StringComparer.Ordinal)
            .Select(f => $"{Relative(f)}\0{ArtifactBuilder.Sha256(File.ReadAllText(f))}\n")));
        // The exact package set is part of the configuration: a different set is a different analysis, never silently different bytes.
        var packageSet = string.Concat(unit.ResolvedPackages.Select(r => $"{r.Id.ToLowerInvariant()}@{r.Version.ToLowerInvariant()}\n"));
        var configHash = ArtifactBuilder.Sha256($"{Tool.Name}/{Tool.Version}|build={Build}|target={p.TargetFramework}|references={unit.ReferenceFingerprint}|includeTests={options.IncludeTests}|packages={(options.PackagesRoot is null ? "none" : options.Lock is null ? "folder" : "lock")}\n{packageSet}");
        fingerprintTiming.Complete();
        fingerprintTiming.Dispose();
        using var serializationTiming = options.Measure("cs.serialize",unitIndex);
        var jsonl = b.ToJsonl(p.RelativePath, p.AssemblyName, "csharp", Rules.Tools, configHash, inputHash, p.SourceFiles.Count);
        serializationTiming.Complete();
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
            if (file.Model.GetDeclaredSymbol(decl) is not INamedTypeSymbol type || type.Name.Length == 0)
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
            if (file.Model.GetDeclaredSymbol(decl) is not IMethodSymbol method || method.Name.Length == 0)
            {
                continue;
            }

            var key = keys.Of(method);
            var header = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(HeaderStart(decl.Modifiers, decl.ReturnType.GetFirstToken()), decl.ParameterList.OpenParenToken.Span.End);
            b.Node(key, "Method", method.Name, [file.Evidence(b, header)], generated: file.Generated);
            b.Edge(keys.Of(method.ContainingType), "DECLARES", key, [file.Evidence(b, header)]);
            foreach (var parameter in decl.ParameterList.Parameters)
            {
                if (file.Model.GetDeclaredSymbol(parameter) is not IParameterSymbol symbol)
                {
                    continue;
                }

                foreach (var type in RepositoryTypes(keys, symbol.Type))
                {
                    b.Edge(key, "REFERENCES", keys.Of(type), [file.Evidence(b, parameter)]);
                }
            }

            foreach (var type in RepositoryTypes(keys, method.ReturnType))
            {
                b.Edge(key, "REFERENCES", keys.Of(type), [file.Evidence(b, decl.ReturnType)]);
            }

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

        // Constructor parameter types are repository REFERENCES because they describe data flow; framework/package types are omitted.
        foreach (var decl in file.Tree.GetRoot().DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            if (file.Model.GetDeclaredSymbol(decl) is not IMethodSymbol ctor || ctor.ContainingType.Name.Length == 0)
            {
                continue;
            }

            var key = keys.Of(ctor);
            var header = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(HeaderStart(decl.Modifiers, decl.Identifier), decl.ParameterList.OpenParenToken.Span.End);
            b.Node(key, "Method", ctor.ContainingType.Name, [file.Evidence(b, header)], signature: decl.ParameterList.ToString(), generated: file.Generated,
                props: new JsonObject { ["constructor"] = true, ["static"] = ctor.IsStatic ? true : null }.WithoutNulls());
            b.Edge(keys.Of(ctor.ContainingType), "DECLARES", key, [file.Evidence(b, header)]);
            foreach (var parameter in decl.ParameterList.Parameters)
            {
                if (file.Model.GetDeclaredSymbol(parameter) is not { } symbol)
                {
                    continue;
                }

                foreach (var type in RepositoryTypes(keys, symbol.Type))
                {
                    b.Edge(key, "REFERENCES", keys.Of(type), [file.Evidence(b, parameter)]);
                }
            }
        }

        foreach (var decl in file.Tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var parameterList = decl switch
            {
                ClassDeclarationSyntax classDecl => classDecl.ParameterList,
                RecordDeclarationSyntax recordDecl => recordDecl.ParameterList,
                _ => null,
            };
            if (parameterList is null || file.Model.GetDeclaredSymbol(decl) is not INamedTypeSymbol type)
            {
                continue;
            }

            var ctor = parameterList.Parameters
                .Select(parameter => file.Model.GetDeclaredSymbol(parameter))
                .OfType<IParameterSymbol>()
                .Select(parameter => parameter.ContainingSymbol)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(candidate => candidate.MethodKind == MethodKind.Constructor)
                ?? type.InstanceConstructors.FirstOrDefault(candidate => candidate.DeclaringSyntaxReferences.Any(reference =>
                    reference.SyntaxTree == decl.SyntaxTree && (reference.Span == decl.Span || reference.Span == parameterList.Span)));
            if (ctor is null || ctor.ContainingType.Name.Length == 0)
            {
                continue;
            }

            var key = keys.Of(ctor);
            var header = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(HeaderStart(decl.Modifiers, decl.Identifier), parameterList.CloseParenToken.Span.End);
            b.Node(key, "Method", ctor.ContainingType.Name, [file.Evidence(b, header)], signature: parameterList.ToString(), generated: file.Generated,
                props: new JsonObject { ["constructor"] = true, ["static"] = ctor.IsStatic ? true : null }.WithoutNulls());
            b.Edge(keys.Of(ctor.ContainingType), "DECLARES", key, [file.Evidence(b, header)]);
            foreach (var parameter in parameterList.Parameters)
            {
                if (file.Model.GetDeclaredSymbol(parameter) is not IParameterSymbol symbol)
                {
                    continue;
                }

                foreach (var referencedType in RepositoryTypes(keys, symbol.Type))
                {
                    b.Edge(key, "REFERENCES", keys.Of(referencedType), [file.Evidence(b, parameter)]);
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
            if (property is null || property.Name.Length == 0)
            {
                continue;
            }

            var key = keys.Of(property);
            var span = decl is PropertyDeclarationSyntax p2 ? Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(HeaderStart(p2.Modifiers, p2.Type.GetFirstToken()), p2.Identifier.Span.End) : decl.Span;
            b.Node(key, "Property", property.Name, [file.Evidence(b, span)], signature: decl is ParameterSyntax ? decl.ToString() : null, generated: file.Generated);
            b.Edge(keys.Of(property.ContainingType), "DECLARES", key, [file.Evidence(b, span)]);
        }

        foreach (var declaration in file.Tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in declaration.Declaration.Variables)
            {
                if (file.Model.GetDeclaredSymbol(variable) is not IFieldSymbol field || field.IsImplicitlyDeclared || field.Name.Length == 0)
                {
                    continue;
                }

                var key = keys.Of(field);
                var evidence = file.Evidence(b, variable.Identifier.Span);
                b.Node(key, "Field", field.Name, [evidence], generated: file.Generated);
                b.Edge(keys.Of(field.ContainingType), "DECLARES", key, [evidence]);
            }
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

    /// <summary>Records constructor calls or type references for repository types; framework/package types are omitted.</summary>
    private static void Creations(ArtifactBuilder b, SymbolKeys keys, SourceFile file)
    {
        foreach (var node in file.Tree.GetRoot().DescendantNodes().Where(n => n is BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax))
        {
            var owner = Owner(file.Model, node.SpanStart);
            if (owner is null)
            {
                // Deferred anonymous bodies are not part of this bounded field-initializer observation.
                var variable = node.Ancestors().TakeWhile(ancestor => ancestor is not AnonymousFunctionExpressionSyntax)
                    .OfType<VariableDeclaratorSyntax>().FirstOrDefault(candidate =>
                        candidate.Parent?.Parent is FieldDeclarationSyntax && candidate.Initializer is not null);
                owner = variable is null ? null : file.Model.GetDeclaredSymbol(variable) as IFieldSymbol;
            }

            if (owner is null || !b.HasNode(keys.Of(owner)) || file.Model.GetSymbolInfo(node).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            {
                continue; // ponytail: an unresolved `new X()` is not recorded; invocations are, because a call is a claim about behaviour and a missing type is a compile diagnostic already
            }

            var type = ctor.ContainingType.OriginalDefinition;
            if (keys.Of(type).StartsWith("cs|@ext|", StringComparison.Ordinal))
            {
                continue;
            }

            var from = keys.Of(owner);
            if (owner is not IFieldSymbol && IsDeclaredConstructor(ctor))
            {
                b.Edge(from, "CALLS", keys.Of(ctor.OriginalDefinition), [file.Evidence(b, node)]);
            }
            else
            {
                b.Edge(from, "REFERENCES", keys.Of(type), [file.Evidence(b, node)]);
            }
        }
    }

    private static bool IsDeclaredConstructor(IMethodSymbol ctor) => ctor.OriginalDefinition.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() switch
    {
        ConstructorDeclarationSyntax => true,
        ClassDeclarationSyntax { ParameterList: not null } => true,
        RecordDeclarationSyntax { ParameterList: not null } => true,
        ParameterListSyntax { Parent: ClassDeclarationSyntax { ParameterList: not null } } => true,
        ParameterListSyntax { Parent: RecordDeclarationSyntax { ParameterList: not null } } => true,
        _ => false,
    });

    /// <summary>The repository types a type mentions: itself, its type arguments and its element type (<c>IReadOnlyList&lt;Cart&gt;</c> mentions Cart).</summary>
    private static IEnumerable<INamedTypeSymbol> RepositoryTypes(SymbolKeys keys, ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return RepositoryTypes(keys, array.ElementType);
            case INamedTypeSymbol named:
                var self = keys.Of(named).StartsWith("cs|@ext|", StringComparison.Ordinal) ? [] : new[] { named.OriginalDefinition };
                return self.Concat(named.TypeArguments.SelectMany(a => RepositoryTypes(keys, a))).DistinctBy(t => keys.Of(t), StringComparer.Ordinal);
            default:
                return [];
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
            IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor } m => m,
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
