namespace Winser.Services;

/// <summary>
/// Process-wide singletons. Winser is a single-process app with a handful of collaborators,
/// so a typed locator beats wiring a container through XAML-constructed views.
/// </summary>
public static class AppServices
{
    private static SettingsService? _settings;
    private static HistoryService? _history;
    private static BookmarkService? _bookmarks;
    private static DownloadService? _downloads;
    private static SessionService? _session;
    private static WebViewService? _webView;
    private static SitePermissionService? _permissions;

    public static SettingsService Settings => _settings ??= new SettingsService();

    public static HistoryService History => _history ??= new HistoryService(Settings);

    public static BookmarkService Bookmarks => _bookmarks ??= new BookmarkService();

    public static DownloadService Downloads => _downloads ??= new DownloadService();

    public static SessionService Session => _session ??= new SessionService();

    public static WebViewService WebView => _webView ??= new WebViewService();

    public static SitePermissionService Permissions => _permissions ??= new SitePermissionService();

    /// <summary>Forces eager construction so the first window does not pay the load cost.</summary>
    public static void Initialize()
    {
        AppPaths.CleanUpPrivateProfiles();
        _ = Settings;
        _ = History;
        _ = Bookmarks;
        _ = Downloads;
        _ = Session;
        _ = WebView;
        _ = Permissions;
    }

    /// <summary>Flushes every pending write. Called from App.Exit.</summary>
    public static void Shutdown()
    {
        if (_settings?.Current.ClearHistoryOnExit == true)
        {
            _history?.Clear();
        }

        _session?.Dispose();
        _permissions?.Dispose();
        _downloads?.Dispose();
        _bookmarks?.Dispose();
        _history?.Dispose();
        _settings?.Dispose();
    }
}
