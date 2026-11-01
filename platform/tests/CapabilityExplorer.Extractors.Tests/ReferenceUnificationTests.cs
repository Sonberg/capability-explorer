using System.Text.Json;
using CapabilityExplorer.Extractors.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

/// <summary>Issue #6 gap 2: one assembly per name, and the reference pack that matches the project's target framework.</summary>
public sealed class ReferenceUnificationTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Directory.CreateTempSubdirectory("ce-unify-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ------------------------------------------------------------ the unifier

    [Fact]
    public void Highest_assembly_version_wins_and_survivors_keep_their_order()
    {
        var old = Dll("lib-old", "Dup", "1.0.0.0", "namespace Dup { public class Thing { } }");
        var other = Dll("lib-other", "Other", "1.0.0.0", "namespace Other { public class X { } }");
        var newer = Dll("lib-new", "Dup", "2.0.0.0", "namespace Dup { public class Thing { public int Extra() => 1; } }");

        var result = ReferenceUnifier.Unify([Ref(old), Ref(other), Ref(newer)]);

        Assert.Equal([other, newer], result.Cast<PortableExecutableReference>().Select(r => r.FilePath));
    }

    [Fact]
    public void Equal_versions_keep_the_earlier_reference_so_the_framework_wins_ties()
    {
        var framework = Dll("framework", "Dup", "8.0.0.0", "namespace Dup { public class Thing { } }");
        var package = Dll("package", "Dup", "8.0.0.0", "namespace Dup { public class Thing { } }");

        Assert.Equal([framework], ReferenceUnifier.Unify([Ref(framework), Ref(package)]).Cast<PortableExecutableReference>().Select(r => r.FilePath));
        Assert.Equal([package], ReferenceUnifier.Unify([Ref(package), Ref(framework)]).Cast<PortableExecutableReference>().Select(r => r.FilePath));
    }

    [Fact]
    public void Unreadable_and_compilation_references_are_kept()
    {
        var junk = Path.Combine(_root, "junk.dll");
        File.WriteAllText(junk, "not an assembly");
        var compilation = CSharpCompilation.Create("Proj", [CSharpSyntaxTree.ParseText("class C { }")], [], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = ReferenceUnifier.Unify([Ref(junk), compilation.ToMetadataReference()]);

        Assert.Equal(2, result.Count);
    }

    // ------------------------------------------------------------ end to end: CS0433 / CS0121 without unification

    [Fact]
    public void Two_packages_shipping_one_assembly_do_not_make_its_types_ambiguous()
    {
        var packages = Path.Combine(_root, "packages");
        Package(packages, "lib.one", "1.0.0", "Dup", "1.0.0.0", "namespace Dup { public class Thing { public int One() => 1; } }");
        Package(packages, "lib.two", "2.0.0", "Dup", "2.0.0.0", "namespace Dup { public class Thing { public int One() => 1; public int Two() => 2; } }");
        var source = NewSource("net10.0", """<ItemGroup><PackageReference Include="Lib.One" Version="1.0.0" /><PackageReference Include="Lib.Two" Version="2.0.0" /></ItemGroup>""");
        File.WriteAllText(Path.Combine(source, "Use.cs"), "namespace Synthetic; public class Use { public int Run() => new Dup.Thing().Two(); }\n");
        var packageLock = new PackageLock();
        packageLock.Set("Synthetic.csproj", [new LockedPackage("lib.one", "1.0.0"), new LockedPackage("lib.two", "2.0.0")]);

        var result = Assert.Single(new CSharpExtractor(new ExtractorOptions(source, "synthetic", Sha, packages, Packs(), packageLock)).Extract());

        Assert.DoesNotContain(Records(result.Jsonl), r => r.GetProperty("kind").GetString() == "diagnostic");
        var call = Assert.Single(Records(result.Jsonl), r => r.GetProperty("kind").GetString() == "edge" && r.GetProperty("relation").GetString() == "CALLS");
        Assert.Equal("resolved", call.GetProperty("resolution").GetString());
    }

    // ------------------------------------------------------------ reference pack per target framework

    [Fact]
    public void Net8_project_gets_the_net8_pack_even_when_net10_is_installed()
    {
        var packs = FakePacks("8.0.24", "net8.0", "10.0.14", "net10.0", "9.0.3", "net9.0");
        var source = NewSource("net8.0");

        var selection = Select(source, packs);

        Assert.Equal(["Microsoft.NETCore.App.Ref/8.0.24/net8.0"], selection.Packs);
        Assert.Empty(selection.Fallbacks);
    }

    [Fact]
    public void Highest_patch_of_the_matching_pack_is_used_and_version_order_is_numeric()
    {
        var packs = FakePacks("8.0.9", "net8.0", "8.0.24", "net8.0");

        Assert.Equal(["Microsoft.NETCore.App.Ref/8.0.24/net8.0"], Select(NewSource("net8.0"), packs).Packs);
    }

    [Fact]
    public void Multi_target_and_platform_suffixed_frameworks_use_the_first_literal_target()
    {
        var packs = FakePacks("8.0.1", "net8.0", "9.0.1", "net9.0");

        Assert.Equal("net9.0", ProjectModel.Discover(NewSource("net9.0;net8.0", multiple: true))[0].TargetFramework);
        Assert.Equal("net8.0", ProjectModel.Discover(NewSource("net8.0-windows"))[0].TargetFramework);
        Assert.Equal(["Microsoft.NETCore.App.Ref/9.0.1/net9.0"], Select(NewSource("net9.0;net8.0", multiple: true), packs).Packs);
    }

    [Fact]
    public void Target_framework_is_inherited_from_directory_build_props()
    {
        var source = Path.Combine(_root, "inherit");
        Directory.CreateDirectory(Path.Combine(source, "App"));
        File.WriteAllText(Path.Combine(source, "Directory.Build.props"), "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(source, "App", "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"></Project>""");

        Assert.Equal("net8.0", ProjectModel.Discover(source)[0].TargetFramework);
    }

    [Fact]
    public void Missing_matching_pack_falls_back_to_the_newest_and_says_so()
    {
        var packs = FakePacks("10.0.14", "net10.0");
        var source = NewSource("net8.0");

        var selection = Select(source, packs);

        Assert.Equal(["Microsoft.NETCore.App.Ref 10.0.14/net10.0"], selection.Fallbacks);
        var result = Assert.Single(new CSharpExtractor(new ExtractorOptions(source, "synthetic", Sha, null, packs)).Extract());
        var d = Assert.Single(Records(result.Jsonl), r => r.GetProperty("kind").GetString() == "diagnostic");
        Assert.Equal("FRAMEWORK_PACK_FALLBACK", d.GetProperty("code").GetString());
        Assert.Equal("info", d.GetProperty("severity").GetString());
    }

    [Fact]
    public void FrameworkReference_to_aspnetcore_adds_that_pack_to_a_non_web_project()
    {
        var source = NewSource("net8.0", """<ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>""");

        Assert.True(ProjectModel.Discover(source)[0].IsWeb);
    }

    [Fact]
    public void Selected_pack_is_part_of_the_analysis_identity()
    {
        string Hash(string packs)
        {
            var source = NewSource("net8.0");
            File.WriteAllText(Path.Combine(source, "A.cs"), "namespace Synthetic; public class A { }\n");
            var jsonl = Assert.Single(new CSharpExtractor(new ExtractorOptions(source, "synthetic", Sha, null, packs)).Extract()).Jsonl;
            return Records(jsonl).First().GetProperty("configHash").GetString()!;
        }

        Assert.NotEqual(Hash(FakePacks("8.0.1", "net8.0")), Hash(FakePacks("8.0.2", "net8.0")));
        Assert.Equal(Hash(FakePacks("8.0.1", "net8.0")), Hash(FakePacks("8.0.1", "net8.0", "10.0.9", "net10.0")));
    }

    // ------------------------------------------------------------ helpers

    private static CSharpExtractor.FrameworkSelection Select(string source, string packs) =>
        new CSharpExtractor(new ExtractorOptions(source, "synthetic", Sha, null, packs)).FrameworkReferences(ProjectModel.Discover(source)[0]);

    private static PortableExecutableReference Ref(string path) => MetadataReference.CreateFromFile(path);

    private string Dll(string folder, string assembly, string version, string code)
    {
        var dir = Path.Combine(_root, "dlls", folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, assembly + ".dll");
        // Built against the reference pack, like a real package, not against the runtime's implementation assemblies.
        var realPack = Directory.EnumerateDirectories(Path.Combine(Packs(), "Microsoft.NETCore.App.Ref")).Order(StringComparer.Ordinal).Last();
        var refs = Directory.EnumerateFiles(Directory.EnumerateDirectories(Path.Combine(realPack, "ref")).Single(), "*.dll").Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var tree = CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]\n{code}");
        // Strong-named (delay-signed with a fixed public key), like the Microsoft.Extensions.* packages: other versions are other identities.
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithCryptoPublicKey(PublicKey).WithDelaySign(true);
        var emit = CSharpCompilation.Create(assembly, [tree], refs, options).Emit(path);
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
        return path;
    }

    private static readonly System.Collections.Immutable.ImmutableArray<byte> PublicKey = BuildPublicKey();

    /// <summary>A valid 1024-bit RSA public key in the strong-name format (the key is never used to sign).</summary>
    private static System.Collections.Immutable.ImmutableArray<byte> BuildPublicKey()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(1024);
        var p = rsa.ExportParameters(false);
        var blob = new List<byte> { 0x00, 0x24, 0x00, 0x00, 0x04, 0x80, 0x00, 0x00 }; // SigAlg RSA, HashAlg SHA1
        var key = new List<byte> { 0x06, 0x02, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x52, 0x53, 0x41, 0x31, 0x00, 0x04, 0x00, 0x00 }; // PUBLICKEYBLOB, "RSA1", 1024 bits
        key.AddRange(BitConverter.GetBytes(BitConverter.ToUInt32([.. p.Exponent!.Reverse(), 0, 0, 0, 0], 0)));
        key.AddRange(p.Modulus!.Reverse());
        blob.AddRange(BitConverter.GetBytes(key.Count));
        blob.AddRange(key);
        return [.. blob];
    }

    private void Package(string root, string id, string version, string assembly, string assemblyVersion, string code)
    {
        var built = Dll($"{id}-{version}", assembly, assemblyVersion, code);
        var dir = Path.Combine(root, id, version);
        Directory.CreateDirectory(Path.Combine(dir, "lib", "net8.0"));
        File.Copy(built, Path.Combine(dir, "lib", "net8.0", assembly + ".dll"));
        File.WriteAllText(Path.Combine(dir, $"{id}.nuspec"), $"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>");
    }

    /// <summary>A reference-pack folder with the given (version, tfm) pairs; the assemblies are copies of the SDK's own, so compilation works.</summary>
    private string FakePacks(params string[] versionAndTfm)
    {
        var real = Directory.EnumerateDirectories(Path.Combine(Packs(), "Microsoft.NETCore.App.Ref")).Order(StringComparer.Ordinal).Last();
        var realRef = Directory.EnumerateDirectories(Path.Combine(real, "ref")).Single();
        var packs = Path.Combine(_root, "packs-" + Guid.NewGuid().ToString("N"));
        for (var i = 0; i < versionAndTfm.Length; i += 2)
        {
            var dir = Path.Combine(packs, "Microsoft.NETCore.App.Ref", versionAndTfm[i], "ref", versionAndTfm[i + 1]);
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.EnumerateFiles(realRef, "System.Runtime.dll").Concat(Directory.EnumerateFiles(realRef, "netstandard.dll")))
            {
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            }
        }

        return packs;
    }

    private string NewSource(string tfm, string items = "", bool multiple = false)
    {
        var source = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        var prop = multiple ? "TargetFrameworks" : "TargetFramework";
        File.WriteAllText(Path.Combine(source, "Synthetic.csproj"), $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><{prop}>{tfm}</{prop}></PropertyGroup>{items}</Project>""");
        return source;
    }

    private static string Packs() => Path.GetFullPath(Path.Combine(
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));

    private static JsonElement[] Records(string jsonl) => jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l =>
    {
        using var doc = JsonDocument.Parse(l);
        return doc.RootElement.Clone();
    }).ToArray();
}
