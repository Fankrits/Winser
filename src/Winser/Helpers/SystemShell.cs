using System.ComponentModel;
using System.Diagnostics;

namespace Winser.Helpers;

/// <summary>Thin wrappers over the few shell verbs Winser needs.</summary>
public static class SystemShell
{
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Winser] Could not open {path}: {ex.Message}");
        }
    }

    /// <summary>Hands a URL to whatever the system default browser is.</summary>
    public static void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"[Winser] Could not launch {url}: {ex.Message}");
        }
    }
}
