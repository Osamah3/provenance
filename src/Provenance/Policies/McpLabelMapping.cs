namespace Provenance;

/// <summary>
/// Derives a conservative policy for a remote MCP tool from its <c>ToolAnnotations</c> hints.
/// </summary>
/// <remarks>
/// Annotations are written by the server, so they are treated as hints that can only make
/// the local policy stricter. They never grant trust, never lift the public-only cap on what
/// the tool may receive, and never let it run in an untrusted context. This mirrors FIDES.
/// </remarks>
public static class McpLabelMapping
{
    /// <summary>
    /// Builds a policy for an MCP tool.
    /// </summary>
    /// <param name="openWorldHint">The tool's <c>openWorldHint</c> annotation, if the server sent one.</param>
    /// <param name="defaultIntegrity">The locally configured integrity for this server's results.</param>
    public static ToolPolicy FromAnnotations(bool? openWorldHint, Integrity defaultIntegrity = Integrity.Untrusted) => new()
    {
        SourceIntegrity = openWorldHint == true ? Integrity.Untrusted : defaultIntegrity,
        MaxAllowedConfidentiality = Confidentiality.Public,
        AcceptsUntrusted = false,
    };

    /// <summary>
    /// Combines the label a server attached to one result (<c>_meta.ifc</c>) with the local label.
    /// By default the server label can only restrict; set <paramref name="trustServerLabel"/> only for
    /// servers you operate yourself.
    /// </summary>
    public static SecurityLabel ApplyServerLabel(SecurityLabel localLabel, SecurityLabel? serverLabel, bool trustServerLabel = false)
    {
        if (serverLabel is not { } server)
        {
            return localLabel;
        }

        return trustServerLabel ? server : localLabel.Join(server);
    }
}
