using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>
/// Derives the fixed attribute set of a calculation from its IR and the syntax it came from. Every value comes
/// from a structural rule; where the code does not settle a value the attribute stays <c>unknown</c>.
/// </summary>
internal sealed class Attributes(CalculationExtractor owner, Lowering lowering, JsonNode expr, string outputType, string headerEvidence)
{
    private readonly Dictionary<string, string> _names = lowering.Inputs.ToDictionary(i => i.Ref, i => i.Name);

    public JsonObject Build()
    {
        var attributes = new JsonObject
        {
            ["aggregation"] = Aggregation(),
            ["timeBucket"] = TimeBucket(),
            ["period"] = Period(),
            ["denominatorSource"] = DenominatorSource(),
            ["filters"] = Filters(),
            ["rounding"] = Rounding(),
            ["numericType"] = Known(outputType, null, Reference(lowering.Declaration.ReturnType)),
            ["nullHandling"] = NullHandling(),
            ["zeroDenominator"] = ZeroDenominator(),
            ["currency"] = new JsonObject { ["status"] = "unknown" },
        };
        if (Overflow() is { } overflow)
        {
            attributes["overflow"] = overflow;
        }

        return attributes;
    }

    // ---------------------------------------------------------------- evidence

    private string Reference(SyntaxNode node)
    {
        var file = owner.FileOf(node.SyntaxTree) ?? lowering.File;
        return file.Evidence(owner.Builder, node);
    }

    private string Fallback => headerEvidence;

    private static JsonObject Attr(string status, string? value, JsonObject? detail, params string[] evidence)
    {
        var o = new JsonObject { ["status"] = status };
        if (value is not null)
        {
            o["value"] = value;
        }

        if (detail is { Count: > 0 })
        {
            o["detail"] = detail;
        }

        if (evidence.Length > 0)
        {
            o["evidence"] = new JsonArray(evidence.Distinct().Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        }

        return o;
    }

    private static JsonObject Known(string value, JsonObject? detail, params string[] evidence) => Attr("known", value, detail, evidence);

    private static JsonObject Unknown() => new() { ["status"] = "unknown" };

    private string Anchor(string role) => lowering.Anchors.TryGetValue(role, out var node) ? Reference(node) : Fallback;

    // ---------------------------------------------------------------- structure

    private static JsonObject? Primary(JsonNode? n) => Ir.Op(n) switch
    {
        "agg" or "fold" => (JsonObject)n!,
        "round" or "conv" => Primary(n!["e"]),
        "cond" => Primary(n!["else"]) ?? Primary(n["then"]),
        "bin" => Primary(n!["l"]) ?? Primary(n["r"]),
        _ => null,
    };

    private IEnumerable<JsonObject> All => Ir.Walk(expr);

    private string PrimaryAnchor(JsonObject primary) =>
        Ir.Op(primary) == "agg" && lowering.Anchors.TryGetValue("agg:" + Ir.Str(primary, "fn"), out var inv) ? Reference(inv) : Anchor("fold");

    // ---------------------------------------------------------------- attributes

    private JsonObject Aggregation()
    {
        if (Primary(expr) is not { } primary)
        {
            return Unknown();
        }

        if (Ir.Op(primary) == "agg")
        {
            return Known(Ir.Str(primary, "fn"), null, PrimaryAnchor(primary));
        }

        var acc = Ir.Str(primary, "acc");
        var bind = Ir.Str(primary, "bind");
        var step = primary["step"];
        if (Ir.Op(step) == "bin" && Ir.Str(step, "o") == "+" && IsVar(step!["l"], acc) && IsVar(step["r"], bind))
        {
            return Known("sum", null, PrimaryAnchor(primary));
        }

        if (Smoothing(step, acc, bind) is { } alpha)
        {
            return Known("exponentialSmoothing", new JsonObject { ["alpha"] = alpha.DeepClone() }, PrimaryAnchor(primary));
        }

        return Unknown();
    }

    private static bool IsVar(JsonNode? n, string reference) => Ir.Op(n) == "var" && Ir.Str(n, "ref") == reference;

    private static bool IsElement(JsonNode? n, string bind) => Ir.Op(n) == "conv" ? IsElement(n!["e"], bind) : IsVar(n, bind);

    /// <summary>alpha * x + (1 - alpha) * acc, with alpha a literal.</summary>
    private static JsonNode? Smoothing(JsonNode? step, string acc, string bind)
    {
        if (Ir.Op(step) != "bin" || Ir.Str(step, "o") != "+")
        {
            return null;
        }

        var l = step!["l"];
        var r = step["r"];
        if (Ir.Op(l) == "bin" && Ir.Str(l, "o") == "*" && Ir.Op(l!["l"]) == "lit" && IsElement(l["r"], bind)
            && Ir.Op(r) == "bin" && Ir.Str(r, "o") == "*" && IsVar(r!["r"], acc)
            && r["l"] is JsonObject { } inverse && Ir.Op(inverse) == "bin" && Ir.Str(inverse, "o") == "-"
            && Ir.Op(inverse["l"]) == "lit" && Ir.Canonical(inverse["l"]!["value"]) == "1" && Ir.Canonical(inverse["r"]) == Ir.Canonical(l["l"]))
        {
            return l["l"]!["value"];
        }

        return null;
    }

    private JsonObject TimeBucket()
    {
        if (lowering.IsoWeekCall(owner) is { } iso)
        {
            var file = iso.File;
            return Known("isoWeek", new JsonObject { ["source"] = "System.Globalization.ISOWeek" }, file.Evidence(owner.Builder, iso.Node));
        }

        if (lowering.Inputs.FirstOrDefault(i => i.IsSequence) is { } sequence && sequence.Declaration is { } declaration)
        {
            return Attr("partial", "suppliedByCaller", new JsonObject { ["reason"] = "the grouping of the input is decided where it is built" }, Reference(declaration));
        }

        return Unknown();
    }

    private JsonObject Period()
    {
        var filters = Filter();
        if (filters is not null && RangeOf(filters) is { } range)
        {
            return Known("requested", new JsonObject { ["bounds"] = $"[{range.From}, {range.To}] inclusive" }, FilterEvidence());
        }

        if (All.FirstOrDefault(o => Ir.Op(o) == "call" && Ir.Str(o, "target").Contains("Enumerable.TakeLast", StringComparison.Ordinal)) is { } takeLast
            && takeLast["args"]!.AsArray() is { Count: 2 } args && Ir.Op(args[1]) == "lit")
        {
            return Known("lastElements", new JsonObject { ["window"] = args[1]!["value"]!.DeepClone() }, Anchor("return"));
        }

        if (lowering.Inputs.FirstOrDefault(i => i.IsSequence) is { } sequence)
        {
            if (lowering.TouchesDate)
            {
                var callers = owner.CallersOf(lowering.Method);
                var evidence = callers.Count > 0 ? callers.Select(c => Reference(c)).ToArray() : new[] { sequence.Declaration is { } d ? Reference(d) : Fallback };
                return Attr("partial", "suppliedByCaller", new JsonObject { ["callers"] = callers.Count }, evidence);
            }

            return Known("allSupplied", null, PrimaryEvidence());
        }

        return Unknown();
    }

    private string PrimaryEvidence() => Primary(expr) is { } p ? PrimaryAnchor(p) : Anchor("return");

    private JsonObject DenominatorSource()
    {
        var division = All.FirstOrDefault(o => Ir.Op(o) == "bin" && Ir.Str(o, "o") == "/");
        if (division is null)
        {
            return Primary(expr) is { } p && Ir.Op(p) == "agg" && Ir.Str(p, "fn") == "avg"
                ? Known("data", new JsonObject { ["description"] = "number of elements averaged" }, PrimaryAnchor(p))
                : Known("none", null, PrimaryEvidence());
        }

        var denominator = division["r"]!;
        while (Ir.Op(denominator) == "conv")
        {
            denominator = denominator["e"]!;
        }

        var evidence = Anchor("division");
        var parts = Ir.Walk(denominator).ToList();
        if (parts.All(o => Ir.Op(o) == "lit"))
        {
            return Known("constant", null, evidence);
        }

        var refs = parts.Where(o => Ir.Op(o) == "var").Select(o => Ir.Str(o, "ref")).ToList();
        var dataDriven = parts.Any(o => Ir.Op(o) is "agg" or "fold") || refs.Any(r => lowering.Inputs.Any(i => i.Ref == r && i.IsSequence));
        if (dataDriven)
        {
            return Known("data", null, evidence);
        }

        return refs.Count > 0 && refs.All(r => lowering.Inputs.Any(i => i.Ref == r && i.IsDate))
            ? Known("period", null, evidence)
            : Attr("partial", "suppliedByCaller", null, evidence);
    }

    private JsonNode? Filter()
    {
        var filters = All.Where(o => Ir.Op(o) == "agg" && o["filter"] is not null).Select(o => o["filter"]!).ToList();
        return filters.Count == 0 ? null : filters[0];
    }

    private string FilterEvidence() => lowering.FilterNodes.Count > 0 ? Reference(lowering.FilterNodes[0]) : Fallback;

    private JsonObject Filters()
    {
        var all = All.Where(o => Ir.Op(o) == "agg" && o["filter"] is not null).ToList();
        if (all.Count == 0)
        {
            return Known("none", null, PrimaryEvidence());
        }

        var rendered = all.Select(a => Render(a["filter"]!, Ir.Str(a, "bind"))).ToList();
        var evidence = lowering.FilterNodes.Select(n => Reference(n)).ToArray();
        if (rendered.Any(r => r is null))
        {
            return Attr("partial", null, new JsonObject { ["reason"] = "a predicate the renderer does not cover" }, evidence);
        }

        return Known(string.Join("; ", rendered.Distinct()), null, evidence);
    }

    private (string From, string To)? RangeOf(JsonNode filter)
    {
        var parts = Conjuncts(filter);
        foreach (var low in parts.Where(p => Ir.Op(p) == "cmp" && Ir.Str(p, "o") == ">=" && Ir.Op(p["l"]) == "member" && Ir.Op(p["r"]) == "var"))
        {
            var high = parts.FirstOrDefault(p => Ir.Op(p) == "cmp" && Ir.Str(p, "o") == "<=" && Ir.Canonical(p["l"]) == Ir.Canonical(low["l"]) && Ir.Op(p["r"]) == "var");
            if (high is not null && _names.TryGetValue(Ir.Str(low["r"], "ref"), out var from) && _names.TryGetValue(Ir.Str(high["r"], "ref"), out var to))
            {
                return (from, to);
            }
        }

        return null;
    }

    private static List<JsonNode> Conjuncts(JsonNode filter) =>
        Ir.Op(filter) == "logic" && Ir.Str(filter, "o") == "and" ? filter["args"]!.AsArray().Select(a => a!).ToList() : [filter];

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>A predicate in words, or null when it uses a shape the renderer does not cover.</summary>
    private string? Render(JsonNode filter, string bind)
    {
        var parts = Conjuncts(filter);
        var used = new HashSet<JsonNode>();
        var rendered = new List<string>();
        foreach (var part in parts)
        {
            if (used.Contains(part))
            {
                continue;
            }

            if (Ir.Op(part) == "cmp" && Ir.Str(part, "o") == ">=" && Ir.Op(part["l"]) == "member" && Ir.Op(part["r"]) == "var")
            {
                var high = parts.FirstOrDefault(p => Ir.Op(p) == "cmp" && Ir.Str(p, "o") == "<=" && Ir.Canonical(p["l"]) == Ir.Canonical(part["l"]) && Ir.Op(p["r"]) == "var");
                if (high is not null && _names.TryGetValue(Ir.Str(part["r"], "ref"), out var from) && _names.TryGetValue(Ir.Str(high["r"], "ref"), out var to))
                {
                    used.Add(high);
                    rendered.Add($"{LowerFirst(Ir.Str(part["l"], "name"))} in [{from},{to}]");
                    continue;
                }
            }

            if (Member(part, bind) is { } flag)
            {
                rendered.Add(flag);
            }
            else if (Ir.Op(part) == "logic" && Ir.Str(part, "o") == "not" && Member(part["args"]![0], bind) is { } negated)
            {
                rendered.Add($"not {negated}");
            }
            else if (Ir.Op(part) == "cmp" && Ir.Str(part, "o") is "!=" or "==" && Member(part["l"], bind) is { } m && Ir.Op(part["r"]) == "lit" && part["r"]!["value"] is null)
            {
                rendered.Add($"{m} is {(Ir.Str(part, "o") == "!=" ? "not " : "")}null");
            }
            else
            {
                return null;
            }
        }

        return string.Join(" and ", rendered);
    }

    private static string? Member(JsonNode? n, string bind) =>
        Ir.Op(n) == "member" && Ir.Op(n!["of"]) == "var" && Ir.Str(n["of"], "ref") == bind ? LowerFirst(Ir.Str(n, "name")) : null;

    private JsonObject Rounding()
    {
        if (All.FirstOrDefault(o => Ir.Op(o) == "round") is not { } round)
        {
            return Known("none", null, Anchor("return"));
        }

        var mode = Ir.Str(round, "mode");
        var detail = new JsonObject();
        if (round["digits"] is { } digits && Ir.Op(digits) == "lit")
        {
            detail["digits"] = digits["value"]!.DeepClone();
        }

        return Attr(mode == "unknown" ? "partial" : "known", mode, detail, Anchor("round"));
    }

    private JsonObject NullHandling()
    {
        if (All.Any(o => Ir.Op(o) == "coalesce" && o["args"]!.AsArray().Any(a => Ir.Op(a) == "lit" && Ir.Canonical(a!["value"]) == "0")))
        {
            return Known("nullAsZero", null, Anchor("coalesce"));
        }

        if (All.Any(o => Ir.Op(o) == "agg" && o["filter"] is { } f && Conjuncts(f).Any(c => Ir.Op(c) == "cmp" && Ir.Str(c, "o") == "!=" && Ir.Op(c["r"]) == "lit" && c["r"]!["value"] is null)))
        {
            return Known("excluded", null, FilterEvidence());
        }

        return lowering.UsesNullable ? Unknown() : Known("notApplicable", null, Reference(lowering.Declaration.ParameterList));
    }

    private JsonObject ZeroDenominator()
    {
        var divisions = All.Where(o => Ir.Op(o) == "bin" && Ir.Str(o, "o") == "/" && Ir.Op(o["r"]) != "lit").ToList();
        if (expr is JsonObject { } root && Ir.Op(root) == "cond" && Ir.Op(root["test"]) == "cmp" && Ir.Str(root["test"], "o") == "==")
        {
            var test = root["test"]!;
            var zero = Ir.Op(test["r"]) == "lit" && Ir.Canonical(test["r"]!["value"]) == "0" ? test["l"] : null;
            var guardsDenominator = zero is not null && (divisions.Any(d => Ir.Canonical(Unconv(d["r"])) == Ir.Canonical(Unconv(zero))) || Ir.Op(zero) == "member" && Ir.Str(zero, "name") is "Count" or "Length");
            if (guardsDenominator && Ir.Op(root["then"]) == "lit" && root["then"]!["value"] is JsonValue literal)
            {
                var evidence = Anchor("guardReturn");
                if (literal.TryGetValue<string>(out var text) && text == "NaN")
                {
                    return Known("returnsNaN", new JsonObject { ["description"] = "an empty input returns NaN" }, evidence);
                }

                if (Ir.Canonical(literal) == "0")
                {
                    return Known("returnsZero", null, evidence);
                }
            }
        }

        if (divisions.Count > 0)
        {
            var type = Ir.Str(divisions[0], "type");
            return type is "decimal" or "int32" or "int64"
                ? Known("throws", new JsonObject { ["exception"] = "System.DivideByZeroException" }, Anchor("division"))
                : Unknown();
        }

        if (Primary(expr) is { } p && Ir.Op(p) == "agg" && Ir.Str(p, "fn") is "avg" or "min" or "max")
        {
            return Known("throws", new JsonObject { ["exception"] = "System.InvalidOperationException" }, PrimaryAnchor(p));
        }

        return Known("notApplicable", null, PrimaryEvidence());
    }

    private static JsonNode? Unconv(JsonNode? n) => Ir.Op(n) == "conv" ? Unconv(n!["e"]) : n;

    private JsonObject? Overflow()
    {
        if (!lowering.Anchors.TryGetValue("intArithmetic", out var node) || !All.Any(o => Ir.Op(o) == "fold" || Ir.Op(o) == "bin"))
        {
            return null;
        }

        var inChecked = node.Ancestors().Any(a => a is CheckedStatementSyntax { RawKind: (int)SyntaxKind.CheckedStatement } or CheckedExpressionSyntax { RawKind: (int)SyntaxKind.CheckedExpression });
        var inUnchecked = node.Ancestors().Any(a => a is CheckedStatementSyntax { RawKind: (int)SyntaxKind.UncheckedStatement } or CheckedExpressionSyntax { RawKind: (int)SyntaxKind.UncheckedExpression });
        var overflows = inChecked || (owner.CheckedProject && !inUnchecked);
        return overflows
            ? Known("throws", new JsonObject { ["context"] = "checked" }, Reference(node))
            : Known("wraps", new JsonObject { ["context"] = "unchecked (C# default)" }, Reference(node));
    }
}
