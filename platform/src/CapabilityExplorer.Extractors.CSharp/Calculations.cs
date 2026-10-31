using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>A construct the IR cannot express faithfully. The method then has no calculation record (never an approximation).</summary>
internal sealed class UnsupportedConstruct(string reason) : Exception(reason);

/// <summary>
/// Lowers C# method bodies to the language-neutral calculation IR and derives the calculation attributes from it.
/// Everything is read from the syntax and the semantic model: nothing is guessed from names, and an attribute the
/// code does not settle stays <c>unknown</c>. Supported: expression and block bodies made of locals, if/return,
/// <c>foreach</c> accumulations, LINQ aggregates (Where/Select/Distinct + Sum/Average/Min/Max/Count), rounding and calls.
/// </summary>
internal sealed class CalculationExtractor(
    ArtifactBuilder b,
    SymbolKeys keys,
    Func<SyntaxTree, CSharpExtractor.SourceFile?> fileOf,
    Func<IEnumerable<(SyntaxTree Tree, SemanticModel Model)>> allTrees,
    bool checkedProject)
{
    private Dictionary<ISymbol, List<InvocationExpressionSyntax>>? _callers;

    public void Extract(CSharpExtractor.SourceFile file, MethodDeclarationSyntax decl, IMethodSymbol method)
    {
        if (file.Generated || (decl.Body is null && decl.ExpressionBody is null) || Lowering.Num(method.ReturnType) is not { } outputType)
        {
            return;
        }

        var lowering = new Lowering(this, file, decl, method, keys);
        JsonNode expr;
        try
        {
            expr = lowering.Run();
        }
        catch (UnsupportedConstruct)
        {
            return;
        }

        var ops = Ir.Walk(expr).Select(o => Ir.Op(o)).ToHashSet();
        if (!ops.Overlaps(["agg", "fold", "round"]) && !Ir.Walk(expr).Any(o => Ir.Op(o) == "bin" && Ir.Str(o, "o") == "/"))
        {
            return; // plain getters and helpers are not calculations
        }

        var key = keys.Of(method);
        var header = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(CSharpExtractor.HeaderStart(decl.Modifiers, decl.ReturnType.GetFirstToken()), decl.ParameterList.OpenParenToken.Span.End);
        var headerEvidence = file.Evidence(b, header);
        var attributes = new Attributes(this, lowering, expr, outputType, headerEvidence).Build();

        var calc = new JsonObject { ["schema"] = "ce.v1", ["kind"] = "calculation", ["id"] = $"c:{ArtifactBuilder.Sha256(key)[..16]}", ["symbolKey"] = key };
        var includes = lowering.Includes(this).Select(m => keys.Of(m)).Distinct().Order(StringComparer.Ordinal).ToList();
        if (includes.Count > 0)
        {
            calc["includes"] = new JsonArray(includes.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
        }

        calc["output"] = new JsonObject { ["type"] = outputType, ["unit"] = new JsonObject { ["status"] = "unknown" } };
        calc["inputs"] = new JsonArray(lowering.Inputs.Select(i =>
        {
            var o = new JsonObject { ["ref"] = i.Ref, ["type"] = i.Type };
            if (i.Origin is not null)
            {
                o["origin"] = i.Origin;
            }

            return (JsonNode)o;
        }).ToArray());
        calc["expr"] = expr;
        calc["attributes"] = attributes;
        calc["irHash"] = ArtifactBuilder.Sha256(Ir.Canonical(expr));
        calc["evidence"] = new JsonArray(headerEvidence);
        b.Calculation(calc);
    }

    internal CSharpExtractor.SourceFile? FileOf(SyntaxTree tree) => fileOf(tree);

    internal bool CheckedProject => checkedProject;

    internal ArtifactBuilder Builder => b;

    /// <summary>Call sites of a method (or of the interface member it implements), found by resolving every invocation once.</summary>
    internal IReadOnlyList<InvocationExpressionSyntax> CallersOf(IMethodSymbol method)
    {
        if (_callers is null)
        {
            _callers = new Dictionary<ISymbol, List<InvocationExpressionSyntax>>(SymbolEqualityComparer.Default);
            foreach (var (tree, model) in allTrees())
            {
                foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol target)
                    {
                        var original = (target.ReducedFrom ?? target).OriginalDefinition;
                        if (!_callers.TryGetValue(original, out var list))
                        {
                            _callers[original] = list = [];
                        }

                        list.Add(inv);
                    }
                }
            }
        }

        var found = new List<InvocationExpressionSyntax>();
        void Add(ISymbol s)
        {
            if (_callers.TryGetValue(s.OriginalDefinition, out var l))
            {
                found.AddRange(l);
            }
        }

        Add(method);
        foreach (var iface in method.ContainingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                if (SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(member), method))
                {
                    Add(member);
                }
            }
        }

        return found;
    }
}

internal sealed record InputInfo(string Ref, string Type, string? Origin, string Name, bool IsSequence, bool IsDate, SyntaxNode? Declaration);

internal sealed record Lam(IParameterSymbol Param, ExpressionSyntax Body);

internal sealed class Seq
{
    public required JsonNode Over { get; init; }

    public List<Lam> Filters { get; init; } = [];

    public Lam? Select { get; set; }

    public bool Distinct { get; set; }

    public Seq Copy() => new() { Over = Over.DeepClone(), Filters = [.. Filters], Select = Select, Distinct = Distinct };
}

internal abstract record Binding;

internal sealed record ExprBinding(JsonNode Node) : Binding;

internal sealed record SeqBinding(Seq Seq) : Binding;

/// <summary>Lowering of one method.</summary>
internal sealed class Lowering
{
    private readonly CSharpExtractor.SourceFile _file;
    private readonly MethodDeclarationSyntax _decl;
    private readonly IMethodSymbol _method;
    private readonly SymbolKeys _keys;
    private readonly SemanticModel _model;
    private readonly Dictionary<ISymbol, Binding> _env = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, InputInfo> _implicitInputs = new(SymbolEqualityComparer.Default);
    private readonly List<IMethodSymbol> _callees = [];
    private int _next;

    public Lowering(CalculationExtractor owner, CSharpExtractor.SourceFile file, MethodDeclarationSyntax decl, IMethodSymbol method, SymbolKeys keys)
    {
        _ = owner;
        _file = file;
        _decl = decl;
        _method = method;
        _keys = keys;
        _model = file.Model;
    }

    public List<InputInfo> Inputs { get; } = [];

    /// <summary>Syntax that supports a fact, by role (first one wins unless stated).</summary>
    public Dictionary<string, SyntaxNode> Anchors { get; } = new(StringComparer.Ordinal);

    public List<SyntaxNode> FilterNodes { get; } = [];

    public bool UsesNullable { get; private set; }

    public bool TouchesDate { get; private set; }

    public CSharpExtractor.SourceFile File => _file;

    public IMethodSymbol Method => _method;

    public MethodDeclarationSyntax Declaration => _decl;

    private void Anchor(string role, SyntaxNode node) => Anchors.TryAdd(role, node);

    private string Alloc() => $"${_next++}";

    public JsonNode Run()
    {
        foreach (var p in _method.Parameters)
        {
            var reference = Alloc();
            _env[p] = new ExprBinding(Ir.Var(reference));
            Inputs.Add(MakeInput(reference, p.Type, p.Name, p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()));
            NoteNullable(p.Type);
        }

        if (_decl.ExpressionBody is { } arrow)
        {
            Anchors["return"] = arrow.Expression;
            return Ex(arrow.Expression);
        }

        return Statements(_decl.Body!.Statements, 0);
    }

    // ------------------------------------------------------------------ statements

    private JsonNode Statements(SyntaxList<StatementSyntax> statements, int from)
    {
        for (var i = from; i < statements.Count; i++)
        {
            switch (statements[i])
            {
                case LocalDeclarationStatementSyntax local:
                    foreach (var v in local.Declaration.Variables)
                    {
                        if (_model.GetDeclaredSymbol(v) is not ILocalSymbol symbol || v.Initializer is null)
                        {
                            throw new UnsupportedConstruct("local without initializer");
                        }

                        _env[symbol] = IsSequence(symbol.Type) ? new SeqBinding(ParseSeq(v.Initializer.Value)) : new ExprBinding(Ex(v.Initializer.Value));
                    }

                    break;
                case ForEachStatementSyntax loop:
                    Fold(loop);
                    break;
                case IfStatementSyntax @if:
                    var test = Ex(@if.Condition);
                    Anchor("guard", @if);
                    var then = BranchValue(@if.Statement, "guardReturn");
                    var otherwise = @if.Else is { } e ? BranchValue(e.Statement, null) : Statements(statements, i + 1);
                    return Ir.Cond(test, then, otherwise);
                case ReturnStatementSyntax ret when ret.Expression is { } value:
                    Anchors["return"] = ret;
                    return Ex(value);
                default:
                    throw new UnsupportedConstruct($"{statements[i].Kind()} is not supported");
            }
        }

        throw new UnsupportedConstruct("no return value");
    }

    private JsonNode BranchValue(StatementSyntax statement, string? role)
    {
        switch (statement)
        {
            case ReturnStatementSyntax { Expression: { } value } ret:
                if (role is not null)
                {
                    Anchor(role, ret);
                }

                return Ex(value);
            case BlockSyntax block:
                if (role is not null && block.Statements.FirstOrDefault() is ReturnStatementSyntax first)
                {
                    Anchor(role, first);
                }

                return Statements(block.Statements, 0);
            default:
                throw new UnsupportedConstruct($"{statement.Kind()} branch is not supported");
        }
    }

    /// <summary><c>foreach (var x in xs) acc = f(acc, x);</c> becomes a fold. Anything else in the loop body is unsupported.</summary>
    private void Fold(ForEachStatementSyntax loop)
    {
        var source = loop.Expression;
        while (source is ParenthesizedExpressionSyntax p)
        {
            source = p.Expression;
        }

        var skip = 0;
        if (source is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Skip" } skipAccess } skipCall
            && _model.GetSymbolInfo(skipCall).Symbol is IMethodSymbol { ContainingType.Name: "Enumerable" }
            && skipCall.ArgumentList.Arguments.Count == 1
            && _model.GetConstantValue(skipCall.ArgumentList.Arguments[0].Expression) is { HasValue: true, Value: int n and >= 0 })
        {
            skip = n;
            source = skipAccess.Expression;
        }

        JsonNode over;
        if (source is IdentifierNameSyntax id && _model.GetSymbolInfo(id).Symbol is ILocalSymbol local && _env.TryGetValue(local, out var binding) && binding is SeqBinding { Seq: { Filters.Count: 0, Select: null, Distinct: false } plain })
        {
            over = plain.Over.DeepClone();
        }
        else
        {
            over = Ex(source);
        }

        var statement = loop.Statement is BlockSyntax { Statements.Count: 1 } block ? block.Statements[0] : loop.Statement;
        if (statement is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
            || _model.GetSymbolInfo(assignment.Left).Symbol is not ILocalSymbol target
            || !_env.TryGetValue(target, out var current) || current is not ExprBinding init
            || _model.GetDeclaredSymbol(loop) is not ILocalSymbol element)
        {
            throw new UnsupportedConstruct("loop body is not a single accumulation");
        }

        var type = Num(target.Type) ?? throw new UnsupportedConstruct("accumulator is not numeric");
        var bind = Alloc();
        var acc = Alloc();
        _env[element] = new ExprBinding(Ir.Var(bind));
        _env[target] = new ExprBinding(Ir.Var(acc));
        JsonNode step;
        if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            step = Ex(assignment.Right);
        }
        else
        {
            var op = assignment.Kind() switch
            {
                SyntaxKind.AddAssignmentExpression => "+",
                SyntaxKind.SubtractAssignmentExpression => "-",
                SyntaxKind.MultiplyAssignmentExpression => "*",
                SyntaxKind.DivideAssignmentExpression => "/",
                _ => throw new UnsupportedConstruct($"{assignment.Kind()} is not supported"),
            };
            step = Ir.Bin(op, Ir.Var(acc), Ex(assignment.Right), type);
            NoteIntegerArithmetic(type, assignment);
        }

        _env.Remove(element);
        _env[target] = new ExprBinding(Ir.Fold(over, bind, acc, init.Node.DeepClone(), step, skip, type));
        Anchor("fold", loop);
        NoteIntegerArithmetic(type, loop);
    }

    private void NoteIntegerArithmetic(string type, SyntaxNode node)
    {
        if (type is "int32" or "int64")
        {
            Anchor("intArithmetic", node);
        }
    }

    // ------------------------------------------------------------------ expressions

    /// <summary>The expression lowered, with the implicit numeric conversion the compiler applies at this position.</summary>
    private JsonNode Ex(ExpressionSyntax e)
    {
        var node = Raw(e);
        if (node is JsonObject lit && Ir.Op(lit) == "lit")
        {
            return node;
        }

        var info = _model.GetTypeInfo(e);
        if (Num(info.Type) is { } from && Num(info.ConvertedType) is { } to && from != to
            && info.Type!.OriginalDefinition.SpecialType != info.ConvertedType!.OriginalDefinition.SpecialType)
        {
            return Ir.Conv(to, node);
        }

        return node;
    }

    private JsonNode Raw(ExpressionSyntax e)
    {
        if (ConstantLeaf(e) is { } constant)
        {
            return constant;
        }

        switch (e)
        {
            case ParenthesizedExpressionSyntax p:
                return Ex(p.Expression);
            case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang:
                return Ex(bang.Operand);
            case CheckedExpressionSyntax c:
                return Ex(c.Expression);
            case IdentifierNameSyntax id:
                return Identifier(id);
            case MemberAccessExpressionSyntax ma:
                return MemberAccess(ma);
            case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } ea:
                return Ir.Index(Ex(ea.Expression), Ex(ea.ArgumentList.Arguments[0].Expression), Num(_model.GetTypeInfo(ea).Type));
            case BinaryExpressionSyntax bin:
                return Binary(bin);
            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not:
                return Ir.Not(Ex(not.Operand));
            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryPlusExpression } plus:
                return Ex(plus.Operand);
            case ConditionalExpressionSyntax cond:
                return Ir.Cond(Ex(cond.Condition), Ex(cond.WhenTrue), Ex(cond.WhenFalse));
            case TupleExpressionSyntax tuple when tuple.Arguments.Count >= 2:
                return Ir.Tuple(tuple.Arguments.Select(a => Ex(a.Expression)));
            case CastExpressionSyntax cast when Num(_model.GetTypeInfo(cast).Type) is { } to:
                return Ir.Conv(to, Ex(cast.Expression));
            case InvocationExpressionSyntax inv:
                return Invoke(inv);
            default:
                throw new UnsupportedConstruct($"{e.Kind()} is not supported");
        }
    }

    /// <summary>Literals and constant fields. Compound constant expressions are kept structural (<c>1 - Alpha</c> stays a subtraction).</summary>
    private JsonObject? ConstantLeaf(ExpressionSyntax e)
    {
        var leaf = e switch
        {
            LiteralExpressionSyntax => true,
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: LiteralExpressionSyntax } => true,
            IdentifierNameSyntax or MemberAccessExpressionSyntax => _model.GetSymbolInfo(e).Symbol is IFieldSymbol { IsConst: true },
            _ => false,
        };
        if (!leaf)
        {
            return null;
        }

        var constant = _model.GetConstantValue(e);
        if (!constant.HasValue)
        {
            return null;
        }

        var info = _model.GetTypeInfo(e);
        var type = Num(info.ConvertedType) ?? Num(info.Type);
        switch (constant.Value)
        {
            case null:
                return Ir.Lit(null);
            case bool flag:
                return Ir.Lit(JsonValue.Create(flag));
            case string text:
                return Ir.Lit(JsonValue.Create(text));
            case char ch:
                return Ir.Lit(JsonValue.Create(ch.ToString()));
        }

        if (type is null)
        {
            throw new UnsupportedConstruct($"constant of type {info.Type?.ToDisplayString()} is not numeric");
        }

        return Ir.Lit(ConstantNumber(constant.Value), type);
    }

    private static JsonNode ConstantNumber(object value)
    {
        switch (value)
        {
            case double d when double.IsNaN(d):
                return JsonValue.Create("NaN");
            case double d when double.IsPositiveInfinity(d):
                return JsonValue.Create("Infinity");
            case double d when double.IsNegativeInfinity(d):
                return JsonValue.Create("-Infinity");
            case double d:
                return Ir.Number(d);
            case float f:
                return Ir.Number(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
            case decimal m:
                return m == decimal.Truncate(m) && Math.Abs(m) < 1e15m ? Ir.Number((long)m) : Ir.Number(double.Parse(m.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
            case ulong u:
                return u <= long.MaxValue ? Ir.Number((long)u) : Ir.Number((double)u);
            default:
                return Ir.Number(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
    }

    private JsonNode Identifier(IdentifierNameSyntax id)
    {
        var symbol = _model.GetSymbolInfo(id).Symbol;
        switch (symbol)
        {
            case ILocalSymbol or IParameterSymbol:
                if (!_env.TryGetValue(symbol, out var binding))
                {
                    throw new UnsupportedConstruct($"{symbol.Name} is not bound");
                }

                NoteNullable(symbol is ILocalSymbol l ? l.Type : ((IParameterSymbol)symbol).Type);
                return binding is ExprBinding expr ? expr.Node.DeepClone() : throw new UnsupportedConstruct($"{symbol.Name} is a sequence used as a value");
            case IPropertySymbol or IFieldSymbol:
                return ImplicitInput(symbol);
            default:
                throw new UnsupportedConstruct($"{id.Identifier.ValueText} does not resolve");
        }
    }

    private JsonNode MemberAccess(MemberAccessExpressionSyntax ma)
    {
        var symbol = _model.GetSymbolInfo(ma).Symbol;
        if (symbol is not (IPropertySymbol or IFieldSymbol))
        {
            throw new UnsupportedConstruct($"{ma} is not a property or field");
        }

        var nullable = symbol.ContainingType is { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
        if (nullable && symbol.Name == "HasValue")
        {
            var operand = Ex(ma.Expression);
            if (operand is JsonObject member && Ir.Op(member) == "member")
            {
                member.Remove("type");
            }

            return Ir.Cmp("!=", operand, Ir.Lit(null));
        }

        if (nullable && symbol.Name == "Value")
        {
            return Ex(ma.Expression);
        }

        if (ma.Expression is ThisExpressionSyntax)
        {
            return ImplicitInput(symbol);
        }

        if (symbol.IsStatic)
        {
            throw new UnsupportedConstruct($"static member {symbol.ContainingType.Name}.{symbol.Name} is not a constant");
        }

        var type = symbol is IPropertySymbol prop ? prop.Type : ((IFieldSymbol)symbol).Type;
        NoteNullable(type);
        if (type.OriginalDefinition.SpecialType is SpecialType.System_DateTime || type.Name is "DateOnly" or "DateTimeOffset")
        {
            TouchesDate = true;
        }

        return Ir.Member(Ex(ma.Expression), symbol.Name, Num(type));
    }

    private JsonObject ImplicitInput(ISymbol symbol)
    {
        if (!_implicitInputs.TryGetValue(symbol, out var input))
        {
            var type = symbol is IPropertySymbol p ? p.Type : ((IFieldSymbol)symbol).Type;
            input = MakeInput(Alloc(), type, symbol.Name, symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(), origin: _keys.Of(symbol));
            _implicitInputs[symbol] = input;
            Inputs.Add(input);
            NoteNullable(type);
        }

        return Ir.Var(input.Ref);
    }

    private InputInfo MakeInput(string reference, ITypeSymbol type, string name, SyntaxNode? declaration, string? origin = null)
    {
        if (origin is null)
        {
            var element = ElementType(type) ?? type;
            var key = _keys.Of(element);
            origin = key.StartsWith("cs|@ext|", StringComparison.Ordinal) || element.SpecialType != SpecialType.None ? null : key;
        }

        var isDate = type.OriginalDefinition.SpecialType == SpecialType.System_DateTime || type.Name is "DateOnly" or "DateTimeOffset";
        return new InputInfo(reference, type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), origin, name, IsSequence(type), isDate, declaration);
    }

    private JsonNode Binary(BinaryExpressionSyntax bin)
    {
        switch (bin.Kind())
        {
            case SyntaxKind.AddExpression or SyntaxKind.SubtractExpression or SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression:
                var type = Num(_model.GetTypeInfo(bin).Type) ?? throw new UnsupportedConstruct("arithmetic on a non-numeric type");
                var op = bin.OperatorToken.ValueText;
                NoteIntegerArithmetic(type, bin);
                if (op == "/")
                {
                    Anchor("division", bin);
                }

                return Ir.Bin(op, Ex(bin.Left), Ex(bin.Right), type);
            case SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
                or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression:
                return Ir.Cmp(bin.OperatorToken.ValueText, Ex(bin.Left), Ex(bin.Right));
            case SyntaxKind.LogicalAndExpression:
                return Ir.Junction("and", [Ex(bin.Left), Ex(bin.Right)]);
            case SyntaxKind.LogicalOrExpression:
                return Ir.Junction("or", [Ex(bin.Left), Ex(bin.Right)]);
            case SyntaxKind.CoalesceExpression:
                Anchor("coalesce", bin);
                var args = new List<JsonNode>();
                void Flatten(ExpressionSyntax x)
                {
                    if (x is BinaryExpressionSyntax inner && inner.IsKind(SyntaxKind.CoalesceExpression))
                    {
                        Flatten(inner.Left);
                        Flatten(inner.Right);
                    }
                    else
                    {
                        args.Add(Ex(x));
                    }
                }

                Flatten(bin);
                return Ir.Coalesce(args);
            default:
                throw new UnsupportedConstruct($"operator {bin.OperatorToken.ValueText} is not supported");
        }
    }

    private JsonObject Invoke(InvocationExpressionSyntax inv)
    {
        if (_model.GetSymbolInfo(inv).Symbol is not IMethodSymbol resolved)
        {
            throw new UnsupportedConstruct($"call {inv.Expression} does not resolve");
        }

        var method = resolved.ReducedFrom ?? resolved;
        var container = method.ContainingType.ToDisplayString();
        var name = method.Name;

        if (container is "System.Math" or "System.Decimal" && name is "Round" or "Floor" or "Ceiling" or "Truncate")
        {
            return Rounding(inv, method, name);
        }

        if (container == "System.Linq.Enumerable" && name is "Sum" or "Average" or "Min" or "Max" or "Count" or "LongCount")
        {
            return Aggregate(inv, name);
        }

        var args = new List<JsonNode>();
        if (resolved.ReducedFrom is not null && inv.Expression is MemberAccessExpressionSyntax extension)
        {
            args.Add(Ex(extension.Expression));
        }
        else if (!method.IsStatic && inv.Expression is MemberAccessExpressionSyntax { Expression: not ThisExpressionSyntax } instance)
        {
            args.Add(Ex(instance.Expression));
        }

        args.AddRange(inv.ArgumentList.Arguments.Select(a => Ex(a.Expression)));
        if (container == "System.Globalization.ISOWeek")
        {
            Anchor("iso" + name, inv);
        }

        if (method.OriginalDefinition.Locations.Any(l => l.IsInSource))
        {
            _callees.Add(method.OriginalDefinition);
        }

        return Ir.Call(_keys.Of(method.OriginalDefinition), args, Num(resolved.ReturnType));
    }

    private JsonObject Rounding(InvocationExpressionSyntax inv, IMethodSymbol method, string name)
    {
        Anchor("round", inv);
        var arguments = inv.ArgumentList.Arguments;
        if (arguments.Count == 0)
        {
            throw new UnsupportedConstruct("rounding without an operand");
        }

        var value = Ex(arguments[0].Expression);
        JsonNode? digits = null;
        var mode = name switch { "Floor" => "floor", "Ceiling" => "ceiling", "Truncate" => "truncate", _ => "halfEven" };
        for (var i = 1; i < arguments.Count; i++)
        {
            var parameter = method.Parameters[i];
            if (parameter.Type.SpecialType == SpecialType.System_Int32)
            {
                digits = Ex(arguments[i].Expression);
            }
            else if (parameter.Type.Name == "MidpointRounding")
            {
                mode = _model.GetConstantValue(arguments[i].Expression) is { HasValue: true, Value: int m }
                    ? m switch { 0 => "halfEven", 1 => "halfAwayFromZero", 2 => "truncate", 3 => "floor", 4 => "ceiling", _ => "unknown" }
                    : "unknown";
            }
            else
            {
                throw new UnsupportedConstruct($"rounding overload {method.ToDisplayString()}");
            }
        }

        return Ir.Round(mode, value, digits, _keys.Of(method.OriginalDefinition));
    }

    private JsonObject Aggregate(InvocationExpressionSyntax inv, string name)
    {
        if (inv.Expression is not MemberAccessExpressionSyntax access)
        {
            throw new UnsupportedConstruct("aggregate without a receiver");
        }

        var seq = ParseSeq(access.Expression);
        Lam? selector = null;
        var arguments = inv.ArgumentList.Arguments;
        if (arguments.Count > 1)
        {
            throw new UnsupportedConstruct("aggregate overload");
        }

        if (arguments.Count == 1)
        {
            var lambda = AsLambda(arguments[0].Expression);
            if (name is "Count" or "LongCount")
            {
                seq.Filters.Add(lambda);
            }
            else if (seq.Select is not null)
            {
                throw new UnsupportedConstruct("selector after Select");
            }
            else
            {
                selector = lambda;
            }
        }

        var value = selector ?? seq.Select;
        var fn = name switch { "Sum" => "sum", "Average" => "avg", "Min" => "min", "Max" => "max", _ => seq.Distinct ? "countDistinct" : "count" };
        if (seq.Distinct && fn != "countDistinct")
        {
            throw new UnsupportedConstruct("Distinct before a non-count aggregate");
        }

        if (fn == "count")
        {
            value = null; // Count() ignores the projection
        }

        var bind = seq.Filters.Count > 0 || value is not null ? Alloc() : null;
        JsonNode? filter = null;
        if (seq.Filters.Count > 0)
        {
            filter = Ir.Junction("and", seq.Filters.Select(f => LowerLambda(f, bind!)));
            FilterNodes.AddRange(seq.Filters.Select(f => (SyntaxNode)f.Body));
        }

        var valueNode = value is null ? null : LowerLambda(value, bind!);
        var resultType = _model.GetTypeInfo(inv).Type;
        if (resultType is not null && IsNullable(resultType))
        {
            throw new UnsupportedConstruct("aggregate over nullable values");
        }

        Anchors.TryAdd("agg:" + fn, inv);
        return Ir.Agg(fn, seq.Over, bind, valueNode, filter, Num(resultType));
    }

    private JsonNode LowerLambda(Lam lambda, string bind)
    {
        _env[lambda.Param] = new ExprBinding(Ir.Var(bind));
        try
        {
            return Ex(lambda.Body);
        }
        finally
        {
            _env.Remove(lambda.Param);
        }
    }

    private Lam AsLambda(ExpressionSyntax e) => e switch
    {
        SimpleLambdaExpressionSyntax { Body: ExpressionSyntax body } simple when _model.GetDeclaredSymbol(simple.Parameter) is { } p => new Lam(p, body),
        ParenthesizedLambdaExpressionSyntax { Body: ExpressionSyntax body, ParameterList.Parameters.Count: 1 } paren when _model.GetDeclaredSymbol(paren.ParameterList.Parameters[0]) is { } p => new Lam(p, body),
        _ => throw new UnsupportedConstruct("only single-parameter expression lambdas are supported"),
    };

    /// <summary>A LINQ chain split into its source and the Where/Select/Distinct stages that the IR folds into one aggregate.</summary>
    private Seq ParseSeq(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax p)
        {
            e = p.Expression;
        }

        if (e is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } inv
            && _model.GetSymbolInfo(inv).Symbol is IMethodSymbol m
            && (m.ReducedFrom ?? m).ContainingType.ToDisplayString() == "System.Linq.Enumerable")
        {
            var args = inv.ArgumentList.Arguments;
            switch (m.Name)
            {
                case "Where" when args.Count == 1:
                    var filtered = ParseSeq(access.Expression);
                    if (filtered.Select is not null || filtered.Distinct)
                    {
                        throw new UnsupportedConstruct("Where after Select");
                    }

                    filtered.Filters.Add(AsLambda(args[0].Expression));
                    return filtered;
                case "Select" when args.Count == 1:
                    var projected = ParseSeq(access.Expression);
                    if (projected.Select is not null || projected.Distinct)
                    {
                        throw new UnsupportedConstruct("chained Select");
                    }

                    projected.Select = AsLambda(args[0].Expression);
                    return projected;
                case "Distinct" when args.Count == 0:
                    var distinct = ParseSeq(access.Expression);
                    distinct.Distinct = true;
                    return distinct;
                case "ToList" or "ToArray" or "AsEnumerable" when args.Count == 0:
                    return ParseSeq(access.Expression);
            }
        }

        if (e is IdentifierNameSyntax id && _model.GetSymbolInfo(id).Symbol is ILocalSymbol local && _env.TryGetValue(local, out var binding) && binding is SeqBinding seq)
        {
            return seq.Seq.Copy();
        }

        return new Seq { Over = Ex(e) };
    }

    // ------------------------------------------------------------------ helpers

    internal static string? Num(ITypeSymbol? type)
    {
        if (type is null)
        {
            return null;
        }

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        return type.SpecialType switch
        {
            SpecialType.System_Decimal => "decimal",
            SpecialType.System_Double => "float64",
            SpecialType.System_Single => "float32",
            SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_UInt16 => "int32",
            SpecialType.System_Int64 or SpecialType.System_UInt32 => "int64",
            _ => null,
        };
    }

    private static bool IsNullable(ITypeSymbol t) =>
        t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } || (!t.IsValueType && t.NullableAnnotation == NullableAnnotation.Annotated);

    private void NoteNullable(ITypeSymbol type)
    {
        if (IsNullable(type) || (ElementType(type) is { } element && IsNullable(element)))
        {
            UsesNullable = true;
        }
    }

    internal static bool IsSequence(ITypeSymbol type) =>
        type.SpecialType != SpecialType.System_String && Num(type) is null
        && (type is IArrayTypeSymbol || type.AllInterfaces.Any(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) || type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);

    internal static ITypeSymbol? ElementType(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => array.ElementType,
        INamedTypeSymbol { TypeArguments.Length: 1 } named when IsSequence(type) => named.TypeArguments[0],
        _ => null,
    };

    // ------------------------------------------------------------------ callees (includes, ISO weeks)

    /// <summary>Source methods reached from the IR's calls, transitively (a bounded walk).</summary>
    public IReadOnlyList<IMethodSymbol> Includes(CalculationExtractor owner) => Closure(owner).Methods;

    /// <summary>The first ISOWeek.GetWeekOfYear call in the calculation or in the helpers it calls.</summary>
    public (SyntaxNode Node, CSharpExtractor.SourceFile File)? IsoWeekCall(CalculationExtractor owner)
    {
        if (Anchors.TryGetValue("isoGetWeekOfYear", out var direct))
        {
            return (direct, _file);
        }

        return Closure(owner).Iso;
    }

    private (IReadOnlyList<IMethodSymbol> Methods, (SyntaxNode Node, CSharpExtractor.SourceFile File)? Iso)? _closure;

    private (IReadOnlyList<IMethodSymbol> Methods, (SyntaxNode Node, CSharpExtractor.SourceFile File)? Iso) Closure(CalculationExtractor owner)
    {
        if (_closure is { } done)
        {
            return done;
        }

        var visited = new List<IMethodSymbol>();
        (SyntaxNode, CSharpExtractor.SourceFile)? iso = null;
        var queue = new Queue<(IMethodSymbol Method, int Depth)>(_callees.Select(c => (c, 1)));
        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (visited.Any(v => SymbolEqualityComparer.Default.Equals(v, current)) || SymbolEqualityComparer.Default.Equals(current, _method.OriginalDefinition))
            {
                continue;
            }

            visited.Add(current);
            if (depth >= 6)
            {
                continue;
            }

            foreach (var reference in current.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax();
                if (owner.FileOf(syntax.SyntaxTree) is not { } file)
                {
                    continue;
                }

                foreach (var call in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (file.Model.GetSymbolInfo(call).Symbol is not IMethodSymbol target)
                    {
                        continue;
                    }

                    var original = (target.ReducedFrom ?? target).OriginalDefinition;
                    if (original.ContainingType.ToDisplayString() == "System.Globalization.ISOWeek" && original.Name == "GetWeekOfYear")
                    {
                        iso ??= (call, file);
                    }
                    else if (original.Locations.Any(l => l.IsInSource))
                    {
                        queue.Enqueue((original, depth + 1));
                    }
                }
            }
        }

        _closure = (visited, iso);
        return _closure.Value;
    }
}
