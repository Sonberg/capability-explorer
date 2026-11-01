using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>Repository-wide facts the per-unit rules need (route groups, EF contexts, typed clients, ...).</summary>
internal sealed class RuleContext(ExtractorOptions options, IReadOnlyList<CSharpExtractor.Unit> units, IReadOnlySet<string> repoAssemblies)
{
    public ExtractorOptions Options => options;

    public IReadOnlyList<CSharpExtractor.Unit> Units => units;

    public IReadOnlySet<string> RepoAssemblies => repoAssemblies;

    public IEnumerable<(SyntaxTree Tree, SemanticModel Model, CSharpExtractor.Unit Unit)> Trees() =>
        units.SelectMany(u => u.Compilation.SyntaxTrees.Where(t => t.FilePath != "<implicit-usings>").Select(t => (t, u.Compilation.GetSemanticModel(t), u)));
}

internal sealed partial class Rules(RuleContext context, CSharpExtractor.Unit unit, ArtifactBuilder b, SymbolKeys keys, Func<string, string> relative)
{
    public static readonly Tool Routes = new("aspnetcore.routes", "1");
    public static readonly Tool Di = new("dotnet.di", "1");
    public static readonly Tool EfRule = new("efcore", "1");
    public static readonly Tool Dapper = new("dapper.sql", "1");
    public static readonly Tool ServiceBus = new("azure.servicebus", "1");
    public static readonly Tool Http = new("dotnet.httpclient", "1");
    public static readonly IReadOnlyList<Tool> Tools = [Routes, Di, EfRule, Dapper, ServiceBus, Http];

    private static readonly string[] HttpVerbs = ["Get", "Post", "Put", "Delete", "Patch"];
    private static readonly HashSet<string> DapperMethods = ["Query", "QueryAsync", "QueryFirst", "QueryFirstAsync", "QueryFirstOrDefault", "QueryFirstOrDefaultAsync", "QuerySingle", "QuerySingleAsync",
        "QuerySingleOrDefault", "QuerySingleOrDefaultAsync", "QueryMultiple", "QueryMultipleAsync", "Execute", "ExecuteAsync", "ExecuteScalar", "ExecuteScalarAsync", "ExecuteReader", "ExecuteReaderAsync"];
    private static readonly HashSet<string> HttpClientMethods = ["GetAsync", "GetFromJsonAsync", "GetStringAsync", "GetStreamAsync", "GetByteArrayAsync", "PostAsync", "PostAsJsonAsync",
        "PutAsync", "PutAsJsonAsync", "DeleteAsync", "DeleteFromJsonAsync", "PatchAsync", "PatchAsJsonAsync"];

    private string Repo => context.Options.RepositoryId;

    private string Assembly => unit.Project.AssemblyName;

    private string Unbound(string kind, string value) => $"@unbound:{Repo}/{Assembly}:{kind}={value}";

    private IEnumerable<CSharpExtractor.SourceFile> Files() => unit.Compilation.SyntaxTrees.Where(t => t.FilePath != "<implicit-usings>")
        .Select(t => new CSharpExtractor.SourceFile(relative(t.FilePath), t.GetText().ToString(), t, unit.Compilation.GetSemanticModel(t)));

    private string? OwnerKey(CSharpExtractor.SourceFile f, SyntaxNode node) =>
        CSharpExtractor.Owner(f.Model, node.SpanStart) is { } owner && b.HasNode(keys.Of(owner)) ? keys.Of(owner) : null;

    public void Apply()
    {
        var files = Files().ToList();
        AspNet(files);
        DependencyInjection(files);
        EntityFramework(files);
        DapperSql(files);
        ServiceBusChannels(files);
        HttpClients(files);
    }

    // ---------------------------------------------------------------- shared helpers

    /// <summary>A string the code fixes at compile time: literals, constants, interpolations (holes become {}), locals initialised from those.</summary>
    internal static (string Value, List<SyntaxNode> Sources)? ConstString(SemanticModel model, ExpressionSyntax? expr, int depth = 0)
    {
        if (expr is null || depth > 4)
        {
            return null;
        }

        switch (expr)
        {
            case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                return (lit.Token.ValueText, [lit]);
            case InterpolatedStringExpressionSyntax interp:
                return (string.Concat(interp.Contents.Select(c => c is InterpolatedStringTextSyntax t ? t.TextToken.ValueText : "{}")), [interp]);
            case ParenthesizedExpressionSyntax p:
                return ConstString(model, p.Expression, depth + 1);
        }

        if (model.GetConstantValue(expr) is { HasValue: true, Value: string constant })
        {
            var symbol = model.GetSymbolInfo(expr).Symbol;
            var declaration = symbol?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
            return (constant, declaration?.Initializer?.Value is { } v ? [v, expr] : [expr]);
        }

        if (model.GetSymbolInfo(expr).Symbol is ILocalSymbol local
            && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } init })
        {
            return ConstString(model, init, depth + 1);
        }

        return null;
    }

    /// <summary>Routes compare by shape: parameters become {}, no query string, lower case, no trailing slash.</summary>
    internal static string NormaliseRoute(string route)
    {
        var path = route.Split('?', '#')[0];
        path = RouteParameter().Replace(path, "{}");
        path = "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return path.ToLowerInvariant();
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteParameter();

    private static string Join(string prefix, string route) =>
        "/" + string.Join('/', (prefix + "/" + route).Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static string? MethodName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        _ => null,
    };

    private static ExpressionSyntax? Receiver(InvocationExpressionSyntax inv) => (inv.Expression as MemberAccessExpressionSyntax)?.Expression;

    private bool InRepo(ITypeSymbol? t) => t is not null && keys.Of(t).StartsWith($"cs|{Repo}|", StringComparison.Ordinal);

    private static SeparatedSyntaxList<TypeSyntax> TypeArguments(InvocationExpressionSyntax inv) =>
        (inv.Expression switch { MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g, GenericNameSyntax g => g, _ => null })?.TypeArgumentList.Arguments ?? default;

    // ---------------------------------------------------------------- ASP.NET Core

    private void AspNet(List<CSharpExtractor.SourceFile> files)
    {
        string? service = null;
        if (unit.Project.IsWeb || unit.Project.IsWorker)
        {
            var text = File.ReadAllText(unit.Project.FullPath);
            service = $"svc|{Repo}|{Assembly}";
            b.Node(service, "Service", Assembly, [b.LineEvidence(unit.Project.RelativePath, unit.Project.SdkLine, text)], props: new JsonObject { ["sdk"] = unit.Project.Sdk });
        }

        void Endpoint(string verb, string route, string style, string? handler, List<(CSharpExtractor.SourceFile File, SyntaxNode Node)> sites, JsonObject? handlerProps = null)
        {
            var normalised = NormaliseRoute(route);
            var key = $"http|{Repo}/{Assembly}|{verb} {normalised}";
            b.Node(key, "Endpoint", $"{verb} {route}", sites.Select(s => s.File.Evidence(b, s.Node)), props: new JsonObject
            {
                ["method"] = verb, ["route"] = route, ["normalizedRoute"] = normalised, ["style"] = style,
            });
            var last = sites[^1];
            if (service is not null)
            {
                b.Edge(service, "EXPOSES", key, [last.File.Evidence(b, last.Node)], "rule", rule: Routes);
            }

            if (handler is not null)
            {
                b.Edge(handler, "HANDLES", key, [last.File.Evidence(b, last.Node)], "rule", rule: Routes, props: handlerProps);
            }
        }

        foreach (var f in files)
        {
            // Controllers: [Route] on the class combined with [Http*] / [Route] on actions.
            foreach (var cls in f.Tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var classRoute = Attribute(cls.AttributeLists, "Route");
                var controller = cls.Identifier.ValueText.EndsWith("Controller", StringComparison.Ordinal) || Attribute(cls.AttributeLists, "ApiController") is not null;
                if (!controller)
                {
                    continue;
                }

                foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
                {
                    foreach (var verb in HttpVerbs)
                    {
                        if (Attribute(method.AttributeLists, $"Http{verb}") is not { } attr)
                        {
                            continue;
                        }

                        var template = attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression is { } e ? ConstString(f.Model, e)?.Value ?? "" : "";
                        var prefix = classRoute?.ArgumentList?.Arguments.FirstOrDefault()?.Expression is { } ce ? ConstString(f.Model, ce)?.Value ?? "" : "";
                        prefix = prefix.Replace("[controller]", cls.Identifier.ValueText.Replace("Controller", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)
                            .Replace("[action]", method.Identifier.ValueText, StringComparison.OrdinalIgnoreCase);
                        var route = template.StartsWith('/') || template.StartsWith('~') ? template.TrimStart('~') : Join(prefix, template);
                        var handler = f.Model.GetDeclaredSymbol(method) is { } m ? keys.Of(m) : null;
                        List<(CSharpExtractor.SourceFile, SyntaxNode)> sites = classRoute is null ? [(f, attr)] : [(f, classRoute), (f, attr)];
                        Endpoint(verb.ToUpperInvariant(), route, "controller", handler, sites);
                    }
                }
            }

            // Minimal APIs: Map{Verb}(route, handler), with prefixes from MapGroup chains, locals and extension-method receivers.
            foreach (var inv in f.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = MethodName(inv);
                if (name is null || !name.StartsWith("Map", StringComparison.Ordinal) || HttpVerbs.FirstOrDefault(v => name == $"Map{v}") is not { } verb)
                {
                    continue;
                }

                var args = inv.ArgumentList.Arguments;
                if (args.Count < 2 || ConstString(f.Model, args[0].Expression) is not { } route)
                {
                    continue;
                }

                var (handler, props) = args[1].Expression switch
                {
                    LambdaExpressionSyntax or AnonymousMethodExpressionSyntax => (OwnerKey(f, inv), new JsonObject { ["handler"] = "lambda" }),
                    var h when f.Model.GetSymbolInfo(h).Symbol is IMethodSymbol m => (keys.Of(m), null),
                    var h when f.Model.GetSymbolInfo(h).CandidateSymbols.FirstOrDefault() is IMethodSymbol m => (keys.Of(m), null),
                    _ => (OwnerKey(f, inv), null),
                };
                foreach (var (prefix, sites) in Prefixes(f, Receiver(inv), 0))
                {
                    Endpoint(verb.ToUpperInvariant(), Join(prefix, route.Value), "minimal-api", handler, [.. sites, (f, inv)], props);
                }
            }
        }
    }

    private static AttributeSyntax? Attribute(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes).FirstOrDefault(a => a.Name.ToString() is var n && (n == name || n == name + "Attribute" || n.EndsWith("." + name, StringComparison.Ordinal)));

    /// <summary>Route prefixes that reach a MapX receiver: MapGroup chains, locals and extension-method receivers.</summary>
    private List<(string Prefix, List<(CSharpExtractor.SourceFile, SyntaxNode)> Sites)> Prefixes(CSharpExtractor.SourceFile f, ExpressionSyntax? receiver, int depth)
    {
        if (receiver is null || depth > 5)
        {
            return [("", [])];
        }

        if (receiver is InvocationExpressionSyntax inv && MethodName(inv) == "MapGroup" && inv.ArgumentList.Arguments.FirstOrDefault() is { } a && ConstString(f.Model, a.Expression) is { } group)
        {
            return Prefixes(f, Receiver(inv), depth + 1).Select(p => (Join(p.Prefix, group.Value), new List<(CSharpExtractor.SourceFile, SyntaxNode)>([.. p.Sites, (f, inv)]))).ToList();
        }

        var symbol = f.Model.GetSymbolInfo(receiver).Symbol;
        if (symbol is ILocalSymbol local && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } init })
        {
            return Prefixes(f, init, depth + 1);
        }

        if (symbol is IParameterSymbol { Ordinal: 0, ContainingSymbol: IMethodSymbol { IsExtensionMethod: true } ext })
        {
            var result = new List<(string, List<(CSharpExtractor.SourceFile, SyntaxNode)>)>();
            foreach (var (tree, model, u) in context.Trees())
            {
                var file = new CSharpExtractor.SourceFile(relative(tree.FilePath), tree.GetText().ToString(), tree, model);
                foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(call).Symbol is IMethodSymbol m && SymbolEqualityComparer.Default.Equals((m.ReducedFrom ?? m).OriginalDefinition, ext.OriginalDefinition))
                    {
                        result.AddRange(Prefixes(file, Receiver(call), depth + 1));
                    }
                }
            }

            return result.Count > 0 ? result : [("", [])];
        }

        return [("", [])];
    }

    // ---------------------------------------------------------------- dependency injection

    private void DependencyInjection(List<CSharpExtractor.SourceFile> files)
    {
        foreach (var f in files)
        {
            foreach (var inv in f.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = MethodName(inv);
                if (name is null || OwnerKey(f, inv) is not { } owner)
                {
                    continue;
                }

                var keyed = name.StartsWith("AddKeyed", StringComparison.Ordinal);
                var lifetime = name.Replace("AddKeyed", "Add", StringComparison.Ordinal) switch
                {
                    "AddScoped" => "scoped",
                    "AddTransient" => "transient",
                    "AddSingleton" => "singleton",
                    "AddHostedService" => "hostedService",
                    "AddHttpClient" => "transient",
                    _ => null,
                };
                if (lifetime is null)
                {
                    continue;
                }

                var typeArgs = TypeArguments(inv).Select(t => f.Model.GetTypeInfo(t).Type).ToList();
                var args = inv.ArgumentList.Arguments.Select(a => a.Expression).ToList();
                var typeofs = args.OfType<TypeOfExpressionSyntax>().Select(t => f.Model.GetTypeInfo(t.Type).Type).ToList();
                ITypeSymbol? service = null;
                ITypeSymbol? implementation = null;
                var openGeneric = false;
                if (typeArgs.Count == 2)
                {
                    (service, implementation) = (typeArgs[0], typeArgs[1]);
                }
                else if (typeArgs.Count == 1)
                {
                    service = typeArgs[0];
                    implementation = args.OfType<LambdaExpressionSyntax>().SelectMany(l => l.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
                        .Select(o => f.Model.GetTypeInfo(o).Type).FirstOrDefault(InRepo) ?? service;
                }
                else if (typeofs.Count > 0)
                {
                    (service, implementation) = (typeofs[0], typeofs.Count > 1 ? typeofs[1] : typeofs[0]);
                    openGeneric = implementation is INamedTypeSymbol { IsUnboundGenericType: true };
                }

                // A type parameter (AddScoped<TService, TImpl>() inside a generic helper) names no concrete class.
                if (implementation is not INamedTypeSymbol named || !InRepo(named))
                {
                    continue;
                }

                var props = new JsonObject { ["lifetime"] = lifetime };
                if (service is not null && !SymbolEqualityComparer.Default.Equals(service, implementation))
                {
                    props["serviceType"] = keys.Of(service);
                }

                if (keyed && args.FirstOrDefault() is { } k && ConstString(f.Model, k) is { } key)
                {
                    props["serviceKey"] = key.Value;
                }

                if (openGeneric)
                {
                    props["openGeneric"] = true;
                }

                if (name == "AddHttpClient")
                {
                    props["registration"] = "typedHttpClient";
                }

                b.Edge(owner, "REGISTERS", keys.Of(named.ConstructedFrom), [f.Evidence(b, inv)], "rule", rule: Di, props: props);
            }
        }

        KeyedResolution(files);
        Reflection(files);
    }

    /// <summary>
    /// GetRequiredKeyedService&lt;T&gt;(key): a constant key resolves to its registration; a runtime key can be any
    /// keyed registration of T, so each becomes a candidate.
    /// </summary>
    private void KeyedResolution(List<CSharpExtractor.SourceFile> files)
    {
        var registrations = new List<(string Service, string Implementation, string? Key)>();
        foreach (var (tree, model, _) in context.Trees())
        {
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => MethodName(i)?.StartsWith("AddKeyed", StringComparison.Ordinal) == true))
            {
                var args = TypeArguments(inv).Select(t => model.GetTypeInfo(t).Type).ToList();
                if (args is [{ } service, { } implementation] && InRepo(implementation))
                {
                    registrations.Add((keys.Of(service), keys.Of(implementation), ConstString(model, inv.ArgumentList.Arguments.FirstOrDefault()?.Expression)?.Value));
                }
            }
        }

        foreach (var f in files)
        {
            foreach (var inv in f.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (MethodName(inv) is not ("GetRequiredKeyedService" or "GetKeyedService") || OwnerKey(f, inv) is not { } owner
                    || TypeArguments(inv).FirstOrDefault() is not { } typeArg || f.Model.GetTypeInfo(typeArg).Type is not { } serviceType)
                {
                    continue;
                }

                var service = keys.Of(serviceType);
                var key = ConstString(f.Model, inv.ArgumentList.Arguments.LastOrDefault()?.Expression)?.Value;
                foreach (var r in registrations.Where(r => r.Service == service && (key is null || r.Key == key)))
                {
                    b.Edge(owner, "REFERENCES", r.Implementation, [f.Evidence(b, inv)], "rule", key is null ? "candidate" : "resolved", rule: Di,
                        props: new JsonObject { ["serviceType"] = service, ["serviceKey"] = key ?? r.Key, ["keySource"] = key is null ? "runtime" : "constant" });
                }
            }
        }
    }

    /// <summary>Type.GetType with a runtime name: the created type is unknowable statically, and says so.</summary>
    private void Reflection(List<CSharpExtractor.SourceFile> files)
    {
        foreach (var f in files)
        {
            foreach (var inv in f.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetType", Expression: var target } && target.ToString() is "Type" or "System.Type"
                    && inv.ArgumentList.Arguments.Count > 0 && ConstString(f.Model, inv.ArgumentList.Arguments[0].Expression) is null && OwnerKey(f, inv) is { } owner)
                {
                    var creation = inv.Ancestors().TakeWhile(a => a is not StatementSyntax).OfType<InvocationExpressionSyntax>().FirstOrDefault()
                        ?? f.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => i.ToString().StartsWith("Activator.CreateInstance", StringComparison.Ordinal) && i.SpanStart > inv.SpanStart && OwnerKey(f, i) == owner);
                    b.Edge(owner, "REFERENCES", null, [f.Evidence(b, inv)], "rule", "unresolved", "reflection", Di,
                        new JsonObject { ["expression"] = creation?.ToString() ?? inv.ToString() });
                }
            }
        }
    }

    // ---------------------------------------------------------------- Entity Framework Core

    private sealed record EfModel(Dictionary<string, (string Table, List<(CSharpExtractor.SourceFile, SyntaxNode)> Sites, string Mapping)> Entities, Dictionary<string, string> DbSets, string Connection, string? ConnectionSite, string? Schema);

    private EfModel? _ef;

    /// <summary>Entity → table across the repository: ToTable &gt; [Table] &gt; DbSet name, with the provider's default schema.</summary>
    private EfModel LoadEf()
    {
        if (_ef is not null)
        {
            return _ef;
        }

        var entities = new Dictionary<string, (string, List<(CSharpExtractor.SourceFile, SyntaxNode)>, string)>(StringComparer.Ordinal);
        var dbSets = new Dictionary<string, string>(StringComparer.Ordinal);
        string? schema = null;
        var connection = "unknown";
        string? connectionSite = null;
        var all = context.Trees().Select(t => new CSharpExtractor.SourceFile(relative(t.Tree.FilePath), t.Tree.GetText().ToString(), t.Tree, t.Model)).ToList();

        foreach (var f in all)
        {
            foreach (var inv in f.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                switch (MethodName(inv))
                {
                    case "UseNpgsql":
                        schema = "public";
                        break;
                    case "UseSqlServer":
                        schema ??= "dbo";
                        break;
                    case "AddDbContext" or "AddDbContextPool" or "AddDbContextFactory":
                        if (inv.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => MethodName(i) == "GetConnectionString") is { } gcs
                            && ConstString(f.Model, gcs.ArgumentList.Arguments.FirstOrDefault()?.Expression) is { } name)
                        {
                            connection = name.Value;
                            connectionSite = f.Evidence(b, gcs);
                        }

                        break;
                }
            }
        }

        foreach (var f in all)
        {
            foreach (var prop in f.Tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (f.Model.GetDeclaredSymbol(prop) is { Type: INamedTypeSymbol { Name: "DbSet", TypeArguments: [var entity] } } dbSet)
                {
                    var key = keys.Of(entity);
                    dbSets[keys.Of(dbSet)] = key;
                    entities.TryAdd(key, (dbSet.Name, [(f, prop)], "convention"));
                }
            }

            foreach (var cls in f.Tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (Attribute(cls.AttributeLists, "Table") is { ArgumentList.Arguments: [var first, ..] } attr && ConstString(f.Model, first.Expression) is { } table
                    && f.Model.GetDeclaredSymbol(cls) is { } type)
                {
                    var schemaArg = attr.ArgumentList.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Schema");
                    var s = schemaArg is null ? null : ConstString(f.Model, schemaArg.Expression)?.Value;
                    entities[keys.Of(type)] = (s is null ? table.Value : $"{s}.{table.Value}", [(f, attr)], "attribute");
                }
            }

            foreach (var inv in f.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => MethodName(i) == "ToTable"))
            {
                var args = inv.ArgumentList.Arguments;
                if (args.Count == 0 || ConstString(f.Model, args[0].Expression) is not { } table)
                {
                    continue;
                }

                var s = args.Count > 1 ? ConstString(f.Model, args[1].Expression)?.Value : null;
                var entity = inv.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault(a => MethodName(a) == "Entity" && TypeArguments(a).Any()) is { } entityCall
                    ? f.Model.GetTypeInfo(TypeArguments(entityCall).First()).Type
                    : (f.Model.GetTypeInfo(Receiver(inv)!).Type as INamedTypeSymbol)?.TypeArguments.FirstOrDefault();
                if (entity is not null)
                {
                    entities[keys.Of(entity)] = (s is null ? table.Value : $"{s}.{table.Value}", [(f, inv)], "fluent");
                }
            }
        }

        // Apply the provider default schema to unqualified tables.
        foreach (var k in entities.Keys.ToList())
        {
            var (table, sites, mapping) = entities[k];
            entities[k] = (!table.Contains('.', StringComparison.Ordinal) && schema is not null ? $"{schema}.{table}" : table, sites, mapping);
        }

        return _ef = new EfModel(entities, dbSets, connection, connectionSite, schema);
    }

    private string TableKey(string connection, string table) => $"sql|{Unbound("conn", connection)}|{table}";

    private void EntityFramework(List<CSharpExtractor.SourceFile> files)
    {
        var ef = LoadEf();
        var hasContext = files.Any(f => f.Tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Any(c => f.Model.GetDeclaredSymbol(c)?.BaseType?.Name == "DbContext"));
        foreach (var (entity, (table, sites, mapping)) in ef.Entities)
        {
            if (!b.HasNode(entity) || !hasContext && !sites.Any(s => files.Any(f => f.Path == s.Item1.Path)))
            {
                continue;
            }

            var evidence = sites.Select(s => s.Item1.Evidence(b, s.Item2)).ToList();
            if (ef.ConnectionSite is not null)
            {
                evidence.Add(ef.ConnectionSite);
            }

            b.Edge(entity, "MAPS_TO", TableKey(ef.Connection, table), evidence, "rule", "unresolved", "configuration", EfRule,
                new JsonObject { ["mapping"] = mapping, ["connectionName"] = ef.Connection, ["table"] = table });
        }

        // Generic repositories: methods on T that read or write db.Set<T>().
        var generic = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (tree, model, _) in context.Trees())
        {
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => MethodName(i) == "Set" && TypeArguments(i).Any()))
            {
                if (model.GetTypeInfo(TypeArguments(inv).First()).Type is ITypeParameterSymbol && CSharpExtractor.Owner(model, inv.SpanStart) is IMethodSymbol m)
                {
                    var writes = inv.Ancestors().OfType<InvocationExpressionSyntax>().Any(a => MethodName(a) is "Add" or "AddRange" or "Remove" or "RemoveRange" or "Update" or "AddAsync");
                    generic[keys.Of(m)] = writes ? "WRITES" : "READS";
                }
            }
        }

        foreach (var f in files)
        {
            foreach (var node in f.Tree.GetRoot().DescendantNodes())
            {
                if (node is MemberAccessExpressionSyntax access && f.Model.GetSymbolInfo(access).Symbol is IPropertySymbol prop
                    && ef.DbSets.TryGetValue(keys.Of(prop), out var entity) && ef.Entities.TryGetValue(entity, out var mapped) && OwnerKey(f, access) is { } owner)
                {
                    var writes = access.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" or "AddRange" or "Remove" or "Update" or "AddAsync" };
                    b.Edge(owner, writes ? "WRITES" : "READS", TableKey(ef.Connection, mapped.Table), [f.Evidence(b, access)], "rule", "unresolved", "configuration", EfRule,
                        new JsonObject { ["via"] = keys.Of(prop.ContainingType), ["connectionName"] = ef.Connection });
                }
                else if (node is InvocationExpressionSyntax inv && f.Model.GetSymbolInfo(inv).Symbol is IMethodSymbol target
                    && generic.TryGetValue(keys.Of(target.OriginalDefinition), out var relation)
                    && target.ContainingType is { TypeArguments: [var typeArg] } && ef.Entities.TryGetValue(keys.Of(typeArg), out var entityTable) && OwnerKey(f, inv) is { } caller)
                {
                    b.Edge(caller, relation, TableKey(ef.Connection, entityTable.Table), [f.Evidence(b, inv)], "rule", "unresolved", "configuration", EfRule,
                        new JsonObject { ["via"] = keys.Of(target.OriginalDefinition), ["typeArgument"] = keys.Of(typeArg) });
                }
            }
        }
    }

    // ---------------------------------------------------------------- Dapper / raw SQL

    [GeneratedRegex(@"\b(?<verb>FROM|JOIN|INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO|MERGE)\s+(?<table>[\[\]\w]+(?:\.[\[\]\w]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlTables();

    internal static IEnumerable<(string Relation, string Table)> TablesIn(string sql) =>
        SqlTables().Matches(sql).Select(m => (
            m.Groups["verb"].Value.ToUpperInvariant() is "FROM" or "JOIN" ? "READS" : "WRITES",
            m.Groups["table"].Value.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal)))
        .Where(t => !t.Item2.StartsWith('@') && !t.Item2.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        .Distinct();

    private void DapperSql(List<CSharpExtractor.SourceFile> files)
    {
        var connections = files.SelectMany(f => f.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => MethodName(i) == "GetConnectionString")
                .Select(i => (Name: ConstString(f.Model, i.ArgumentList.Arguments.FirstOrDefault()?.Expression)?.Value, Site: f.Evidence(b, i))))
            .Where(c => c.Name is not null)
            .ToList();
        var names = connections.Select(c => c.Name!).Distinct().ToList();
        var connection = names.Count == 1 ? names[0] : "unknown";

        foreach (var f in files)
        {
            foreach (var inv in f.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (MethodName(inv) is not { } name || !DapperMethods.Contains(name) || OwnerKey(f, inv) is not { } owner
                    || ConstString(f.Model, inv.ArgumentList.Arguments.FirstOrDefault()?.Expression) is not { } sql)
                {
                    continue;
                }

                var evidence = sql.Sources.Select(s => f.Evidence(b, s)).Append(f.Evidence(b, inv)).ToList();
                if (connections.FirstOrDefault(c => c.Name == connection).Site is { } site)
                {
                    evidence.Add(site);
                }

                foreach (var (relation, table) in TablesIn(sql.Value))
                {
                    b.Edge(owner, relation, TableKey(connection, table), evidence, "rule", "unresolved", "configuration", Dapper,
                        new JsonObject { ["connectionName"] = connection, ["sqlAnalysis"] = sql.Value.Contains("{}", StringComparison.Ordinal) ? "partial" : "full" });
                }
            }
        }
    }

    // ---------------------------------------------------------------- Azure Service Bus

    private void ServiceBusChannels(List<CSharpExtractor.SourceFile> files)
    {
        string? ns = null;
        string? nsSite = null;
        foreach (var f in files)
        {
            foreach (var creation in f.Tree.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (f.Model.GetTypeInfo(creation).Type?.Name != "ServiceBusClient" && (creation as ObjectCreationExpressionSyntax)?.Type.ToString() != "ServiceBusClient")
                {
                    continue;
                }

                var arg = creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                ns = arg switch
                {
                    ElementAccessExpressionSyntax ea when ConstString(f.Model, ea.ArgumentList.Arguments.FirstOrDefault()?.Expression) is { } k => Unbound("config", k.Value),
                    InvocationExpressionSyntax i when MethodName(i) == "GetConnectionString" && ConstString(f.Model, i.ArgumentList.Arguments.FirstOrDefault()?.Expression) is { } c => Unbound("conn", c.Value),
                    _ when ConstString(f.Model, arg) is { Value: var v } && ServiceBusNamespace.Parse(v) is { } host => host,
                    _ => Unbound("config", "unknown"),
                };
                nsSite = f.Evidence(b, arg ?? (SyntaxNode)creation);
            }
        }

        ns ??= Unbound("config", "unknown");
        var bound = !ns.StartsWith("@unbound:", StringComparison.Ordinal);
        foreach (var f in files)
        {
            foreach (var inv in f.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = MethodName(inv);
                if (name is not ("CreateSender" or "CreateProcessor" or "CreateReceiver" or "CreateSessionProcessor") || OwnerKey(f, inv) is not { } owner)
                {
                    continue;
                }

                var args = inv.ArgumentList.Arguments;
                if (args.Count == 0 || ConstString(f.Model, args[0].Expression) is not { } entityName)
                {
                    continue;
                }

                // A sender cannot tell a queue from a topic; a processor/receiver with a subscription proves a topic.
                var entityType = name == "CreateSender" ? "entity" : args.Count >= 2 && ConstString(f.Model, args[1].Expression) is not null ? "topic" : "queue";
                var key = $"chan|servicebus|{ns}|{entityType}/{entityName.Value}";
                var evidence = entityName.Sources.Select(s => f.Evidence(b, s)).Append(f.Evidence(b, inv)).ToList();
                if (nsSite is not null)
                {
                    evidence.Add(nsSite);
                }

                b.Node(key, "Channel", entityName.Value, evidence, props: new JsonObject { ["provider"] = "azure-servicebus", ["entityType"] = entityType == "entity" ? "queue-or-topic" : entityType });
                var props = new JsonObject();
                if (entityType == "topic")
                {
                    props["subscription"] = ConstString(f.Model, args[1].Expression)!.Value.Value;
                }

                b.Edge(owner, name == "CreateSender" ? "PUBLISHES" : "CONSUMES", key, evidence, "rule", bound ? "resolved" : "unresolved", bound ? null : "configuration", ServiceBus, props);
            }
        }
    }

    // ---------------------------------------------------------------- HttpClient

    private void HttpClients(List<CSharpExtractor.SourceFile> files)
    {
        // Typed clients: AddHttpClient<T>(c => c.BaseAddress = new Uri(<expr>)).
        var bases = new Dictionary<string, (string Hint, List<(CSharpExtractor.SourceFile File, SyntaxNode Node)> Sources, bool Ambiguous)>(StringComparer.Ordinal);
        foreach (var (tree, model, _) in context.Trees())
        {
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => MethodName(i) == "AddHttpClient" && TypeArguments(i).Any()))
            {
                if (model.GetTypeInfo(TypeArguments(inv).Last()).Type is not { } clientType)
                {
                    continue;
                }

                var assignments = inv.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.Left.ToString().EndsWith("BaseAddress", StringComparison.Ordinal)).ToArray();
                var assignment = assignments.FirstOrDefault();
                var uriArg = assignment?.Right.DescendantNodesAndSelf().OfType<BaseObjectCreationExpressionSyntax>().FirstOrDefault()?.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                var hint = uriArg switch
                {
                    PostfixUnaryExpressionSyntax { Operand: ElementAccessExpressionSyntax ea } when ConstString(model, ea.ArgumentList.Arguments.FirstOrDefault()?.Expression) is { } k => $"config={k.Value}",
                    ElementAccessExpressionSyntax ea when ConstString(model, ea.ArgumentList.Arguments.FirstOrDefault()?.Expression) is { } k => $"config={k.Value}",
                    _ when ConstString(model, uriArg) is { } literal => $"base={literal.Value}",
                    _ => $"client={clientType.Name}",
                };
                var key = keys.Of(clientType);
                var source = new CSharpExtractor.SourceFile(relative(tree.FilePath), tree.GetText().ToString(), tree, model);
                var proof = new List<(CSharpExtractor.SourceFile File, SyntaxNode Node)> { (source, inv) };
                var ambiguous = assignments.Length != 1;
                if (bases.TryGetValue(key, out var previous)) { proof.AddRange(previous.Sources); ambiguous = true; }
                bases[key] = (hint, proof, ambiguous);
            }
        }

        foreach (var f in files)
        {
            foreach (var inv in f.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (MethodName(inv) is not { } name || !HttpClientMethods.Contains(name) || OwnerKey(f, inv) is not { } owner)
                {
                    continue;
                }

                var receiverType = Receiver(inv) is { } r ? f.Model.GetTypeInfo(r).Type : null;
                if (receiverType is not null && receiverType.Name != "HttpClient")
                {
                    continue;
                }

                if (ConstString(f.Model, inv.ArgumentList.Arguments.FirstOrDefault()?.Expression) is not { } url)
                {
                    b.Edge(owner, "REQUESTS", null, [f.Evidence(b, inv)], "rule", "unresolved", "dynamic-string", Http);
                    continue;
                }

                var verb = HttpVerbs.First(v => name.StartsWith(v, StringComparison.Ordinal)).ToUpperInvariant();
                var containing = CSharpExtractor.Owner(f.Model, inv.SpanStart)?.ContainingType;
                var hasBase = containing is not null && bases.ContainsKey(keys.Of(containing));
                var configured = hasBase ? bases[keys.Of(containing!)] : default;
                var hint = hasBase ? configured.Hint : $"client={containing?.Name ?? "unknown"}";
                // A literal absolute request ignores any base; otherwise ambiguous registrations cannot bind it.
                var absolute = url.Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                var proof = url.Sources.Select(s => f.Evidence(b, s)).Append(f.Evidence(b, inv)).ToList();
                if (hasBase && !absolute) proof.AddRange(configured.Sources.Select(s => s.File.Evidence(b, s.Node)));
                var target = hasBase && configured.Ambiguous && !absolute ? null : HttpRequestTarget.Parse(url.Value, hint);
                if (target is null)
                {
                    b.Edge(owner, "REQUESTS", null, proof, "rule", "unresolved", "dynamic-string", Http);
                    continue;
                }

                var props = new JsonObject { ["method"] = verb, ["normalizedRoute"] = target.Route };
                if (target.Scheme is not null) props["scheme"] = target.Scheme;
                if (target.QueryNames.Length > 0) props["query"] = new JsonArray(target.QueryNames.Select(q => (JsonNode)JsonValue.Create(q)!).ToArray());
                b.Edge(owner, "REQUESTS", $"http|{Unbound(target.Kind, target.Hint)}|{verb} {target.Route}", proof, "rule", "unresolved", "configuration", Http, props);
            }
        }
    }
}
