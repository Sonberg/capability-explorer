using System.Security.Cryptography;
using System.Text;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class CandidateSafetyTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string scratch;

    public CandidateSafetyTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "fixtures/candidates"))) root = root.Parent;
        scratch = Path.Combine(root!.FullName, "platform/tests/CapabilityExplorer.Extractors.Tests/Fixtures/CandidateScratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
    }

    public void Dispose() => Directory.Delete(scratch, true);

    [Theory]
    [InlineData("if(input == null) throw new System.ArgumentNullException(paramName: input); Record(input, \"x\");")]
    [InlineData("if(input == null) throw new System.ArgumentNullException(input); Record(second: \"x\", first: input);")]
    public void Given_unmodeled_argument_binding_then_reject_instead_of_losing_parameter_mapping(string body)
    {
        var result = Extract("public static class C { public static extern void Record(string first, string second = \"x\"); public static bool M(string input) { " + body + " return true; } }");
        Assert.Empty(result.Candidates);
        Assert.Equal("partial", result.Coverage.Status);
    }

    [Fact]
    public void Given_spoofed_framework_exception_then_no_approved_exception_semantics()
    {
        var result = Extract("""
            namespace System { public class ArgumentException : Exception { public ArgumentException(string message) {} } }
            public static class C {
                public static extern void Record(string input);
                public static bool M(string input) {
                    if(input == null) throw new System.ArgumentException(input);
                    Record(input); return true;
                }
            }
            """);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, d => d.Code == "unsupported-exception");
    }

    [Theory]
    [InlineData("#if UNKNOWN\n public static string Hidden(int x) { return \"hidden\"; }\n#endif")]
    [InlineData("static int initialization = Bootstrap(); public static extern int Bootstrap();")]
    public void Given_uncovered_executable_context_then_coverage_is_not_full(string declarations)
    {
        var result = Extract("public static class C {\n" + declarations + "\npublic static string M(int x) { if(x < 0) return \"Invalid\"; return \"None\"; } }");
        Assert.Single(result.Candidates);
        Assert.Equal("partial", result.Coverage.Status);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Theory]
    [InlineData("public static int Value => Effect(); public static extern int Effect();")]
    [InlineData("public int this[int index] => Effect(); public static extern int Effect();")]
    [InlineData("public static C operator +(C left, C right) { Effect(); return left; } public static extern void Effect();")]
    [InlineData("public static C operator -(C value) => Effect(); public static extern C Effect();")]
    [InlineData("public static implicit operator int(C value) { return Effect(); } public static extern int Effect();")]
    [InlineData("public static explicit operator bool(C value) => Effect(); public static extern bool Effect();")]
    [InlineData("public static event System.Action Changed = Effect(); public static extern System.Action Effect();")]
    public void Given_uncovered_member_effect_then_partial_preserves_supported_neighbor(string declaration)
    {
        var result = Extract("public class C { " + declaration +
            " public static string Classify(int amount) { if(amount < 0) return \"Invalid\"; return \"None\"; } }");
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("decision", candidate.Kind);
        Assert.Equal(1, result.Coverage.SupportedMethods);
        Assert.Equal(0, result.Coverage.UnsupportedMethods);
        Assert.Equal("partial", result.Coverage.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == "outside-method-scope");
        Assert.Equal(result.ToJson(), CandidateExtractor.Extract(Path.Combine(scratch, "input.cs"), "synthetic", Sha).ToJson());
    }

    [Fact]
    public void Given_primary_constructor_base_effect_then_partial_without_changing_classifier()
    {
        var result = Extract("""
            public class Base(int value);
            public class C(int value) : Base(Effect(value)) {
                public static extern int Effect(int value);
                public static string Classify(int amount) { if(amount < 0) return "Invalid"; return "None"; }
            }
            """);
        Assert.Single(result.Candidates);
        Assert.Equal(1, result.Coverage.SupportedMethods);
        Assert.Equal(0, result.Coverage.UnsupportedMethods);
        Assert.Equal("partial", result.Coverage.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == "outside-method-scope");
        Assert.Equal(result.ToJson(), CandidateExtractor.Extract(Path.Combine(scratch, "input.cs"), "synthetic", Sha).ToJson());
    }

    [Fact]
    public void Given_compile_time_only_declarations_then_full_and_repeat_identity_remain()
    {
        var result = Extract("""
            [System.Obsolete("compile-time")]
            public class C {
                public const int Limit = 100;
                public enum Category { Invalid = -1, None = 0, Gold = Limit }
                public static extern void Declaration(int amount = Limit);
                public static string Classify(int amount) { if(amount < 0) return "Invalid"; return "None"; }
            }
            """);
        Assert.Single(result.Candidates);
        Assert.Equal("full", result.Coverage.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(result.ToJson(), CandidateExtractor.Extract(Path.Combine(scratch, "input.cs"), "synthetic", Sha).ToJson());
    }

    [Fact]
    public void Given_nullable_reference_annotation_then_scalar_type_remains_schema_compatible()
    {
        var result = Extract("""
            #nullable enable
            public static class C {
                public static extern void Record(string input);
                public static bool M(string? input) {
                    if(input == null) throw new System.ArgumentNullException(nameof(input));
                    Record(input); return true;
                }
            }
            """);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("string", Assert.Single(candidate.Parameters).Type);
        Assert.All(CandidateRecipe.Walk(candidate.Body).Where(n => n.Kind == "parameter"),
            node => Assert.Equal("string", node.Type));
    }

    [Fact]
    public void Given_invalid_utf8_then_no_replacement_characters_or_false_identity()
    {
        var path = Path.Combine(scratch, "invalid.cs");
        File.WriteAllBytes(path, [0xff, 0xfe, 0xff]);
        Assert.Throws<ArgumentException>(() => CandidateExtractor.Extract(path, "synthetic", Sha));
    }

    [Fact]
    public void Given_renamed_symbols_and_bom_then_semantics_and_original_byte_spans_survive()
    {
        const string text = "\uFEFFpublic static class Renamed { public static string NotBusinessNamed(int number, bool flag) { if(number < 0) return \"Invalid\"; if(flag && number >= 100) return \"Gold\"; return \"None\"; } }";
        var result = Extract(text);
        Assert.Equal("full", result.Coverage.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("decision", candidate.Kind);
        Assert.Equal("Gold", new CandidateOracle(candidate, new() { ["number"] = 100, ["flag"] = true }, false).Run());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), result.SourceSha256);
        var predicate = CandidateRecipe.Walk(candidate.Body).First(n => n.RawPredicate is not null);
        Assert.Equal("number < 0", text.Substring(predicate.Source.Start, predicate.Source.Length));
    }

    private CandidateArtifact Extract(string source)
    {
        var path = Path.Combine(scratch, "input.cs");
        File.WriteAllText(path, source, new UTF8Encoding(false));
        return CandidateExtractor.Extract(path, "synthetic", Sha);
    }
}
