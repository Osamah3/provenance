using ModelContextProtocol.Client;

namespace Provenance.Mcp;

/// <summary>
/// Protects MCP client tools with Provenance.
/// </summary>
/// <example>
/// <code>
/// var session = new ProvenanceSession(new ProvenanceOptions { QuarantineClient = quarantineModel });
/// var githubTools = await github.ListProtectedToolsAsync(session);
/// var fileTools = await files.ListProtectedToolsAsync(session, new McpProvenanceOptions { ServerIntegrity = Integrity.Trusted });
/// IList&lt;AITool&gt; tools = [.. githubTools, .. fileTools, .. session.CreateSecurityTools()];
/// </code>
/// </example>
public static class McpProvenanceExtensions
{
    /// <summary>
    /// Wraps one MCP tool. Its policy comes from <see cref="McpProvenanceOptions.Policies"/> if listed there,
    /// otherwise from the tool's annotations, and labels the server sends with each result are applied.
    /// </summary>
    public static ProtectedAIFunction ProtectMcpTool(this ProvenanceSession session, McpClientTool tool, McpProvenanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tool);
        options ??= new McpProvenanceOptions();

        var (policy, resultLabel) = McpToolPolicies.For(tool.Name, tool.ProtocolTool, options);
        return session.Protect(new LabeledMcpTool(tool, resultLabel, options.TrustServerLabels), policy);
    }

    /// <summary>Wraps several MCP tools from one server with the same options.</summary>
    public static IReadOnlyList<ProtectedAIFunction> ProtectMcpTools(
        this ProvenanceSession session, IEnumerable<McpClientTool> tools, McpProvenanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Select(tool => session.ProtectMcpTool(tool, options)).ToList();
    }

    /// <summary>Lists a server's tools and wraps them all.</summary>
    public static async Task<IReadOnlyList<ProtectedAIFunction>> ListProtectedToolsAsync(
        this McpClient client,
        ProvenanceSession session,
        McpProvenanceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return session.ProtectMcpTools(tools, options);
    }
}
