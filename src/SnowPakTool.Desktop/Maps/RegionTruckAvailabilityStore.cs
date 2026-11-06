using System.Text.Json;

namespace SnowPakTool.Desktop;

/// <summary>User resolutions for individual map placements, kept independently of game/workspace files.</summary>
internal sealed class RegionTruckAvailabilityStore
{
    internal static readonly string[] Choices = ["Unverified", "Obtainable — discovery/start", "Obtainable — task reward", "Mission/task only"];
    internal sealed record Selection(string Availability, DateTimeOffset SavedAt);
    private readonly string _path;
    private Dictionary<string, Selection> _selections;

    public RegionTruckAvailabilityStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnowPakTool.Desktop", "region-truck-availability.json");
        try
        {
            var saved = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, Selection>>(File.ReadAllText(_path)) : null;
            _selections = new(StringComparer.OrdinalIgnoreCase);
            if (saved is not null)
                foreach (var (key, value) in saved)
                    if (value is not null && Choices.Contains(value.Availability, StringComparer.Ordinal)) _selections[key] = value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _selections = new(StringComparer.OrdinalIgnoreCase); }
    }

    public void Set(string map, string placementId, string availability)
    {
        if (string.IsNullOrWhiteSpace(map) || string.IsNullOrWhiteSpace(placementId) || !Choices.Contains(availability, StringComparer.Ordinal))
            throw new ArgumentException("Choose a valid availability for a named map placement.");
        var updated = new Dictionary<string, Selection>(_selections, StringComparer.OrdinalIgnoreCase)
        { [Key(map, placementId)] = new(availability, DateTimeOffset.UtcNow) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
            _selections = updated;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public RegionTruckEntry Apply(RegionTruckEntry entry)
    {
        // The 17 already-confirmed mission placements are not editable unknowns.
        if (RegionTruckPlacementOverrides.GetMissionOnlyReason(entry.MapCode, entry.PlacementId) is not null)
            return entry with { AvailabilityEditable = false };
        if (!_selections.TryGetValue(Key(entry.MapCode, entry.PlacementId), out var selection))
            return entry with { AvailabilityEditable = entry.Availability == "Unverified" && entry.PlacementId.Length > 0 };
        const string separator = "\r\nUnderlying scan: ";
        var index = entry.Evidence.IndexOf(separator, StringComparison.Ordinal);
        var evidence = index < 0 ? entry.Evidence : entry.Evidence[(index + separator.Length)..];
        return entry with
        {
            Availability = selection.Availability,
            AvailabilityEditable = true,
            Evidence = $"User-selected availability: {selection.Availability} (saved {selection.SavedAt.ToLocalTime():yyyy-MM-dd})." + separator + evidence
        };
    }

    private static string Key(string map, string placementId) => map + "|" + placementId;
}
