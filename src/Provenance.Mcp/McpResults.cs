using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Provenance.Mcp;

/// <summary>Converts an MCP tool result into a value for the model, labelled for Provenance.</summary>
internal static class McpResults
{
    public static LabeledValue ToLabeledValue(CallToolResult result, SecurityLabel resultLabel, bool trustServerLabels, JsonSerializerOptions? options)
    {
        var label = McpLabelMapping.ApplyServerLabel(resultLabel, ReadServerLabel(result), trustServerLabels);
        return new LabeledValue(ToValue(result, options), label);
    }

    /// <summary>The label in <c>_meta.ifc</c>, if any. A malformed label fails closed as untrusted.</summary>
    internal static SecurityLabel? ReadServerLabel(CallToolResult result)
    {
        if (result.Meta is not { } meta || !meta.ContainsKey("ifc"))
        {
            return null;
        }

        using var document = JsonDocument.Parse(meta.ToJsonString());
        return LabelWire.TryReadIfcMeta(document.RootElement, out var label) ? label : SecurityLabel.UntrustedPublic;
    }

    /// <summary>
    /// Plain text for text-only results, so hidden-variable expansion and the model see a string; AI content
    /// for other content; the whole result as JSON for errors and structured results, so nothing is lost.
    /// </summary>
    internal static object? ToValue(CallToolResult result, JsonSerializerOptions? options)
    {
        if (result.IsError is not true && result.StructuredContent is null)
        {
            if (result.Content.All(static c => c is TextContentBlock))
            {
                return string.Join("\n", result.Content.Cast<TextContentBlock>().Select(static t => t.Text));
            }

            IList<AIContent> contents = result.Content.ToAIContents(options);
            return contents.Count == 1 ? contents[0] : contents;
        }

        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
    }
}
