using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Provenance;

/// <summary>
/// Refuses any request that offers the model a tool not protected by this client's session.
/// </summary>
/// <remarks>
/// Without this check, a single unwrapped tool, or a tool wrapped by a different conversation's
/// session, would run with no information-flow checks at all. The check fails closed: the request
/// is never sent to the model.
/// </remarks>
public sealed class ProvenanceGuardChatClient : DelegatingChatClient
{
    private readonly ProvenanceSession _session;

    public ProvenanceGuardChatClient(IChatClient innerClient, ProvenanceSession session)
        : base(innerClient)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Validate(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate(options);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private void Validate(ChatOptions? options)
    {
        // Tools the model can call: those offered in the request, plus any the tool loop
        // below this client will execute on request even though they are not advertised.
        var tools = new List<AITool>();
        if (options?.Tools is { } offered)
        {
            tools.AddRange(offered);
        }

        if (InnerClient.GetService<FunctionInvokingChatClient>()?.AdditionalTools is { } additional)
        {
            tools.AddRange(additional);
        }

        if (tools.Count == 0)
        {
            return;
        }

        var unprotected = tools
            .Where(tool => tool is not ProtectedAIFunction protectedTool || !ReferenceEquals(protectedTool.Session, _session))
            .Select(tool => tool is ProtectedAIFunction ? $"{tool.Name} (protected by a different session)" : tool.Name)
            .ToList();

        if (unprotected.Count > 0)
        {
            throw new ProvenanceConfigurationException(
                "These tools are not protected by this conversation's Provenance session, so they would run unchecked: " +
                string.Join(", ", unprotected) + ". Wrap each one with ProvenanceSession.Protect. " +
                "Service-hosted tools cannot be checked and are not supported.");
        }
    }
}

/// <summary>Registration helpers for <see cref="ChatClientBuilder"/>.</summary>
public static class ProvenanceChatClientBuilderExtensions
{
    /// <summary>
    /// Adds the Provenance guard. Call this before <c>UseFunctionInvocation()</c>, so the guard sits
    /// outside the tool loop and checks every tool the loop could execute before the conversation starts.
    /// </summary>
    public static ChatClientBuilder UseProvenance(this ChatClientBuilder builder, ProvenanceSession session)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(session);
        return builder.Use(inner => new ProvenanceGuardChatClient(inner, session));
    }
}
