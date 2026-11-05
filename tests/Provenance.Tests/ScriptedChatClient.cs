using System.Text.Json;

namespace Provenance.Tests;

/// <summary>
/// A fake model that replays a script. Each step sees the messages the model would see,
/// so tests can both drive the agent and assert on exactly what reached the model.
/// </summary>
internal sealed class ScriptedChatClient(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] steps) : IChatClient
{
    private int _next;

    /// <summary>Every request the model received, in order.</summary>
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToList();
        Requests.Add(snapshot);
        if (_next >= steps.Length)
        {
            throw new InvalidOperationException($"The script has only {steps.Length} steps.");
        }

        return Task.FromResult(steps[_next++](snapshot));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    /// <summary>Everything the model has seen, as one string, for "never saw X" assertions.</summary>
    public string AllTextSeen() => string.Join("\n", Requests.SelectMany(r => r).SelectMany(m => m.Contents).Select(c => c switch
    {
        TextContent text => text.Text,
        FunctionResultContent result => result.Result?.ToString(),
        FunctionCallContent call => JsonSerializer.Serialize(call.Arguments),
        _ => null,
    }));

    public static ChatResponse Call(string name, Dictionary<string, object?>? arguments = null) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, arguments)]));

    public static ChatResponse Say(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    public static string LastToolResult(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last().Result?.ToString() ?? string.Empty;

    public static string LastVariableId(IReadOnlyList<ChatMessage> messages)
    {
        using var document = JsonDocument.Parse(LastToolResult(messages));
        return document.RootElement.GetProperty("variable_id").GetString()!;
    }
}
