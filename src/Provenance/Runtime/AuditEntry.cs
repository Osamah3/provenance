namespace Provenance;

/// <summary>Why a tool call was blocked.</summary>
public enum ViolationKind
{
    /// <summary>The conversation contains untrusted content and the tool does not accept it.</summary>
    UntrustedContext,

    /// <summary>The tool's arguments resolve to untrusted hidden content and the tool does not accept it.</summary>
    UntrustedArguments,

    /// <summary>The data in play is more sensitive than the tool may receive.</summary>
    Confidentiality,

    /// <summary>The tool's declared policy is malformed.</summary>
    InvalidPolicy,

    /// <summary>The enforcement layer itself failed, so the call was refused.</summary>
    EnforcementError,
}

/// <summary>One allow or deny decision, kept for audit.</summary>
public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string Tool,
    bool Allowed,
    IReadOnlyList<ViolationKind> Violations,
    string? Reason,
    SecurityLabel ContextLabel,
    SecurityLabel ArgumentLabel,
    SecurityLabel? ResultLabel,
    string? HiddenAs);
