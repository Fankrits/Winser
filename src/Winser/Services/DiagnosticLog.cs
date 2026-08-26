using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Winser.Services;

/// <summary>
/// A small append-only log next to Winser's other state, for the class of problem a screenshot
/// cannot answer.
/// </summary>
/// <remarks>
/// A page that fails to render is the motivating case: whether it is the browser painting
/// nothing, painting into the wrong place, or painting correctly into a container that is
/// itself the wrong size, the picture is the same flat rectangle - and every on-screen
/// diagnostic for it necessarily lives inside the thing whose size is in question, so it
/// disappears along with everything else. A file does not.
/// </remarks>
public static class DiagnosticLog
{
    /// <summary>Truncated rather than rotated past this: it is a breadcrumb trail, not an archive.</summary>
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    public static string Path => AppPaths.DataFile("diagnostics.log");

    public static void Write(string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");

        lock (Gate)
        {
            try
            {
                if (new FileInfo(Path) is { Exists: true, Length: > MaxBytes })
                {
                    File.Delete(Path);
                }

                File.AppendAllText(Path, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never be the reason something fails.
                Debug.WriteLine($"[Winser] Could not write the diagnostic log: {ex.Message}");
            }
        }
    }
}
