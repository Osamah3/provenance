using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Provenance;

/// <summary>
/// The information-flow state of one conversation: the running context label, the hidden
/// variables, and the audit log. Create one session per conversation and protect that
/// conversation's tools with it.
/// </summary>
/// <example>
/// <code>
/// var session = new ProvenanceSession(new ProvenanceOptions { QuarantineClient = quarantineModel });
/// IList&lt;AITool&gt; tools =
/// [
///     session.Protect(readIssue, ToolPolicy.UntrustedSource),
///     session.Protect(postComment, ToolPolicy.PublicSink),
///     .. session.CreateSecurityTools(),
/// ];
/// IChatClient agent = new ChatClientBuilder(model).UseProvenance(session).UseFunctionInvocation().Build();
/// </code>
/// </example>
public sealed partial class ProvenanceSession
{
    private readonly Lock _gate = new();
    private readonly List<AuditEntry> _audit = [];
    private SecurityLabel _context = SecurityLabel.TrustedPublic;

    public ProvenanceSession(ProvenanceOptions? options = null)
    {
        Options = options ?? new ProvenanceOptions();
        if (Options.QuarantineClient?.GetService<FunctionInvokingChatClient>() is not null)
        {
            throw new ProvenanceConfigurationException(
                "The quarantine client contains a FunctionInvokingChatClient. It must be tool-less, " +
                "or untrusted content could trigger tool calls.");
        }
    }

    public ProvenanceOptions Options { get; }

    public VariableStore Variables { get; } = new();

    /// <summary>The combined label of everything the model has seen so far in this conversation.</summary>
    public SecurityLabel ContextLabel
    {
        get
        {
            lock (_gate)
            {
                return _context;
            }
        }
    }

    public IReadOnlyList<AuditEntry> AuditLog
    {
        get
        {
            lock (_gate)
            {
                return [.. _audit];
            }
        }
    }

    /// <summary>
    /// Records that content with <paramref name="label"/> entered the model's context by some
    /// route other than a protected tool, for example an untrusted document pasted into a user message.
    /// </summary>
    public void Taint(SecurityLabel label)
    {
        lock (_gate)
        {
            _context = _context.Join(label);
        }
    }

    /// <summary>
    /// Wraps a tool so every call is checked against its policy and every result is labelled.
    /// When <paramref name="policy"/> is <c>null</c>, the policy is read from the tool's
    /// <c>AdditionalProperties</c>; an explicit policy always wins.
    /// </summary>
    public ProtectedAIFunction Protect(AIFunction function, ToolPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (function is ProtectedAIFunction)
        {
            throw new ProvenanceConfigurationException($"Tool '{function.Name}' is already protected.");
        }

        IReadOnlyList<string> errors = [];
        policy ??= ToolPolicy.Parse(function.AdditionalProperties, out errors);
        return new ProtectedAIFunction(function, this, policy, errors, ProtectedAIFunction.Mode.Standard);
    }

    /// <summary>
    /// Creates the security tools the model uses to work with hidden content:
    /// <c>quarantined_llm</c> (requires <see cref="ProvenanceOptions.QuarantineClient"/>) and <c>inspect_variable</c>.
    /// </summary>
    public IReadOnlyList<AITool> CreateSecurityTools() => SecurityTools.Create(this);

    /// <summary>
    /// Replaces variable ids in text meant for a human (not a model) with the hidden content.
    /// Use this on the agent's final answer before displaying it.
    /// </summary>
    public string RenderForUser(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return VariableIdPattern().Replace(text, match =>
            Variables.TryGet(match.Value, out var variable) ? variable.Value?.ToString() ?? string.Empty : match.Value);
    }

    internal SecurityLabel JoinContext(SecurityLabel label)
    {
        lock (_gate)
        {
            _context = _context.Join(label);
            return _context;
        }
    }

    internal void Record(AuditEntry entry)
    {
        lock (_gate)
        {
            _audit.Add(entry);
        }
    }

    [GeneratedRegex("var_[0-9a-f]{16}")]
    private static partial Regex VariableIdPattern();
}

/// <summary>Thrown when Provenance is set up in a way that would let tools run unchecked.</summary>
public sealed class ProvenanceConfigurationException(string message) : InvalidOperationException(message);
