using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapabilityExplorer.Cli;

internal sealed class AnalysisEngineRuntime(string root) : IAnalysisContainerRuntime
{
    private string? _snapshot;
    private string? _backend;

    public async Task EnsureFrozen()
    {
        if (_snapshot is not null) return;
        var json = await Run(["--analysis-snapshot"]);
        var snapshot = JsonSerializer.Deserialize<RuntimeSnapshot>(json,JsonSerializerOptions.Web);
        if (snapshot is null || snapshot.Backend is not ("docker" or "podman") ||
            snapshot.Executable is not { Length: > 0 } || !Path.IsPathFullyQualified(snapshot.Executable) ||
            snapshot.PrefixArguments is null || snapshot.PrefixArguments.Length is < 1 or > 4 ||
            snapshot.PrefixArguments.Any(argument => string.IsNullOrEmpty(argument) || argument.Any(char.IsControl)) ||
            snapshot.Fingerprint is not { Length: 64 } || snapshot.Fingerprint.Any(character => character is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')) ||
            snapshot.Backend != AnalysisWorkerCommand.ResolveEngine())
            throw new InvalidDataException("invalid selected analysis runtime");
        _backend = snapshot.Backend;
        _snapshot = json.Trim();
    }

    public void ApplyEnvironment(ProcessStartInfo start)
    {
        if (_snapshot is null || _backend is null) throw new InvalidOperationException("analysis runtime was not captured");
        foreach (var name in new[] { "DOCKER_HOST","DOCKER_CONTEXT","CONTAINER_HOST","CONTAINER_CONNECTION","CONTAINER_SSHKEY","CE_PODMAN_CONNECTION" })
            start.Environment.Remove(name);
        start.Environment["CE_CONTAINER_ENGINE"] = _backend;
        start.Environment["CE_ANALYSIS_RUNTIME"] = _snapshot;
    }

    public async Task<bool> CleanupOwned(string label)
    {
        if (_snapshot is null) return false;
        try
        {
            var ids = ParseIds(await Run(["ps","-q","--filter","label=" + label]));
            if (ids.Length > 0) await Run(["kill",.. ids]);
            return ParseIds(await Run(["ps","-q","--filter","label=" + label])).Length == 0;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static string[] ParseIds(string output)
    {
        var ids = output.Split('\n',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length > 64 || ids.Any(id => id.Length is < 12 or > 64 || !id.All(char.IsAsciiHexDigit)))
            throw new IOException("invalid owned-container listing");
        return ids;
    }

    private async Task<string> Run(string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(root,"scripts/container-engine"))
        {
            WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
        };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH","HOME","TMPDIR","DOCKER_CONFIG","CE_CONTAINER_ENGINE","CE_PODMAN_CONNECTION","CE_ANALYSIS_RUNTIME",
            "DOCKER_HOST","DOCKER_CONTEXT","CONTAINER_HOST","CONTAINER_CONNECTION","CONTAINER_SSHKEY" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        if (_snapshot is not null) ApplyEnvironment(start);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start analysis runtime probe");
        var stdout = ReadBounded(child.StandardOutput,timeout.Token);
        var stderr = ReadBounded(child.StandardError,timeout.Token);
        try
        {
            child.StandardInput.Close();
            while (!child.HasExited)
            {
                if (stdout.IsFaulted || stderr.IsFaulted) throw new IOException("analysis runtime output exceeded bounds");
                await Task.Delay(50,timeout.Token);
            }
            await child.WaitForExitAsync(timeout.Token);
            var output = await stdout.WaitAsync(timeout.Token);
            await stderr.WaitAsync(timeout.Token);
            if (child.ExitCode != 0) throw new IOException("selected analysis runtime is unavailable or changed");
            return output;
        }
        catch
        {
            if (!child.HasExited)
            {
                try { child.Kill(entireProcessTree:true); } catch (InvalidOperationException) { }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await child.WaitForExitAsync(cleanup.Token); } catch (OperationCanceledException) { }
            }
            throw;
        }
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(),token);
            if (count == 0) return text.ToString();
            if (text.Length + count > 16384) throw new IOException("analysis runtime output exceeded bounds");
            text.Append(buffer,0,count);
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record RuntimeSnapshot(string Backend,string Executable,string[] PrefixArguments,string Fingerprint);
}