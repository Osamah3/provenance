using ModelContextProtocol.Protocol;

namespace Provenance.Mcp;

/// <summary>Turns an MCP tool's annotations and the server options into a Provenance policy.</summary>
internal static class McpToolPolicies
{
    /// <summary>
    /// Returns the policy to enforce, and the label a result gets before any per-result server label is applied.
    /// </summary>
    public static (ToolPolicy Policy, SecurityLabel ResultLabel) For(string name, Tool tool, McpProvenanceOptions options)
    {
        var hints = tool.Annotations;
        var policy = options.Policies.TryGetValue(name, out var configured)
            ? configured
            : Derive(hints, options);

        var resultLabel = new SecurityLabel(policy.SourceIntegrity ?? Integrity.Untrusted, policy.Confidentiality);

        // With trusted server labels each result carries its own label (see McpResults), so the policy must not
        // force results down to untrusted first; the result label still applies when a server sends none.
        if (options.TrustServerLabels && !options.Policies.ContainsKey(name))
        {
            policy = policy with { SourceIntegrity = Integrity.Trusted };
        }

        return (policy, resultLabel);
    }

    private static ToolPolicy Derive(ToolAnnotations? hints, McpProvenanceOptions options)
    {
        var policy = McpLabelMapping.FromAnnotations(hints?.OpenWorldHint, options.ServerIntegrity);

        if (options.AllowReadOnlyToolsInUntrustedContext && hints?.ReadOnlyHint == true && hints.OpenWorldHint == false)
        {
            policy = policy with { AcceptsUntrusted = true };
        }

        return policy;
    }
}
