using System.Text.Json;

namespace SnowPakTool.Desktop;

/// <summary>Shared owned-region checkbox state used by Owned Content and Experience / Ranks.</summary>
internal sealed class OwnedRegionState
{
    private readonly Dictionary<string, bool> _owned;
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop",
        "owned-regions.json");

    public event Action<string, bool>? Changed;

    public OwnedRegionState()
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

    public bool IsOwned(string regionCode) => _owned.GetValueOrDefault(regionCode);

    public void SetOwned(string regionCode, bool isOwned)
    {
        if (_owned.TryGetValue(regionCode, out var current) && current == isOwned) return;
        _owned[regionCode] = isOwned;
        Save();
        Changed?.Invoke(regionCode, isOwned);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(_owned));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ownership is a convenience preference; keep the app usable if it cannot be saved.
        }
    }
}
