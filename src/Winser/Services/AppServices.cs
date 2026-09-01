using System.Diagnostics;

namespace Winser.Services;

/// <summary>
/// Process-wide singletons. Winser is a single-process app with a handful of collaborators,
/// so a typed locator beats wiring a container through XAML-constructed views.
/// </summary>
/// <remarks>
/// <see cref="Lazy{T}"/> rather than a plain <c>??=</c> field, because <see cref="Initialize"/>
/// now warms part of this set on a thread-pool thread: construction is no longer confined to
/// the UI thread, and a null-coalescing assignment reached from two threads at once can build
/// two instances - which here would mean two <see cref="JsonStore{T}"/> objects over one file,
/// each quietly overwriting the other's writes. Lazy's default mode
/// (<c>ExecutionAndPublication</c>) is exactly the needed guarantee: whoever asks first builds
/// it, everyone else waits for that one.
/// </remarks>
public static class AppServices
{
    private static readonly Lazy<SettingsService> LazySettings = new(() => new SettingsService());
    private static readonly Lazy<HistoryService> LazyHistory = new(() => new HistoryService(Settings));
    private static readonly Lazy<BookmarkService> LazyBookmarks = new(() => new BookmarkService());
    private static readonly Lazy<DownloadService> LazyDownloads = new(() => new DownloadService());
    private static readonly Lazy<SessionService> LazySession = new(() => new SessionService());
    private static readonly Lazy<WebViewService> LazyWebView = new(() => new WebViewService());
    private static readonly Lazy<SitePermissionService> LazyPermissions = new(() => new SitePermissionService());

    public static SettingsService Settings => LazySettings.Value;

    public static HistoryService History => LazyHistory.Value;

    public static BookmarkService Bookmarks => LazyBookmarks.Value;

    public static DownloadService Downloads => LazyDownloads.Value;

    public static SessionService Session => LazySession.Value;

    public static WebViewService WebView => LazyWebView.Value;

    public static SitePermissionService Permissions => LazyPermissions.Value;

    /// <summary>
    /// Builds what the first window cannot open without, starts the browser engine coming up
    /// alongside it, and leaves the rest warming behind both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every service here reads a JSON file synchronously, and this runs on the UI thread
    /// before <see cref="Helpers.WindowManager.CreateWindow"/> has created anything at all, so
    /// all of it used to land squarely between the user's double-click and the first pixel.
    /// </para>
    /// <para>
    /// Most of it has to stay there. <c>MainWindow</c>'s constructor reads <see cref="Settings"/>
    /// (theme) and <see cref="Session"/> (window placement, tabs to restore), and
    /// <c>BrowserViewModel</c>'s constructor subscribes to <see cref="Bookmarks"/> and
    /// <see cref="Downloads"/> before the window is ever shown - deferring those would trade a
    /// synchronous load for a synchronous wait and buy nothing.
    /// </para>
    /// <para>
    /// <see cref="History"/> and <see cref="Permissions"/> are different: nothing touches the
    /// first until a navigation completes or the address bar is typed into, or the second until
    /// a page asks for the camera. History is also by far the most expensive of the seven - up
    /// to 10,000 entries to deserialize, sort and index - which is what makes it the one worth
    /// moving.
    /// </para>
    /// </remarks>
    public static void Initialize()
    {
        // First, because it is the longest pole: bringing up a CoreWebView2 environment starts
        // Chromium's browser process, which until now did not begin until the first tab had
        // finished laying out - i.e. after every line below, plus all of XAML startup. Started
        // here it runs alongside them instead.
        StartBrowserWarmUp();

        _ = Settings;
        _ = Session;
        _ = Bookmarks;
        _ = Downloads;

        // Nothing in here touches the UI, and the lazy accessors above make the handover safe:
        // a caller that gets there before the warm-up does simply constructs the service itself,
        // exactly as it did when this method built all seven inline.
        _ = Task.Run(() =>
        {
            AppPaths.CleanUpPrivateProfiles();
            _ = History;
            _ = Permissions;
        });
    }

    /// <summary>Flushes every pending write. Called from App.Exit.</summary>
    public static void Shutdown()
    {
        // Deliberately only touches services that were actually built - constructing one here
        // purely to dispose it would read a file off disk on the way out for no reason.
        if (Created(LazySettings)?.Current.ClearHistoryOnExit == true)
        {
            Created(LazyHistory)?.Clear();
        }

        Created(LazySession)?.Dispose();
        Created(LazyPermissions)?.Dispose();
        Created(LazyDownloads)?.Dispose();
        Created(LazyBookmarks)?.Dispose();
        Created(LazyHistory)?.Dispose();
        Created(LazySettings)?.Dispose();
    }

    /// <summary>
    /// Kicks off the shared CoreWebView2 environment without waiting for it.
    /// </summary>
    /// <remarks>
    /// Safe to fire and forget: <see cref="WebViewService.GetSharedAsync"/> is idempotent
    /// behind its own gate and caches the result, so the first tab to ask joins this same
    /// operation rather than starting a second one. The exception is swallowed on purpose -
    /// the overwhelmingly likely failure is a machine with no Evergreen runtime installed, and
    /// that has to surface as <c>WebContentView.InitializeAsync</c>'s "the runtime is not
    /// installed" page, not as an unhandled exception thrown out of app launch. Failing here
    /// leaves the environment uncreated, which is precisely what makes that retry work.
    /// </remarks>
    private static void StartBrowserWarmUp() => _ = WarmUpBrowserAsync();

    private static async Task WarmUpBrowserAsync()
    {
        try
        {
            await WebView.GetSharedAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Winser] Browser warm-up failed, first tab will report it: {ex.Message}");
        }
    }

    private static T? Created<T>(Lazy<T> lazy)
        where T : class => lazy.IsValueCreated ? lazy.Value : null;
}
