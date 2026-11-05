using Microsoft.Extensions.AI;

namespace Provenance.AgentDojo.Host;

/// <summary>
/// A fake model for checking the plumbing without a real one: it calls <c>get_current_day</c>
/// once if that tool is offered, then reports the answer. It scores 0% on AgentDojo by design.
/// </summary>
internal sealed class ScriptedPlumbingModel : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        var lastResult = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        var offersDay = options?.Tools?.Any(t => t.Name == "get_current_day") == true;

        ChatMessage reply = lastResult is null && offersDay
            ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call_{Guid.NewGuid():N}", "get_current_day", new Dictionary<string, object?>())])
            : new ChatMessage(ChatRole.Assistant, lastResult is null ? "I have no tools to use." : $"Today is {lastResult.Result}.");

        return Task.FromResult(new ChatResponse(reply));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
