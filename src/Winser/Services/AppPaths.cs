using System.Runtime.InteropServices;

namespace Winser.Services;

/// <summary>
/// Where Winser keeps its state. The app runs unpackaged, so <c>ApplicationData.Current</c>
/// is off limits and everything lives under <c>%LOCALAPPDATA%\Winser</c> instead.
/// </summary>
public static class AppPaths
{
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Winser");

    /// <summary>JSON documents: settings, bookmarks, history, downloads, session.</summary>
    public static string Data { get; } = Ensure(Path.Combine(Root, "Data"));

    /// <summary>The WebView2 user data folder (cookies, cache, local storage).</summary>
    public static string Profile { get; } = Ensure(Path.Combine(Root, "Profile"));

    /// <summary>Ephemeral user data folders for InPrivate windows.</summary>
    public static string PrivateProfiles { get; } = Ensure(Path.Combine(Root, "Private"));

    /// <summary>Static HTML shipped with the app and exposed to WebView2 via a virtual host.</summary>
    public static string WebAssets { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");

    public static string DataFile(string fileName) => Path.Combine(Data, fileName);

    public static string DefaultDownloadFolder()
    {
        if (TryGetKnownFolder(FolderIdDownloads, out var path))
        {
            return path;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>Deletes leftover InPrivate profiles from sessions that did not shut down cleanly.</summary>
    public static void CleanUpPrivateProfiles()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(PrivateProfiles))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                    // Still locked by a browser process that outlived us; it will go next launch.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryGetKnownFolder(Guid folderId, out string path)
    {
        path = string.Empty;
        var id = folderId;
        var ptr = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out ptr) != 0)
            {
                return false;
            }

            path = Marshal.PtrToStringUni(ptr) ?? string.Empty;
            return path.Length > 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        finally
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
