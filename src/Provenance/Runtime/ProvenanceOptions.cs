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
    /// The tool-less model that processes hidden content for <c>quarantined_llm</c>.
    /// It must not contain a <see cref="FunctionInvokingChatClient"/>.
    /// </summary>
    public IChatClient? QuarantineClient { get; set; }
}
