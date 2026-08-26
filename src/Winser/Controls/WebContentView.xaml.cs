using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;
using Winser.Helpers;
using Winser.Models;
using Winser.Services;
using Winser.ViewModels;

namespace Winser.Controls;

/// <summary>
/// Hosts one CoreWebView2 and translates its events into calls on the owning
/// <see cref="BrowserTabViewModel"/>. This is the only place in the app that talks to WebView2.
/// </summary>
public sealed partial class WebContentView : UserControl, IWebViewHost
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
        nameof(Tab),
        typeof(BrowserTabViewModel),
        typeof(WebContentView),
        new PropertyMetadata(null, OnTabChanged));

    /// <summary>
    /// How many forwarded shortcuts a page may land in <see cref="ShortcutBurstMs"/>.
    /// Sized well above what a hand can do and far below what a script can.
    /// </summary>
    private const int ShortcutBurstLimit = 12;

    private const long ShortcutBurstMs = 1000;

    private WebView2? _browser;
    private CoreWebView2? _core;
    private double _zoomFactor = 1.0;
    private bool _initializing;
    private bool _suspended;

    /// <summary>Arrival times of recently accepted shortcut messages; see <see cref="AllowShortcut"/>.</summary>
    private readonly Queue<long> _shortcutTimes = new();

    /// <summary>
    /// How many script dialogs (alert/confirm/prompt) a page may show per navigation before
    /// Winser starts dismissing them unseen. A real page needs at most one or two; a script
    /// stuck in a loop calling alert() otherwise blocks the tab - and the shell's own UI -
    /// indefinitely, since each dialog is modal.
    /// </summary>
    private const int MaxScriptDialogsPerNavigation = 10;

    private int _scriptDialogCount;

    public WebContentView()
    {
        InitializeComponent();

        // Set before anything else can report over it, so that "InitializeAsync was never
        // entered at all" - no Tab attached, or TryInitialize's gate never passing - reads as
        // its own distinct state rather than as the blank rectangle it is trying to explain.
        ReportRendererStatus("Preparing this tab\u2026");

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public BrowserTabViewModel? Tab
    {
        get => (BrowserTabViewModel?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    // ------------------------------------------------------------------ IWebViewHost

    public bool IsReady => _core is not null;

    /// <summary>
    /// Page zoom. The WinUI WebView2 element does not surface CoreWebView2Controller.ZoomFactor
    /// (only the WPF and WinForms wrappers do), so Winser emulates it with the CSS zoom property
    /// on the document element — which reflows the page the same way browser zoom does — and
    /// reapplies it on every navigation.
    /// </summary>
    public double ZoomFactor
    {
        get => _zoomFactor;
        set
        {
            _zoomFactor = value;
            ApplyZoom();
        }
    }

    public void Navigate(string url)
    {
        if (_core is null || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            _core.Navigate(url);
        }
        catch (ArgumentException)
        {
            Tab?.ReportNavigationCompleted(false, $"“{url}” is not a valid address.");
        }
    }

    public void GoBack()
    {
        if (_core is { CanGoBack: true })
        {
            _core.GoBack();
        }
    }

    public void GoForward()
    {
        if (_core is { CanGoForward: true })
        {
            _core.GoForward();
        }
    }

    public void Reload(bool bypassCache)
    {
        if (_core is null)
        {
            return;
        }

        if (bypassCache)
        {
            // There is no "hard reload" API; the DevTools protocol has one.
            _ = _core.CallDevToolsProtocolMethodAsync("Page.reload", """{"ignoreCache":true}""");
        }
        else
        {
            _core.Reload();
        }
    }

    public void Stop() => _core?.Stop();

    public void FocusContent() => _browser?.Focus(FocusState.Programmatic);

    public void SetMuted(bool muted)
    {
        if (_core is not null)
        {
            _core.IsMuted = muted;
        }
    }

    public void OpenDevTools() => _core?.OpenDevToolsWindow();

    public Task PrintAsync()
    {
        _core?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        return Task.CompletedTask;
    }

    public async Task<string> ExecuteScriptAsync(string script)
    {
        if (_core is null)
        {
            return "null";
        }

        try
        {
            return await _core.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return "null";
        }
    }

    public async Task ClearBrowsingDataAsync()
    {
        if (_core is null)
        {
            return;
        }

        try
        {
            await _core.Profile.ClearBrowsingDataAsync();
        }
        catch (Exception ex) when (ex is NotImplementedException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[Winser] Clearing browsing data failed: {ex.Message}");
        }
    }

    public void SetMemoryPressure(bool constrained)
    {
        if (_core is not { } core)
        {
            return;
        }

        try
        {
            core.MemoryUsageTargetLevel = constrained
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[Winser] Could not adjust memory pressure: {ex.Message}");
        }
    }

    /// <summary>
    /// Tears the current renderer down. Used both when a tab closes for good and when an idle
    /// tab is discarded to free its memory (see <see cref="BrowserTabViewModel.TryDiscardAsync"/>)
    /// - the two cases differ only in whether anything ever calls <see cref="TryInitialize"/>
    /// again afterwards, not in what teardown itself has to do.
    /// </summary>
    public void Release()
    {
        if (_browser is not { } browser)
        {
            return;
        }

        Unhook();
        browser.SizeChanged -= OnBrowserSizeChanged;

        // Cleared before Close() rather than after: IsReady must read false for the whole
        // duration of a call that is about to make the underlying CoreWebView2 unusable.
        _core = null;
        _browser = null;

        try
        {
            browser.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[Winser] WebView2 close failed: {ex.Message}");
        }
        finally
        {
            ContentGrid.Children.Remove(browser);
        }
    }

    // -------------------------------------------------------------------- lifecycle

    private static void OnTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        (d as WebContentView)?.TryInitialize();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A tab becomes visible either because the user selected it or because it was just
        // created selected; either way this is "the tab was looked at just now" for the idle
        // discard sweep on BrowserViewModel.
        Tab?.MarkActive();

        // Order matters: Resume makes the WebView2 visible again, and TryInitialize refuses to
        // create a CoreWebView2 that cannot be seen. Swap these two and a tab that has never
        // been shown comes back permanently blank.
        Resume();
        TryInitialize();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _ = SuspendAsync();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => TryInitialize();

    /// <summary>
    /// Holds CoreWebView2 creation back until the control is genuinely laid out on screen.
    /// A WebView2 brought up while collapsed or zero-sized ends up with a zero-bounds
    /// controller: it navigates, reports its title and favicon, and looks entirely healthy
    /// from the outside, while never compositing a single pixel - and it does not recover
    /// once the control is shown. Waiting for a real size is the difference between a live
    /// page and a permanently blank one.
    /// </summary>
    private void TryInitialize()
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        _ = InitializeAsync();
    }

    /// <summary>
    /// Hands a background tab's memory back while nobody is looking at it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Chromium renderer is the expensive thing in this application by an order of
    /// magnitude - tens of megabytes per tab, held for as long as the tab is open. TabView
    /// shows one tab at a time through a single ContentPresenter, so switching tabs unloads
    /// the page that was showing, and that is the precise moment its renderer stops earning
    /// what it costs.
    /// </para>
    /// <para>
    /// Chromium freezes the process rather than discarding it, so coming back is a resume and
    /// not a reload: scroll position, form contents and page state all survive. What does not
    /// survive is anything the page wanted to keep doing off screen, which is why this is a
    /// setting and why a tab that is audibly playing something is left alone.
    /// </para>
    /// </remarks>
    private async Task SuspendAsync()
    {
        if (_core is not { } core || _browser is not { } browser ||
            !AppServices.Settings.Current.SleepBackgroundTabs)
        {
            return;
        }

        // Backgrounded but still making noise - a music tab is doing its job.
        if (core.IsDocumentPlayingAudio && !core.IsMuted)
        {
            return;
        }

        try
        {
            // Independent of the freeze and worth having on its own: it asks Chromium to trim
            // caches it would otherwise keep warm, and it still applies if the freeze is refused.
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;

            // WebView2 refuses to freeze a browser it still considers on screen, and an
            // unloaded element is not reliably invisible to it - so say so explicitly.
            browser.Visibility = Visibility.Collapsed;
            _suspended = await core.TrySuspendAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[Winser] Could not suspend this tab: {ex.Message}");
            browser.Visibility = Visibility.Visible;
            return;
        }

        // The freeze is asynchronous and the user is not: they may already have switched back.
        if (IsLoaded)
        {
            Resume();
        }
    }

    private void Resume()
    {
        if (_browser is { } browser)
        {
            browser.Visibility = Visibility.Visible;
        }

        if (_core is not { } core)
        {
            return;
        }

        try
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            if (_suspended)
            {
                core.Resume();
                _suspended = false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[Winser] Could not resume this tab: {ex.Message}");
        }
    }

    private async Task InitializeAsync()
    {
        if (_initializing || _core is not null || Tab is null)
        {
            return;
        }

        _initializing = true;
        try
        {
            // Reuses a still-elementless _browser left over from a previous attempt that threw
            // below before ever reaching CoreWebView2 (e.g. a first try with no runtime
            // installed), rather than leaving an orphaned WebView2 behind on every retry.
            var browser = _browser ??= CreateBrowserElement();

            // Adding a child to a Grid does not lay that child out - measure and arrange happen
            // on a later pass, and until they do the new element's ActualWidth/ActualHeight are
            // still zero. TryInitialize's gate above measures *this control*, which has been
            // laid out for a while; it says nothing about an element created seconds ago. Force
            // the pass, and if that is somehow not enough, leave: the element's own SizeChanged
            // brings us straight back here the moment it has real bounds.
            //
            // Skipping this is not a subtle inefficiency. A CoreWebView2 created against a
            // zero-sized element gets a zero-bounds controller, and a zero-bounds controller
            // never composites a pixel for the rest of its life, however large the element
            // later becomes.
            browser.UpdateLayout();
            if (browser.ActualWidth <= 0 || browser.ActualHeight <= 0)
            {
                ReportRendererStatus("Waiting for the page area to be laid out\u2026");
                return;
            }

            try
            {
                ReportRendererStatus("Starting the browser engine\u2026");
                var profile = await Tab.Shell.GetProfileAsync();
                await browser.EnsureCoreWebView2Async(profile.Environment);
            }
            catch (Exception ex)
            {
                // A tab that cannot start its browser must still render its error state rather
                // than take the window down with it. The overwhelmingly likely cause is a machine
                // without the Evergreen runtime, so say that rather than showing an HRESULT.
                Debug.WriteLine($"[Winser] EnsureCoreWebView2Async failed: {ex}");
                var message = AppServices.WebView.RuntimeVersion is null
                    ? "The Microsoft Edge WebView2 Runtime is not installed. Winser renders pages " +
                      "with WebView2, so it needs the runtime before it can browse."
                    : $"WebView2 could not start: {ex.Message}";
                ReportRendererStatus(message);
                Tab?.ReportNavigationCompleted(false, message);
                return;
            }

            if (browser.CoreWebView2 is not { } core)
            {
                ReportRendererStatus(
                    "The browser engine started but handed back no CoreWebView2 for this tab.");
                return;
            }

            // Deliberately left set rather than cleared. The WebView2 is on top of this text, so
            // it is invisible the moment a single frame is painted - which makes it visible in
            // exactly one situation: the engine is alive and navigating, and still nothing is
            // reaching the screen. That is a different bug from the engine failing to start, and
            // without this they look identical from the outside: an empty rectangle.
            ReportRendererStatus(
                $"The browser engine is running (WebView2 {AppServices.WebView.RuntimeVersion ?? "version unknown"}) " +
                "but it is not painting. If you can read this, page content is being rendered somewhere " +
                "off-screen rather than into this tab.");

            _core = core;
            await ConfigureAsync(core);
            Hook(core);
            Tab?.AttachHost(this);
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>
    /// A WebView2 that has had Close() called on it - which every discard does, see
    /// <see cref="Release"/> - never works again, so a discarded tab being revisited needs a
    /// brand new element rather than its old one back.
    /// </summary>
    private WebView2 CreateBrowserElement()
    {
        var browser = new WebView2();

        // The retry that makes deferring safe in InitializeAsync: a newly added element is
        // unlaid-out, and this is what fires once it is not.
        browser.SizeChanged += OnBrowserSizeChanged;
        ContentGrid.Children.Add(browser);
        return browser;
    }

    private void OnBrowserSizeChanged(object sender, SizeChangedEventArgs e) => TryInitialize();

    /// <summary>
    /// Writes the placeholder that sits behind the WebView2 (see WebContentView.xaml). Mirrored
    /// to the debug output so a build run under a debugger records the same sequence.
    /// </summary>
    private void ReportRendererStatus(string status)
    {
        Debug.WriteLine($"[Winser] Renderer: {status}");
        RendererStatus.Text = status;
    }

    private async Task ConfigureAsync(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDefaultScriptDialogsEnabled = true;
        core.Settings.IsSwipeNavigationEnabled = true;
        // Winser owns zoom (see ZoomFactor), and WebView2's own Ctrl+scroll zoom would be a
        // second, invisible source of truth. The injected script forwards Ctrl+wheel instead.
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        // Winser draws its own link preview, so the built-in one would just double up.
        core.Settings.IsStatusBarEnabled = false;
        // The injected shortcut bridge (Scripts.ShortcutBridge) is what actually forwards
        // Ctrl+T/Ctrl+W/etc. to the shell, because WinUI accelerators never fire while WebView2
        // holds focus. Leaving WebView2's own browser accelerators on as well means the same
        // keypress could be handled twice; this keeps the bridge the single path.
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        // On by default in WebView2; set explicitly so SmartScreen's reputation check cannot
        // be silently off because nobody wrote it down.
        core.Settings.IsReputationCheckingRequired = true;

        ApplyPreferences();

        // Serves Assets\Web (the new tab page) from https://assets.winser/ so it runs on a
        // normal secure origin instead of file://. DenyCors rather than Allow: the new tab page
        // only ever fetches its own files, so there is no reason to let some site on the
        // internet read them out of the app folder.
        if (Directory.Exists(AppPaths.WebAssets))
        {
            core.SetVirtualHostNameToFolderMapping(
                InternalPages.VirtualHost,
                AppPaths.WebAssets,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }

        await core.AddScriptToExecuteOnDocumentCreatedAsync(Scripts.FindInPage);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Scripts.ShortcutBridge);
    }

    /// <summary>
    /// Applies every setting that maps onto a CoreWebView2 option, so a change in
    /// <c>winser://settings</c> reaches pages that are already open.
    /// </summary>
    public void ApplyPreferences()
    {
        if (_core is not { } core)
        {
            return;
        }

        var settings = AppServices.Settings.Current;
        core.Settings.AreDevToolsEnabled = settings.EnableDevTools;
        core.Settings.IsScriptEnabled = settings.EnableJavaScript;
        core.Settings.IsGeneralAutofillEnabled = settings.EnableAutofill;
        core.Settings.IsPasswordAutosaveEnabled = settings.EnableAutofill;

        try
        {
            core.Profile.PreferredColorScheme = settings.Theme switch
            {
                AppTheme.Light => CoreWebView2PreferredColorScheme.Light,
                AppTheme.Dark => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };
            core.Profile.PreferredTrackingPreventionLevel = settings.TrackingPrevention switch
            {
                TrackingPrevention.Off => CoreWebView2TrackingPreventionLevel.None,
                TrackingPrevention.Basic => CoreWebView2TrackingPreventionLevel.Basic,
                TrackingPrevention.Strict => CoreWebView2TrackingPreventionLevel.Strict,
                _ => CoreWebView2TrackingPreventionLevel.Balanced,
            };

            var downloadFolder = AppServices.Settings.EffectiveDownloadFolder;
            Directory.CreateDirectory(downloadFolder);
            core.Profile.DefaultDownloadFolderPath = downloadFolder;
        }
        catch (Exception ex) when (ex is NotImplementedException or ArgumentException
                                      or UnauthorizedAccessException or IOException)
        {
            Debug.WriteLine($"[Winser] Profile setting unavailable on this runtime: {ex.Message}");
        }
    }

    /// <summary>
    /// Each navigation gets a brand-new JS global scope, so this is re-asserted on every
    /// NavigationCompleted as well as whenever the window's full-screen state actually changes.
    /// </summary>
    public void SyncFullScreenFlag(bool isFullScreen)
    {
        if (_core is null)
        {
            return;
        }

        _ = ExecuteScriptAsync($"window.__winserFullScreen = {(isFullScreen ? "true" : "false")}");
    }

    private void Hook(CoreWebView2 core)
    {
        core.NavigationStarting += OnNavigationStarting;
        core.SourceChanged += OnSourceChanged;
        core.NavigationCompleted += OnNavigationCompleted;
        core.HistoryChanged += OnHistoryChanged;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.FaviconChanged += OnFaviconChanged;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.ScriptDialogOpening += OnScriptDialogOpening;
        core.WindowCloseRequested += OnWindowCloseRequested;
        core.ContainsFullScreenElementChanged += OnFullScreenElementChanged;
        core.DownloadStarting += OnDownloadStarting;
        core.StatusBarTextChanged += OnStatusBarTextChanged;
        core.IsDocumentPlayingAudioChanged += OnAudioStateChanged;
        core.IsMutedChanged += OnAudioStateChanged;
        core.ProcessFailed += OnProcessFailed;
        core.WebMessageReceived += OnWebMessageReceived;
        core.DOMContentLoaded += OnDomContentLoaded;
    }

    private void Unhook()
    {
        if (_core is not { } core)
        {
            return;
        }

        core.NavigationStarting -= OnNavigationStarting;
        core.SourceChanged -= OnSourceChanged;
        core.NavigationCompleted -= OnNavigationCompleted;
        core.HistoryChanged -= OnHistoryChanged;
        core.DocumentTitleChanged -= OnDocumentTitleChanged;
        core.FaviconChanged -= OnFaviconChanged;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.PermissionRequested -= OnPermissionRequested;
        core.ScriptDialogOpening -= OnScriptDialogOpening;
        core.WindowCloseRequested -= OnWindowCloseRequested;
        core.ContainsFullScreenElementChanged -= OnFullScreenElementChanged;
        core.DownloadStarting -= OnDownloadStarting;
        core.StatusBarTextChanged -= OnStatusBarTextChanged;
        core.IsDocumentPlayingAudioChanged -= OnAudioStateChanged;
        core.IsMutedChanged -= OnAudioStateChanged;
        core.ProcessFailed -= OnProcessFailed;
        core.WebMessageReceived -= OnWebMessageReceived;
        core.DOMContentLoaded -= OnDomContentLoaded;
        _core = null;
    }

    // ----------------------------------------------------------------- core events

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _scriptDialogCount = 0;
        Tab?.ReportNavigationStarting(e.Uri);
    }

    private void OnSourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs e) =>
        Tab?.ReportSourceChanged(sender.Source);

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Tab?.ReportNavigationCompleted(e.IsSuccess, e.IsSuccess ? null : Describe(e.WebErrorStatus));

        if (Tab is { } tab)
        {
            SyncFullScreenFlag(tab.Shell.IsFullScreen);
        }

        if (e.IsSuccess && InternalPages.IsNewTab(sender.Source))
        {
            _ = PushTopSitesAsync(sender);
        }
    }

    private void OnHistoryChanged(CoreWebView2 sender, object args) =>
        Tab?.ReportHistoryChanged(sender.CanGoBack, sender.CanGoForward);

    private void OnDocumentTitleChanged(CoreWebView2 sender, object args) =>
        Tab?.ReportTitleChanged(sender.DocumentTitle);

    private async void OnFaviconChanged(CoreWebView2 sender, object args)
    {
        try
        {
            using var stream = await sender.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null || stream.Size == 0)
            {
                Tab?.ReportFavicon(null);
                return;
            }

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            Tab?.ReportFavicon(bitmap);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            Tab?.ReportFavicon(null);
        }
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (Tab is null || !AppServices.Settings.Current.OpenPopupsAsTabs)
        {
            return;
        }

        // Handling it ourselves means the new page has no window.opener back-reference; in
        // exchange every popup lands as a tab in this window instead of an unmanaged window.
        // Claimed before the scheme check on purpose: leaving a rejected popup unhandled would
        // hand it straight back to WebView2 to open in a window Winser does not control.
        e.Handled = true;

        if (!UrlHelper.IsWebRequestable(e.Uri))
        {
            // window.open() is page-driven, so it only gets the schemes web content is trusted
            // with. winser:// would open Winser's own settings or history UI on a site's say-so.
            Debug.WriteLine($"[Winser] Blocked a page-requested popup to {e.Uri}");
            return;
        }

        Tab.Shell.OpenInNewTab(e.Uri, background: !e.IsUserInitiated);
    }

    /// <summary>
    /// Mediates camera/microphone/geolocation/notifications/clipboard-read so Winser can
    /// remember the decision and let it be revoked later, instead of leaving it entirely to
    /// WebView2's own one-shot prompt.
    /// </summary>
    private async void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (MapPermissionKind(e.PermissionKind) is not { } kind)
        {
            // A kind Winser has no UI for (autoplay, local fonts, ...) - leave it to WebView2's
            // own default rather than mediating something it cannot explain or let you revoke.
            return;
        }

        e.Handled = true;

        if (Tab is not { } tab || tab.IsPrivate)
        {
            // An ephemeral profile that could still hand out the camera would not really be
            // private, so InPrivate windows deny these outright rather than prompting.
            e.State = CoreWebView2PermissionState.Deny;
            return;
        }

        var origin = UrlHelper.OriginKey(e.Uri);
        if (AppServices.Permissions.TryGet(origin, kind) is { } remembered)
        {
            e.State = remembered == SitePermissionState.Allow
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var allow = await tab.RequestPermissionAsync(origin, kind);
            AppServices.Permissions.Set(
                origin, kind, allow ? SitePermissionState.Allow : SitePermissionState.Deny);
            e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static SitePermissionKind? MapPermissionKind(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Camera => SitePermissionKind.Camera,
        CoreWebView2PermissionKind.Microphone => SitePermissionKind.Microphone,
        CoreWebView2PermissionKind.Geolocation => SitePermissionKind.Geolocation,
        CoreWebView2PermissionKind.Notifications => SitePermissionKind.Notifications,
        CoreWebView2PermissionKind.ClipboardRead => SitePermissionKind.ClipboardRead,
        _ => null,
    };

    /// <summary>
    /// Lets ordinary alert/confirm/prompt dialogs show normally, but stops answering once a
    /// page has shown more than <see cref="MaxScriptDialogsPerNavigation"/> on the same
    /// navigation. Taking the deferral and completing it without calling Accept() resolves the
    /// dialog exactly as if the user closed it without answering - no UI, no page hang.
    /// </summary>
    private void OnScriptDialogOpening(CoreWebView2 sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        if (++_scriptDialogCount <= MaxScriptDialogsPerNavigation)
        {
            return;
        }

        var deferral = e.GetDeferral();
        deferral.Complete();
    }

    private void OnWindowCloseRequested(CoreWebView2 sender, object args)
    {
        if (Tab is { } tab)
        {
            tab.Shell.CloseTab(tab);
        }
    }

    private void OnFullScreenElementChanged(CoreWebView2 sender, object args) =>
        Tab?.Shell.SetContentFullScreen(sender.ContainsFullScreenElement);

    private async void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // Winser shows downloads in its own flyout and downloads page.
        e.Handled = true;
        var isPrivate = Tab?.IsPrivate ?? false;

        if (AppServices.Settings.Current.AskWhereToSaveDownloads)
        {
            // The event args are only valid until the deferral completes, so everything that
            // reads them - including handing the operation to the download service - happens first.
            var deferral = e.GetDeferral();
            try
            {
                var chosen = await PickSaveLocationAsync(e.ResultFilePath);
                if (chosen is null)
                {
                    e.Cancel = true;
                    return;
                }

                e.ResultFilePath = chosen;
                AppServices.Downloads.Track(e.DownloadOperation, isPrivate);
            }
            finally
            {
                deferral.Complete();
            }

            return;
        }

        AppServices.Downloads.Track(e.DownloadOperation, isPrivate);
    }

    private void OnStatusBarTextChanged(CoreWebView2 sender, object args) =>
        Tab?.ReportStatusText(sender.StatusBarText);

    private void OnAudioStateChanged(CoreWebView2 sender, object args) =>
        Tab?.ReportAudioState(sender.IsDocumentPlayingAudio, sender.IsMuted);

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            Tab?.ReportNavigationCompleted(false, "The browser engine stopped. Reopen this tab to try again.");
            return;
        }

        Tab?.ReportNavigationCompleted(false, "This page stopped responding and was closed.");
    }

    private void OnDomContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args) =>
        ApplyZoom();

    private void ApplyZoom()
    {
        if (_core is null)
        {
            return;
        }

        var factor = _zoomFactor.ToString("0.###", CultureInfo.InvariantCulture);
        _ = ExecuteScriptAsync(
            $"document.documentElement && (document.documentElement.style.zoom = '{factor}')");
    }

    /// <summary>
    /// The one channel that runs from page script back into the shell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It cannot be closed off. <c>Scripts.ShortcutBridge</c> has to be injected into every
    /// page, because the WinUI WebView2 element exposes no CoreWebView2Controller and therefore
    /// no AcceleratorKeyPressed - a focused page swallows XAML accelerators outright, so the
    /// page itself is the only thing that can hand Ctrl+T back. That means
    /// <c>window.chrome.webview.postMessage</c> is reachable from any site's own JavaScript
    /// too, and every message arriving here has to be treated as attacker-controlled.
    /// </para>
    /// <para>
    /// So the messages are split by what they can actually do. <c>navigate</c> and
    /// <c>newtab</c> point the browser somewhere and belong to the new tab page alone; they
    /// are gated on <see cref="InternalPages.IsTrustedOrigin"/> and on the scheme being one
    /// web content may ask for. <c>key</c> and <c>zoom</c> have to keep working from any page
    /// - that is their entire purpose - and a forged one is indistinguishable from a real key
    /// press, so instead they are held to a rate no hand can exceed and no script can spam.
    /// </para>
    /// </remarks>
    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string payload;
        string source;
        try
        {
            payload = e.TryGetWebMessageAsString();
            source = e.Source;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Not a string message - nothing Winser posts looks like that.
            return;
        }

        if (string.IsNullOrEmpty(payload))
        {
            return;
        }

        var trusted = InternalPages.IsTrustedOrigin(source);

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("t", out var kind))
            {
                return;
            }

            switch (kind.GetString())
            {
                case "key" when AllowShortcut():
                    HandleForwardedKey(root);
                    break;
                case "zoom" when AllowShortcut() && root.TryGetProperty("d", out var direction):
                    if (direction.GetInt32() > 0)
                    {
                        Tab?.ZoomInCommand.Execute(null);
                    }
                    else
                    {
                        Tab?.ZoomOutCommand.Execute(null);
                    }

                    break;
                case "navigate" when trusted && root.TryGetProperty("url", out var url):
                    // The new tab page's own search box and shortcut buttons, so whatever the
                    // user typed there gets the same treatment as the address bar - including
                    // winser:// links, which is why this case is origin-gated.
                    Tab?.Navigate(url.GetString());
                    break;
                case "newtab" when trusted && root.TryGetProperty("url", out var newTabUrl):
                    var target = newTabUrl.GetString() ?? string.Empty;
                    if (UrlHelper.IsWebRequestable(target))
                    {
                        Tab?.Shell.OpenInNewTab(target, background: true);
                    }

                    break;
            }
        }
        catch (JsonException)
        {
            // Pages can post anything; ignore what is not ours.
        }
    }

    /// <summary>
    /// A sliding one-second budget for shortcuts arriving off a page. Key repeat tops out
    /// around 30 keys a second and a real shortcut needs a chord, so nothing a person does
    /// comes close to the cap - while a script posting Ctrl+T in a loop stops after twelve
    /// tabs instead of opening them until the machine gives up.
    /// </summary>
    private bool AllowShortcut()
    {
        var now = Environment.TickCount64;
        while (_shortcutTimes.Count > 0 && now - _shortcutTimes.Peek() >= ShortcutBurstMs)
        {
            _shortcutTimes.Dequeue();
        }

        if (_shortcutTimes.Count >= ShortcutBurstLimit)
        {
            return false;
        }

        _shortcutTimes.Enqueue(now);
        return true;
    }

    private void HandleForwardedKey(JsonElement message)
    {
        if (Tab is null ||
            !message.TryGetProperty("key", out var keyElement) ||
            KeyNames.FromJavaScript(keyElement.GetString()) is not { } key)
        {
            return;
        }

        var ctrl = message.TryGetProperty("ctrl", out var c) && c.GetBoolean();
        var shift = message.TryGetProperty("shift", out var s) && s.GetBoolean();
        var alt = message.TryGetProperty("alt", out var a) && a.GetBoolean();

        // The bridge only ever forwards a Ctrl or Alt chord, a function key, or Escape while
        // full screen. A bare letter on this channel did not come from it, so drop it rather
        // than let a page invent shortcuts the bridge would never have sent.
        var plausible = ctrl || alt
            || key is >= Windows.System.VirtualKey.F1 and <= Windows.System.VirtualKey.F12
            || (key == Windows.System.VirtualKey.Escape && Tab.Shell.IsFullScreen);
        if (!plausible)
        {
            return;
        }

        Tab.Shell.HandleShortcut(key, ctrl, shift, alt);
    }

    // ---------------------------------------------------------------------- helpers

    /// <summary>Hands the new tab page the user's most-visited sites.</summary>
    private static async Task PushTopSitesAsync(CoreWebView2 core)
    {
        var sites = AppServices.History.TopSites()
            .Select(entry => new TopSite(entry.Title, entry.Url))
            .ToList();

        var json = JsonSerializer.Serialize(sites, WinserJsonContext.Default.ListTopSite);
        try
        {
            await core.ExecuteScriptAsync($"window.__winserTopSites && window.__winserTopSites({json})");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
    }

    private async Task<string?> PickSaveLocationAsync(string suggestedPath)
    {
        if (Tab?.Shell.WindowHandle is not { } hwnd || hwnd == IntPtr.Zero)
        {
            return suggestedPath;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedPath),
        };

        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var extension = Path.GetExtension(suggestedPath);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".bin";
        }

        picker.FileTypeChoices.Add($"{extension.TrimStart('.').ToUpperInvariant()} file", [extension]);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private static string Describe(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved =>
            "That site's address could not be found. Check the spelling, or your connection.",
        CoreWebView2WebErrorStatus.ConnectionAborted or
        CoreWebView2WebErrorStatus.ConnectionReset or
        CoreWebView2WebErrorStatus.Disconnected =>
            "The connection was interrupted before the page finished loading.",
        CoreWebView2WebErrorStatus.CannotConnect =>
            "Winser could not reach that server.",
        CoreWebView2WebErrorStatus.Timeout =>
            "The server took too long to respond.",
        CoreWebView2WebErrorStatus.ServerUnreachable =>
            "That server is unreachable from this network.",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or
        CoreWebView2WebErrorStatus.CertificateExpired or
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or
        CoreWebView2WebErrorStatus.CertificateRevoked or
        CoreWebView2WebErrorStatus.CertificateIsInvalid =>
            "This site's security certificate is not trusted, so Winser stopped the connection.",
        CoreWebView2WebErrorStatus.OperationCanceled =>
            "Loading was cancelled.",
        CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired or
        CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired =>
            "This page needs credentials that were not provided.",
        _ => "Winser could not load this page.",
    };
}
