using System.Globalization;

namespace Provenance;

/// <summary>The shapes a typed <c>quarantined_llm</c> answer can take.</summary>
internal enum AnswerType
{
    /// <summary>Free text. Stays untrusted and hidden.</summary>
    Text,
    Boolean,
    Integer,
    Number,

    /// <summary>A calendar date, <c>YYYY-MM-DD</c>.</summary>
    Date,

    /// <summary>A date and time, <c>YYYY-MM-DD HH:MM</c>.</summary>
    DateTime,

    /// <summary>Exactly one of a list of options supplied by the planner.</summary>
    Choice,
}

/// <summary>
/// Parses a quarantined model's answer into a small typed value. A value that parses cannot carry
/// instructions: an attacker who controls the hidden data can at most pick which allowed value comes
/// back (flip a yes/no, choose a date, pick one of the planner's options), not tell the planner what
/// to do. Anything that does not parse exactly is rejected and stays hidden.
/// </summary>
internal static class TypedAnswer
{
    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"];

    public static AnswerType ParseType(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "text" => AnswerType.Text,
        "boolean" or "bool" => AnswerType.Boolean,
        "integer" or "int" => AnswerType.Integer,
        "number" => AnswerType.Number,
        "date" => AnswerType.Date,
        "datetime" => AnswerType.DateTime,
        "choice" => AnswerType.Choice,
        _ => throw new ArgumentException(
            $"Unknown output_type '{name}'. Use text, boolean, integer, number, date, datetime or choice."),
    };

    /// <summary>The instruction appended to the quarantined model's task.</summary>
    public static string Instruction(AnswerType type, IReadOnlyList<string> choices) => type switch
    {
        AnswerType.Boolean => "Answer with only the word true or the word false, and nothing else.",
        AnswerType.Integer => "Answer with only a whole number, and nothing else.",
        AnswerType.Number => "Answer with only a number, and nothing else.",
        AnswerType.Date => "Answer with only a date in YYYY-MM-DD format, and nothing else.",
        AnswerType.DateTime => "Answer with only a date and time in YYYY-MM-DD HH:MM format (24-hour), and nothing else.",
        AnswerType.Choice => "Answer with exactly one of these options, copied exactly, and nothing else:\n" +
                             string.Join("\n", choices.Select(c => "- " + c)),
        _ => string.Empty,
    };

    public static bool TryParse(string? answer, AnswerType type, IReadOnlyList<string> choices, out object? value)
    {
        value = null;
        var text = Clean(answer);
        if (text.Length == 0)
        {
            return false;
        }

        switch (type)
        {
            case AnswerType.Boolean:
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    value = true;
                    return true;
                }

                if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Equals("no", StringComparison.OrdinalIgnoreCase))
                {
                    value = false;
                    return true;
                }

                return false;

            case AnswerType.Integer when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                value = integer;
                return true;

            case AnswerType.Number when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number):
                value = number;
                return true;

            case AnswerType.Date when DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return true;

            case AnswerType.DateTime when System.DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime):
                value = dateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                return true;

            case AnswerType.Choice:
                // Return the planner's own string, never the model's copy of it.
                var match = choices.FirstOrDefault(c => string.Equals(c.Trim(), text, StringComparison.OrdinalIgnoreCase));
                value = match;
                return match is not null;

            default:
                return false;
        }
    }

    // Models often wrap a one-word answer in quotes or end it with a full stop; nothing more is forgiven.
    private static string Clean(string? answer) =>
        (answer ?? string.Empty).Trim().Trim('`', '"', '\'').Trim().TrimEnd('.').Trim();
}
