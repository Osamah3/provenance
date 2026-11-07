using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
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
/// what makes local runs slow), <c>MaxRateLimitWaitSeconds</c> (default 90: a rate limit that resets
/// sooner is waited out, a longer one such as a daily cap fails the task at once), <c>Auth</c> (<c>key</c>,
/// the default, or <c>entra</c>: sign in with Microsoft Entra ID, for Azure OpenAI resources where key
/// authentication is disabled; uses your Azure CLI or Visual Studio sign-in) and <c>EntraScope</c>.
/// </remarks>
public sealed record ModelSettings(string Provider, Uri Endpoint, string Name, string ApiKey, TimeSpan Timeout, bool NoThink, TimeSpan MaxRateLimitWait, string Auth, string EntraScope)
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
            MaxRateLimitWait: TimeSpan.FromSeconds(section.GetValue("MaxRateLimitWaitSeconds", 90)),
            Auth: (section["Auth"] ?? "key").ToLowerInvariant(),
            EntraScope: section["EntraScope"] ?? "https://cognitiveservices.azure.com/.default");
    }

    /// <param name="waitForRateLimits">Wait out every rate limit, even a daily cap, instead of failing.</param>
    public IChatClient CreateClient(bool waitForRateLimits = false)
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
            // No blind retries: a timeout would only repeat a slow call, and the client's own retries
            // honour any Retry-After, waiting up to an hour on a daily cap. Rate limits are handled
            // by RateLimitChatClient instead.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };

        var chat = Auth switch
        {
            "key" => new OpenAI.Chat.ChatClient(Name, new ApiKeyCredential(ApiKey), options),
#pragma warning disable OPENAI001, SCME0002 // custom authentication policies are marked experimental
            "entra" => new OpenAI.Chat.ChatClient(Name, new BearerTokenPolicy(AzureCredential.Value, EntraScope), options),
#pragma warning restore OPENAI001, SCME0002
            _ => throw new InvalidOperationException($"Unknown Model:Auth '{Auth}'. Use 'key' or 'entra'."),
        };

        IChatClient client = new RateLimitChatClient(
            chat.AsIChatClient(),
            maxWait: waitForRateLimits ? TimeSpan.FromHours(6) : MaxRateLimitWait,
            maxAttempts: waitForRateLimits ? 200 : 5);

        return NoThink ? new NoThinkChatClient(client) : client;
    }

    // One credential for the whole process, so its token cache is shared by every task.
    private static readonly Lazy<DefaultAzureCredential> AzureCredential = new(() => new DefaultAzureCredential());

    // Never print the API key.
    public override string ToString() => Provider == "scripted"
        ? "scripted (no model)"
        : $"{Name} at {Endpoint} (auth {Auth}, timeout {Timeout.TotalSeconds:0}s{(NoThink ? ", thinking off" : "")})";
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
