using System.ComponentModel;
using System.Diagnostics;

namespace Winser.Helpers;

/// <summary>Thin wrappers over the few shell verbs Winser needs.</summary>
public static class SystemShell
{
    /// <summary>Opens a folder in Explorer.</summary>
    public static void OpenFolder(string path)
    {
        if (!IsLocalPath(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Start("explorer.exe", Quote(path));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Winser] Could not open {path}: {ex.Message}");
        }
    }

    /// <summary>Opens a downloaded file with whatever the system associates with its type.</summary>
    public static void OpenFile(string path)
    {
        if (!IsLocalPath(path))
        {
            return;
        }

        Start(path, arguments: null);
    }

    /// <summary>Shows a file in Explorer with it selected, falling back to its folder.</summary>
    public static void RevealFile(string path)
    {
        if (!IsLocalPath(path))
        {
            return;
        }

        Start("explorer.exe", File.Exists(path)
            ? $"/select,{Quote(path)}"
            : Quote(Path.GetDirectoryName(path) ?? string.Empty));
    }

    /// <summary>
    /// ShellExecute is not a file API: hand it <c>http://…</c> and it launches a browser, hand
    /// it a registered scheme and it launches whatever claims that scheme. Everything on its
    /// way there is held to being an ordinary absolute path first.
    /// </summary>
    /// <remarks>
    /// Winser writes these paths itself, but it reads them back out of <c>downloads.json</c>
    /// on the next run, and a string that has been round-tripped through a file on disk is no
    /// longer one the app can vouch for. Rejecting quotes matters for the same reason: the
    /// Explorer arguments below are a command line, not an argument vector.
    /// </remarks>
    private static bool IsLocalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.Contains('"', StringComparison.Ordinal) &&
        path.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
        Path.IsPathFullyQualified(path);

    private static string Quote(string value) => $"\"{value}\"";

    private static void Start(string fileName, string? arguments)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = true };
        if (arguments is not null)
        {
            info.Arguments = arguments;
        }

        try
        {
            Process.Start(info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"[Winser] Could not launch {fileName}: {ex.Message}");
        }
    }
}
