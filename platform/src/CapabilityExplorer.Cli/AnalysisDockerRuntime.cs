using System.Diagnostics;

namespace CapabilityExplorer.Cli;

internal interface IAnalysisContainerRuntime
{
    Task EnsureFrozen();
    void ApplyEnvironment(ProcessStartInfo start);
    Task<bool> CleanupOwned(string label);
}

/// <summary>Captures one local endpoint; cleanup never follows a mutable Docker context selection.</summary>
internal sealed class AnalysisDockerRuntime : IAnalysisContainerRuntime
{
    private string? _endpoint;
    private string? _docker;
    public async Task EnsureFrozen()
    {
        if (_endpoint is not null) return;
        _docker = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.GetFullPath(Path.Combine(directory,"docker"))).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Docker executable unavailable");
        var endpoint = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (string.IsNullOrEmpty(endpoint))
        {
            var context = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
            if (string.IsNullOrEmpty(context)) context = (await Run(["context","show"])).Trim();
            if (context.Length is < 1 or > 100 || !char.IsAsciiLetterOrDigit(context[0]) ||
                context.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
                throw new InvalidOperationException("unsupported Docker context name");
            endpoint = (await Run(["context","inspect",context,"--format","{{.Endpoints.docker.Host}}"])).Trim();
        }
        if (endpoint.Length > 1024 || !endpoint.StartsWith("unix:///",StringComparison.Ordinal) || endpoint.Any(char.IsControl) ||
            !Uri.TryCreate(endpoint,UriKind.Absolute,out var uri) || uri.Scheme != "unix" || uri.Host.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("analysis requires a local Unix Docker endpoint");
        _endpoint = endpoint;
    }
    public void ApplyEnvironment(ProcessStartInfo start)
    {
        if (_endpoint is null) throw new InvalidOperationException("Docker endpoint was not captured");
        start.Environment.Remove("DOCKER_CONTEXT");
        start.Environment["DOCKER_HOST"] = _endpoint;
    }
    public async Task<bool> CleanupOwned(string label)
    {
        if (_endpoint is null || _docker is null) return false;
        try
        {
            var ids = ParseIds(await Run(["--host",_endpoint,"ps","-q","--filter","label=" + label]));
            if (ids.Length > 0)
                await Run(["--host",_endpoint,"kill",.. ids]);
            return ParseIds(await Run(["--host",_endpoint,"ps","-q","--filter","label=" + label])).Length == 0;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
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
        var start = new ProcessStartInfo(_docker!) { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, RedirectStandardInput=true };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "HOME", "TMPDIR", "DOCKER_CONFIG" })
            if (Environment.GetEnvironmentVariable(name) is {} value) start.Environment[name] = value;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start Docker metadata command");
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var output = ReadBounded(process.StandardOutput,drain.Token);
        var error = ReadBounded(process.StandardError,drain.Token);
        try
        {
            process.StandardInput.Close();
            while (!process.HasExited)
            {
                if (output.IsFaulted || error.IsFaulted) throw new IOException("Docker metadata exceeded bounds");
                await Task.Delay(50,timeout.Token);
            }
            if (process.ExitCode != 0) throw new IOException("Docker metadata operation failed");
            var text = await output.WaitAsync(timeout.Token); await error.WaitAsync(timeout.Token);
            return text;
        }
        catch
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree:true); } catch (InvalidOperationException) { } }
            throw;
        }
        finally { drain.Cancel(); }
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096]; var text = new System.Text.StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(),token);
            if (count == 0) return text.ToString();
            if (text.Length + count > 16384) throw new IOException("Docker metadata exceeded bounds");
            text.Append(buffer,0,count);
        }
    }
}
