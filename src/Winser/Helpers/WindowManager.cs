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

    /// <summary>
    /// Asks for efficiency-class scheduling exactly when no window is on screen.
    /// </summary>
    /// <remarks>
    /// EcoQoS is a property of the process, not of a window, so no single window can decide it:
    /// one window still visible is enough to need normal scheduling, however many others are
    /// minimized. Hence this lives here rather than in <see cref="MainWindow"/>, which is where
    /// the equivalent per-window memory-pressure signal is decided.
    /// </remarks>
    internal static void UpdateProcessPowerState() =>
        PowerEfficiency.SetEcoQoS(Open.Count > 0 && Open.All(w => w.IsMinimized));

    internal static void Register(MainWindow window)
    {
        Open.Add(window);

        // A brand new window is on screen, so whatever the others are doing, this is not the
        // moment to be running slowly.
        UpdateProcessPowerState();
    }

    internal static void Unregister(MainWindow window)
    {
        Open.Remove(window);

        // Closing the one visible window can leave nothing but minimized ones behind.
        UpdateProcessPowerState();

        if (Open.Count == 0)
        {
            LastWindowClosed?.Invoke(null, EventArgs.Empty);
        }
    }
}
