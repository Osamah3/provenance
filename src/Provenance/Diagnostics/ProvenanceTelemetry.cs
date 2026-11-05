using System.Diagnostics;

namespace Provenance;

/// <summary>
/// OpenTelemetry integration. Add <see cref="SourceName"/> to your tracer provider
/// (<c>.AddSource("Provenance")</c>) to record one span per policy decision.
/// </summary>
public static class ProvenanceTelemetry
{
    public const string SourceName = "Provenance";

    internal static readonly ActivitySource Source = new(SourceName);

    internal static void Tag(Activity? activity, AuditEntry entry)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("provenance.tool", entry.Tool);
        activity.SetTag("provenance.decision", entry.Allowed ? "allow" : "deny");
        activity.SetTag("provenance.context_label", entry.ContextLabel.ToString());
        activity.SetTag("provenance.argument_label", entry.ArgumentLabel.ToString());
        if (entry.ResultLabel is { } result)
        {
            activity.SetTag("provenance.result_label", result.ToString());
        }

        if (entry.HiddenAs is not null)
        {
            activity.SetTag("provenance.hidden_as", entry.HiddenAs);
        }

        if (entry.Violations.Count > 0)
        {
            activity.SetTag("provenance.violations", string.Join(",", entry.Violations));
            activity.SetStatus(ActivityStatusCode.Error, entry.Reason);
        }
    }
}
