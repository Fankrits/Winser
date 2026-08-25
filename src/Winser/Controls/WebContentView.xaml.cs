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

    private CoreWebView2? _core;
    private double _zoomFactor = 1.0;
    private bool _initializing;
    private bool _released;

    public WebContentView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public BrowserTabViewModel? Tab
    {
        get => (BrowserTabViewModel?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    // ------------------------------------------------------------------ IWebViewHost

    public bool IsReady => _core is not null && !_released;

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

    public void FocusContent() => Browser.Focus(FocusState.Programmatic);

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

    public void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        Unhook();

        try
        {
            Browser.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[Winser] WebView2 close failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------- lifecycle

    private static void OnTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WebContentView view && view.IsLoaded)
        {
            _ = view.InitializeAsync();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _ = InitializeAsync();

    private async Task InitializeAsync()
    {
        if (_initializing || _core is not null || _released || Tab is null)
        {
            return;
        }

        _initializing = true;
        try
        {
            var profile = await Tab.Shell.GetProfileAsync();
            await Browser.EnsureCoreWebView2Async(profile.Environment);
        }
        catch (Exception ex)
        {
            // A tab that cannot start its browser must still render its error state rather
            // than take the window down with it. The overwhelmingly likely cause is a machine
            // without the Evergreen runtime, so say that rather than showing an HRESULT.
            Tab?.ReportNavigationCompleted(
                false,
                AppServices.WebView.RuntimeVersion is null
                    ? "The Microsoft Edge WebView2 Runtime is not installed. Winser renders pages " +
                      "with WebView2, so it needs the runtime before it can browse."
                    : $"WebView2 could not start: {ex.Message}");
            return;
        }
        finally
        {
            _initializing = false;
        }

        if (_released || Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        _core = core;
        await ConfigureAsync(core);
        Hook(core);
        Tab?.AttachHost(this);
    }

    private static async Task ConfigureAsync(CoreWebView2 core)
    {
        var settings = AppServices.Settings.Current;

        core.Settings.AreDevToolsEnabled = settings.EnableDevTools;
        core.Settings.IsScriptEnabled = settings.EnableJavaScript;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDefaultScriptDialogsEnabled = true;
        core.Settings.IsSwipeNavigationEnabled = true;
        // Winser owns zoom (see ZoomFactor), and WebView2's own Ctrl+scroll zoom would be a
        // second, invisible source of truth. The injected script forwards Ctrl+wheel instead.
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        // Winser draws its own link preview, so the built-in one would just double up.
        core.Settings.IsStatusBarEnabled = false;

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
        catch (Exception ex) when (ex is NotImplementedException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            Debug.WriteLine($"[Winser] Profile setting unavailable on this runtime: {ex.Message}");
        }

        // Serves Assets\Web (the new tab page) from https://assets.winser/ so it runs on a
        // normal secure origin instead of file://.
        if (Directory.Exists(AppPaths.WebAssets))
        {
            core.SetVirtualHostNameToFolderMapping(
                InternalPages.VirtualHost,
                AppPaths.WebAssets,
                CoreWebView2HostResourceAccessKind.Allow);
        }

        await core.AddScriptToExecuteOnDocumentCreatedAsync(Scripts.FindInPage);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Scripts.ShortcutBridge);
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

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e) =>
        Tab?.ReportNavigationStarting(e.Uri);

    private void OnSourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs e) =>
        Tab?.ReportSourceChanged(sender.Source);

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Tab?.ReportNavigationCompleted(e.IsSuccess, e.IsSuccess ? null : Describe(e.WebErrorStatus));

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
        e.Handled = true;
        Tab.Shell.OpenInNewTab(e.Uri, background: !e.IsUserInitiated);
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
                AppServices.Downloads.Track(e.DownloadOperation);
            }
            finally
            {
                deferral.Complete();
            }

            return;
        }

        AppServices.Downloads.Track(e.DownloadOperation);
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

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string payload;
        try
        {
            payload = e.TryGetWebMessageAsString();
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
                case "key":
                    HandleForwardedKey(root);
                    break;
                case "zoom" when root.TryGetProperty("d", out var direction):
                    if (direction.GetInt32() > 0)
                    {
                        Tab?.ZoomInCommand.Execute(null);
                    }
                    else
                    {
                        Tab?.ZoomOutCommand.Execute(null);
                    }

                    break;
                case "navigate" when root.TryGetProperty("url", out var url):
                    Tab?.Navigate(url.GetString());
                    break;
                case "newtab" when root.TryGetProperty("url", out var newTabUrl):
                    Tab?.Shell.OpenInNewTab(newTabUrl.GetString() ?? string.Empty, background: true);
                    break;
            }
        }
        catch (JsonException)
        {
            // Pages can post anything; ignore what is not ours.
        }
    }

    private void HandleForwardedKey(JsonElement message)
    {
        if (Tab is null ||
            !message.TryGetProperty("key", out var keyElement) ||
            KeyNames.FromJavaScript(keyElement.GetString()) is not { } key)
        {
            return;
        }

        Tab.Shell.HandleShortcut(
            key,
            message.TryGetProperty("ctrl", out var c) && c.GetBoolean(),
            message.TryGetProperty("shift", out var s) && s.GetBoolean(),
            message.TryGetProperty("alt", out var a) && a.GetBoolean());
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
