using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>Builders for the language-neutral calculation IR (schema <c>$defs/expr</c>). Mirrors <c>ir</c> in fixtures/golden/lib.mjs.</summary>
internal static class Ir
{
    public static JsonNode Number(long v) => JsonValue.Create(v);

    public static JsonNode Number(double v) => double.IsFinite(v) && v == Math.Floor(v) && Math.Abs(v) < 1e15 ? JsonValue.Create((long)v) : JsonValue.Create(v);

    public static JsonObject Lit(JsonNode? value, string? type = null)
    {
        var o = new JsonObject { ["op"] = "lit" };
        if (type is not null)
        {
            o["type"] = type;
        }

        o["value"] = value;
        return o;
    }

    public static JsonObject Var(string reference, string? type = null) => Typed(new JsonObject { ["op"] = "var", ["ref"] = reference }, type);

    public static JsonObject Member(JsonNode of, string name, string? type = null) => Typed(new JsonObject { ["op"] = "member", ["of"] = of, ["name"] = name }, type);

    public static JsonObject Bin(string o, JsonNode l, JsonNode r, string? type = null) => Typed(new JsonObject { ["op"] = "bin", ["o"] = o, ["l"] = l, ["r"] = r }, type);

    public static JsonObject Cmp(string o, JsonNode l, JsonNode r) => new() { ["op"] = "cmp", ["o"] = o, ["l"] = l, ["r"] = r };

    public static JsonObject Logic(string o, IEnumerable<JsonNode> args) => new() { ["op"] = "logic", ["o"] = o, ["args"] = new JsonArray(args.ToArray()) };

    public static JsonObject Not(JsonNode a) => Logic("not", [a]);

    /// <summary>A conjunction/disjunction, flattening nested operands of the same operator and unwrapping a single operand.</summary>
    public static JsonNode Junction(string o, IEnumerable<JsonNode> args)
    {
        var flat = new List<JsonNode>();
        foreach (var a in args)
        {
            if (a is JsonObject { } obj && obj["op"]?.GetValue<string>() == "logic" && obj["o"]?.GetValue<string>() == o)
            {
                flat.AddRange(obj["args"]!.AsArray().Select(x => x!.DeepClone()));
            }
            else
            {
                flat.Add(a);
            }
        }

        return flat.Count == 1 ? flat[0] : Logic(o, flat);
    }

    public static JsonObject Cond(JsonNode test, JsonNode then, JsonNode otherwise) => new() { ["op"] = "cond", ["test"] = test, ["then"] = then, ["else"] = otherwise };

    public static JsonObject Coalesce(IEnumerable<JsonNode> args) => new() { ["op"] = "coalesce", ["args"] = new JsonArray(args.ToArray()) };

    public static JsonObject Conv(string to, JsonNode e) => new() { ["op"] = "conv", ["to"] = to, ["e"] = e };

    public static JsonObject Round(string mode, JsonNode e, JsonNode? digits, string? target)
    {
        var o = new JsonObject { ["op"] = "round", ["mode"] = mode, ["e"] = e };
        if (digits is not null)
        {
            o["digits"] = digits;
        }

        if (target is not null)
        {
            o["target"] = target;
        }

        return o;
    }

    public static JsonObject Agg(string fn, JsonNode over, string? bind, JsonNode? value, JsonNode? filter, string? type)
    {
        var o = new JsonObject { ["op"] = "agg", ["fn"] = fn, ["over"] = over };
        if (bind is not null)
        {
            o["bind"] = bind;
        }

        if (value is not null)
        {
            o["value"] = value;
        }

        if (filter is not null)
        {
            o["filter"] = filter;
        }

        return Typed(o, type);
    }

    public static JsonObject Call(string target, IEnumerable<JsonNode> args, string? type) =>
        Typed(new JsonObject { ["op"] = "call", ["target"] = target, ["args"] = new JsonArray(args.ToArray()) }, type);

    public static JsonObject Tuple(IEnumerable<JsonNode> items) => new() { ["op"] = "tuple", ["items"] = new JsonArray(items.ToArray()) };

    public static JsonObject Index(JsonNode of, JsonNode key, string? type) => Typed(new JsonObject { ["op"] = "index", ["of"] = of, ["key"] = key }, type);

    public static JsonObject Fold(JsonNode over, string bind, string acc, JsonNode init, JsonNode step, int skip, string? type)
    {
        var o = new JsonObject { ["op"] = "fold", ["over"] = over, ["bind"] = bind, ["acc"] = acc, ["init"] = init, ["step"] = step };
        if (skip > 0)
        {
            o["skip"] = skip;
        }

        return Typed(o, type);
    }

    public static JsonObject Opaque(string reason) => new() { ["op"] = "opaque", ["reason"] = reason };

    private static JsonObject Typed(JsonObject o, string? type)
    {
        if (type is not null)
        {
            o["type"] = type;
        }

        return o;
    }

    public static string Op(JsonNode? n) => (n as JsonObject)?["op"]?.GetValue<string>() ?? "";

    public static string Str(JsonNode? n, string property) => (n as JsonObject)?[property] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>The node and every expression below it.</summary>
    public static IEnumerable<JsonObject> Walk(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                if (o["op"] is not null)
                {
                    yield return o;
                }

                foreach (var (_, child) in o)
                {
                    foreach (var d in Walk(child))
                    {
                        yield return d;
                    }
                }

                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    foreach (var d in Walk(child))
                    {
                        yield return d;
                    }
                }

                break;
        }
    }

    // ---- canonical JSON: sorted keys, JSON.stringify scalars. Must equal canonicalJson in fixtures/golden/lib.mjs. ----

    public static string Canonical(JsonNode? n) => n switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Quote(kv.Key) + ":" + Canonical(kv.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        JsonValue v => Scalar(v),
        _ => throw new InvalidOperationException("unexpected JSON node"),
    };

    private static string Scalar(JsonValue v)
    {
        if (v.TryGetValue<string>(out var s))
        {
            return Quote(s);
        }

        if (v.TryGetValue<bool>(out var b))
        {
            return b ? "true" : "false";
        }

        if (v.TryGetValue<long>(out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        if (v.TryGetValue<int>(out var i))
        {
            return i.ToString(CultureInfo.InvariantCulture);
        }

        return v.TryGetValue<double>(out var d) ? JsNumber(d) : throw new InvalidOperationException("unexpected JSON scalar");
    }

    /// <summary>Number.prototype.toString for finite doubles.</summary>
    internal static string JsNumber(double d)
    {
        if (d == Math.Floor(d) && Math.Abs(d) < 1e21)
        {
            return d.ToString("0", CultureInfo.InvariantCulture);
        }

        var r = d.ToString("R", CultureInfo.InvariantCulture);
        var e = r.IndexOf('E', StringComparison.Ordinal);
        if (e < 0)
        {
            return r;
        }

        var mantissa = r[..e];
        var exponent = int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture);
        var negative = mantissa.StartsWith('-');
        var digits = mantissa.TrimStart('-').Replace(".", "", StringComparison.Ordinal);
        var pointAt = (mantissa.TrimStart('-').IndexOf('.', StringComparison.Ordinal) is var p and >= 0 ? p : mantissa.TrimStart('-').Length) + exponent;
        string text;
        if (exponent >= -6 && exponent < 21)
        {
            text = pointAt <= 0 ? "0." + new string('0', -pointAt) + digits : pointAt >= digits.Length ? digits + new string('0', pointAt - digits.Length) : digits[..pointAt] + "." + digits[pointAt..];
        }
        else
        {
            var sign = exponent < 0 ? "-" : "+";
            text = (digits.Length > 1 ? digits[..1] + "." + digits[1..] : digits) + "e" + sign + Math.Abs(exponent);
        }

        return (negative ? "-" : "") + text;
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }
}
