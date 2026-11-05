namespace Provenance;

/// <summary>
/// A tool result that carries its own label, for tools that know more about their
/// output than their static policy does (for example, a repository reader that knows
/// whether the repository is private).
/// </summary>
/// <remarks>
/// <para>
/// An embedded label can only make the result more restrictive than the tool's
/// declared policy, never less. A tool cannot vouch for its own output.
/// </para>
/// <para>
/// <see cref="Microsoft.Extensions.AI.AIFunctionFactory"/> serialises return values to JSON by default,
/// which would strip the label. Set <see cref="Microsoft.Extensions.AI.AIFunctionFactoryOptions.MarshalResult"/>
/// to <see cref="PassThrough"/> on tools that return a <see cref="LabeledValue"/>.
/// </para>
/// </remarks>
public sealed record LabeledValue(object? Value, SecurityLabel Label)
{
    /// <summary>A <c>MarshalResult</c> delegate that returns the tool's result object unchanged.</summary>
    public static Func<object?, Type?, CancellationToken, ValueTask<object?>> PassThrough { get; } =
        static (result, _, _) => new ValueTask<object?>(result);
}
