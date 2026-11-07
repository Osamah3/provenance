using Microsoft.Extensions.AI;

namespace Provenance;

/// <summary>Configuration for a <see cref="ProvenanceSession"/>.</summary>
public sealed class ProvenanceOptions
{
    /// <summary>Integrity given to results of tools that declare no <see cref="ToolPolicy.SourceIntegrity"/>. Untrusted by default.</summary>
    public Integrity DefaultIntegrity { get; set; } = Integrity.Untrusted;

    /// <summary>
    /// Cap applied to tools that declare no <see cref="ToolPolicy.MaxAllowedConfidentiality"/>.
    /// <c>null</c> (the default) means no cap, matching FIDES; set <see cref="Confidentiality.Public"/>
    /// to make every undeclared tool a public-only sink.
    /// </summary>
    public Confidentiality? DefaultMaxAllowedConfidentiality { get; set; }

    /// <summary>
    /// Hide untrusted tool results from the model behind a variable reference. On by default:
    /// this is what lets the agent keep working after reading untrusted content.
    /// </summary>
    public bool AutoHideUntrusted { get; set; } = true;

    /// <summary>Names of tools allowed to run in an untrusted context regardless of their own policy.</summary>
    public ISet<string> AllowUntrustedTools { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Stop the whole tool loop on a policy violation instead of returning the denial to the model.
    /// Enforcement errors always stop the loop, whatever this is set to.
    /// </summary>
    public bool TerminateOnViolation { get; set; }

    /// <summary>
    /// Let <c>quarantined_llm</c> return small typed answers straight to the model: true or false, a number,
    /// a date, or one of a list of options the model supplies. Off by default.
    /// </summary>
    /// <remarks>
    /// A typed answer that parses exactly is labelled trusted (keeping the confidentiality of the data it
    /// came from), so the model can act on it without making the conversation untrusted. This is what
    /// lets an agent check "am I free at noon?" and then book the meeting. The trade-off: an attacker who
    /// controls the hidden data cannot inject instructions this way, but can influence which allowed value
    /// comes back, for example flip a yes/no or choose a date.
    /// </remarks>
    public bool AllowTypedQuarantineAnswers { get; set; }

    /// <summary>
    /// The tool-less model that processes hidden content for <c>quarantined_llm</c>.
    /// It must not contain a <see cref="FunctionInvokingChatClient"/>.
    /// </summary>
    public IChatClient? QuarantineClient { get; set; }
}
