using System.Text;

namespace SnowPakTool.Desktop;

/// <summary>Loads SnowRunner .str files, which store one key/value pair per line.</summary>
internal static class LocalizedStrings
{
    public static Dictionary<string, string> Load(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (!TryParseLine(line, out var key, out var value)) continue;
            result[key] = value;
        }
        return result;
    }

    private static bool TryParseLine(string line, out string key, out string value)
    {
        key = value = string.Empty;
        var i = 0;
        SkipWhitespace(line, ref i);
        if (i == line.Length) return false;

        key = ReadToken(line, ref i);
        if (key.Length == 0) return false;
        SkipWhitespace(line, ref i);
        if (i >= line.Length || line[i] != '"') return false;
        i++;

        var decoded = new StringBuilder();
        while (i < line.Length)
        {
            var ch = line[i++];
            if (ch == '"')
            {
                value = decoded.ToString();
                return true;
            }
            if (ch == '\\' && i < line.Length)
            {
                ch = line[i++];
                decoded.Append(ch switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => ch });
            }
            else decoded.Append(ch);
        }
        return false;
    }

    private static string ReadToken(string line, ref int i)
    {
        var start = i;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        return line[start..i];
    }

    private static void SkipWhitespace(string line, ref int i)
    {
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
    }
}
