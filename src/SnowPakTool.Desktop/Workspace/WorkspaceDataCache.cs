using System.Text;
using System.Collections.Concurrent;

namespace SnowPakTool.Desktop;

/// <summary>Indexes and warms the active workspace once so tabs share the same file snapshot.</summary>
internal static class WorkspaceDataCache
{
    private static readonly object Gate = new();
    private static string? _workspace;
    private static Task? _loadTask;
    private static TaskCompletionSource<bool> _ownedReady = NewReadySignal();
    private static volatile WorkspaceSnapshot? _snapshot;
    private static long _contentVersion;
    public static long ContentVersion => Interlocked.Read(ref _contentVersion);

    public static Task PreloadAsync(string workspace, bool forceReload = false)
    {
        workspace = workspace.Trim();
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            lock (Gate)
            {
                _workspace = workspace;
                _snapshot = WorkspaceSnapshot.Empty(workspace);
                _loadTask = Task.CompletedTask;
                _ownedReady = NewReadySignal();
                _ownedReady.TrySetResult(true);
            }
            return Task.CompletedTask;
        }
        workspace = Path.GetFullPath(workspace);

        lock (Gate)
        {
            if (!forceReload && string.Equals(_workspace, workspace, StringComparison.OrdinalIgnoreCase) && _loadTask is not null && !_loadTask.IsFaulted)
                return _loadTask;

            _workspace = workspace;
            _snapshot = null;
            Interlocked.Increment(ref _contentVersion);
            _ownedReady = NewReadySignal();
            var ownedReady = _ownedReady;
            _loadTask = Task.Run(() =>
            {
                try { LoadSnapshot(workspace, ownedReady); }
                catch (Exception ex)
                {
                    ownedReady.TrySetException(ex);
                    throw;
                }
            });
            return _loadTask;
        }
    }

    public static async Task PreloadOwnedAsync(string workspace)
    {
        var fullWorkspace = workspace.Trim();
        var preload = PreloadAsync(fullWorkspace);
        Task ownedReady;
        lock (Gate) ownedReady = _ownedReady.Task;
        await ownedReady.ConfigureAwait(false);
    }

    public static void InvalidateFile(string path)
    {
        Interlocked.Increment(ref _contentVersion);
        var snapshot = _snapshot;
        snapshot?.TextByPath.TryRemove(Path.GetFullPath(path), out _);
    }

    public static void RefreshLocalizedNames(string workspace)
    {
        workspace = Path.GetFullPath(workspace.Trim());
        var stringsPath = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[strings]", "strings_english.str"));
        if (!File.Exists(stringsPath)) return;
        var names = LocalizedStrings.Load(stringsPath);
        lock (Gate)
        {
            var snapshot = _snapshot;
            if (snapshot is null || !string.Equals(snapshot.Workspace, workspace, StringComparison.OrdinalIgnoreCase)) return;
            _snapshot = snapshot with { LocalizedNames = names };
        }
    }

    public static string ReadAllText(string path, Encoding? encoding = null)
    {
        var snapshot = _snapshot;
        var fullPath = Path.GetFullPath(path);
        if (snapshot is not null && snapshot.TextByPath.TryGetValue(fullPath, out var text))
            return text;
        return encoding is null ? File.ReadAllText(path) : File.ReadAllText(path, encoding);
    }

    public static IReadOnlyDictionary<string, string> GetLocalizedNames(string workspace)
    {
        var snapshot = EnsureOwnedLoaded(workspace);
        return snapshot.LocalizedNames;
    }

    public static IReadOnlyDictionary<string, ExperienceEditorTab.RegionMetadata> GetRegionMetadata(string workspace)
    {
        var snapshot = EnsureOwnedLoaded(workspace);
        return snapshot.RegionMetadata;
    }

    public static object GetRankData(string workspace) => EnsureLoaded(workspace).RankData
        ?? throw new InvalidDataException("Rank data could not be prepared for this workspace.");

    public static object GetRegionXpData(string workspace) => EnsureLoaded(workspace).RegionXpData
        ?? throw new InvalidDataException("Region XP data could not be prepared for this workspace.");

    public static object GetOwnedData(string workspace) => EnsureOwnedLoaded(workspace).OwnedData
        ?? throw new InvalidDataException("Owned truck data could not be prepared for this workspace.");

    public static object GetEngineData(string workspace) => EnsureLoaded(workspace).EngineData
        ?? throw new InvalidDataException("Engine data could not be prepared for this workspace.");

    public static object GetWinchData(string workspace) => EnsureLoaded(workspace).WinchData
        ?? throw new InvalidDataException("Winch data could not be prepared for this workspace.");
    public static bool TryGetCurrentFiles(
        string workspace,
        string relativeDirectory,
        string searchPattern,
        SearchOption searchOption,
        out IReadOnlyList<string> files)
    {
        var snapshot = _snapshot;
        var fullWorkspace = Path.GetFullPath(workspace);
        if (snapshot is null || !string.Equals(snapshot.Workspace, fullWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            files = Array.Empty<string>();
            return false;
        }

        var directory = relativeDirectory.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        files = snapshot.CurrentFiles
            .Where(file => IsWithinDirectory(file.RelativePath, directory, searchOption))
            .Where(file => MatchesPattern(file.RelativePath, searchPattern))
            .Select(file => WorkspaceOriginals.GetCurrentPath(workspace, file.RelativePath))
            .ToArray();
        return true;
    }

    private static WorkspaceSnapshot EnsureLoaded(string workspace)
    {
        PreloadAsync(workspace).GetAwaiter().GetResult();
        return _snapshot ?? WorkspaceSnapshot.Empty(workspace);
    }

    private static WorkspaceSnapshot EnsureOwnedLoaded(string workspace)
    {
        PreloadOwnedAsync(workspace).GetAwaiter().GetResult();
        return _snapshot ?? WorkspaceSnapshot.Empty(workspace);
    }

    private static TaskCompletionSource<bool> NewReadySignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void LoadSnapshot(string workspace, TaskCompletionSource<bool> ownedReady)
    {
        var updatedRoot = WorkspaceOriginals.GetUpdatedRoot(workspace);
        var originalRoot = WorkspaceOriginals.GetOriginalRoot(workspace);
        var currentByRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var textByPath = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { updatedRoot, originalRoot })
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var path in SafeEnumerateFiles(root))
            {
                var relative = Path.GetRelativePath(root, path);
                currentByRelativePath.TryAdd(relative, path);
                if (!IsTextAsset(relative))
                    continue;

                try
                {
                    textByPath[Path.GetFullPath(path)] = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        var files = currentByRelativePath
            .Select(pair => new WorkspaceFile(pair.Key, pair.Value))
            .ToArray();
        var snapshot = new WorkspaceSnapshot(workspace, files, textByPath,
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ExperienceEditorTab.RegionMetadata>(StringComparer.OrdinalIgnoreCase));
        lock (Gate)
        {
            if (!string.Equals(_workspace, workspace, StringComparison.OrdinalIgnoreCase))
                return;
            _snapshot = snapshot;
        }

        var stringsPath = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[strings]", "strings_english.str"));
        var localizedNames = File.Exists(stringsPath)
            ? LocalizedStrings.Load(stringsPath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, ExperienceEditorTab.RegionMetadata> regionMetadata =
            new Dictionary<string, ExperienceEditorTab.RegionMetadata>(StringComparer.OrdinalIgnoreCase);
        try
        {
            regionMetadata = ExperienceEditorTab.LoadRegionMetadata(workspace, localizedNames);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }

        object? ownedData = null;
        object? engineData = null;
        object? winchData = null;
        object? regionXpData = null;
        object? rankData = null;
        try
        {
            var dlcLibrary = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "dlc_library.sso"));
            var zoneDescriptions = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "zone_desc_storage.sso"));
            if (File.Exists(stringsPath) && File.Exists(dlcLibrary) && File.Exists(zoneDescriptions))
                ownedData = OwnedContentTab.PrepareWorkspaceData(workspace, localizedNames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        lock (Gate)
        {
            if (!string.Equals(_workspace, workspace, StringComparison.OrdinalIgnoreCase))
            {
                ownedReady.TrySetResult(true);
                return;
            }
            _snapshot = snapshot with
            {
                LocalizedNames = localizedNames,
                RegionMetadata = regionMetadata,
                OwnedData = ownedData
            };
        }
        ownedReady.TrySetResult(true);

        try { regionXpData = ExperienceEditorTab.PrepareRegionXpData(workspace); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        try { rankData = ExperienceEditorTab.PrepareRankData(workspace); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        try { engineData = EngineEditTab.PrepareWorkspaceData(workspace, localizedNames); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        try { winchData = TruckEditTab.PrepareWinchData(workspace, localizedNames); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }

        lock (Gate)
        {
            if (!string.Equals(_workspace, workspace, StringComparison.OrdinalIgnoreCase))
                return;
            _snapshot = snapshot with
            {
                LocalizedNames = localizedNames,
                RegionMetadata = regionMetadata,
                OwnedData = ownedData,
                EngineData = engineData,
                WinchData = winchData,
                RegionXpData = regionXpData,
                RankData = rankData
            };
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
    private static bool IsTextAsset(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var extension = Path.GetExtension(normalized);
        if (extension.Equals(".sso", StringComparison.OrdinalIgnoreCase))
            return normalized.StartsWith("[ps]" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (extension.Equals(".str", StringComparison.OrdinalIgnoreCase))
            return string.Equals(normalized, Path.Combine("[strings]", "strings_english.str"), StringComparison.OrdinalIgnoreCase);
        if (!extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return false;

        return new[]
        {
            Path.Combine("[media]", "classes", "trucks"),
            Path.Combine("[media]", "classes", "engines"),
            Path.Combine("[media]", "classes", "winches"),
            Path.Combine("[media]", "_dlc")
        }.Any(directory => normalized.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsWithinDirectory(string relativePath, string directory, SearchOption searchOption)
    {
        var parent = Path.GetDirectoryName(relativePath)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
        if (searchOption == SearchOption.TopDirectoryOnly)
            return string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase);
        return string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase)
            || parent.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPattern(string relativePath, string pattern)
    {
        if (pattern is "*" or "*.*")
            return true;
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            return string.Equals(Path.GetExtension(relativePath), pattern[1..], StringComparison.OrdinalIgnoreCase);
        return string.Equals(Path.GetFileName(relativePath), pattern, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record WorkspaceFile(string RelativePath, string FullPath);
    private sealed record WorkspaceSnapshot(
        string Workspace,
        WorkspaceFile[] CurrentFiles,
        ConcurrentDictionary<string, string> TextByPath,
        IReadOnlyDictionary<string, string> LocalizedNames,
        IReadOnlyDictionary<string, ExperienceEditorTab.RegionMetadata> RegionMetadata,
        object? OwnedData = null,
        object? EngineData = null,
        object? WinchData = null,
        object? RegionXpData = null,
        object? RankData = null)
    {
        public static WorkspaceSnapshot Empty(string workspace) => new(
            workspace, [], new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ExperienceEditorTab.RegionMetadata>(StringComparer.OrdinalIgnoreCase));
    }
}
