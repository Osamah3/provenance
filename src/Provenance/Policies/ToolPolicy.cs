using System.Text.Json;

namespace Provenance;

/// <summary>
/// The information-flow policy of one tool: what its output is labelled, and what it may receive.
/// </summary>
/// <remarks>
/// Policies can be passed explicitly to <see cref="ProvenanceSession.Protect(Microsoft.Extensions.AI.AIFunction, ToolPolicy?)"/>
/// or declared in a tool's <c>AdditionalProperties</c> using the same keys as FIDES:
/// <c>source_integrity</c>, <c>confidentiality</c>, <c>max_allowed_confidentiality</c> and <c>accepts_untrusted</c>.
/// </remarks>
public sealed record ToolPolicy
{
    public const string SourceIntegrityKey = "source_integrity";
    public const string ConfidentialityKey = "confidentiality";
    public const string MaxAllowedConfidentialityKey = "max_allowed_confidentiality";
    public const string AcceptsUntrustedKey = "accepts_untrusted";

    private static readonly string[] KnownKeys =
        [SourceIntegrityKey, ConfidentialityKey, MaxAllowedConfidentialityKey, AcceptsUntrustedKey];

    /// <summary>
    /// The integrity of what this tool returns. <c>null</c> means "not declared", in which case
    /// <see cref="ProvenanceOptions.DefaultIntegrity"/> (untrusted, by default) applies.
    /// </summary>
    public Integrity? SourceIntegrity { get; init; }

    /// <summary>The confidentiality of what this tool returns.</summary>
    public Confidentiality Confidentiality { get; init; } = Confidentiality.Public;

    /// <summary>
    /// The most sensitive data this tool may receive, either through its arguments or because
    /// the conversation already contains it. Set this on every tool that sends data anywhere.
    /// <c>null</c> means no cap, matching FIDES.
    /// </summary>
    public Confidentiality? MaxAllowedConfidentiality { get; init; }

    /// <summary>
    /// Whether the tool may run when the conversation, or its own arguments, are untrusted.
    /// Leave this <c>false</c> for any tool with side effects.
    /// </summary>
    public bool AcceptsUntrusted { get; init; }

    /// <summary>A tool that reads trusted, public data and sends nothing anywhere.</summary>
    public static ToolPolicy TrustedSource { get; } = new() { SourceIntegrity = Integrity.Trusted };

    /// <summary>A tool that fetches external content.</summary>
    public static ToolPolicy UntrustedSource { get; } = new() { SourceIntegrity = Integrity.Untrusted };

    /// <summary>A tool that publishes data, so it must never receive private data or act on untrusted instructions.</summary>
    public static ToolPolicy PublicSink { get; } = new()
    {
        SourceIntegrity = Integrity.Trusted,
        MaxAllowedConfidentiality = Confidentiality.Public,
    };

    /// <summary>Writes this policy as tool metadata, in the FIDES key format.</summary>
    public IReadOnlyDictionary<string, object?> ToAdditionalProperties()
    {
        var properties = new Dictionary<string, object?>
        {
            [ConfidentialityKey] = LabelWire.ToWire(Confidentiality),
            [AcceptsUntrustedKey] = AcceptsUntrusted,
        };
        if (SourceIntegrity is { } integrity)
        {
            properties[SourceIntegrityKey] = LabelWire.ToWire(integrity);
        }

        if (MaxAllowedConfidentiality is { } max)
        {
            properties[MaxAllowedConfidentialityKey] = LabelWire.ToWire(max);
        }

        return properties;
    }

    /// <summary>
    /// Parses a policy from tool metadata. Malformed values are reported in <paramref name="errors"/>;
    /// a tool whose policy has errors is always blocked, so a typo never silently weakens enforcement.
    /// </summary>
    public static ToolPolicy Parse(IReadOnlyDictionary<string, object?>? properties, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        var policy = new ToolPolicy();
        if (properties is null || properties.Count == 0)
        {
            errors = problems;
            return policy;
        }

        foreach (var key in properties.Keys)
        {
            var near = KnownKeys.FirstOrDefault(k => !string.Equals(k, key, StringComparison.Ordinal)
                && string.Equals(k.Replace("_", string.Empty), key.Replace("_", string.Empty), StringComparison.OrdinalIgnoreCase));
            if (near is not null)
            {
                problems.Add($"Unknown policy key '{key}'. Did you mean '{near}'?");
            }
        }

        if (properties.TryGetValue(SourceIntegrityKey, out var rawIntegrity))
        {
            if (TryGetString(rawIntegrity, out var text) && LabelWire.TryParseIntegrity(text, out var integrity))
            {
                policy = policy with { SourceIntegrity = integrity };
            }
            else
            {
                problems.Add($"'{SourceIntegrityKey}' must be \"trusted\" or \"untrusted\".");
            }
        }

        if (properties.TryGetValue(ConfidentialityKey, out var rawConfidentiality))
        {
            if (TryGetString(rawConfidentiality, out var text) && LabelWire.TryParseConfidentiality(text, out var confidentiality))
            {
                policy = policy with { Confidentiality = confidentiality };
            }
            else
            {
                problems.Add($"'{ConfidentialityKey}' must be \"public\", \"private\" or \"user_identity\".");
            }
        }

        if (properties.TryGetValue(MaxAllowedConfidentialityKey, out var rawMax))
        {
            if (TryGetString(rawMax, out var text) && LabelWire.TryParseConfidentiality(text, out var max))
            {
                policy = policy with { MaxAllowedConfidentiality = max };
            }
            else
            {
                problems.Add($"'{MaxAllowedConfidentialityKey}' must be \"public\", \"private\" or \"user_identity\".");
            }
        }

        if (properties.TryGetValue(AcceptsUntrustedKey, out var rawAccepts))
        {
            if (TryGetBool(rawAccepts, out var accepts))
            {
                policy = policy with { AcceptsUntrusted = accepts };
            }
            else
            {
                problems.Add($"'{AcceptsUntrustedKey}' must be a boolean.");
            }
        }

        errors = problems;
        return policy;
    }

    private static bool TryGetString(object? value, out string? text)
    {
        switch (value)
        {
            case string s:
                text = s;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                text = element.GetString();
                return true;
            default:
                text = null;
                return false;
        }
    }

    private static bool TryGetBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                result = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
