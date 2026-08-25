namespace Winser.Helpers;

/// <summary>Tracks every open browser window so "new window" and shutdown behave sanely.</summary>
public static class WindowManager
{
    private static readonly List<MainWindow> Open = [];

    public static IReadOnlyList<MainWindow> Windows => Open;

    public static event EventHandler? LastWindowClosed;

    public static MainWindow CreateWindow(bool isPrivate = false, string? initialUrl = null)
    {
        var window = new MainWindow(isPrivate, initialUrl);
        window.Activate();
        return window;
    }

    public static bool IsLastNormalWindow(MainWindow window) =>
        !window.IsPrivate && Open.Count(w => !w.IsPrivate) <= 1;

    internal static void Register(MainWindow window) => Open.Add(window);

    internal static void Unregister(MainWindow window)
    {
        Open.Remove(window);
        if (Open.Count == 0)
        {
            LastWindowClosed?.Invoke(null, EventArgs.Empty);
        }
    }
}
