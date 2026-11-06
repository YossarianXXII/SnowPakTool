using System.Text.Json;

namespace SnowPakTool.Desktop;

internal sealed record WinchCustomPresetCollection(List<WinchCustomPreset> Presets)
{
    public static WinchCustomPresetCollection Empty => new([]);
}

internal sealed record WinchCustomPreset(string Name, List<WinchCustomPresetDefinition> Definitions);

internal sealed record WinchCustomPresetDefinition(
    string ClassCode,
    string WinchName,
    string UiName,
    string Description,
    string Length,
    string StrengthMultiplier,
    string IgnitionRequired,
    string UnlockByRank);

internal static class WinchCustomPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string PathFor(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "winches", "custom-presets.json");

    public static WinchCustomPresetCollection Load(string workspace)
    {
        var path = PathFor(workspace);
        if (!File.Exists(path)) return WinchCustomPresetCollection.Empty;
        try
        {
            var collection = JsonSerializer.Deserialize<WinchCustomPresetCollection>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("The custom winch presets file is empty or invalid.");
            if (collection.Presets is null || collection.Presets.Any(item => item is null || string.IsNullOrWhiteSpace(item.Name) || item.Definitions is null))
                throw new InvalidDataException("The custom winch presets file contains invalid entries.");
            return collection;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The custom winch presets file is invalid.", ex);
        }
    }

    public static void Save(string workspace, WinchCustomPresetCollection collection)
    {
        var path = PathFor(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(collection, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
