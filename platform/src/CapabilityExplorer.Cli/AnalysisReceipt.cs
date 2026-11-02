using System.Diagnostics;
using System.Text.Json;
using CapabilityExplorer.Capabilities.Analysis;

namespace CapabilityExplorer.Cli;

/// <summary>Replay inventory is written only by the already owning host after every selected core run is done.</summary>
internal sealed class AnalysisReceipt
{
    private readonly string _root;
    private readonly string _configurationHash;
    private readonly string? _bindingsHash;
    private readonly string _applicationCommit;
    private readonly string _sourceState;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private AnalysisReceipt(string root, string configurationHash, string? bindingsHash, string commit, string state)
    { _root=root; _configurationHash=configurationHash; _bindingsHash=bindingsHash; _applicationCommit=commit; _sourceState=state; }

    internal static async Task<AnalysisReceipt> Start(string root, string configuration, string? bindings)
    {
        var output = Path.Combine(root,".sandbox-cache/analysis-receipt.json");
        AnalysisPreparation.RejectLinks(output);
        if (File.Exists(output)) File.Delete(output);
        return new(root,AnalysisWorkerCommand.HashFile(configuration),bindings is null ? null : AnalysisWorkerCommand.HashFile(bindings),
            (await Git(root,["rev-parse","--verify","HEAD"])).Trim(),
            string.IsNullOrWhiteSpace(await Git(root,["status","--porcelain","--untracked-files=all"])) ? "clean" : "dirty");
    }

    internal async Task Complete(FileRunStore store, IReadOnlyList<string> runIds, string configuration, string? bindings)
    {
        if (AnalysisWorkerCommand.HashFile(configuration) != _configurationHash || (bindings is null ? null : AnalysisWorkerCommand.HashFile(bindings)) != _bindingsHash)
            throw new InvalidOperationException("aggregate inputs changed during analysis");
        var artifacts = new SortedDictionary<string,object>(StringComparer.Ordinal);
        var runs = new List<object>();
        foreach (var runId in runIds)
        {
            var run = store.ReadRun(runId);
            if (run.State != "done" || run.ManifestHash is null || AnalysisWorkerCommand.HashFile(run.ConfigurationPath) != _configurationHash ||
                (run.BindingsPath is null ? null : AnalysisWorkerCommand.HashFile(run.BindingsPath)) != _bindingsHash)
                throw new InvalidOperationException("aggregate requires completed runs with identical frozen configuration");
            var resolvedPath = Path.Combine(store.RunDirectory(runId),"resolved.json");
            if (AnalysisWorkerCommand.HashFile(resolvedPath) != run.ManifestHash) throw new IOException("resolved run receipt changed");
            var manifest = JsonSerializer.Deserialize<ResolvedManifest>(await File.ReadAllTextAsync(resolvedPath),Json) ?? throw new FormatException("missing resolved manifest");
            if (manifest.AcceptedInputsHash != run.AcceptedInputsHash) throw new FormatException("aggregate resolved identity mismatch");
            foreach (var input in manifest.Inputs.Where(p => p.Key.StartsWith("artifact:",StringComparison.Ordinal)))
            {
                var parts = input.Key.Split(':',3);
                if (parts.Length != 3 || !manifest.Inputs.TryGetValue("revision:" + parts[1],out var revision)) throw new FormatException("invalid aggregate artifact identity");
                var relative = Path.Combine(parts[1],revision,parts[2]).Replace('\\','/');
                var artifactRoot = Path.GetFullPath(Path.Combine(_root,".sandbox-cache/artifacts"));
                var path = Path.GetFullPath(Path.Combine(artifactRoot,relative));
                if (!path.StartsWith(artifactRoot + Path.DirectorySeparatorChar,StringComparison.Ordinal)) throw new IOException("aggregate artifact escapes root");
                AnalysisPreparation.RejectLinks(path);
                if (AnalysisWorkerCommand.HashFile(path) != input.Value) throw new IOException("aggregate artifact changed");
                artifacts[relative] = new { path=relative, repositoryId=parts[1], commitSha=revision, sha256=input.Value };
            }
            runs.Add(new { run.RunId, run.AttemptId, run.ManifestHash, run.AcceptedInputsHash });
        }
        if (artifacts.Count == 0) throw new InvalidOperationException("aggregate contains no artifacts");
        var receipt = new { format="ce.analysis-replay.v1", applicationCommit=_applicationCommit, sourceState=_sourceState,
            capabilitiesHash=_configurationHash, runtimeImagesHash=AnalysisWorkerCommand.HashFile(Path.Combine(_root,"config/runtime/images.env")),
            bindingsHash=_bindingsHash, artifacts=artifacts.Values, runs };
        var destination = Path.Combine(_root,".sandbox-cache/analysis-receipt.json");
        AnalysisPreparation.RejectLinks(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix host required");
        await using (var stream = new FileStream(temporary,new FileStreamOptions { Mode=FileMode.CreateNew, Access=FileAccess.Write,
            UnixCreateMode=UnixFileMode.UserRead | UnixFileMode.UserWrite }))
        {
            await JsonSerializer.SerializeAsync(stream,receipt,Json);
            await stream.FlushAsync(); stream.Flush(flushToDisk:true);
        }
        File.Move(temporary,destination,overwrite:true);
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var result = new System.Text.StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(),cancellationToken);
            if (count == 0) return result.ToString();
            if (result.Length + count > 65536) throw new IOException("application metadata exceeded bounds");
            result.Append(buffer,0,count);
        }
    }

    private static async Task<string> Git(string root, string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory=root, UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.fsmonitor=false");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not inspect application source");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var output = ReadBounded(process.StandardOutput,timeout.Token);
            var error = ReadBounded(process.StandardError,timeout.Token);
            while (!process.HasExited)
            {
                if (output.IsFaulted || error.IsFaulted) throw new IOException("application metadata exceeded bounds");
                await Task.Delay(50,timeout.Token);
            }
            await process.WaitForExitAsync(timeout.Token);
            var text = await output; await error;
            if (process.ExitCode != 0 || text.Length > 65536) throw new IOException("application source inspection failed or exceeded bounds");
            return text;
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree:true); throw; }
    }
}
