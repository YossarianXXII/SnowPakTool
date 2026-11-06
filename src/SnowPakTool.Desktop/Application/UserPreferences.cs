using System.Text;

namespace SnowPakTool.Desktop;

internal static class UserPreferences
{
    private static readonly string LastPakPathFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop",
        "last-pak-path.txt");
    private static readonly string LastWorkspacePathFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop",
        "last-workspace-path.txt");
    private static readonly string WindowSizeFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop",
        "window-size.txt");

    public static string LoadLastPakPath()
    {
        return LoadPath(LastPakPathFile);
    }

    public static void SaveLastPakPath(string path)
    {
        SavePath(LastPakPathFile, path);
    }

    public static string LoadLastWorkspacePath()
    {
        var savedPath = LoadPath(LastWorkspacePathFile);
        if (string.IsNullOrWhiteSpace(savedPath) || Directory.Exists(savedPath) ||
            !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(savedPath)), "original_unpacked", StringComparison.OrdinalIgnoreCase))
            return savedPath;

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(savedPath));
        var updatedPath = parent is null ? string.Empty : Path.Combine(parent, "updated");
        return Directory.Exists(updatedPath) ? updatedPath : savedPath;
    }

    public static void SaveLastWorkspacePath(string path)
    {
        SavePath(LastWorkspacePathFile, path);
    }

    public static Size LoadWindowSize(Size defaultSize, Size minimumSize)
    {
        var saved = LoadPath(WindowSizeFile).Split(',', StringSplitOptions.TrimEntries);
        if (saved.Length != 2 ||
            !int.TryParse(saved[0], out var width) ||
            !int.TryParse(saved[1], out var height))
            return defaultSize;
        return new Size(Math.Max(width, minimumSize.Width), Math.Max(height, minimumSize.Height));
    }

    public static void SaveWindowSize(Size size)
    {
        SavePath(WindowSizeFile, $"{size.Width},{size.Height}");
    }

    private static string LoadPath(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static void SavePath(string preferencePath, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
            File.WriteAllText(preferencePath, path, Encoding.UTF8);
        }
        catch (IOException)
        {
            // A preference write failure should not prevent using the tool.
        }
        catch (UnauthorizedAccessException)
        {
            // A preference write failure should not prevent using the tool.
        }
    }
}
