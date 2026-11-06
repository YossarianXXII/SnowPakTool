using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SnowPakTool.Desktop;

internal sealed class StagedRankXp
{
    public string SourceText { get; set; } = "";
    public List<int> Values { get; set; } = [];
}

/// <summary>Only explicitly confirmed XP values enter this per-workspace preference store.</summary>
internal sealed class RankXpStageStore
{
    private readonly string _directory;
    public RankXpStageStore(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowPakTool.Desktop", "staged-rank-xp");

    internal string StatePath(string workspace)
    {
        var key = WorkspaceOriginals.GetWorkspaceRoot(workspace).ToUpperInvariant();
        return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
    }

    public StagedRankXp? Load(string workspace)
    {
        var path = StatePath(workspace);
        if (!File.Exists(path)) return null;
        var staged = JsonSerializer.Deserialize<StagedRankXp>(File.ReadAllText(path))
            ?? throw new JsonException("The staged rank XP settings are empty.");
        Validate(staged);
        return staged;
    }

    public void Stage(string workspace, string sourceText, IReadOnlyList<int> values)
    {
        var staged = new StagedRankXp { SourceText = sourceText, Values = [.. values] };
        Validate(staged);
        RankProgressionChanges.TryRead(sourceText, out var before);
        if (before.SequenceEqual(values)) { Clear(workspace); return; }
        var path = StatePath(workspace);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(staged, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public void Clear(string workspace) => File.Delete(StatePath(workspace));

    private static void Validate(StagedRankXp staged)
    {
        if (staged.SourceText is null || staged.Values is null || staged.Values.Count == 0 || staged.Values.Any(value => value < 0) ||
            !RankProgressionChanges.TryRead(staged.SourceText, out var baseline) || baseline.Count != staged.Values.Count)
            throw new JsonException("The staged rank XP values are invalid.");
    }
}
