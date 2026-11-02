using System.Text.Json;
using CapabilityExplorer.Extractors.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class ResolutionFidelityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ce-resolution-").FullName;
    private string Cache => Path.Combine(_root, "packages");
    private static string Packs => Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Older_compatible_framework_outside_the_previous_fixed_list_is_visible()
    {
        Package("Old", "net5.0", "namespace Old; public static class Api { public static int Read() => 1; }");
        var result = new PackageResolver(Cache).Resolve([new PackageRef("Old", "1.0.0", 1)]);
        Assert.Single(result.Assemblies);
        Assert.Contains("net5.0", result.Assemblies[0]);
    }

    [Theory]
    [InlineData("public class { }")]
    [InlineData("public class App { public void () { } }")]
    [InlineData("public class App { public int { get; set; } }")]
    public void Missing_declaration_names_preserve_diagnostics_without_unnamed_nodes(string code)
    {
        Project("", code);
        var output = Extract();
        Assert.Contains("COMPILER_", output);
        Assert.Contains("\"status\":\"partial\"", output);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("kind").GetString() == "node")
            {
                Assert.NotEmpty(record.RootElement.GetProperty("name").GetString()!);
            }
        }
    }

    [Fact]
    public void Conflicting_versions_in_a_lock_fail_instead_of_silently_taking_the_first()
    {
        var packages = new PackageLock();
        Assert.Throws<InvalidDataException>(() => packages.Set("App.csproj", [new("Same", "1.0.0"), new("same", "2.0.0")]));
    }

    [Fact]
    public void Extension_blocks_emit_named_members_without_unnamed_type_nodes()
    {
        Project("", "public static class Extensions { extension(string value) { public bool IsLong() => value.Length > 3; public int Count => value.Length; } }");
        var output = Extract();
        Assert.DoesNotContain("COMPILER_", output);
        Assert.Contains("\"status\":\"full\"", output);
        Assert.Contains("\"name\":\"IsLong\"", output);
        Assert.Contains("\"name\":\"Count\"", output);
        Assert.DoesNotContain("\"name\":\"\"", output);
    }

    [Fact]
    public void Package_with_incompatible_compile_assets_reports_a_coverage_diagnostic()
    {
        Package("Wrong", "net48", "namespace Wrong; public class Api { }");
        Project("<PackageReference Include=\"Wrong\" Version=\"1.0.0\" />", "public class App { }");
        var output = Extract();
        Assert.Contains("PACKAGE_NO_COMPATIBLE_ASSETS", output);
        Assert.Contains("\"status\":\"partial\"", output);
    }

    [Fact]
    public void Same_assembly_bytes_under_two_package_paths_do_not_make_calls_ambiguous()
    {
        var one = Package("One", "net8.0", "namespace Shared; public static class Api { public static int Read() => 1; }");
        var two = Path.Combine(Cache, "two", "1.0.0", "lib", "net8.0");
        Directory.CreateDirectory(two);
        File.Copy(one, Path.Combine(two, "One.dll"));
        Project("<PackageReference Include=\"One\" Version=\"1.0.0\" /><PackageReference Include=\"Two\" Version=\"1.0.0\" />",
            "public class App { public int Run() => Shared.Api.Read(); }");
        var output = Extract();
        Assert.DoesNotContain("COMPILER_", output);
        Assert.Contains("cs|@ext|One|M:Shared.Api.Read", output);
    }

    [Fact]
    public void Conflicting_assembly_identities_are_reported_instead_of_picking_a_version()
    {
        Package("One", "net8.0", "namespace Shared; public static class Api { public static int Read() => 1; }");
        Package("Two", "net8.0", "namespace Shared; public static class Api { public static int Read() => 2; }", assembly: "One");
        Project("<PackageReference Include=\"One\" Version=\"1.0.0\" /><PackageReference Include=\"Two\" Version=\"1.0.0\" />",
            "public class App { public int Run() => Shared.Api.Read(); }");
        Assert.Contains("ASSEMBLY_REFERENCE_CONFLICT", Extract());
    }

    [Fact]
    public void Exact_target_wins_over_newer_assets_and_nearest_older_target_is_supported()
    {
        Package("Multi", "net10.0", "public class Newer { }");
        var expected = Package("Multi", "net8.0", "public class Older { }");
        var resolver = new PackageResolver(Cache);
        Assert.Equal([expected], resolver.Resolve([new("Multi", "1.0.0", 1)], targetFramework: "net8.0").Assemblies);
        Assert.Equal([expected], resolver.Resolve([new("Multi", "1.0.0", 1)], targetFramework: "net9.0").Assemblies);
    }

    [Fact]
    public void Compatible_ref_group_precedes_lib_and_incompatible_ref_allows_lib_fallback()
    {
        Package("Surface", "net8.0", "public class RuntimeOnly { }");
        var reference = Package("Surface", "netstandard2.0", "public class CompileOnly { }");
        var referenceFolder = reference.Replace("/lib/", "/ref/", StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(referenceFolder)!);
        File.Move(reference, referenceFolder);
        var resolver = new PackageResolver(Cache);
        Assert.Equal([referenceFolder], resolver.Resolve([new("Surface", "1.0.0", 1)], targetFramework: "net8.0").Assemblies);
        Directory.Move(Path.GetDirectoryName(referenceFolder)!, Path.Combine(Cache, "surface", "1.0.0", "ref", "net11.0"));
        Assert.Contains("/lib/net8.0/", Assert.Single(resolver.Resolve([new("Surface", "1.0.0", 1)], targetFramework: "net8.0").Assemblies));
    }

    [Theory]
    [InlineData("net8.0", "netstandard2.0")]
    [InlineData("net8.0-windows", "net8.0-windows")]
    public void Platform_qualified_assets_do_not_leak_into_plain_targets(string target, string expected)
    {
        Package("Platform", "net8.0-windows", "public class WindowsApi { }");
        Package("Platform", "netstandard2.0", "public class PortableApi { }");
        var result = new PackageResolver(Cache).Resolve([new("Platform", "1.0.0", 1)], targetFramework: target);
        Assert.Contains("/" + expected + "/", Assert.Single(result.Assemblies));
    }

    [Fact]
    public void Empty_reference_marker_suppresses_lib_while_meta_package_has_no_missing_asset_diagnostic()
    {
        Package("Empty", "net8.0", "public class RuntimeOnly { }");
        var reference = Directory.CreateDirectory(Path.Combine(Cache, "empty", "1.0.0", "ref", "net8.0")).FullName;
        File.WriteAllText(Path.Combine(reference, "_._"), "");
        Directory.CreateDirectory(Path.Combine(Cache, "meta", "1.0.0"));
        var result = new PackageResolver(Cache).Resolve([new("Empty", "1.0.0", 1), new("Meta", "1.0.0", 1)], targetFramework: "net8.0");
        Assert.Empty(result.Assemblies);
        Assert.Empty(result.Issues);
        Assert.Equal(2, result.Resolved.Count);
    }

    [Fact]
    public void Conditional_and_multiple_targets_are_explicitly_unsupported()
    {
        Project("<PackageReference Include=\"Excluded\" Version=\"1.0.0\" Condition=\"'$(TargetFramework)' == 'net8.0'\" />", "public class App { }");
        var path = Path.Combine(_root, "src", "App.csproj");
        File.WriteAllText(path, File.ReadAllText(path).Replace("<TargetFramework>net10.0</TargetFramework>", "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>", StringComparison.Ordinal));
        var model = Assert.Single(ProjectModel.Discover(Path.Combine(_root, "src")));
        Assert.Null(model.TargetFramework);
        Assert.Empty(model.Packages);
        var output = Extract();
        Assert.Contains("UNSUPPORTED_TARGET_FRAMEWORK", output);
        Assert.Contains("UNSUPPORTED_CONDITIONAL_REFERENCES", output);
    }

    [Fact]
    public void Malformed_framework_versions_degrade_to_an_explicit_diagnostic()
    {
        Project("", "public class App { }", ".NETCoreApp,Version=vbad");
        Assert.Contains("UNSUPPORTED_TARGET_FRAMEWORK", Extract());
    }

    [Fact]
    public void Unsupported_asset_controls_and_central_conditions_are_diagnosed_without_compile_visibility()
    {
        Project("<PackageReference Include=\"Hidden\" Version=\"1.0.0\" ExcludeAssets=\"compile\" /><PackageReference Include=\"Central\" />", "public class App { }");
        File.WriteAllText(Path.Combine(_root, "src", "Directory.Packages.props"), "<Project><ItemGroup><PackageVersion Include=\"Central\" Version=\"9.0.0\" /></ItemGroup><ItemGroup Condition=\"'$(TargetFramework)'=='net8.0'\"><PackageVersion Include=\"Central\" Version=\"1.0.0\" /></ItemGroup></Project>");
        var model = Assert.Single(ProjectModel.Discover(Path.Combine(_root, "src")));
        Assert.Equal(2, model.Packages.Count);
        Assert.Empty(model.PackageRoots());
        var output = Extract();
        Assert.Contains("UNSUPPORTED_PACKAGE_ASSET_METADATA", output);
        Assert.Contains("UNSUPPORTED_CENTRAL_PACKAGE_CONDITIONS", output);
    }

    [Fact]
    public void Unknown_referenced_target_does_not_leak_symbols_into_a_known_target()
    {
        Project("<ProjectReference Include=\"Dep/Dep.csproj\" />", "public class App { public int Run() => Dep.Read(); }");
        var dep = Directory.CreateDirectory(Path.Combine(_root, "src", "Dep")).FullName;
        File.WriteAllText(Path.Combine(dep, "Dep.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dep, "Dep.cs"), "public static class Dep { public static int Read() => 1; }");
        var app = new CSharpExtractor(new ExtractorOptions(Path.Combine(_root, "src"), "resolution", "0123456789abcdef0123456789abcdef01234567", Cache, Packs)).Extract().Single(u => u.UnitName == "App").Jsonl;
        Assert.Contains("UNSUPPORTED_PROJECT_TARGET", app);
        Assert.DoesNotContain("cs|resolution|Dep|M:Dep.Read", app);
    }

    [Fact]
    public void Framework_packs_use_numeric_patch_and_never_a_different_target()
    {
        var packs = Path.Combine(_root, "packs");
        Pack("10.0.9", "public static class FrameworkApi { public static int Old() => 1; }");
        Pack("10.0.10", "public static class FrameworkApi { public static int New() => 1; }");
        Project("", "public class App { public int Run() => FrameworkApi.New(); }");
        var output = Extract(packs);
        Assert.DoesNotContain("COMPILER_", output);
        Assert.Contains("cs|@ext|FrameworkSurface|M:FrameworkApi.New", output);
        Project("", "public class App { }", "net8.0");
        Assert.Contains("MISSING_FRAMEWORK_REFERENCE_PACK", Extract(packs));

        void Pack(string version, string source)
        {
            var dir = Directory.CreateDirectory(Path.Combine(packs, "Microsoft.NETCore.App.Ref", version, "ref", "net10.0")).FullName;
            var runtime = Directory.EnumerateFiles(Path.Combine(Packs, "Microsoft.NETCore.App.Ref"), "System.Runtime.dll", SearchOption.AllDirectories).Single();
            File.Copy(runtime, Path.Combine(dir, "System.Runtime.dll"));
            var path = Package("FrameworkSurface", version, source);
            File.Copy(path, Path.Combine(dir, "FrameworkSurface.dll"));
        }
    }

    [Fact]
    public void Reference_bytes_participate_in_config_hash_and_repeat_without_absolute_cache_paths()
    {
        Package("Stable", "net8.0", "public static class Api { public static int Read() => 1; }");
        Project("<PackageReference Include=\"Stable\" Version=\"1.0.0\" />", "public class App { public int Run() => Api.Read(); }");
        var first = Extract();
        Assert.Equal(first, Extract());
        var path = Path.Combine(Cache, "stable", "1.0.0", "lib", "net8.0", "Stable.dll");
        File.Delete(path);
        Package("Stable", "net8.0", "public static class Api { public static int Read() => 2; }");
        var second = Extract();
        Assert.NotEqual(Config(first), Config(second));
        Assert.DoesNotContain(_root, second);

        static string? Config(string output) => JsonDocument.Parse(output.Split('\n')[0]).RootElement.GetProperty("configHash").GetString();
    }

    [Fact]
    public void Explicit_aspnet_framework_reference_is_visible_to_a_class_library()
    {
        Project("<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />", "public class App { public string Run(Microsoft.AspNetCore.Http.HttpRequest request) => request.Method; }");
        Assert.DoesNotContain("COMPILER_", Extract());
    }

    [Fact]
    public void Equal_signed_shared_framework_identity_prefers_the_reference_pack()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Cache, "frameworkcopy", "1.0.0", "lib", "net8.0")).FullName;
        var runtime = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.dll");
        File.Copy(runtime, Path.Combine(folder, "System.Runtime.dll"));
        Project("<PackageReference Include=\"FrameworkCopy\" Version=\"1.0.0\" />", "public class App { }");
        var output = Extract();
        Assert.DoesNotContain("ASSEMBLY_REFERENCE_CONFLICT", output);
        Assert.DoesNotContain("COMPILER_", output);
    }

    [Fact]
    public void Invalid_package_assembly_reports_partial_coverage_without_loading_it()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Cache, "invalid", "1.0.0", "lib", "net8.0")).FullName;
        File.WriteAllText(Path.Combine(folder, "Invalid.dll"), "not a managed assembly");
        Project("<PackageReference Include=\"Invalid\" Version=\"1.0.0\" />", "public class App { }");
        var output = Extract();
        Assert.Contains("INVALID_ASSEMBLY_REFERENCE", output);
        Assert.Contains("\"status\":\"partial\"", output);
    }

    [Fact]
    public void Meta_package_nuspec_and_incompatible_reference_decisions_change_analysis_identity()
    {
        var meta = Directory.CreateDirectory(Path.Combine(Cache, "meta", "1.0.0")).FullName;
        var nuspec = Path.Combine(meta, "meta.nuspec");
        Project("<PackageReference Include=\"Meta\" Version=\"1.0.0\" />", "public class App { }");
        File.WriteAllText(nuspec, "<package><metadata><dependencies><group targetFramework=\"net8.0\" /></dependencies></metadata></package>");
        var compatible = Extract();
        File.WriteAllText(nuspec, "<package><metadata><dependencies><group targetFramework=\"net11.0\" /></dependencies></metadata></package>");
        var incompatible = Extract();
        Assert.Contains("PACKAGE_NO_COMPATIBLE_DEPENDENCIES", incompatible);
        Assert.NotEqual(Config(compatible), Config(incompatible));

        Project("<ProjectReference Include=\"Dep/Dep.csproj\" />", "public class App { }");
        var dep = Directory.CreateDirectory(Path.Combine(_root, "src", "Dep")).FullName;
        string App(string target)
        {
            File.WriteAllText(Path.Combine(dep, "Dep.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{target}</TargetFramework></PropertyGroup></Project>");
            return new CSharpExtractor(new ExtractorOptions(Path.Combine(_root, "src"), "resolution", "0123456789abcdef0123456789abcdef01234567", Cache, Packs)).Extract().Single(u => u.UnitName == "App").Jsonl;
        }
        var eleven = App("net11.0");
        var twelve = App("net12.0");
        Assert.Contains("INCOMPATIBLE_PROJECT_TARGET", twelve);
        Assert.NotEqual(Config(eleven), Config(twelve));

        static string? Config(string output) => JsonDocument.Parse(output.Split('\n')[0]).RootElement.GetProperty("configHash").GetString();
    }

    [Fact]
    public void Documentation_only_reference_folder_does_not_suppress_compatible_lib_assets()
    {
        var expected = Package("Docs", "net8.0", "public class Api { }");
        var docs = Directory.CreateDirectory(Path.Combine(Cache, "docs", "1.0.0", "ref", "net8.0")).FullName;
        File.WriteAllText(Path.Combine(docs, "readme.txt"), "No compile assets");
        Assert.Equal([expected], new PackageResolver(Cache).Resolve([new("Docs", "1.0.0", 1)], targetFramework: "net8.0").Assemblies);
    }

    [Fact]
    public void Sdk_declared_framework_packages_supply_pruned_diagnostic_source_and_memory_references()
    {
        Project("<PackageReference Include=\"System.Diagnostics.DiagnosticSource\" Version=\"6.0.1\" /><PackageReference Include=\"System.Memory\" Version=\"4.5.4\" />",
            "public class App { public void Run() { var activity = new System.Diagnostics.Activity(\"test\"); activity.Start(); } }");
        var output = Extract();
        Assert.DoesNotContain("UNRESOLVED_", output);
        Assert.DoesNotContain("COMPILER_", output);
        Assert.Contains("cs|@ext|System.Diagnostics.DiagnosticSource|M:System.Diagnostics.Activity.Start", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pruned_framework_dependencies_are_satisfied_in_folder_and_locked_package_graphs(bool locked)
    {
        Package("Root", "net8.0", "public class RootApi { }");
        File.WriteAllText(Path.Combine(Cache, "root", "1.0.0", "root.nuspec"), "<package><metadata><dependencies><dependency id=\"System.Diagnostics.DiagnosticSource\" version=\"6.0.1\" /><dependency id=\"System.Memory\" version=\"4.5.4\" /></dependencies></metadata></package>");
        Project("<PackageReference Include=\"Root\" Version=\"1.0.0\" />", "public class App { }");
        var packageLock = new PackageLock();
        packageLock.Set("App.csproj", [new("Root", "1.0.0")]);
        var output = Assert.Single(new CSharpExtractor(new ExtractorOptions(Path.Combine(_root, "src"), "resolution", "0123456789abcdef0123456789abcdef01234567", Cache, Packs, locked ? packageLock : null)).Extract()).Jsonl;
        Assert.DoesNotContain("UNRESOLVED_TRANSITIVE_PACKAGE", output);
        Assert.Contains("\"status\":\"full\"", output);
    }

    [Fact]
    public void Framework_coverage_does_not_bypass_explicit_locked_package_folder_validation()
    {
        Project("<PackageReference Include=\"System.Memory\" Version=\"4.5.4\" />", "public class App { }");
        var packageLock = new PackageLock();
        packageLock.Set("App.csproj", [new("System.Memory", "4.5.4")]);
        Assert.Throws<InvalidOperationException>(() => new CSharpExtractor(new ExtractorOptions(Path.Combine(_root, "src"), "resolution", "0123456789abcdef0123456789abcdef01234567", Cache, Packs, packageLock)).Extract());
    }

    [Theory]
    [InlineData("6.0.1", true)]
    [InlineData("[6.0.1]", true)]
    [InlineData("[10.0.14,)", true)]
    [InlineData("(10.0.14,)", false)]
    [InlineData("10.0.15", false)]
    [InlineData("[11.0.0,)", false)]
    [InlineData("6.0.1-preview.1", false)]
    [InlineData("*", false)]
    [InlineData("(,6.0.1)", false)]
    public void Framework_package_proof_requires_a_known_id_and_covered_stable_constraint(string range, bool satisfied)
    {
        var coverage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Known.Package"] = "10.0.14" };
        var result = new PackageResolver(Cache, coverage).Resolve([new("Known.Package", range, 1)], targetFramework: "net10.0");
        Assert.Equal(satisfied, result.Missing.Count == 0);
        Assert.Single(new PackageResolver(Cache, coverage).Resolve([new("Unknown.Package", range, 1)], targetFramework: "net10.0").Missing);
    }

    [Fact]
    public void A_newer_cached_version_satisfies_a_naked_NuGet_minimum()
    {
        Directory.CreateDirectory(Path.Combine(Cache, "cached", "6.0.2"));
        var result = new PackageResolver(Cache).Resolve([new("Cached", "6.0.1", 1)], targetFramework: "net10.0");
        Assert.Empty(result.Missing);
        Assert.Equal("6.0.2", Assert.Single(result.Resolved).Version);
    }

    [Fact]
    public void Framework_package_metadata_is_bound_to_the_analysis_identity()
    {
        var packs = Path.Combine(_root, "packs");
        var pack = Path.Combine(packs, "Microsoft.NETCore.App.Ref", "10.0.0");
        var refs = Directory.CreateDirectory(Path.Combine(pack, "ref", "net10.0")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(pack, "data")).FullName;
        File.Copy(Directory.EnumerateFiles(Path.Combine(Packs, "Microsoft.NETCore.App.Ref"), "System.Runtime.dll", SearchOption.AllDirectories).Single(), Path.Combine(refs, "System.Runtime.dll"));
        Project("", "public class App { }");
        var metadata = Path.Combine(data, "PackageOverrides.txt");
        File.WriteAllText(metadata, "System.Memory|5.0.0\n");
        var first = Extract(packs);
        File.WriteAllText(metadata, "System.Memory|5.0.1\n");
        var second = Extract(packs);
        Assert.NotEqual(JsonDocument.Parse(first.Split('\n')[0]).RootElement.GetProperty("configHash").GetString(),
            JsonDocument.Parse(second.Split('\n')[0]).RootElement.GetProperty("configHash").GetString());
    }

    [Fact]
    public void A_later_incompatible_package_request_cannot_leave_calls_resolved_to_the_omitted_version()
    {
        Package("Same", "net8.0", "namespace Shared; public static class Api { public static int Read() => 2; }");
        Directory.Move(Path.Combine(Cache, "same", "1.0.0"), Path.Combine(Cache, "same", "2.0.0"));
        Package("Same", "net8.0", "namespace Shared; public static class Api { public static int Read() => 1; }");
        Package("RootA", "net8.0", "public class RootA { }");
        Package("RootB", "net8.0", "public class RootB { }");
        File.WriteAllText(Path.Combine(Cache, "roota", "1.0.0", "roota.nuspec"), "<package><metadata><dependencies><dependency id=\"Same\" version=\"[1.0.0]\" /></dependencies></metadata></package>");
        File.WriteAllText(Path.Combine(Cache, "rootb", "1.0.0", "rootb.nuspec"), "<package><metadata><dependencies><dependency id=\"Same\" version=\"2.0.0\" /></dependencies></metadata></package>");
        Project("<PackageReference Include=\"RootA\" Version=\"1.0.0\" /><PackageReference Include=\"RootB\" Version=\"1.0.0\" />", "public class App { public int Run() => Shared.Api.Read(); }");
        var output = Extract();
        Assert.Contains("PACKAGE_VERSION_CONFLICT", output);
        Assert.Contains("\"resolution\":\"unresolved\"", output);
        Assert.DoesNotContain("cs|@ext|Same|M:Shared.Api.Read", output);
    }

    [Theory]
    [InlineData("10.0.18", false)]
    [InlineData("10.0.32767", false)]
    [InlineData("10.1.0", true)]
    [InlineData("11.0.0", true)]
    public void Sdk_pruning_metadata_covers_servicing_patches_but_not_the_next_minor(string request, bool missing)
    {
        var packs = Path.Combine(_root, "patch-packs");
        var pack = Path.Combine(packs, "Microsoft.NETCore.App.Ref", "10.0.0");
        var refs = Directory.CreateDirectory(Path.Combine(pack, "ref", "net10.0")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(pack, "data")).FullName;
        File.Copy(Directory.EnumerateFiles(Path.Combine(Packs, "Microsoft.NETCore.App.Ref"), "System.Runtime.dll", SearchOption.AllDirectories).Single(), Path.Combine(refs, "System.Runtime.dll"));
        File.WriteAllText(Path.Combine(data, "PackageOverrides.txt"), "Microsoft.Extensions.Caching.Memory|10.0.0\n");
        Project($"<PackageReference Include=\"Microsoft.Extensions.Caching.Memory\" Version=\"{request}\" />", "public class App { }");
        var output = Extract(packs);
        Assert.Equal(missing, output.Contains("UNRESOLVED_PACKAGE", StringComparison.Ordinal));
    }

    private string Package(string id, string tfm, string source, string? assembly = null)
    {
        var dir = Path.Combine(Cache, id.ToLowerInvariant(), "1.0.0", "lib", tfm);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, (assembly ?? id) + ".dll");
        var framework = Directory.EnumerateDirectories(Path.Combine(Packs, "Microsoft.NETCore.App.Ref"))
            .SelectMany(d => Directory.EnumerateDirectories(Path.Combine(d, "ref"))).Single();
        var refs = Directory.EnumerateFiles(framework, "*.dll").Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(assembly ?? id, [CSharpSyntaxTree.ParseText(source)], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emit = compilation.Emit(path);
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
        return path;
    }

    private void Project(string packages, string source, string target = "net10.0")
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        File.WriteAllText(Path.Combine(dir, "App.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{target}</TargetFramework></PropertyGroup><ItemGroup>{packages}</ItemGroup></Project>");
        File.WriteAllText(Path.Combine(dir, "App.cs"), source);
    }

    private string Extract(string? packs = null) => Assert.Single(new CSharpExtractor(new ExtractorOptions(Path.Combine(_root, "src"), "resolution", "0123456789abcdef0123456789abcdef01234567", Cache, packs ?? Packs)).Extract()).Jsonl;
}
