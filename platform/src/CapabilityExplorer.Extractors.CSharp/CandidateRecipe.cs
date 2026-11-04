using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CapabilityExplorer.Extractors.CSharp;

internal sealed class CandidateRecipe(SemanticModel model, Func<SyntaxNode, CandidateSource> source)
{
    private IMethodSymbol? methodSymbol;
    private int count;

    internal StaticCandidate Read(MethodDeclarationSyntax syntax)
    {
        methodSymbol = model.GetDeclaredSymbol(syntax) as IMethodSymbol ?? throw Unsupported("unresolved-symbol");
        if (methodSymbol.IsAsync || !methodSymbol.IsStatic || methodSymbol.IsGenericMethod ||
            methodSymbol.ContainingType.IsGenericType || syntax.Body is null || methodSymbol.Parameters.Length > 3 ||
            methodSymbol.Parameters.Any(p => !Scalar(p.Type) || p.RefKind != RefKind.None))
            throw Unsupported("unsupported-method-shape");
        var flow = model.AnalyzeDataFlow(syntax.Body);
        if (flow is null || !flow.Succeeded || flow.WrittenInside.Any(s => s is IParameterSymbol) ||
            flow.Captured.Length > 0)
            throw Unsupported("mutating-or-captured-input");
        var operation = model.GetOperation(syntax.Body) ?? throw Unsupported("unresolved-body");
        var body = Statement(operation);
        var calls = Walk(body).Where(n => n.Kind == "call").ToArray();
        if (body.Children.Count == 0 || body.Children[^1].Kind != "return")
            throw Unsupported("missing-terminal-return");
        if (calls.Length == 0)
        {
            CandidateDecision.Check(body, methodSymbol.Parameters);
        }
        else
        {
            // Deliberately bounded: ordered terminating guards, one opaque effect, final literal result.
            if (calls.Length != 1 || body.Children.Count < 2 || body.Children[^2].Kind != "call" ||
                body.Children[^1].Children.SingleOrDefault()?.Kind != "constant" ||
                body.Children.Take(body.Children.Count - 2).Any(n => n.Kind != "if" || n.Children.Count != 2 || !Terminates(n.Children[1])))
                throw Unsupported("unsupported-process-shape");
        }
        return new(Key(methodSymbol), calls.Length == 0 ? "decision" : "process", "UNREVIEWED", false,
            "candidate-content", source(syntax),
            methodSymbol.Parameters.Select(p => new CandidateParameter(p.Name, Key(p), ScalarType(p.Type))).ToArray(),
            flow.ReadInside.OfType<IParameterSymbol>().Select(Key).Order(StringComparer.Ordinal).ToArray(),
            flow.WrittenInside.OfType<IParameterSymbol>().Select(Key).Order(StringComparer.Ordinal).ToArray(), body);
    }

    private CandidateNode Statement(IOperation operation)
    {
        if (++count > 128) throw Unsupported("method-operation-limit");
        var location = source(operation.Syntax);
        switch (operation)
        {
            case IBlockOperation block:
                return new("block", location, block.Operations.Select(Statement).ToArray());
            case IConditionalOperation conditional when conditional.Type is null:
                var predicate = Expression(conditional.Condition);
                predicate = predicate with { RawPredicate = conditional.Condition.Syntax.ToString() };
                return new("if", location, conditional.WhenFalse is null
                    ? [predicate, Statement(conditional.WhenTrue)]
                    : [predicate, Statement(conditional.WhenTrue), Statement(conditional.WhenFalse)]);
            case IReturnOperation returned when returned.ReturnedValue is not null:
                return new("return", location, [Expression(returned.ReturnedValue)]);
            case IThrowOperation thrown:
                var exception = thrown.Exception is IConversionOperation { OperatorMethod: null } wrapped
                    ? wrapped.Operand : thrown.Exception;
                if (exception is not IObjectCreationOperation creation || creation.Constructor is null ||
                    creation.Type?.ContainingNamespace.ToDisplayString() != "System" ||
                    creation.Type.Name is not ("ArgumentNullException" or "ArgumentException") || creation.Initializer is not null ||
                    !SymbolEqualityComparer.Default.Equals(creation.Type.ContainingAssembly,
                        model.Compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly) ||
                    creation.Arguments.Any(UnmodeledArgument))
                    throw Unsupported("unsupported-exception");
                return new("throw", location, creation.Arguments.Select(a => Expression(a.Value)).ToArray(),
                    Type: creation.Type.ToDisplayString(), Symbol: Key(creation.Constructor), Failure: "propagate");
            case IExpressionStatementOperation expression when expression.Operation is IInvocationOperation invocation:
                var target = invocation.TargetMethod;
                if (!target.IsStatic || !target.ReturnsVoid || !target.IsExtern ||
                    target.IsGenericMethod || invocation.Instance is not null ||
                    SymbolEqualityComparer.Default.Equals(target, methodSymbol) ||
                    invocation.Arguments.Any(UnmodeledArgument))
                    throw Unsupported("unsupported-effect-call");
                return new("call", location, invocation.Arguments.Select(a => Expression(a.Value)).ToArray(),
                    Symbol: Key(target), Failure: "propagate");
            default: throw Unsupported("unsupported-operation-" + operation.Kind);
        }
    }

    private CandidateNode Expression(IOperation operation)
    {
        if (++count > 128) throw Unsupported("method-operation-limit");
        var location = source(operation.Syntax);
        if (operation is IConversionOperation conversion)
        {
            if (conversion.Operand.ConstantValue is { HasValue: true, Value: null } &&
                conversion.Type?.SpecialType == SpecialType.System_String)
                return new("constant", location, [], Type: "null");
            if (!conversion.Conversion.IsIdentity || conversion.OperatorMethod is not null)
                throw Unsupported("unsupported-conversion");
            return Expression(conversion.Operand);
        }
        if (operation is IParenthesizedOperation parenthesized) return Expression(parenthesized.Operand) with { Source = location };
        if (operation.ConstantValue.HasValue && (Scalar(operation.Type) || operation.ConstantValue.Value is null))
        {
            var value = operation.ConstantValue.Value;
            return new("constant", location, [], Value: value switch
            {
                null => null,
                bool boolean => boolean ? "true" : "false",
                int integer => integer.ToString(CultureInfo.InvariantCulture),
                string text => text,
                _ => throw Unsupported("unsupported-constant")
            }, Type: value is null ? "null" : ScalarType(operation.Type!));
        }
        switch (operation)
        {
            case IParameterReferenceOperation parameter when Scalar(parameter.Type):
                return new("parameter", location, [], Symbol: Key(parameter.Parameter), Type: ScalarType(parameter.Type!));
            case IUnaryOperation unary when unary.OperatorKind == UnaryOperatorKind.Not && unary.OperatorMethod is null &&
                unary.Type?.SpecialType == SpecialType.System_Boolean:
                return new("not", location, [Expression(unary.Operand)]);
            case IBinaryOperation binary when binary.OperatorMethod is null && !binary.IsLifted &&
                binary.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr or
                BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals or BinaryOperatorKind.LessThan or
                BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual:
                return new("binary", location, [Expression(binary.LeftOperand), Expression(binary.RightOperand)],
                    Operator: binary.OperatorKind.ToString());
            case IPropertyReferenceOperation property when property.Property.ContainingType.SpecialType == SpecialType.System_String &&
                property.Property.Name == "Length" && property.Instance is not null && property.Arguments.Length == 0:
                return new("length", location, [Expression(property.Instance)], Type: "int", Symbol: Key(property.Property), Failure: "propagate");
            default: throw Unsupported("unsupported-expression-" + operation.Kind);
        }
    }

    private static bool Scalar(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Boolean or SpecialType.System_String;
    private static string ScalarType(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Int32 => "int",
        SpecialType.System_Boolean => "bool",
        SpecialType.System_String => "string",
        _ => throw Unsupported("unsupported-scalar-type")
    };
    private static bool UnmodeledArgument(IArgumentOperation argument) => argument.Parameter?.RefKind != RefKind.None ||
        argument.IsImplicit || argument.Syntax is ArgumentSyntax { NameColon: not null };
    private static string Key(ISymbol symbol) => symbol is IParameterSymbol parameter
        ? Key(parameter.ContainingSymbol) + ":" + parameter.Name
        : symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    internal static IEnumerable<CandidateNode> Walk(CandidateNode node) => new[] { node }.Concat(node.Children.SelectMany(Walk));
    private static bool Terminates(CandidateNode node) => node.Kind is "return" or "throw" ||
        (node.Kind == "block" && node.Children.Count == 1 && Terminates(node.Children[0]));
    private static CandidateUnsupportedException Unsupported(string reason) => new(reason);
}
