namespace Provenance.Mcp;

/// <summary>
/// How Provenance labels the tools of one MCP server.
/// </summary>
/// <remarks>
/// The defaults treat every server as untrusted: its results are hidden from the model, its tools may not
/// receive private data, and none of them run once the conversation is untrusted. Loosen these only for
/// servers you operate and whose data you control.
/// </remarks>
public sealed class McpProvenanceOptions
{
    /// <summary>
    /// The integrity of this server's results when the tool does not say otherwise. Untrusted by default.
    /// Tools the server marks <c>openWorldHint: true</c> (they reach the internet) are always untrusted.
    /// </summary>
    public Integrity ServerIntegrity { get; init; } = Integrity.Untrusted;

    /// <summary>
    /// Use the label a server attaches to each result (<c>_meta.ifc</c>) as given. By default such a label can
    /// only make a result stricter than <see cref="ServerIntegrity"/>, never more trusted. Only for servers you operate.
    /// </summary>
    public bool TrustServerLabels { get; init; }

    /// <summary>
    /// Let tools the server marks as both read-only and closed-world (<c>readOnlyHint: true</c>,
    /// <c>openWorldHint: false</c>) run after the conversation has become untrusted.
    /// </summary>
    /// <remarks>
    /// Off by default, because annotations are written by the server. A tool that is wrongly marked read-only
    /// could act on instructions from untrusted content. Open-world tools are never allowed: even a read-only
    /// web fetch can leak data through the URL it requests.
    /// </remarks>
    public bool AllowReadOnlyToolsInUntrustedContext { get; init; }

    /// <summary>
    /// Policies by tool name. A tool listed here gets exactly this policy instead of one derived from its annotations.
    /// </summary>
    public IDictionary<string, ToolPolicy> Policies { get; init; } = new Dictionary<string, ToolPolicy>(StringComparer.Ordinal);
}
