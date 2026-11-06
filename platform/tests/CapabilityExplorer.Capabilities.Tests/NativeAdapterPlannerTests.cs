using System.Text.Json;
using CapabilityExplorer.Capabilities;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class NativeAdapterPlannerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string Tree = "2222222222222222222222222222222222222222";
    private const string Recipe = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Given_mixed_repository_when_planning_then_results_are_deterministic_and_disjoint()
    {
        var root = NewRoot();
        try
        {
            Write(root, "src/Catalog/Catalog.csproj", "<Project />");
            Write(root, "src/Catalog/Catalog.cs", "public sealed class Catalog { }");
            Write(root, "src/Worker/Worker.csproj", "<Project />");
            Write(root, "src/Worker/Worker.cs", "public sealed class Worker { }");
            Write(root, "frontend/app.ts", "export const app = true;");
            Write(root, "views/Index.cshtml", "<p>embedded source is unsupported</p>");

            var manifest = Manifest();
            var first = await NativeAdapterPlanner.PlanAsync(root, "mixed", Commit, Tree, manifest);
            var second = await NativeAdapterPlanner.PlanAsync(root, "mixed", Commit, Tree, manifest);

            Assert.Equal(JsonSerializer.Serialize(first, Json), JsonSerializer.Serialize(second, Json));
            Assert.Equal(["src/Catalog/Catalog.csproj", "src/Worker/Worker.csproj"],
                first.Units.Select(unit => unit.UnitPath));
            Assert.Equal(new AdapterPlanCounts(2, 2, 0, 0, 0), first.UnitCounts);
            Assert.Equal(new AdapterPlanCounts(6, 4, 0, 2, 0), first.InputCounts);
            Assert.Contains(first.Diagnostics, diagnostic =>
                diagnostic.Code == "unsupported-input-profile" && diagnostic.Path == "frontend/app.ts");
            Assert.Contains(first.Diagnostics, diagnostic =>
                diagnostic.Code == "unsupported-embedded-source" && diagnostic.Path == "views/Index.cshtml");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Given_eligible_and_excluded_projects_when_planning_then_manifest_exclusions_are_explicit()
    {
        var root = NewRoot();
        try
        {
            Write(root, "src/Orders/Orders.csproj", "<Project />");
            Write(root, "src/Orders/Order.cs", "public sealed class Order { }");
            Write(root, "tests/Orders.Tests/Orders.Tests.csproj", "<Project />");
            Write(root, "tests/Orders.Tests/OrderTests.cs", "public sealed class OrderTests { }");

            var plan = await NativeAdapterPlanner.PlanAsync(root, "orders", Commit, Tree,
                Manifest(["tests"]));

            Assert.Equal(new AdapterPlanCounts(2, 1, 1, 0, 0), plan.UnitCounts);
            Assert.Equal("eligible", plan.Units[0].Status);
            Assert.Equal("excluded", plan.Units[1].Status);
            Assert.Equal(0, plan.UnknownCount);
            Assert.True(plan.EnumerationComplete);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Given_source_recipe_or_configuration_change_when_replanned_then_identity_changes()
    {
        var root = NewRoot();
        try
        {
            var project = Write(root, "src/App/App.csproj", "<Project />");
            var source = Write(root, "src/App/App.cs", "public sealed class App { }");
            var baseline = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree, Manifest());

            await File.WriteAllTextAsync(source, "public sealed class App { public int Value => 1; }");
            var changedSource = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree, Manifest());
            Assert.NotEqual(baseline.Units[0].UnitId, changedSource.Units[0].UnitId);
            Assert.NotEqual(baseline.SourceSnapshotSha256, changedSource.SourceSnapshotSha256);

            var changedRecipe = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree,
                Manifest(recipeSha256: new string('b', 64)));
            Assert.NotEqual(changedSource.Units[0].UnitId, changedRecipe.Units[0].UnitId);

            var changedConfiguration = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree,
                Manifest(["src/App"]));
            Assert.NotEqual(changedSource.ConfigurationSha256, changedConfiguration.ConfigurationSha256);
            Assert.Equal("excluded", changedConfiguration.Units[0].Status);
            Assert.True(File.Exists(project));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Given_over_limit_input_when_planning_then_unknown_is_not_reported_as_empty_success()
    {
        var root = NewRoot();
        try
        {
            Write(root, "src/App/App.csproj", "<Project />");
            Write(root, "src/App/App.cs", "public sealed class App { }");
            var manifest = Manifest() with
            {
                Limits = new NativeAdapterLimits(MaxEntries: 1, MaxUnits: 100, MaxFileBytes: 1024, MaxTotalBytes: 4096, MaxDepth: 8)
            };

            var plan = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree, manifest);

            Assert.False(plan.EnumerationComplete);
            Assert.True(plan.UnknownCount > 0);
            Assert.Equal(new AdapterPlanCounts(0, 0, 0, 0, 0), plan.UnitCounts);
            Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "entry-limit-exceeded");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Given_project_unit_cap_when_planning_then_overflow_units_are_counted_unknown()
    {
        var root = NewRoot();
        try
        {
            Write(root, "src/A/A.csproj", "<Project />");
            Write(root, "src/A/A.cs", "public sealed class A { }");
            Write(root, "src/B/B.csproj", "<Project />");
            Write(root, "src/B/B.cs", "public sealed class B { }");

            var plan = await NativeAdapterPlanner.PlanAsync(root, "units", Commit, Tree,
                Manifest() with
                {
                    Limits = new NativeAdapterLimits(MaxEntries: 100, MaxUnits: 1, MaxFileBytes: 1024,
                        MaxTotalBytes: 4096, MaxDepth: 8)
                });

            Assert.False(plan.EnumerationComplete);
            Assert.Single(plan.Units);
            Assert.Equal(new AdapterPlanCounts(2, 1, 0, 0, 1), plan.UnitCounts);
            Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "unit-limit-exceeded");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Given_missing_root_or_malformed_manifest_when_planning_then_operation_fails()
    {
        var missingRoot = Path.Combine(AppContext.BaseDirectory, "missing-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAnyAsync<IOException>(() =>
            NativeAdapterPlanner.PlanAsync(missingRoot, "app", Commit, Tree, Manifest()));

        var root = NewRoot();
        try
        {
            var malformed = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(malformed, """{"schema":"ce.adapter.manifest.v1","unexpected":true}""");
            await Assert.ThrowsAsync<JsonException>(() => NativeAdapterPlanner.ReadManifestAsync(malformed));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Given_unknown_manifest_identity_or_profile_when_planning_then_manifest_is_rejected()
    {
        var root = NewRoot();
        try
        {
            Write(root, "src/App/App.csproj", "<Project />");
            await Assert.ThrowsAsync<JsonException>(() =>
                NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree, Manifest() with { Schema = "ce.adapter.manifest.v9" }));
            await Assert.ThrowsAsync<JsonException>(() =>
                NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree,
                    Manifest() with { SupportedInputProfiles = ["unknown.source.v1"] }));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Given_per_file_or_aggregate_byte_cap_when_planning_then_unknown_state_is_reported()
    {
        var root = NewRoot();
        try
        {
            Write(root, "src/App/App.csproj", "<Project />");
            Write(root, "src/App/App.cs", "public sealed class App { }");
            var fileLimited = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree,
                Manifest() with
                {
                    Limits = new NativeAdapterLimits(MaxEntries: 100, MaxUnits: 100, MaxFileBytes: 4,
                        MaxTotalBytes: 4096, MaxDepth: 8)
                });
            Assert.False(fileLimited.EnumerationComplete);
            Assert.Contains(fileLimited.Diagnostics, diagnostic => diagnostic.Code == "file-byte-limit");
            Assert.True(fileLimited.UnknownCount > 0);

            var totalLimited = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree,
                Manifest() with
                {
                    Limits = new NativeAdapterLimits(MaxEntries: 100, MaxUnits: 100, MaxFileBytes: 4096,
                        MaxTotalBytes: 1, MaxDepth: 8)
                });
            Assert.False(totalLimited.EnumerationComplete);
            Assert.Contains(totalLimited.Diagnostics, diagnostic => diagnostic.Code == "total-byte-limit");
            Assert.True(totalLimited.UnknownCount > 0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Given_canceled_or_linked_source_when_planning_then_no_complete_plan_is_returned()
    {
        var root = NewRoot();
        try
        {
            Write(root, "src/App/App.csproj", "<Project />");
            Write(root, "src/App/App.cs", "public sealed class App { }");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree, Manifest(), canceled.Token));

            var linkTarget = Path.Combine(root, "src/App");
            var linkPath = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(linkPath, linkTarget);
            var plan = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree, Manifest());
            Assert.False(plan.EnumerationComplete);
            Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "linked-input");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Given_an_executable_tripwire_source_when_planning_then_source_is_never_run()
    {
        var root = NewRoot();
        try
        {
            var marker = Path.Combine(root, "target-executed.marker");
            Write(root, "src/Tripwire/Tripwire.csproj", "<Project />");
            Write(root, "src/Tripwire/Tripwire.cs",
                $"public static class Tripwire {{ static Tripwire() => System.IO.File.WriteAllText(@\"{marker}\", \"executed\"); }}");

            var plan = await NativeAdapterPlanner.PlanAsync(root, "tripwire", Commit, Tree, Manifest());

            Assert.Equal("eligible", Assert.Single(plan.Units).Status);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Given_overflowing_directory_in_different_creation_orders_then_plans_are_identical()
    {
        var roots = new[] { NewRoot(), NewRoot() };
        try
        {
            var paths = new[] { "App.csproj", "App.cs", "Extra.cshtml" };
            foreach (var path in paths) Write(roots[0], path, "same content");
            foreach (var path in paths.Reverse()) Write(roots[1], path, "same content");
            var manifest = Manifest() with { Limits = Manifest().Limits with { MaxEntries = 2 } };
            var first = await NativeAdapterPlanner.PlanAsync(roots[0], "app", Commit, Tree, manifest);
            var second = await NativeAdapterPlanner.PlanAsync(roots[1], "app", Commit, Tree, manifest);
            Assert.Equal(JsonSerializer.Serialize(first, Json), JsonSerializer.Serialize(second, Json));
            Assert.Empty(first.Inputs);
            Assert.Empty(first.Units);
            Assert.False(first.EnumerationComplete);
        }
        finally
        {
            foreach (var root in roots) DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Given_unseen_embedded_source_beyond_depth_cap_then_known_unit_is_unknown()
    {
        var root = NewRoot();
        try
        {
            Write(root, "App.csproj", "<Project />");
            Write(root, "App.cs", "public class App { }");
            Write(root, "nested/deeper/View.cshtml", "@{ }");
            var plan = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree,
                Manifest() with { Limits = Manifest().Limits with { MaxDepth = 1 } });
            var unit = Assert.Single(plan.Units);
            Assert.Equal("unknown", unit.Status);
            Assert.Contains("incomplete-source-traversal", unit.Reasons);
            Assert.Equal(0, plan.UnitCounts.Eligible);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Given_same_directory_projects_then_ownership_is_explicitly_ambiguous()
    {
        var root = NewRoot();
        try
        {
            Write(root, "A.csproj", "<Project />");
            Write(root, "B.csproj", "<Project />");
            Write(root, "Shared.cs", "public class Shared { }");
            var plan = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree, Manifest());
            Assert.Equal(2, plan.UnitCounts.Unsupported);
            Assert.All(plan.Units, unit => Assert.Contains("ambiguous-project-directory", unit.Reasons));
            Assert.Contains(plan.Diagnostics, item => item.Code == "ambiguous-project-directory");
            Assert.Null(Assert.Single(plan.Inputs, item => item.Path == "Shared.cs").UnitPath);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Given_limit_configuration_change_then_config_and_unit_identity_change()
    {
        var root = NewRoot();
        try
        {
            Write(root, "App.csproj", "<Project />");
            Write(root, "App.cs", "public class App { }");
            var manifest = Manifest();
            var first = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree, manifest);
            var second = await NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree,
                manifest with { Limits = manifest.Limits with { MaxDepth = 31 } });
            Assert.NotEqual(first.ConfigurationSha256, second.ConfigurationSha256);
            Assert.NotEqual(first.Units[0].UnitId, second.Units[0].UnitId);
            await Assert.ThrowsAsync<JsonException>(() => NativeAdapterPlanner.PlanAsync(root, "app", Commit, Tree,
                manifest with { Limits = manifest.Limits with { MaxUnits = 101 } }));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Given_nested_projects_then_indexed_ownership_uses_the_nearest_directory()
    {
        var root = NewRoot();
        try
        {
            Write(root, "Parent.csproj", "<Project />");
            Write(root, "Parent.cs", "public class Parent { }");
            Write(root, "child/Child.csproj", "<Project />");
            Write(root, "child/deep/Child.cs", "public class Child { }");
            var plan = await NativeAdapterPlanner.PlanAsync(root, "nested", Commit, Tree, Manifest());
            Assert.Equal(2, plan.UnitCounts.Eligible);
            Assert.All(plan.Units, unit => Assert.Equal(1, unit.SourceFileCount));
            Assert.Equal("child/Child.csproj",
                Assert.Single(plan.Inputs, input => input.Path == "child/deep/Child.cs").UnitPath);

            var capped = await NativeAdapterPlanner.PlanAsync(root, "nested", Commit, Tree,
                Manifest() with { Limits = Manifest().Limits with { MaxUnits = 1 } });
            Assert.Single(capped.Units);
            Assert.Equal(1, capped.UnitCounts.Unknown);
            Assert.Equal("unknown",
                Assert.Single(capped.Inputs, input => input.Path == "child/deep/Child.cs").Status);
        }
        finally { DeleteRoot(root); }
    }

    private static NativeAdapterManifest Manifest(
        IReadOnlyList<string>? excludedPathPrefixes = null,
        string recipeSha256 = Recipe) =>
        new(
            "ce.adapter.manifest.v1",
            "csharp.roslyn",
            "0.2.0",
            "csharp-project-discovery-v1",
            recipeSha256,
            "dotnet10.0",
            ["csharp.project.v1"],
            new NativeAdapterLimits(1000, 100, 1024 * 1024, 8 * 1024 * 1024, 32),
            new NativeAdapterConfiguration(excludedPathPrefixes ?? []));

    private static string NewRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "native-adapter-planner", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
