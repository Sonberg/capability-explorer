using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Presents an <see cref="IChatModel"/> (our OpenAI-compatible client, with its endpoint, key and Entra ID handling) as a
/// Microsoft.Extensions.AI <see cref="IChatClient"/>, so Microsoft Agent Framework can run the tool-calling loop on top of it.
/// </summary>
public sealed class ModelChatClient(IChatModel model) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var tools = options?.Tools?.OfType<AIFunctionDeclaration>().Select(t => new ChatTool(t.Name, t.Description, t.JsonSchema)).ToList();
        var sent = messages.SelectMany(ToModel).ToList();
        if (options?.Instructions is { Length: > 0 } instructions)
        {
            sent.Insert(0, new ChatMessage("system", instructions)); // Agent Framework passes the agent's instructions here, not as a message
        }

        var result = await model.ChatAsync(sent, tools, cancellationToken);

        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(result.Content))
        {
            contents.Add(new TextContent(result.Content));
        }

        contents.AddRange(result.ToolCalls.Select(c => new FunctionCallContent(c.Id, c.Name, Arguments(c.Arguments))));
        return new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, contents))
        {
            Usage = new UsageDetails { InputTokenCount = result.InputTokens, OutputTokenCount = result.OutputTokens },
        };
    }

    // ponytail: the chat panel waits for the whole answer; add SSE streaming to FoundryChatModel if that feels slow.
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("streaming is not implemented");

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static IEnumerable<ChatMessage> ToModel(Microsoft.Extensions.AI.ChatMessage m)
    {
        if (m.Role == ChatRole.Tool)
        {
            foreach (var r in m.Contents.OfType<FunctionResultContent>())
            {
                yield return new ChatMessage("tool", r.Result as string ?? JsonSerializer.Serialize(r.Result), ToolCallId: r.CallId);
            }

            yield break;
        }

        var calls = m.Contents.OfType<FunctionCallContent>().Select(c => new ToolCall(c.CallId, c.Name, JsonSerializer.Serialize(c.Arguments ?? new Dictionary<string, object?>()))).ToList();
        yield return new ChatMessage(m.Role.Value, string.IsNullOrEmpty(m.Text) ? null : m.Text, calls.Count > 0 ? calls : null);
    }

    private static Dictionary<string, object?>? Arguments(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(); // the tool then reports the missing arguments to the model
        }
    }
}
