using CapabilityExplorer.Extractors.CSharp;

namespace CapabilityExplorer.Extractors.Tests;

// Test-only interpreter: expected results live in the separately frozen oracle.json.
internal sealed class CandidateOracle(StaticCandidate candidate, Dictionary<string, object?> inputs, bool callFailure)
{
    public List<string> Reads { get; } = [];
    public List<string> Effects { get; } = [];

    public object? Run() => Step(candidate.Body).Value;

    private (bool Stops, object? Value) Step(CandidateNode node)
    {
        switch (node.Kind)
        {
            case "block":
                foreach (var child in node.Children)
                {
                    var result = Step(child);
                    if (result.Stops) return result;
                }
                return (false, null);
            case "if":
                var branch = (bool)Expression(node.Children[0])! ? 1 : 2;
                return branch < node.Children.Count ? Step(node.Children[branch]) : (false, null);
            case "return": return (true, Expression(node.Children[0]));
            case "throw": return (true, node.Type!.Split('.').Last());
            case "call":
                foreach (var child in node.Children) Expression(child);
                Effects.Add(node.Symbol!.Contains(".Record(", StringComparison.Ordinal) ? "Record" : node.Symbol);
                return callFailure ? (true, "external-call-failure") : (false, null);
            default: throw new InvalidOperationException(node.Kind);
        }
    }

    private object? Expression(CandidateNode node)
    {
        if (node.Kind == "constant")
            return node.Type switch
            {
                "bool" => bool.Parse(node.Value!),
                "int" => int.Parse(node.Value!, System.Globalization.CultureInfo.InvariantCulture),
                "string" => node.Value,
                "null" => null,
                _ => throw new InvalidOperationException(node.Type)
            };
        if (node.Kind == "parameter")
        {
            var name = candidate.Parameters.Single(p => p.Symbol == node.Symbol).Name;
            Reads.Add(name);
            return inputs[name];
        }
        if (node.Kind == "length") return ((string)Expression(node.Children[0])!).Length;
        if (node.Kind == "not") return !(bool)Expression(node.Children[0])!;
        var left = Expression(node.Children[0]);
        if (node.Operator == "ConditionalAnd") return (bool)left! && (bool)Expression(node.Children[1])!;
        if (node.Operator == "ConditionalOr") return (bool)left! || (bool)Expression(node.Children[1])!;
        var right = Expression(node.Children[1]);
        return node.Operator switch
        {
            "Equals" => Equals(left, right),
            "NotEquals" => !Equals(left, right),
            "LessThan" => (int)left! < (int)right!,
            "LessThanOrEqual" => (int)left! <= (int)right!,
            "GreaterThan" => (int)left! > (int)right!,
            "GreaterThanOrEqual" => (int)left! >= (int)right!,
            _ => throw new InvalidOperationException(node.Operator)
        };
    }
}
