using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Provenance.Mcp;

/// <summary>
/// Calls an MCP tool and returns its result as a <see cref="LabeledValue"/>, so per-result labels sent by the
/// server reach Provenance. <see cref="McpClientTool"/> alone would drop them.
/// </summary>
internal sealed class LabeledMcpTool : DelegatingAIFunction
{
    private readonly McpClientTool _tool;
    private readonly SecurityLabel _resultLabel;
    private readonly bool _trustServerLabels;

    public LabeledMcpTool(McpClientTool tool, SecurityLabel resultLabel, bool trustServerLabels)
        : base(tool)
    {
        _tool = tool;
        _resultLabel = resultLabel;
        _trustServerLabels = trustServerLabels;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var result = await _tool.CallAsync(arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        return McpResults.ToLabeledValue(result, _resultLabel, _trustServerLabels, _tool.JsonSerializerOptions);
    }
}
