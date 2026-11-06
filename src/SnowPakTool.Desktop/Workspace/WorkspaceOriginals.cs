namespace SnowPakTool.Desktop;

/// <summary>Resolves the immutable original tree and sparse edited overlay for a workspace.</summary>
internal static class WorkspaceOriginals
{
    private const string LegacyOriginalsDirectory = "originals";

    public static string GetOriginalRoot(string workspace)
    {
        var root = GetWorkspaceRoot(workspace); var grouped = Path.Combine(root, "initial.pak", "original_files");
        if (Directory.Exists(grouped)) return grouped;
        var legacy = Path.Combine(root, "original_files");
        return Directory.Exists(legacy) ? legacy : Path.Combine(InitialPakRoot(workspace), "original_files");
    }
    public static string GetUpdatedRoot(string workspace)
    {
        var root = GetWorkspaceRoot(workspace);
        var initial = InitialPakRoot(workspace);
        var legacyUpdated = Path.Combine(root, "updated");
        if (!Directory.Exists(Path.Combine(initial, "updated")) && Directory.Exists(legacyUpdated)) return legacyUpdated;
        if (!initial.Equals(root, StringComparison.OrdinalIgnoreCase)) return Path.Combine(initial, "updated");
        var full = Path.GetFullPath(workspace);
        var childUpdated = Path.Combine(full, "updated");
        return !string.Equals(Path.GetFileName(full), "updated", StringComparison.OrdinalIgnoreCase) && Directory.Exists(childUpdated)
            ? childUpdated
            : full;
    }

    public static string GetWorkspaceRoot(string workspace)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (Path.GetFileName(full).Equals("updated", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(full).Equals("original_files", StringComparison.OrdinalIgnoreCase)
            || (Path.GetFileName(full).Equals("original_unpacked", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(full))) full = Path.GetDirectoryName(full)!;
        if (Path.GetFileName(full).Equals("initial.pak", StringComparison.OrdinalIgnoreCase)) full = Path.GetDirectoryName(full)!;
        return full;
    }

    private static string InitialPakRoot(string workspace)
    {
        var root = GetWorkspaceRoot(workspace);
        var grouped = Path.Combine(root, "initial.pak");
        if (Directory.Exists(grouped)) return grouped;
        // Existing legacy/flat workspaces remain readable until explicitly migrated.
        if (Directory.Exists(Path.Combine(root, "original_files")) || Directory.Exists(Path.Combine(root, "updated"))
            || Directory.Exists(Path.Combine(root, "[media]")) || Directory.Exists(Path.Combine(root, "[ps]"))) return root;
        return grouped;
    }

    public static string GetMetadataPath(string workspace, params string[] pathParts)
    {
        var root = GetWorkspaceRoot(workspace);
        var current = Path.Combine(new[] { root }.Concat(pathParts).ToArray());
        var oldMetadata = Path.Combine(new[] { root, ".snowpaktool" }.Concat(pathParts).ToArray());
        if (!File.Exists(current) && !Directory.Exists(current) && (File.Exists(oldMetadata) || Directory.Exists(oldMetadata))) return oldMetadata;
        var legacy = Path.Combine(new[] { GetOriginalRoot(workspace) }.Concat(pathParts).ToArray());
        if (!File.Exists(current) && File.Exists(legacy))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            File.Copy(legacy, current, overwrite: false);
        }
        return current;
    }

    public static string GetOriginalPath(string workspace, string relativePath) => SafeCombine(GetOriginalRoot(workspace), relativePath);

    public static string GetOriginalReadPath(string workspace, string relativePath)
    {
        var original = GetOriginalPath(workspace, relativePath);
        if (File.Exists(original)) return original;
        var legacy = SafeCombine(Path.Combine(GetOriginalRoot(workspace), LegacyOriginalsDirectory), relativePath);
        return File.Exists(legacy) ? legacy : original;
    }

    public static string GetUpdatedPath(string workspace, string relativePath) => SafeCombine(GetUpdatedRoot(workspace), relativePath);

    public static string GetCurrentPath(string workspace, string relativePath)
    {
        var updated = GetUpdatedPath(workspace, relativePath);
        if (File.Exists(updated)) return updated;
        var original = GetOriginalReadPath(workspace, relativePath);
        return File.Exists(original) ? original : updated;
    }

    public static bool CurrentFileExists(string workspace, string relativePath) => File.Exists(GetCurrentPath(workspace, relativePath));

    public static IEnumerable<string> EnumerateCurrentFiles(string workspace, string relativeDirectory, string searchPattern = "*", SearchOption searchOption = SearchOption.AllDirectories)
    {
        if (WorkspaceDataCache.TryGetCurrentFiles(workspace, relativeDirectory, searchPattern, searchOption, out var cachedFiles))
            return cachedFiles;
        return EnumerateCurrentFilesFromDisk(workspace, relativeDirectory, searchPattern, searchOption);
    }

    private static IEnumerable<string> EnumerateCurrentFilesFromDisk(string workspace, string relativeDirectory, string searchPattern, SearchOption searchOption)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { GetUpdatedRoot(workspace), GetOriginalRoot(workspace) })
        {
            var directory = SafeCombine(root, relativeDirectory);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, searchPattern, searchOption))
            {
                var relative = Path.GetRelativePath(root, file);
                if (!seen.Add(relative)) continue;
                yield return GetCurrentPath(workspace, relative);
            }
        }
    }
    public static string GetRelativePath(string workspace, string filePath)
    {
        var path = Path.GetFullPath(filePath);
        // A flat updated root may contain original_files; resolve the more
        // specific original roots first so edits never target the baseline.
        foreach (var root in new[] { Path.Combine(GetOriginalRoot(workspace), LegacyOriginalsDirectory), GetOriginalRoot(workspace), GetUpdatedRoot(workspace) })
        {
            var fullRoot = Path.GetFullPath(root);
            var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return Path.GetRelativePath(fullRoot, path);
        }
        throw new InvalidDataException($"File is outside this workspace: {filePath}");
    }

    public static string PrepareForEdit(string workspace, string filePath)
    {
        var relative = GetRelativePath(workspace, filePath);
        var updated = GetUpdatedPath(workspace, relative);
        if (!File.Exists(updated))
        {
            var current = GetCurrentPath(workspace, relative);
            if (!File.Exists(current)) throw new FileNotFoundException("The workspace file no longer exists.", current);
            Directory.CreateDirectory(Path.GetDirectoryName(updated)!);
            File.Copy(current, updated, overwrite: false);
        }
        return updated;
    }

    // Kept for callers migrating from the old snapshot API.
    public static string Preserve(string workspace, string relativePath, string sourcePath)
    {
        var original = GetOriginalPath(workspace, relativePath);
        if (File.Exists(original)) return original;
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.Copy(sourcePath, original, overwrite: false);
        return original;
    }

    public static void PreserveCurrent(string workspace, string filePath) => _ = PrepareForEdit(workspace, filePath);

    private static string SafeCombine(string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(part => part is ".." or ""))
            throw new InvalidDataException($"Unsafe workspace file path: {relativePath}");
        var fullRoot = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Workspace path escapes root: {relativePath}");
        return result;
    }
}
