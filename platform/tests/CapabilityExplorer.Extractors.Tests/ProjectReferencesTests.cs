using System.Diagnostics;
using System.Text.Json;
using CapabilityExplorer.Extractors.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class ProjectReferencesTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "ce-projects-" + Guid.NewGuid().ToString("N"));
    private readonly string _outputRoot = Path.Combine(AppContext.BaseDirectory, "ce-project-outputs-" + Guid.NewGuid().ToString("N"));

    public ProjectReferencesTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outputRoot);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outputRoot, recursive: true);
    }

    [Fact]
    public void Chain_resolves_a_type_returned_by_B_and_a_direct_call_to_C_without_a_direct_reference()
    {
        Project("C", """
            namespace C;
            public sealed class Value { public int Read() => 7; }
            public static class Api { public static int Read() => 9; }
            """);
        Project("B", "namespace B; public static class Bridge { public static C.Value Create() => new C.Value(); }", "C");
        Project("A", """
            namespace A;
            public static class Caller
            {
                public static int ThroughB() => B.Bridge.Create().Read();
                public static int Directly() => C.Api.Read();
            }
            """, "B");

        var units = Extract();
        var a = Records(Unit(units, "A"));
        AssertDependencies(a, "A", "B");
        AssertDependencies(Records(Unit(units, "B")), "B", "C");
        AssertDependencies(Records(Unit(units, "C")), "C");
        AssertCalls(a, "cs|projects|A|M:A.Caller.ThroughB",
            "cs|projects|B|M:B.Bridge.Create", "cs|projects|C|M:C.Value.Read");
        AssertCalls(a, "cs|projects|A|M:A.Caller.Directly", "cs|projects|C|M:C.Api.Read");
        AssertAllFull(units);
    }

    [Fact]
    public void Diamond_shares_C_without_duplicate_diagnostics_and_repeats_byte_for_byte()
    {
        Project("C", "namespace C; public sealed class Value { public int Read() => 7; }");
        Project("B", "namespace B; public static class Bridge { public static C.Value Create() => new C.Value(); }", "C");
        Project("D", "namespace D; public static class Bridge { public static C.Value Create() => new C.Value(); }", "C");
        Project("A", """
            namespace A;
            public static class Caller
            {
                public static int Left() => B.Bridge.Create().Read();
                public static int Right() => D.Bridge.Create().Read();
            }
            """, "B", "D");

        var first = Extract();
        var second = Extract();
        Assert.Equal(first.Select(u => u.UnitPath), second.Select(u => u.UnitPath));
        Assert.Equal(first.Select(u => u.Jsonl), second.Select(u => u.Jsonl));
        var a = Records(Unit(first, "A"));
        AssertDependencies(a, "A", "B", "D");
        AssertDependencies(Records(Unit(first, "B")), "B", "C");
        AssertDependencies(Records(Unit(first, "D")), "D", "C");
        AssertCalls(a, "cs|projects|A|M:A.Caller.Left",
            "cs|projects|B|M:B.Bridge.Create", "cs|projects|C|M:C.Value.Read");
        AssertCalls(a, "cs|projects|A|M:A.Caller.Right",
            "cs|projects|D|M:D.Bridge.Create", "cs|projects|C|M:C.Value.Read");
        AssertAllFull(first);
    }

    [Fact]
    public void Missing_project_preserves_diagnostic_partial_status_and_resolvable_neighbor()
    {
        Project("B", "namespace B; public static class Api { public static int Read() => 7; }");
        Project("A", "namespace A; public static class Caller { public static int Read() => B.Api.Read(); }", "B", "Missing");

        var units = Extract();
        var a = Records(Unit(units, "A"));
        var diagnostic = Assert.Single(OfKind(a, "diagnostic"));
        Assert.Equal("MISSING_PROJECT_REFERENCE", diagnostic.GetProperty("code").GetString());
        Assert.Equal("A/A.csproj", diagnostic.GetProperty("path").GetString());
        Assert.True(diagnostic.GetProperty("affectsCoverage").GetBoolean());
        Assert.Contains("Missing", diagnostic.GetProperty("message").GetString());
        Assert.Equal("partial", Complete(a).GetProperty("status").GetString());
        AssertCalls(a, "cs|projects|A|M:A.Caller.Read", "cs|projects|B|M:B.Api.Read");
        AssertAllFull([Unit(units, "B")]);
    }

    [Fact]
    public void Missing_build_order_only_project_preserves_diagnostic_and_resolvable_neighbor()
    {
        Project("C", "namespace C; public static class Api { public static int Read() => 7; }");
        Project("A", "namespace A; public static class Caller { public static int Read() => C.Api.Read(); }", "B", "C");
        ReferenceMetadata(" ReferenceOutputAssembly=\"false\"", "");

        var units = Extract();
        var records = Records(Unit(units, "A"));
        var diagnostic = Assert.Single(OfKind(records, "diagnostic"));
        Assert.Equal("MISSING_PROJECT_REFERENCE", diagnostic.GetProperty("code").GetString());
        Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
        Assert.Equal("A/A.csproj", diagnostic.GetProperty("path").GetString());
        Assert.True(diagnostic.GetProperty("affectsCoverage").GetBoolean());
        Assert.Contains("../B/B.csproj", diagnostic.GetProperty("message").GetString());
        Assert.Equal("partial", Complete(records).GetProperty("status").GetString());
        AssertDependencies(records, "A", "C");
        AssertCalls(records, "cs|projects|A|M:A.Caller.Read", "cs|projects|C|M:C.Api.Read");
        AssertAllFull([Unit(units, "C")]);
    }

    [Fact]
    public void Unrelated_discovered_project_does_not_make_its_symbols_visible_to_A()
    {
        Project("B", "namespace B; public static class Api { public static int Read() => 7; }");
        Project("Unrelated", "namespace Unrelated; public static class Api { public static int Read() => 9; }");
        Project("A", """
            namespace A;
            public static class Caller
            {
                public static int Visible() => B.Api.Read();
                public static int Hidden() => Unrelated.Api.Read();
            }
            """, "B");

        var units = Extract();
        Assert.Equal(3, units.Count);
        var a = Records(Unit(units, "A"));
        AssertDependencies(a, "A", "B");
        AssertCalls(a, "cs|projects|A|M:A.Caller.Visible", "cs|projects|B|M:B.Api.Read");
        var hidden = Assert.Single(Calls(a, "cs|projects|A|M:A.Caller.Hidden"));
        Assert.Equal("unresolved", hidden.GetProperty("resolution").GetString());
        Assert.Equal("missing-reference", hidden.GetProperty("unresolvedReason").GetString());
        Assert.False(hidden.TryGetProperty("to", out _));
        var diagnostic = Assert.Single(OfKind(a, "diagnostic"));
        Assert.Equal("COMPILER_CS0103", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Unrelated", diagnostic.GetProperty("message").GetString());
        Assert.Equal("partial", Complete(a).GetProperty("status").GetString());
        AssertAllFull(units.Where(u => u.UnitName != "A"));
    }

    [Fact]
    public async Task Given_project_references_when_the_cli_extracts_then_stage_timings_preserve_artifact_bytes()
    {
        Project("B","namespace B; public static class Api { public static int Read() => 7; }");
        Project("A","namespace A; public static class Api { public static int Read() => B.Api.Read(); }","B");
        var expected = Extract();
        var output = Path.Combine(_root,"out");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repository.FullName,"platform/global.json")))
            repository = repository.Parent ?? throw new InvalidOperationException("repository root not found");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        start.ArgumentList.Add(Path.Combine(repository.FullName,
            "platform/src/CapabilityExplorer.Extractors.CSharp/bin",configuration,"net10.0/ce-cs.dll"));
        foreach (var argument in new[] { "extract", "--source", _root, "--repository", "projects", "--commit", Sha, "--out", output })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await child.WaitForExitAsync(timeout.Token);
        Assert.True(child.ExitCode == 0,await stderr);
        foreach (var unit in expected)
            Assert.Equal(unit.Jsonl,File.ReadAllText(Path.Combine(output,unit.UnitName + ".jsonl")));
        var timings = (await stdout).Split('\n').Where(line => line.StartsWith("-- cs-timing: ",StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line[14..]).RootElement.Clone()).ToArray();
        foreach (var stage in new[] { "cs.projects", "cs.compilations", "cs.total" })
            Assert.Single(timings,entry => entry.GetProperty("stage").GetString() == stage);
        foreach (var stage in new[] { "cs.references", "cs.parse", "cs.reference-resolution", "cs.reference-fingerprint", "cs.diagnostics", "cs.walk", "cs.calculations", "cs.rules.routes", "cs.rules.di", "cs.rules.ef", "cs.rules.dapper", "cs.rules.servicebus", "cs.rules.http", "cs.serialize", "cs.validate", "cs.write" })
            Assert.Equal(2,timings.Count(entry => entry.GetProperty("stage").GetString() == stage));
        Assert.All(timings,entry =>
        {
            Assert.Equal("ce.csharp.timing.v1",entry.GetProperty("schema").GetString());
            Assert.Equal("completed",entry.GetProperty("outcome").GetString());
            Assert.True(entry.GetProperty("elapsedMilliseconds").GetDouble() >= 0);
            Assert.DoesNotContain(_root,entry.GetRawText(),StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(" ReferenceOutputAssembly=\"false\"", "")]
    [InlineData("", "<ReferenceOutputAssembly>false</ReferenceOutputAssembly>")]
    [InlineData(" ReferenceOutputAssembly=\" FALSE \"", "")]
    public void Build_order_only_reference_excludes_project_transitive_and_package_symbols(string attributes, string metadata)
    {
        var packages = Path.Combine(_root, "packages");
        var lib = Directory.CreateDirectory(Path.Combine(packages, "package", "1.0.0", "lib", "net10.0")).FullName;
        var framework = Directory.EnumerateDirectories(Path.Combine(Packs(), "Microsoft.NETCore.App.Ref"))
            .Order(StringComparer.Ordinal).Select(d => Path.Combine(d, "ref", "net10.0")).Last(Directory.Exists);
        var frameworkReferences = Directory.EnumerateFiles(framework, "*.dll")
            .Order(StringComparer.Ordinal).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
        var package = CSharpCompilation.Create("Package",
            [CSharpSyntaxTree.ParseText("namespace Package; public static class Api { public static int Read() => 3; }")],
            frameworkReferences, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(package.Emit(Path.Combine(lib, "Package.dll")).Success);
        Project("C", "namespace C; public static class Api { public static int Read() => 1; }");
        Project("B", "namespace B; public static class Api { public static int Read() => C.Api.Read() + Package.Api.Read(); }", "C");
        var bPath = Path.Combine(_root, "B", "B.csproj");
        File.WriteAllText(bPath, File.ReadAllText(bPath).Replace("</ItemGroup>",
            "<PackageReference Include=\"Package\" Version=\"1.0.0\" /></ItemGroup>", StringComparison.Ordinal));
        const string caller = """
            namespace A;
            public static class Caller
            {
                public static int Direct() => B.Api.Read();
                public static int Transitive() => C.Api.Read();
                public static int PackageCall() => Package.Api.Read();
            }
            """;
        Project("A", caller, "B");
        ReferenceMetadata(attributes, metadata);

        // This oracle intentionally has no B, C or package reference, independently of project XML parsing.
        var oracle = CSharpCompilation.Create("Oracle", [CSharpSyntaxTree.ParseText(caller)], frameworkReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var oracleErrors = oracle.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Equal(3, oracleErrors.Length);
        Assert.All(oracleErrors, d => Assert.Equal("CS0103", d.Id));

        IReadOnlyList<UnitResult> Run() => new CSharpExtractor(new ExtractorOptions(_root, "projects", Sha, packages, Packs())).Extract();
        var first = Run();
        var second = Run();
        Assert.Equal(first.Select(u => u.Jsonl), second.Select(u => u.Jsonl));
        var records = Records(Unit(first, "A"));
        AssertDependencies(records, "A", "B");
        var diagnostic = Assert.Single(OfKind(records, "diagnostic"));
        Assert.Equal($"COMPILER_{oracleErrors[0].Id}", diagnostic.GetProperty("code").GetString());
        Assert.Equal(oracleErrors[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            diagnostic.GetProperty("message").GetString());
        foreach (var method in new[] { "Direct", "Transitive", "PackageCall" })
        {
            var call = Assert.Single(Calls(records, $"cs|projects|A|M:A.Caller.{method}"));
            Assert.Equal("unresolved", call.GetProperty("resolution").GetString());
            Assert.False(call.TryGetProperty("to", out _));
        }
        Assert.Equal("partial", Complete(records).GetProperty("status").GetString());
        AssertAllFull(first.Where(u => u.UnitName != "A"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(" ReferenceOutputAssembly=\"true\"", "")]
    [InlineData("", "<ReferenceOutputAssembly> TRUE </ReferenceOutputAssembly>")]
    public void Default_and_literal_true_metadata_preserve_compile_visibility(string attributes, string metadata)
    {
        Project("C", "namespace C; public static class Api { public static int Read() => 1; }");
        Project("B", "namespace B; public static class Api { public static int Read() => C.Api.Read(); }", "C");
        Project("A", "namespace A; public static class Caller { public static int Read() => B.Api.Read() + C.Api.Read(); }", "B");
        ReferenceMetadata(attributes, metadata);
        var units = Extract();
        var records = Records(Unit(units, "A"));
        AssertDependencies(records, "A", "B");
        AssertCalls(records, "cs|projects|A|M:A.Caller.Read", "cs|projects|B|M:B.Api.Read", "cs|projects|C|M:C.Api.Read");
        AssertAllFull(units);
    }

    [Fact]
    public void Build_order_only_reference_without_symbol_usage_preserves_full_coverage()
    {
        Project("B", "namespace B; public static class Api { public static int Read() => 1; }");
        Project("A", "namespace A; public static class Caller { public static int Read() => 2; }", "B");
        ReferenceMetadata(" ReferenceOutputAssembly=\"false\"", "");
        var units = Extract();
        AssertDependencies(Records(Unit(units, "A")), "A", "B");
        AssertAllFull(units);
    }

    [Theory]
    [InlineData("", "<ReferenceOutputAssembly Condition=\"'$(Configuration)' == 'Debug'\">true</ReferenceOutputAssembly>")]
    [InlineData(" ReferenceOutputAssembly=\"$(ExposeDependency)\"", "")]
    [InlineData("", "<ReferenceOutputAssembly>$(ExposeDependency)</ReferenceOutputAssembly>")]
    [InlineData(" ReferenceOutputAssembly=\"true\"", "<ReferenceOutputAssembly>false</ReferenceOutputAssembly>")]
    [InlineData("", "<ReferenceOutputAssembly>true</ReferenceOutputAssembly><ReferenceOutputAssembly>false</ReferenceOutputAssembly>")]
    [InlineData(" ReferenceOutputAssembly=\"not-a-boolean\"", "")]
    public void Unsupported_output_metadata_is_diagnosed_without_compile_visibility(string attributes, string metadata)
    {
        Project("B", "namespace B; public static class Api { public static int Read() => 1; }");
        Project("A", "namespace A; public static class Caller { public static int Read() => B.Api.Read(); }", "B");
        ReferenceMetadata(attributes, metadata);
        var units = Extract();
        var records = Records(Unit(units, "A"));
        AssertDependencies(records, "A", "B");
        var diagnostic = Assert.Single(OfKind(records, "diagnostic"),
            d => d.GetProperty("code").GetString() == "UNSUPPORTED_PROJECT_REFERENCE_METADATA");
        Assert.True(diagnostic.GetProperty("affectsCoverage").GetBoolean());
        Assert.Equal("warning", diagnostic.GetProperty("severity").GetString());
        Assert.Equal("A/A.csproj", diagnostic.GetProperty("path").GetString());
        var call = Assert.Single(Calls(records, "cs|projects|A|M:A.Caller.Read"));
        Assert.Equal("unresolved", call.GetProperty("resolution").GetString());
        Assert.False(call.TryGetProperty("to", out _));
        Assert.Equal("partial", Complete(records).GetProperty("status").GetString());
        Assert.Equal(units.Select(u => u.Jsonl), Extract().Select(u => u.Jsonl));
    }

    private void ReferenceMetadata(string attributes, string metadata)
    {
        var path = Path.Combine(_root, "A", "A.csproj");
        File.WriteAllText(path, File.ReadAllText(path).Replace("<ProjectReference Include=\"../B/B.csproj\" />",
            $"<ProjectReference Include=\"../B/B.csproj\"{attributes}>{metadata}</ProjectReference>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Legacy_and_selected_modes_from_the_same_binary_emit_identical_artifact_bytes()
    {
        Project("B", "namespace B; public static class Api { public static int Read() => 7; }");
        Project("A", "namespace A; public static class Caller { public static int Read() => B.Api.Read(); }", "B");
        var projects = new[] { "A/A.csproj", "B/B.csproj" };
        var selection = ProjectModel.ComputeSelectionContract(_root, projects);
        var output = Path.Combine(_outputRoot, "legacy-out");
        var selectedOutput = Path.Combine(_outputRoot, "selected-out");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(selectedOutput);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repository.FullName, "platform/global.json")))
            repository = repository.Parent ?? throw new InvalidOperationException("repository root not found");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(repository.FullName,
            "platform/src/CapabilityExplorer.Extractors.CSharp/bin", configuration, "net10.0/ce-cs.dll");

        async Task<string> Run(string command, string destination, bool selected)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repository.FullName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(cli);
            foreach (var argument in new[] { command, "--source", _root, "--repository", "projects", "--commit", Sha, "--out", destination })
                start.ArgumentList.Add(argument);
            if (selected)
            {
                start.ArgumentList.Add("--selection-sha256");
                start.ArgumentList.Add(selection.SelectionSha256);
                start.ArgumentList.Add("--source-sha256");
                start.ArgumentList.Add(selection.SourceSha256);
                foreach (var path in selection.ProjectPaths)
                {
                    start.ArgumentList.Add("--project");
                    start.ArgumentList.Add(path);
                }
            }
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await child.WaitForExitAsync(timeout.Token);
            var outputText = await stdout;
            Assert.True(child.ExitCode == 0, await stderr);
            return outputText;
        }

        await Run("extract", output, selected: false);
        var selectedStdout = await Run("extract-selected-v1", selectedOutput, selected: true);

        var legacyFiles = Directory.GetFiles(output).Order(StringComparer.Ordinal).ToArray();
        var selectedFiles = Directory.GetFiles(selectedOutput).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(legacyFiles.Select(Path.GetFileName), selectedFiles.Select(Path.GetFileName));
        Assert.Equal(legacyFiles.Select(File.ReadAllBytes), selectedFiles.Select(File.ReadAllBytes));
        Assert.Contains("A/A.csproj", selectedStdout, StringComparison.Ordinal);
    }

    private void Project(string name, string source, params string[] references)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var items = string.Concat(references.Select(r => $"<ProjectReference Include=\"../{r}/{r}.csproj\" />"));
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>{items}</ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, name + ".cs"), source);
    }

    private IReadOnlyList<UnitResult> Extract()
    {
        return new CSharpExtractor(new ExtractorOptions(_root, "projects", Sha, PackagesRoot: null, ReferencePacks: Packs())).Extract();
    }

    private static string Packs() => Path.GetFullPath(Path.Combine(
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));

    private static UnitResult Unit(IEnumerable<UnitResult> units, string name) =>
        Assert.Single(units, u => u.UnitPath == $"{name}/{name}.csproj");

    private static JsonElement[] Records(UnitResult unit) =>
        unit.Jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    private static IEnumerable<JsonElement> OfKind(IEnumerable<JsonElement> records, string kind) =>
        records.Where(r => r.GetProperty("kind").GetString() == kind);

    private static JsonElement Complete(IEnumerable<JsonElement> records) => Assert.Single(OfKind(records, "complete"));

    private static IEnumerable<JsonElement> Calls(IEnumerable<JsonElement> records, string caller) =>
        OfKind(records, "edge").Where(r => r.GetProperty("relation").GetString() == "CALLS"
            && r.GetProperty("from").GetString() == caller);

    private static void AssertCalls(JsonElement[] records, string caller, params string[] targets)
    {
        var calls = Calls(records, caller).ToArray();
        var resolved = calls.Where(c => c.GetProperty("resolution").GetString() == "resolved")
            .Select(c => c.GetProperty("to").GetString()).Order(StringComparer.Ordinal).ToArray();
        Assert.True(targets.Order(StringComparer.Ordinal).SequenceEqual(resolved) && calls.Length == targets.Length,
            $"Expected resolved repository CALLS from {caller} to [{string.Join(", ", targets)}]. " +
            $"Actual calls: {string.Join("\n", calls.Select(c => c.GetRawText()))}. " +
            $"Diagnostics: {string.Join("\n", OfKind(records, "diagnostic").Select(d => d.GetRawText()))}");
    }

    private static void AssertDependencies(JsonElement[] records, string project, params string[] targets)
    {
        var dependencies = OfKind(records, "edge").Where(r => r.GetProperty("relation").GetString() == "DEPENDS_ON").ToArray();
        Assert.All(dependencies, d => Assert.Equal($"proj|projects|{project}/{project}.csproj", d.GetProperty("from").GetString()));
        Assert.Equal(targets.Select(t => $"proj|projects|{t}/{t}.csproj").Order(StringComparer.Ordinal),
            dependencies.Select(d => d.GetProperty("to").GetString()).Order(StringComparer.Ordinal));
    }

    private static void AssertAllFull(IEnumerable<UnitResult> units)
    {
        foreach (var unit in units)
        {
            var records = Records(unit);
            Assert.Empty(OfKind(records, "diagnostic"));
            var complete = Complete(records);
            Assert.Equal("full", complete.GetProperty("status").GetString());
            Assert.Equal(0, complete.GetProperty("coverage").GetProperty("unresolvedEdges").GetInt32());
        }
    }
}
