using System.Text.Json;
using CapabilityExplorer.Capabilities.Analysis;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class AnalysisConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath().Replace("/var/", "/private/var/", StringComparison.Ordinal), "ce-overlay-" + Guid.NewGuid().ToString("N"));
    private string Shared => Path.Combine(_directory, "capabilities.json");
    private string Local => Path.Combine(_directory, "capabilities.local.json");
    public AnalysisConfigurationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Capture_without_overlay_preserves_exact_shared_bytes()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{ \"futureMetadata\":true,\"capabilities\":[] }\n");
        File.WriteAllBytes(Shared, bytes);
        Assert.Equal(bytes, FileRunStore.CaptureConfiguration(Shared));
    }
    [Fact]
    public void Local_only_capability_and_overrides_are_frozen_with_shared_metadata()
    {
        File.WriteAllText(Shared, """{"futureMetadata":{"version":7},"capabilities":[{"id":"sales","name":"shared","repositories":[]},{"id":"shared-only","repositories":[]}]}""");
        File.WriteAllText(Local, """{"capabilities":[{"id":"sales","name":"local override","includeTests":true,"repositories":[]},{"id":"local-only","repositories":[]}]}""");
        using var effective = JsonDocument.Parse(FileRunStore.CaptureConfiguration(Shared));
        Assert.Equal(7, effective.RootElement.GetProperty("futureMetadata").GetProperty("version").GetInt32());
        var definitions = effective.RootElement.GetProperty("capabilities").EnumerateArray().ToArray();
        Assert.Equal(new[] { "sales", "shared-only", "local-only" }, definitions.Select(definition => definition.GetProperty("id").GetString()));
        Assert.Equal("local override", definitions[0].GetProperty("name").GetString());
        Assert.True(definitions[0].GetProperty("includeTests").GetBoolean());
        var store = new FileRunStore(Path.Combine(_directory, "requests"));
        Assert.True(store.Admit("local-only", Shared, null, new()).Accepted);
    }
    [Fact]
    public void Post_admission_overlay_mutation_cannot_change_accepted_definition()
    {
        File.WriteAllText(Shared, """{"capabilities":[{"id":"sales","name":"shared","repositories":[]}]}""");
        File.WriteAllText(Local, """{"capabilities":[{"id":"sales","name":"accepted override","repositories":[]}]}""");
        var store = new FileRunStore(Path.Combine(_directory, "requests"));
        var admitted = store.Admit("sales", Shared, null, new()).Run;
        File.WriteAllText(Local, """{"capabilities":[{"id":"sales","name":"changed later","repositories":[]}]}""");
        using var owner = store.AcquireWorker();
        var run = store.ClaimNext(owner)!;
        Assert.Equal(admitted.AcceptedInputsHash, run.AcceptedInputsHash);
        using var frozen = JsonDocument.Parse(File.ReadAllBytes(run.ConfigurationPath));
        Assert.Equal("accepted override", frozen.RootElement.GetProperty("capabilities")[0].GetProperty("name").GetString());
        Assert.DoesNotContain("changed later", File.ReadAllText(run.ConfigurationPath), StringComparison.Ordinal);
    }
    [Fact]
    public void Invalid_or_symlinked_local_overlay_is_refused()
    {
        File.WriteAllText(Shared, """{"capabilities":[]}""");
        File.WriteAllText(Local, """{"capabilities":[{"id":"sales"},{"id":"sales"}]}""");
        Assert.Throws<InvalidDataException>(() => FileRunStore.CaptureConfiguration(Shared));
        File.Delete(Local);
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(Local, Shared);
        Assert.Throws<IOException>(() => FileRunStore.CaptureConfiguration(Shared));
    }
    [Fact]
    public void Unbound_overlay_beside_frozen_configuration_cannot_change_execution()
    {
        File.WriteAllText(Shared, """{"capabilities":[{"id":"sales","repositories":[]}]}""");
        var store = new FileRunStore(Path.Combine(_directory, "requests"));
        var admitted = store.Admit("sales", Shared, null, new()).Run;
        var injected = Path.Combine(store.RunDirectory(admitted.RunId), "capabilities.local.json");
        File.WriteAllText(injected, """{"capabilities":[{"id":"sales","name":"unaccepted override","repositories":[]}]}""");
        using var owner = store.AcquireWorker();
        Assert.Throws<InvalidDataException>(() => store.ClaimNext(owner));
        Assert.Equal("queued", store.ReadRun(admitted.RunId).State);
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
