using System.Globalization;
using Microsoft.CodeAnalysis;

namespace CapabilityExplorer.Extractors.CSharp;

internal static class CandidateDecision
{
    internal static void Check(CandidateNode body, IReadOnlyList<IParameterSymbol> parameters)
    {
        if (parameters.Any(p => p.Type.SpecialType is not (SpecialType.System_Int32 or SpecialType.System_Boolean)) ||
            body.Children.Any(n => n.Kind is not ("if" or "return")) ||
            body.Children.Take(body.Children.Count - 1).Any(n => n.Kind != "if" || n.Children.Count != 2 || !LiteralReturn(n.Children[1])) ||
            !LiteralReturn(body.Children[^1]))
            throw new CandidateUnsupportedException("unsupported-decision-shape");
        var predicates = body.Children.Where(n => n.Kind == "if").Select(n => n.Children[0]).ToArray();
        foreach (var predicate in predicates) CheckPredicate(predicate);
        var constants = predicates.SelectMany(CandidateRecipe.Walk).Where(n => n.Kind == "constant" && n.Type == "int")
            .Select(n => int.Parse(n.Value!, CultureInfo.InvariantCulture)).Distinct().ToArray();
        if (constants.Length > 16) throw new CandidateUnsupportedException("predicate-domain-limit");
        var integers = new HashSet<int> { int.MinValue, 0, int.MaxValue };
        foreach (var value in constants)
        {
            integers.Add(value);
            if (value > int.MinValue) integers.Add(value - 1);
            if (value < int.MaxValue) integers.Add(value + 1);
        }
        // For unary comparisons to integer constants, these cells partition the full Int32 domain.
        // This is a bounded satisfiability check, not execution of source or exported candidates.
        IEnumerable<Dictionary<string, object>> rows = [new(StringComparer.Ordinal)];
        foreach (var parameter in parameters)
        {
            var domain = parameter.Type.SpecialType == SpecialType.System_Boolean
                ? new object[] { false, true } : integers.Order().Cast<object>().ToArray();
            var prefix = rows.ToArray();
            if (prefix.Length * domain.Length > 4096) throw new CandidateUnsupportedException("predicate-domain-limit");
            rows = prefix.SelectMany(row => domain.Select(value =>
                new Dictionary<string, object>(row, StringComparer.Ordinal) { [parameter.Name] = value })).ToArray();
        }
        foreach (var row in rows)
            if (predicates.Count(p => (bool)Evaluate(p, row)) > 1)
                throw new CandidateUnsupportedException("overlapping-predicates");
    }

    private static bool LiteralReturn(CandidateNode node) => node.Kind == "return" &&
        node.Children.Count == 1 && node.Children[0].Kind == "constant" && node.Children[0].Type != "null";

    private static void CheckPredicate(CandidateNode node)
    {
        if (node.Kind == "parameter" && node.Type == "bool" || node.Kind == "constant" && node.Type == "bool") return;
        if (node.Kind == "not") { CheckPredicate(node.Children[0]); return; }
        if (node.Kind == "binary")
        {
            if (node.Operator is "ConditionalAnd" or "ConditionalOr")
            {
                foreach (var child in node.Children) CheckPredicate(child);
                return;
            }
            if (node.Children.Count == 2 &&
                ((node.Children[0].Kind == "parameter" && node.Children[1].Kind == "constant") ||
                 (node.Children[1].Kind == "parameter" && node.Children[0].Kind == "constant")) &&
                node.Children[0].Type == node.Children[1].Type &&
                node.Children[0].Type is "int" or "bool")
                return;
        }
        throw new CandidateUnsupportedException("uncertain-predicate-domain");
    }

    private static object Evaluate(CandidateNode node, Dictionary<string, object> row)
    {
        if (node.Kind == "constant")
            return node.Type == "bool" ? bool.Parse(node.Value!) : int.Parse(node.Value!, CultureInfo.InvariantCulture);
        if (node.Kind == "parameter") return row[node.Symbol!.Split(':').Last()];
        if (node.Kind == "not") return !(bool)Evaluate(node.Children[0], row);
        var left = Evaluate(node.Children[0], row);
        if (node.Operator == "ConditionalAnd") return (bool)left && (bool)Evaluate(node.Children[1], row);
        if (node.Operator == "ConditionalOr") return (bool)left || (bool)Evaluate(node.Children[1], row);
        var right = Evaluate(node.Children[1], row);
        return node.Operator switch
        {
            "Equals" => Equals(left, right),
            "NotEquals" => !Equals(left, right),
            "LessThan" => (int)left < (int)right,
            "LessThanOrEqual" => (int)left <= (int)right,
            "GreaterThan" => (int)left > (int)right,
            "GreaterThanOrEqual" => (int)left >= (int)right,
            _ => throw new CandidateUnsupportedException("uncertain-predicate-domain")
        };
    }
}
