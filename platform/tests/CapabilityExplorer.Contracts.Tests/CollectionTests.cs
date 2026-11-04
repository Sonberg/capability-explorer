using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class CollectionTests
{
    private static Type? ExporterType =>
        typeof(ArtifactValidator).Assembly.GetType("CapabilityExplorer.Contracts.Collection.CollectionExporter");

    [Fact]
    public void Offline_collection_api_exposes_export_and_independent_validation()
    {
        var type = ExporterType;
        Assert.NotNull(type);
        Assert.True(type!.IsPublic);
        AssertMethod(type, "ExportAsync", typeof(string), typeof(string), typeof(CancellationToken));
        AssertMethod(type, "ValidateAsync", typeof(string), typeof(CancellationToken));
    }

    [Fact]
    public async Task Required_missing_input_fails_without_creating_a_success_bundle()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "collection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var plan = new
            {
                schema = "ce.collection.plan.v1",
                recipeSha256 = new string('a', 64),
                configSha256 = new string('b', 64),
                policySha256 = new string('c', 64),
                repositories = new[]
                {
                    new
                    {
                        repositoryId = "repo-a", providerId = "provider-1", producerId = "producer-1",
                        tenantRef = "tenant-1", rightsRef = "rights-1", commitSha = new string('d', 40),
                        treeSha = new string('e', 40), permittedContentProfiles = Array.Empty<string>()
                    }
                },
                inputs = new[]
                {
                    new
                    {
                        repositoryId = "repo-a", profile = "ce.models.v1", path = "missing.json",
                        unitPath = "models/process.json", sha256 = new string('f', 64), byteLength = 12,
                        required = true
                    }
                }
            };
            var planPath = Path.Combine(root, "plan.json");
            var output = Path.Combine(root, "bundle");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan));

            var result = await InvokeAsync(ExporterType!, "ExportAsync", planPath, output);

            Assert.False(ReadProperty<bool>(result, "IsValid"));
            Assert.Contains((IReadOnlyList<string>)ReadProperty<object>(result, "Problems"),
                problem => problem.Contains("required", StringComparison.Ordinal));
            Assert.False(Directory.Exists(output));
            Assert.False(File.Exists(Path.Combine(output, "collection.json")));
            Assert.Empty(Directory.GetDirectories(root, ".bundle.collection-stage-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Collection_validation_rejects_a_modified_descriptor_or_artifact()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "collection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var report = await InvokeAsync(ExporterType!, "ValidateAsync", root);
            Assert.False(ReadProperty<bool>(report, "IsValid"));
            Assert.NotEmpty((IReadOnlyList<string>)ReadProperty<object>(report, "Problems"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Two_repository_export_is_deterministic_retryable_and_independently_validated()
    {
        var root = NewTestRoot();
        try
        {
            var plan = await WritePlanAsync(root,
                [("repo-a", "models/a.json", CreateModel("repo-a", "models/a.json")),
                 ("repo-b", "models/b.json", CreateModel("repo-b", "models/b.json"))]);
            var output = Path.Combine(root, "bundle");

            var first = await CollectionExporter.ExportAsync(plan, output);
            var repeated = await CollectionExporter.ExportAsync(plan, output);
            var validation = await CollectionExporter.ValidateAsync(output);

            Assert.True(first.IsValid, string.Join("; ", first.Problems));
            Assert.True(repeated.IsValid, string.Join("; ", repeated.Problems));
            Assert.True(repeated.Reused);
            Assert.Equal(first.ContentSha256, repeated.ContentSha256);
            Assert.True(validation.IsValid, string.Join("; ", validation.Problems));
            using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "collection.json")));
            var repositories = descriptor.RootElement.GetProperty("content").GetProperty("repositories");
            Assert.Equal(["repo-a", "repo-b"], repositories.EnumerateArray()
                .Select(r => r.GetProperty("repositoryId").GetString()!).ToArray());
            Assert.Equal(2, Directory.GetFiles(Path.Combine(output, "artifacts"), "*.json", SearchOption.AllDirectories).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Input_order_and_attempt_time_do_not_change_content_identity()
    {
        var root = NewTestRoot();
        try
        {
            var artifacts = new[]
            {
                ("repo-a", "models/a.json", CreateModel("repo-a", "models/a.json")),
                ("repo-a", "models/b.json", CreateModel("repo-a", "models/b.json")),
            };
            var planA = await WritePlanAsync(Path.Combine(root, "a"), artifacts);
            var planB = await WritePlanAsync(Path.Combine(root, "b"), artifacts.Reverse().ToArray());

            var a = await CollectionExporter.ExportAsync(planA, Path.Combine(root, "bundle-a"));
            var b = await CollectionExporter.ExportAsync(planB, Path.Combine(root, "bundle-b"));

            Assert.True(a.IsValid, string.Join("; ", a.Problems));
            Assert.True(b.IsValid, string.Join("; ", b.Problems));
            Assert.Equal(a.ContentSha256, b.ContentSha256);
            Assert.NotEqual(
                JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "bundle-a", "attempt.json")))
                    .RootElement.GetProperty("attemptId").GetString(),
                JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "bundle-b", "attempt.json")))
                    .RootElement.GetProperty("attemptId").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Tampered_artifact_bytes_are_rejected_after_export()
    {
        var root = NewTestRoot();
        try
        {
            var plan = await WritePlanAsync(root, [("repo-a", "models/a.json", CreateModel("repo-a", "models/a.json"))]);
            var output = Path.Combine(root, "bundle");
            Assert.True((await CollectionExporter.ExportAsync(plan, output)).IsValid);
            await File.WriteAllTextAsync(Path.Combine(output, "artifacts", "repo-a", "ce.models.v1", "0000.json"), "{}");

            var report = await CollectionExporter.ValidateAsync(output);

            Assert.False(report.IsValid);
            Assert.Contains(report.Problems, p => p.Contains("bytes", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unsafe_path_and_unauthorized_content_are_refused()
    {
        var root = NewTestRoot();
        try
        {
            var pathPlan = await WritePlanAsync(root, [("repo-a", "../outside.json", CreateModel("repo-a", "models/a.json"))]);
            var pathResult = await CollectionExporter.ExportAsync(pathPlan, Path.Combine(root, "unsafe"));
            Assert.False(pathResult.IsValid);
            Assert.False(Directory.Exists(Path.Combine(root, "unsafe")));

            var modelContent = CreateModel("repo-a", "models/a.json", "model-content");
            var contentPlan = await WritePlanAsync(root, [("repo-a", "models/content.json", modelContent)],
                permittedContentProfiles: [], contentProfile: null);
            var contentResult = await CollectionExporter.ExportAsync(contentPlan, Path.Combine(root, "content"));
            Assert.False(contentResult.IsValid);
            Assert.Contains(contentResult.Problems, p => p.Contains("contentProfile", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Optional_failed_profile_is_explicit_partial_content()
    {
        var root = NewTestRoot();
        try
        {
            Directory.CreateDirectory(root);
            var expectedBytes = Encoding.UTF8.GetBytes("{}");
            var plan = new CollectionPlan("ce.collection.plan.v1", new string('a', 64), new string('b', 64),
                new string('c', 64), [Repository("repo-a")],
                [new CollectionInputPlan("repo-a", "ce.models.v1", "not-present.json", "models/missing.json",
                    Convert.ToHexStringLower(SHA256.HashData(expectedBytes)), expectedBytes.Length, false)]);
            var planPath = Path.Combine(root, "optional.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, JsonOptions));
            var output = Path.Combine(root, "optional-bundle");

            var exported = await CollectionExporter.ExportAsync(planPath, output);
            var validated = await CollectionExporter.ValidateAsync(output);

            Assert.True(exported.IsValid, string.Join("; ", exported.Problems));
            Assert.True(validated.IsValid, string.Join("; ", validated.Problems));
            using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "collection.json")));
            Assert.Equal("optional-failed", descriptor.RootElement.GetProperty("content").GetProperty("inputs")[0]
                .GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Content_leaves_only_with_a_declared_permitted_content_profile()
    {
        var root = NewTestRoot();
        try
        {
            var bytes = CreateModel("repo-a", "models/content.json", "model-content");
            var permitted = "model-content";
            var plan = await WritePlanAsync(root, [("repo-a", "models/content.json", bytes)],
                permittedContentProfiles: [permitted], contentProfile: permitted);

            var result = await CollectionExporter.ExportAsync(plan, Path.Combine(root, "authorized-content"));

            Assert.True(result.IsValid, string.Join("; ", result.Problems));
            Assert.True((await CollectionExporter.ValidateAsync(Path.Combine(root, "authorized-content"))).IsValid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Changed_policy_conflicts_without_replacing_last_good_bundle()
    {
        var root = NewTestRoot();
        try
        {
            var plan = await WritePlanAsync(root, [("repo-a", "models/a.json", CreateModel("repo-a", "models/a.json"))]);
            var output = Path.Combine(root, "bundle");
            var initial = await CollectionExporter.ExportAsync(plan, output);
            var changed = JsonDocument.Parse(await File.ReadAllTextAsync(plan)).RootElement.Clone();
            var changedJson = JsonNode.Parse(changed.GetRawText())!.AsObject();
            changedJson["policySha256"] = new string('9', 64);
            var changedPlan = Path.Combine(root, "changed-plan.json");
            await File.WriteAllTextAsync(changedPlan, changedJson.ToJsonString());

            var conflict = await CollectionExporter.ExportAsync(changedPlan, output);
            var stillValid = await CollectionExporter.ValidateAsync(output);

            Assert.True(initial.IsValid);
            Assert.False(conflict.IsValid);
            Assert.True(stillValid.IsValid);
            Assert.Equal(initial.ContentSha256, stillValid.ContentSha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_does_not_create_a_bundle()
    {
        var root = NewTestRoot();
        try
        {
            var plan = await WritePlanAsync(root, [("repo-a", "models/a.json", CreateModel("repo-a", "models/a.json"))]);
            var output = Path.Combine(root, "canceled");

            var result = await CollectionExporter.ExportAsync(plan, output, new CancellationToken(canceled: true));

            Assert.False(result.IsValid);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Dotnet_consumer_accepts_the_sealed_typescript_fixture()
    {
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../../extractors/ts/test/collection/fixtures/valid"));

        var result = await CollectionExporter.ValidateAsync(fixture);

        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        Assert.Equal("309077bb610183045b650dac95972602f418dcc7f8033a4441244a3d5f95fa77", result.ContentSha256);
    }

    [Fact]
    public async Task Canonical_digest_matches_javascript_for_unicode_and_metacharacters()
    {
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../../extractors/ts/test/collection/fixtures/unicode"));
        var inputPath = Path.Combine(fixture, "artifacts", "repo-a", "ce.models.v1", "0000.json");
        var modelBytes = await File.ReadAllBytesAsync(inputPath);
        var root = Path.Combine(AppContext.BaseDirectory, "collection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var plan = await WritePlanAsync(root,
                [("repo-a", "models/漢字+<workflow>.json", modelBytes)]);

            var exported = await CollectionExporter.ExportAsync(plan, Path.Combine(root, "bundle"));
            var independentlyValidated = await CollectionExporter.ValidateAsync(fixture);

            Assert.True(exported.IsValid, string.Join("; ", exported.Problems));
            Assert.True(independentlyValidated.IsValid, string.Join("; ", independentlyValidated.Problems));
            Assert.Equal(independentlyValidated.ContentSha256, exported.ContentSha256);
            Assert.Equal("01ec741e70af99231a58b85d9f241b0bee4bb158573697df39f20d5ba8f79044",
                exported.ContentSha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Nested_invalid_model_evidence_is_refused_even_when_common_identity_matches()
    {
        var root = NewTestRoot();
        try
        {
            var model = JsonNode.Parse(CreateModel("repo-a", "models/a.json"))!.AsObject();
            model["elements"] = new JsonArray(new JsonObject());
            var plan = await WritePlanAsync(root,
                [("repo-a", "models/a.json", Encoding.UTF8.GetBytes(model.ToJsonString()))],
                ["model-content"], "model-content");
            var output = Path.Combine(root, "invalid-evidence");

            var result = await CollectionExporter.ExportAsync(plan, output);

            Assert.False(result.IsValid);
            Assert.False(Directory.Exists(output));
            Assert.Contains(result.Problems, problem => problem.Contains("schema", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unrelated-content")]
    public async Task Structural_evidence_requires_the_matching_content_permission(string? permission)
    {
        var root = NewTestRoot();
        try
        {
            var plan = await WritePlanAsync(root,
                [("repo-a", "models/a.json", CreateModel("repo-a", "models/a.json"))],
                permission is null ? [] : [permission], permission);
            var output = Path.Combine(root, "denied");

            var result = await CollectionExporter.ExportAsync(plan, output);

            Assert.False(result.IsValid);
            Assert.False(Directory.Exists(output));
            Assert.Contains(result.Problems, problem => problem.Contains("contentProfile", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTestRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "collection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<string> WritePlanAsync(
        string root,
        IReadOnlyList<(string Repository, string UnitPath, byte[] Bytes)> artifacts,
        IReadOnlyList<string>? permittedContentProfiles = null,
        string? contentProfile = "model-content")
    {
        Directory.CreateDirectory(root);
        var repositories = artifacts.Select(a => a.Repository).Distinct(StringComparer.Ordinal)
            .Select(id => Repository(id, permittedContentProfiles ?? ["model-content"])).ToArray();
        var inputs = new List<CollectionInputPlan>();
        foreach (var (repository, unitPath, bytes) in artifacts)
        {
            var path = $"inputs/{repository}-{Path.GetFileName(unitPath)}";
            Directory.CreateDirectory(Path.Combine(root, "inputs"));
            await File.WriteAllBytesAsync(Path.Combine(root, path), bytes);
            inputs.Add(new CollectionInputPlan(repository, "ce.models.v1", path, unitPath,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, true, contentProfile));
        }

        var plan = new CollectionPlan("ce.collection.plan.v1", new string('a', 64), new string('b', 64),
            new string('c', 64), repositories, inputs);
        var planPath = Path.Combine(root, "plan.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, JsonOptions));
        return planPath;
    }

    private static CollectionRepositoryPlan Repository(string id, IReadOnlyList<string>? permitted = null) =>
        new(id, $"github:{id}", "producer-1", "tenant-1", "rights-1", new string('d', 40),
            new string('e', 40), permitted ?? []);

    private static byte[] CreateModel(string repository, string unitPath, string contentProfile = "structural")
    {
        var document = new JsonObject
        {
            ["schema"] = "ce.models.v1",
            ["repositoryId"] = repository,
            ["commitSha"] = new string('d', 40),
            ["unitPath"] = unitPath,
            ["sourceSha256"] = new string('f', 64),
            ["contentProfile"] = contentProfile,
            ["coverage"] = new JsonObject
            {
                ["status"] = "full",
                ["supported"] = new JsonArray(),
                ["limitations"] = new JsonArray()
            },
            ["diagnostics"] = new JsonArray(),
            ["elements"] = new JsonArray(),
            ["relationships"] = new JsonArray()
        };

        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    private static void AssertMethod(Type type, string name, params Type[] parameterTypes)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, parameterTypes);
        Assert.NotNull(method);
        Assert.Equal("System.Threading.Tasks.Task`1[CapabilityExplorer.Contracts.Collection.CollectionOperationResult]",
            method!.ReturnType.ToString());
    }

    private static async Task<object> InvokeAsync(Type type, string name, params object[] arguments)
    {
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == name);
        var parameters = method.GetParameters();
        var callArguments = arguments.Concat(parameters.Skip(arguments.Length)
            .Select(_ => (object)CancellationToken.None)).ToArray();
        var task = (Task)method.Invoke(null, callArguments)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static T ReadProperty<T>(object instance, string name) =>
        (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
}
