using System.Text;
using System.Text.RegularExpressions;

namespace SnowPakTool.Desktop;

/// <summary>Reads the nested object/array syntax used by extracted game settings.</summary>
internal sealed class SsoValue
{
    public string Key { get; set; } = "";
    public string Text { get; init; } = "";
    public int ValueStart { get; set; }
    public int ValueLength { get; set; }
    public Dictionary<string, SsoValue> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SsoValue> Items { get; } = [];
    public string Get(string key) => Members.TryGetValue(key, out var value) ? value.Text : "";
    public IEnumerable<SsoValue> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Members.Values.Concat(Items))
            foreach (var node in child.DescendantsAndSelf()) yield return node;
    }
}

internal static class SsoDataReader
{
    private static readonly Regex Tokens = new("\"(?:\\\\.|[^\"\\\\])*\"|//[^\\r\\n]*|[{}\\[\\]=,]|[^\\s{}\\[\\]=,]+", RegexOptions.Compiled);

    public static SsoValue Parse(string text)
    {
        var matches = Tokens.Matches(text).Where(match => !match.Value.StartsWith("//")).ToArray();
        var tokens = matches.Select(match => match.Value).ToArray();
        var position = 0;
        var root = new SsoValue();
        ReadMembers(root, null, 0);
        return root;

        void ReadMembers(SsoValue value, string? end, int depth)
        {
            if (depth > 128) throw new FormatException("Game settings are nested too deeply.");
            while (position < tokens.Length && tokens[position] != end)
            {
                if (tokens[position] == ",") { position++; continue; }
                var key = Decode(tokens[position++]);
                Require("=");
                var child = ReadValue(depth + 1);
                child.Key = key;
                value.Members[key] = child;
            }
            if (end is not null) Require(end);
        }

        SsoValue ReadValue(int depth)
        {
            if (position >= tokens.Length) throw new FormatException("Missing game setting value.");
            var start = matches[position].Index;
            var token = tokens[position++];
            var value = new SsoValue();
            if (token == "{") ReadMembers(value, "}", depth);
            else if (token == "[")
            {
                if (depth > 128) throw new FormatException("Game settings are nested too deeply.");
                while (position < tokens.Length && tokens[position] != "]")
                {
                    if (tokens[position] == ",") { position++; continue; }
                    value.Items.Add(ReadValue(depth + 1));
                }
                Require("]");
            }
            else value = new SsoValue { Text = Decode(token) };
            value.ValueStart = start;
            value.ValueLength = matches[position - 1].Index + matches[position - 1].Length - start;
            return value;
        }

        void Require(string token)
        {
            if (position >= tokens.Length || tokens[position++] != token) throw new FormatException($"Expected '{token}' in game settings.");
        }
    }

    private static string Decode(string token)
    {
        if (!token.StartsWith('"')) return token;
        var result = new StringBuilder();
        for (var i = 1; i < token.Length - 1; i++)
        {
            var ch = token[i];
            if (ch == '\\' && i + 1 < token.Length - 1)
                ch = token[++i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var other => other };
            result.Append(ch);
        }
        return result.ToString();
    }
}
