using System.Text.Json;

namespace Provenance.AgentDojo.Host;

/// <summary>
/// The Provenance policy for each AgentDojo tool, one JSON file per suite (<c>policies/&lt;suite&gt;.json</c>).
/// Tools a file doesn't list get its <c>default</c> policy.
/// </summary>
public sealed class SuitePolicies
{
    private readonly Dictionary<string, SuitePolicy> _suites;

    private SuitePolicies(Dictionary<string, SuitePolicy> suites) => _suites = suites;

    public IEnumerable<string> Suites => _suites.Keys;

    public static SuitePolicies Load(string directory)
    {
        var suites = new Dictionary<string, SuitePolicy>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                suites[Path.GetFileNameWithoutExtension(file)] = SuitePolicy.Parse(File.ReadAllText(file), file);
            }
        }

        return new SuitePolicies(suites);
    }

    public SuitePolicy For(string suite) =>
        _suites.TryGetValue(suite, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No Provenance policy file for suite '{suite}'. Add policies/{suite}.json; refusing to guess tool policies.");
}

public sealed class SuitePolicy(ToolPolicy defaultPolicy, IReadOnlyDictionary<string, ToolPolicy> tools)
{
    public ToolPolicy For(string tool) => tools.TryGetValue(tool, out var policy) ? policy : defaultPolicy;

    public static SuitePolicy Parse(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var defaultPolicy = root.TryGetProperty("default", out var d) ? ParseOne(d, $"{source}: default") : new ToolPolicy();
        var tools = new Dictionary<string, ToolPolicy>(StringComparer.Ordinal);
        if (root.TryGetProperty("tools", out var toolsElement))
        {
            foreach (var tool in toolsElement.EnumerateObject())
            {
                tools[tool.Name] = ParseOne(tool.Value, $"{source}: {tool.Name}");
            }
        }

        return new SuitePolicy(defaultPolicy, tools);
    }

    private static ToolPolicy ParseOne(JsonElement element, string where)
    {
        var properties = element.EnumerateObject()
            .Where(p => !p.Name.StartsWith('_')) // "_why" and other underscore keys are comments
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
        var policy = ToolPolicy.Parse(properties, out var errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid policy in {where}: {string.Join(" ", errors)}");
        }

        return policy;
    }
}
