using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Provenance.AgentDojo.Host;

/// <summary>
/// Which model the agent uses. Any OpenAI-compatible endpoint works: Ollama (the default),
/// OpenAI, Azure OpenAI's v1 endpoint, vLLM, LM Studio.
/// </summary>
/// <remarks>
/// Configure with command-line arguments (<c>--Model:Name=qwen3:8b</c>) or environment variables
/// (<c>Model__Name=qwen3:8b</c>). Keys: <c>Provider</c> (<c>openai</c> or <c>scripted</c>),
/// <c>Endpoint</c>, <c>Name</c>, <c>ApiKey</c>, <c>TimeoutSeconds</c> (per model call, default 600),
/// <c>NoThink</c> (default true for Qwen3 models: turns off their long hidden reasoning, which is
/// what makes local runs slow), <c>Retries</c> (default 0 for a local endpoint, where a retry just
/// repeats a slow call, and 3 for a hosted one, where it rides out rate limits).
/// </remarks>
public sealed record ModelSettings(string Provider, Uri Endpoint, string Name, string ApiKey, TimeSpan Timeout, bool NoThink, int Retries)
{
    public static ModelSettings From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Model");
        var name = section["Name"] ?? "qwen3:8b";
        var endpoint = new Uri(section["Endpoint"] ?? "http://localhost:11434/v1");
        return new ModelSettings(
            Provider: section["Provider"] ?? "openai",
            Endpoint: endpoint,
            Name: name,
            // Ollama ignores the key, but the OpenAI client requires one.
            ApiKey: section["ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "ollama",
            Timeout: TimeSpan.FromSeconds(section.GetValue("TimeoutSeconds", 600)),
            NoThink: section.GetValue("NoThink", name.Contains("qwen3", StringComparison.OrdinalIgnoreCase)),
            Retries: section.GetValue("Retries", endpoint.IsLoopback ? 0 : 3));
    }

    public IChatClient CreateClient()
    {
        if (Provider == "scripted")
        {
            return new ScriptedPlumbingModel();
        }

        if (Provider != "openai")
        {
            throw new InvalidOperationException($"Unknown model provider '{Provider}'. Use 'openai' or 'scripted'.");
        }

        var options = new OpenAIClientOptions
        {
            Endpoint = Endpoint,
            // Local models are slow; the default 100 s timeout cuts them off mid-answer.
            NetworkTimeout = Timeout,
            // Retries back off and honour Retry-After, which hosted free tiers send when rate limiting.
            RetryPolicy = new ClientRetryPolicy(maxRetries: Retries),
        };

        IChatClient client = new OpenAIClient(new ApiKeyCredential(ApiKey), options)
            .GetChatClient(Name)
            .AsIChatClient();

        return NoThink ? new NoThinkChatClient(client) : client;
    }

    // Never print the API key.
    public override string ToString() => Provider == "scripted"
        ? "scripted (no model)"
        : $"{Name} at {Endpoint} (timeout {Timeout.TotalSeconds:0}s{(NoThink ? ", thinking off" : "")})";
}

/// <summary>
/// Adds Qwen3's <c>/no_think</c> switch to the system message so the model answers directly
/// instead of writing a long hidden chain of thought first.
/// </summary>
internal sealed class NoThinkChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private const string Switch = "/no_think";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(WithSwitch(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(WithSwitch(messages), options, cancellationToken);

    private static List<ChatMessage> WithSwitch(IEnumerable<ChatMessage> messages)
    {
        var list = messages.ToList();
        var index = list.FindIndex(m => m.Role == ChatRole.System);
        if (index < 0)
        {
            list.Insert(0, new ChatMessage(ChatRole.System, Switch));
        }
        else if (!(list[index].Text ?? "").Contains(Switch, StringComparison.Ordinal))
        {
            // Copy rather than mutate: the caller keeps its own history.
            list[index] = new ChatMessage(ChatRole.System, list[index].Text + "\n" + Switch);
        }

        return list;
    }
}
