using System.Text.Json;

namespace SnowPakTool.Desktop;

internal sealed record PendingWinchChanges(
    List<PendingWinchTruckChange> Trucks,
    List<PendingWinchDefinitionChange> Definitions)
{
    public static PendingWinchChanges Empty => new([], []);
}

internal sealed record PendingWinchTruckChange(string RelativeFile, string TruckName, string[] Types);

internal sealed record PendingWinchDefinitionChange(
    string ClassCode,
    string WinchName,
    string UiNameKey,
    string UiName,
    string Length,
    string StrengthMultiplier,
    string IgnitionRequired,
    string UnlockByRank,
    string UiDescriptionKey = "",
    string Description = "");

/// <summary>Persists winch edit intent until Start applies it to the workspace's updated files.</summary>
internal static class PendingWinchChangesStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string PathFor(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "winches", "pending-changes.json");

    public static PendingWinchChanges Load(string workspace)
    {
        var path = PathFor(workspace);
        if (!File.Exists(path)) return PendingWinchChanges.Empty;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            var changes = JsonSerializer.Deserialize<PendingWinchChanges>(input, JsonOptions)
                ?? throw new InvalidDataException("The pending winch changes file is empty or invalid.");
            if (changes.Trucks is null || changes.Definitions is null)
                throw new InvalidDataException("The pending winch changes file is missing required data.");
            return changes;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The pending winch changes file is invalid.", ex);
        }
    }

    public static void Save(string workspace, PendingWinchChanges changes)
    {
        lock (Gate)
        {
            var path = PathFor(workspace);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(changes, JsonOptions));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
