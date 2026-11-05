using System.Text.Json.Serialization;

namespace Provenance.AgentDojo.Host;

/// <summary>One AgentDojo task, as sent by the Python bridge.</summary>
public sealed record RunRequest(
    [property: JsonPropertyName("mcp_url")] string McpUrl,
    [property: JsonPropertyName("suite")] string Suite,
    [property: JsonPropertyName("defended")] bool Defended,
    [property: JsonPropertyName("system_prompt")] string SystemPrompt,
    [property: JsonPropertyName("query")] string Query,
    // Wait out rate limits however long they last, instead of failing the task (bridge's --wait).
    [property: JsonPropertyName("wait_for_rate_limits")] bool WaitForRateLimits = false);

/// <summary>The agent's answer, the Provenance decisions behind it, or the error that stopped it.</summary>
public sealed record RunResponse(
    [property: JsonPropertyName("output")] string? Output,
    [property: JsonPropertyName("audit")] IReadOnlyList<AuditRecord> Audit,
    [property: JsonPropertyName("error")] string? Error);

public sealed record AuditRecord(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("violations")] IReadOnlyList<string> Violations,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("context_label")] string ContextLabel,
    [property: JsonPropertyName("hidden_as")] string? HiddenAs)
{
    public static AuditRecord From(AuditEntry entry) => new(
        entry.Tool,
        entry.Allowed,
        [.. entry.Violations.Select(v => v.ToString())],
        entry.Reason,
        entry.ContextLabel.ToString(),
        entry.HiddenAs);
}
