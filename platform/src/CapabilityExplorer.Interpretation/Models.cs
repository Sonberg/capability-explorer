using System.Text.Json;

namespace CapabilityExplorer.Interpretation;

/// <summary>A request for structured JSON output. Everything that influences the answer is in here, so the input hash is complete.</summary>
public sealed record ModelRequest(string System, string User, string SchemaName, JsonElement Schema, string InputHash);

public sealed record ModelResponse(string Content, int? InputTokens = null, int? OutputTokens = null);

/// <summary>A language model behind the optional interpretation layer.</summary>
public interface IInterpretationModel
{
    /// <summary>Provider and deployment, e.g. "foundry:gpt-x" or "recorded"; part of every input hash.</summary>
    string Id { get; }

    Task<ModelResponse> CompleteJsonAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The model could not be used (not configured, no recording, HTTP failure). Never a reason to fail non-AI features.</summary>
public sealed class InterpretationUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Replays stored responses keyed by input hash. Used by tests and CI, and for offline demos.</summary>
public sealed class RecordedModel(string directory, string id = "recorded") : IInterpretationModel
{
    public string Id { get; } = id;

    public async Task<ModelResponse> CompleteJsonAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = Path.Combine(directory, $"{request.InputHash}.json");
        if (!File.Exists(path))
        {
            throw new InterpretationUnavailableException($"no recorded response for input {request.InputHash[..12]} in {directory}");
        }

        return new ModelResponse(await File.ReadAllTextAsync(path, cancellationToken));
    }
}

/// <summary>Wraps a live model and stores each response under its input hash, producing recordings for <see cref="RecordedModel"/>.</summary>
public sealed class RecordingModel(IInterpretationModel inner, string directory) : IInterpretationModel
{
    public string Id => inner.Id;

    public async Task<ModelResponse> CompleteJsonAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await inner.CompleteJsonAsync(request, cancellationToken);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{request.InputHash}.json"), response.Content, cancellationToken);
        return response;
    }
}
