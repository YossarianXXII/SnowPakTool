using System.Globalization;
using System.Text.RegularExpressions;

namespace SnowPakTool.Desktop;

internal static class RankProgressionChanges
{
    public const string Property = "Rank progression (rankExp)";
    private static readonly Regex Array = new(@"\brankExp\s*=\s*\[(?<body>.*?)\]", RegexOptions.Singleline);

    public static string Summary(IReadOnlyList<int> values) =>
        $"{values.Count} steps; {values.Sum(value => (long)value).ToString("N0", CultureInfo.InvariantCulture)} XP total";

    public static WorkspaceChange? Create(string state, string target, IReadOnlyList<int> before, IReadOnlyList<int> after) =>
        before.SequenceEqual(after) ? null : new(state, target, Property, Summary(before), Summary(after));

    public static bool TryRead(string text, out List<int> values)
    {
        values = [];
        var match = Array.Match(text);
        if (!match.Success) return false;
        foreach (var token in match.Groups["body"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0) return false;
            values.Add(value);
        }
        return true;
    }

    public static string WithoutValues(string text) => Array.Replace(text, "rankExp = []", 1);
}
