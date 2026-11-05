using System.ClientModel;
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
/// <c>Endpoint</c>, <c>Name</c>, <c>ApiKey</c>.
/// </remarks>
public sealed record ModelSettings(string Provider, Uri Endpoint, string Name, string ApiKey)
{
    public static ModelSettings From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Model");
        return new ModelSettings(
            Provider: section["Provider"] ?? "openai",
            Endpoint: new Uri(section["Endpoint"] ?? "http://localhost:11434/v1"),
            Name: section["Name"] ?? "qwen3:8b",
            // Ollama ignores the key, but the OpenAI client requires one.
            ApiKey: section["ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "ollama");
    }

    public IChatClient CreateClient() => Provider switch
    {
        "scripted" => new ScriptedPlumbingModel(),
        "openai" => new OpenAIClient(new ApiKeyCredential(ApiKey), new OpenAIClientOptions { Endpoint = Endpoint })
            .GetChatClient(Name)
            .AsIChatClient(),
        _ => throw new InvalidOperationException($"Unknown model provider '{Provider}'. Use 'openai' or 'scripted'."),
    };

    // Never print the API key.
    public override string ToString() => Provider == "scripted" ? "scripted (no model)" : $"{Name} at {Endpoint}";
}
