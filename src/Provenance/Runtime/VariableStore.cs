using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Provenance;

/// <summary>A value hidden from the model, with the label it carried.</summary>
public sealed record HiddenVariable(string Id, object? Value, SecurityLabel Label, string SourceTool);

/// <summary>
/// Holds untrusted content outside the model's context. The model only ever sees the variable id.
/// </summary>
public sealed class VariableStore
{
    /// <summary>Prefix of every variable id, matching FIDES.</summary>
    public const string Prefix = "var_";

    private readonly ConcurrentDictionary<string, HiddenVariable> _variables = new(StringComparer.Ordinal);

    public int Count => _variables.Count;

    public IReadOnlyCollection<HiddenVariable> All => [.. _variables.Values];

    public HiddenVariable Store(object? value, SecurityLabel label, string sourceTool)
    {
        // 64 bits of randomness: ids must not be guessable by injected content.
        var id = Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var variable = new HiddenVariable(id, value, label, sourceTool);
        _variables[id] = variable;
        return variable;
    }

    public bool TryGet(string? id, [NotNullWhen(true)] out HiddenVariable? variable)
    {
        variable = null;
        return id is not null
            && id.StartsWith(Prefix, StringComparison.Ordinal)
            && _variables.TryGetValue(id, out variable);
    }
}
