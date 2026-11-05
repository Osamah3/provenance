using System.Text.Json;

namespace Provenance.Tests;

public sealed class LabelAndPolicyTests
{
    [Fact]
    public void Combining_keeps_the_most_restrictive_value_of_each_dimension()
    {
        var combined = new SecurityLabel(Integrity.Trusted, Confidentiality.Private)
            .Join(new SecurityLabel(Integrity.Untrusted, Confidentiality.Public));

        Assert.Equal(new SecurityLabel(Integrity.Untrusted, Confidentiality.Private), combined);
    }

    [Fact]
    public void Combining_nothing_is_trusted_public()
    {
        Assert.Equal(SecurityLabel.TrustedPublic, SecurityLabel.Combine([]));
    }

    [Fact]
    public void A_default_label_fails_closed()
    {
        Assert.False(default(SecurityLabel).IsTrusted);
    }

    [Fact]
    public void Labels_round_trip_through_the_fides_wire_format()
    {
        var label = new SecurityLabel(Integrity.Untrusted, Confidentiality.UserIdentity);
        using var document = JsonDocument.Parse(LabelWire.ToJson(label).ToJsonString());

        Assert.True(LabelWire.TryRead(document.RootElement, out var parsed));
        Assert.Equal(label, parsed);
        Assert.Equal("untrusted", document.RootElement.GetProperty("integrity").GetString());
        Assert.Equal("user_identity", document.RootElement.GetProperty("confidentiality").GetString());
    }

    [Theory]
    [InlineData("""{"integrity":"trusted"}""")]
    [InlineData("""{"integrity":"mostly","confidentiality":"public"}""")]
    [InlineData("""{"integrity":"trusted","confidentiality":"secret"}""")]
    [InlineData("""["trusted","public"]""")]
    public void Incomplete_or_unknown_labels_are_rejected(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.False(LabelWire.TryRead(document.RootElement, out _));
    }

    [Fact]
    public void Github_mcp_server_ifc_metadata_is_read()
    {
        using var document = JsonDocument.Parse("""{"ifc":{"integrity":"untrusted","confidentiality":"private"}}""");

        Assert.True(LabelWire.TryReadIfcMeta(document.RootElement, out var label));
        Assert.Equal(new SecurityLabel(Integrity.Untrusted, Confidentiality.Private), label);
    }

    [Fact]
    public void A_server_label_can_only_restrict_unless_trusted_locally()
    {
        var local = new SecurityLabel(Integrity.Untrusted, Confidentiality.Private);
        var server = SecurityLabel.TrustedPublic;

        Assert.Equal(local, McpLabelMapping.ApplyServerLabel(local, server));
        Assert.Equal(server, McpLabelMapping.ApplyServerLabel(local, server, trustServerLabel: true));
    }

    [Fact]
    public void Open_world_mcp_tools_are_untrusted_public_sinks()
    {
        var policy = McpLabelMapping.FromAnnotations(openWorldHint: true, defaultIntegrity: Integrity.Trusted);

        Assert.Equal(Integrity.Untrusted, policy.SourceIntegrity);
        Assert.Equal(Confidentiality.Public, policy.MaxAllowedConfidentiality);
        Assert.False(policy.AcceptsUntrusted);
    }

    [Fact]
    public void Policies_parse_from_fides_style_tool_metadata()
    {
        var policy = ToolPolicy.Parse(
            new Dictionary<string, object?>
            {
                ["source_integrity"] = "trusted",
                ["confidentiality"] = "private",
                ["max_allowed_confidentiality"] = "public",
                ["accepts_untrusted"] = true,
            },
            out var errors);

        Assert.Empty(errors);
        Assert.Equal(new ToolPolicy
        {
            SourceIntegrity = Integrity.Trusted,
            Confidentiality = Confidentiality.Private,
            MaxAllowedConfidentiality = Confidentiality.Public,
            AcceptsUntrusted = true,
        }, policy);
    }

    [Fact]
    public void Policy_round_trips_through_tool_metadata()
    {
        var parsed = ToolPolicy.Parse(ToolPolicy.PublicSink.ToAdditionalProperties(), out var errors);

        Assert.Empty(errors);
        Assert.Equal(ToolPolicy.PublicSink, parsed);
    }

    [Theory]
    [InlineData("accepts_untrusted", "yes")]
    [InlineData("source_integrity", "trusted-ish")]
    [InlineData("acceptsUntrusted", true)]
    public void Malformed_policy_metadata_is_reported(string key, object value)
    {
        ToolPolicy.Parse(new Dictionary<string, object?> { [key] = value }, out var errors);

        Assert.NotEmpty(errors);
    }
}
