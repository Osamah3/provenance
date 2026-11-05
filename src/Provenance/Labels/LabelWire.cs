using System.Text.Json;
using System.Text.Json.Nodes;

namespace Provenance;

/// <summary>
/// Reads and writes labels in the wire format shared with FIDES (Python) and
/// IFC-aware MCP servers such as GitHub's: <c>{"integrity": "untrusted", "confidentiality": "private"}</c>.
/// </summary>
public static class LabelWire
{
    /// <summary>Property name used for a label on content, matching FIDES.</summary>
    public const string SecurityLabelKey = "security_label";

    public static string ToWire(Integrity integrity) => integrity switch
    {
        Integrity.Trusted => "trusted",
        Integrity.Untrusted => "untrusted",
        _ => throw new ArgumentOutOfRangeException(nameof(integrity)),
    };

    public static string ToWire(Confidentiality confidentiality) => confidentiality switch
    {
        Confidentiality.Public => "public",
        Confidentiality.Private => "private",
        Confidentiality.UserIdentity => "user_identity",
        _ => throw new ArgumentOutOfRangeException(nameof(confidentiality)),
    };

    public static bool TryParseIntegrity(string? value, out Integrity integrity)
    {
        switch (value)
        {
            case "trusted": integrity = Integrity.Trusted; return true;
            case "untrusted": integrity = Integrity.Untrusted; return true;
            default: integrity = Integrity.Untrusted; return false;
        }
    }

    public static bool TryParseConfidentiality(string? value, out Confidentiality confidentiality)
    {
        switch (value)
        {
            case "public": confidentiality = Confidentiality.Public; return true;
            case "private": confidentiality = Confidentiality.Private; return true;
            case "user_identity": confidentiality = Confidentiality.UserIdentity; return true;
            default: confidentiality = Confidentiality.UserIdentity; return false;
        }
    }

    /// <summary>Serialises a label to its wire object.</summary>
    public static JsonObject ToJson(SecurityLabel label) => new()
    {
        ["integrity"] = ToWire(label.Integrity),
        ["confidentiality"] = ToWire(label.Confidentiality),
    };

    /// <summary>
    /// Reads a label from its wire object. Both fields are required and must use
    /// known values; anything else is rejected so callers can fall back to a stricter label.
    /// </summary>
    public static bool TryRead(JsonElement element, out SecurityLabel label)
    {
        label = default;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("integrity", out var integrityElement)
            || !element.TryGetProperty("confidentiality", out var confidentialityElement)
            || integrityElement.ValueKind != JsonValueKind.String
            || confidentialityElement.ValueKind != JsonValueKind.String
            || !TryParseIntegrity(integrityElement.GetString(), out var integrity)
            || !TryParseConfidentiality(confidentialityElement.GetString(), out var confidentiality))
        {
            return false;
        }

        label = new SecurityLabel(integrity, confidentiality);
        return true;
    }

    /// <summary>
    /// Reads the per-result label an IFC-aware MCP server puts in a result's <c>_meta</c>,
    /// for example GitHub's MCP server: <c>{"ifc": {"integrity": "untrusted", "confidentiality": "public"}}</c>.
    /// </summary>
    public static bool TryReadIfcMeta(JsonElement meta, out SecurityLabel label)
    {
        label = default;
        return meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("ifc", out var ifc)
            && TryRead(ifc, out label);
    }
}
