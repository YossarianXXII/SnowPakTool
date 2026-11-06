using System.Text.Json;

namespace SnowPakTool.Desktop;

internal sealed class OwnedDlcState
{
    private readonly Dictionary<string, bool> _owned;
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop",
        "owned-dlcs.json");

    public OwnedDlcState()
    {
        try
        {
            var loaded = File.Exists(StateFile)
                ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(StateFile))
                : null;
            _owned = loaded is null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _owned = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool IsOwned(string dlcCode) => _owned.GetValueOrDefault(dlcCode);

    public void SetOwned(string dlcCode, bool isOwned)
    {
        if (_owned.TryGetValue(dlcCode, out var current) && current == isOwned) return;
        _owned[dlcCode] = isOwned;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(_owned));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the current session usable if the preference file cannot be written.
        }
    }
}
