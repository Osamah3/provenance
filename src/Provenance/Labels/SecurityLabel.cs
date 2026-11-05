namespace Provenance;

/// <summary>
/// Whether content may influence what the agent does.
/// </summary>
/// <remarks>
/// <see cref="Untrusted"/> is deliberately the zero value, so a default or
/// uninitialised label fails closed rather than open.
/// </remarks>
public enum Integrity
{
    /// <summary>Content from a source you do not control: issue bodies, emails, web pages, third-party APIs.</summary>
    Untrusted = 0,

    /// <summary>Developer-controlled content: system prompts, the user's own request, internal data.</summary>
    Trusted = 1,
}

/// <summary>
/// How sensitive content is, ordered from least to most sensitive.
/// </summary>
public enum Confidentiality
{
    /// <summary>Safe to share publicly.</summary>
    Public = 0,

    /// <summary>Must not leave private destinations.</summary>
    Private = 1,

    /// <summary>Restricted to specific user identities.</summary>
    UserIdentity = 2,
}

/// <summary>
/// An information-flow label: the integrity and confidentiality of a piece of content.
/// </summary>
/// <remarks>
/// Labels form a lattice. Combining two labels always keeps the most restrictive
/// value of each dimension: untrusted beats trusted, and the higher
/// confidentiality wins. This matches FIDES in Microsoft Agent Framework (Python),
/// so labels are interchangeable on the wire.
/// </remarks>
public readonly record struct SecurityLabel(Integrity Integrity, Confidentiality Confidentiality)
{
    /// <summary>The label of developer-controlled, shareable content.</summary>
    public static SecurityLabel TrustedPublic { get; } = new(Integrity.Trusted, Confidentiality.Public);

    /// <summary>The label of external content that is not sensitive.</summary>
    public static SecurityLabel UntrustedPublic { get; } = new(Integrity.Untrusted, Confidentiality.Public);

    public bool IsTrusted => Integrity == Integrity.Trusted;

    /// <summary>Combines two labels, keeping the most restrictive value of each dimension.</summary>
    public SecurityLabel Join(SecurityLabel other) => new(
        (Integrity)Math.Min((int)Integrity, (int)other.Integrity),
        (Confidentiality)Math.Max((int)Confidentiality, (int)other.Confidentiality));

    /// <summary>Combines any number of labels. With no labels, returns <see cref="TrustedPublic"/>.</summary>
    public static SecurityLabel Combine(IEnumerable<SecurityLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var result = TrustedPublic;
        foreach (var label in labels)
        {
            result = result.Join(label);
        }

        return result;
    }

    /// <summary>
    /// Whether content with this label may flow to a destination that accepts at most <paramref name="maxAllowed"/>.
    /// </summary>
    public bool FitsWithin(Confidentiality maxAllowed) => (int)Confidentiality <= (int)maxAllowed;

    public override string ToString() => $"{LabelWire.ToWire(Integrity)}/{LabelWire.ToWire(Confidentiality)}";
}
