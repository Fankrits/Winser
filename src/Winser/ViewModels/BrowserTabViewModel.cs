using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Winser.Helpers;
using Winser.Models;
using Winser.Services;

namespace Winser.ViewModels;

/// <summary>
/// One browser tab. Owns everything about the page except the CoreWebView2 itself, which the
/// tab reaches through <see cref="IWebViewHost"/> once the control behind it is ready.
/// </summary>
public sealed partial class BrowserTabViewModel : ObservableObject
{
    private static readonly double[] ZoomLadder =
        [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    private static FontFamily? _symbolFont;

    private readonly BrowserViewModel _shell;

    private IWebViewHost? _host;
    private bool _syncingZoom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeb), nameof(IsSettings), nameof(IsHistory))]
    [NotifyPropertyChangedFor(nameof(IsDownloads), nameof(IsBookmarks), nameof(IsNativePage))]
    private BrowserTabKind _kind;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostLabel), nameof(Security), nameof(SecurityGlyph))]
    [NotifyPropertyChangedFor(nameof(SecurityTooltip), nameof(IsNewTabPage))]
    private string _url;

    [ObservableProperty]
    private string _addressText;

    [ObservableProperty]
    private IconSource? _tabIcon;

    [ObservableProperty]
    private ImageSource? _faviconImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReloadGlyph), nameof(ReloadTooltip))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool _canGoBack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
    private bool _canGoForward;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BookmarkGlyph), nameof(BookmarkTooltip))]
    private bool _isBookmarked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomLabel), nameof(IsZoomed))]
    private double _zoomFactor = 1.0;

    [ObservableProperty]
    private bool _isAudioPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteGlyph), nameof(MuteTooltip))]
    private bool _isMuted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isFindOpen;

    [ObservableProperty]
    private string _findQuery = string.Empty;

    [ObservableProperty]
    private bool _findMatchCase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindStatus))]
    private int _findCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindStatus))]
    private int _findIndex;

    /// <summary>False on pages where the browser's CSS Custom Highlight API is unavailable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindStatus))]
    private bool _findSupported = true;

    /// <summary>
    /// Set once the tab has shown web content and never unset, so hopping to
    /// <c>winser://settings</c> and back does not tear down the WebView2 (and its history).
    /// </summary>
    [ObservableProperty]
    private bool _needsWebView;

    public BrowserTabViewModel(BrowserViewModel shell, string? url = null)
    {
        _shell = shell;

        var target = string.IsNullOrWhiteSpace(url)
            ? InternalPages.NewTab
            : InternalPages.Normalize(url);

        _kind = InternalPages.ResolveKind(target);
        _url = target;
        _title = InternalPages.Title(_kind);
        _addressText = UrlHelper.ForDisplay(target);
        _zoomFactor = AppServices.Settings.Current.DefaultZoomFactor;
        _needsWebView = _kind == BrowserTabKind.Web;

        if (_kind == BrowserTabKind.Web)
        {
            PendingUrl = InternalPages.ToNavigationTarget(target);
        }

        RefreshIcon();
        RefreshBookmarkState();
    }

    /// <summary>Raised when something (Ctrl+L, a new tab) wants the address box focused.</summary>
    public event EventHandler? AddressFocusRequested;

    /// <summary>The window this tab belongs to.</summary>
    public BrowserViewModel Shell => _shell;

    /// <summary>True for tabs in an InPrivate window: nothing is written to history.</summary>
    public bool IsPrivate => _shell.IsPrivate;

    /// <summary>Where the host should navigate as soon as CoreWebView2 comes up.</summary>
    public string? PendingUrl { get; private set; }

    public ObservableCollection<AddressSuggestion> Suggestions { get; } = [];

    /// <summary>Set by the view while the address box has keyboard focus.</summary>
    public bool IsAddressFocused { get; set; }

    public bool IsWeb => Kind == BrowserTabKind.Web;

    public bool IsSettings => Kind == BrowserTabKind.Settings;

    public bool IsHistory => Kind == BrowserTabKind.History;

    public bool IsDownloads => Kind == BrowserTabKind.Downloads;

    public bool IsBookmarks => Kind == BrowserTabKind.Bookmarks;

    public bool IsNativePage => Kind != BrowserTabKind.Web;

    public bool IsNewTabPage => InternalPages.IsNewTab(Url);

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    public string HostLabel => UrlHelper.HostLabel(Url);

    public PageSecurity Security => UrlHelper.Security(Url);

    public string SecurityGlyph => Security switch
    {
        PageSecurity.Secure => Glyphs.Lock,
        PageSecurity.Insecure => Glyphs.Warning,
        PageSecurity.Local => Glyphs.Info,
        _ => Glyphs.Search,
    };

    public string SecurityTooltip => Security switch
    {
        PageSecurity.Secure => $"Connection to {HostLabel} is encrypted",
        PageSecurity.Insecure => "Not secure — this connection is not encrypted",
        PageSecurity.Local => "This page is part of Winser or came from this device",
        _ => "Search or enter web address",
    };

    public string ReloadGlyph => IsLoading ? Glyphs.Stop : Glyphs.Refresh;

    public string ReloadTooltip => IsLoading ? "Stop loading (Esc)" : "Reload (Ctrl+R)";

    public string BookmarkGlyph => IsBookmarked ? Glyphs.FavoriteFilled : Glyphs.Favorite;

    public string BookmarkTooltip => IsBookmarked ? "Remove bookmark (Ctrl+D)" : "Bookmark this page (Ctrl+D)";

    public string MuteGlyph => IsMuted ? Glyphs.Mute : Glyphs.Volume;

    public string MuteTooltip => IsMuted ? "Unmute this tab" : "Mute this tab";

    public bool IsZoomed => Math.Abs(ZoomFactor - 1.0) > 0.001;

    public string ZoomLabel => $"{ZoomFactor * 100:0}%";

    public string FindStatus => !FindSupported
        ? "Find is unavailable on this page"
        : FindCount == 0
            ? string.IsNullOrEmpty(FindQuery) ? string.Empty : "No results"
            : $"{FindIndex}/{FindCount}";

    // ---------------------------------------------------------------- host wiring

    public void AttachHost(IWebViewHost host)
    {
        _host = host;
        _syncingZoom = true;
        host.ZoomFactor = ZoomFactor;
        _syncingZoom = false;

        if (PendingUrl is { } pending)
        {
            PendingUrl = null;
            host.Navigate(pending);
        }

        host.SyncFullScreenFlag(_shell.IsFullScreen);
    }

    public void DetachHost()
    {
        _host?.Release();
        _host = null;
    }

    public void RequestAddressFocus() => AddressFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Pushes changed settings (theme, tracking prevention, ...) into a live page.</summary>
    public void ApplyPreferences() => _host?.ApplyPreferences();

    /// <summary>Pushes the window's full-screen state into this tab's page. See IWebViewHost.</summary>
    public void SyncFullScreenFlag() => _host?.SyncFullScreenFlag(_shell.IsFullScreen);

    /// <summary>Clears site data through this tab's browser, if it has one yet.</summary>
    public async Task<bool> TryClearBrowsingDataAsync()
    {
        if (_host is not { IsReady: true } host)
        {
            return false;
        }

        await host.ClearBrowsingDataAsync();
        return true;
    }

    // ------------------------------------------------------------------ navigation

    /// <summary>Navigates to whatever the user typed, searching when it is not an address.</summary>
    public void Navigate(string? input) =>
        NavigateResolved(UrlHelper.Resolve(input, AppServices.Settings.SearchEngine));

    /// <summary>Navigates to an already-resolved URL (a link, a bookmark, a history entry).</summary>
    public void NavigateResolved(string target)
    {
        var kind = InternalPages.ResolveKind(target);
        ErrorMessage = null;

        if (kind != BrowserTabKind.Web)
        {
            Kind = kind;
            Url = InternalPages.Normalize(target);
            Title = InternalPages.Title(kind);
            AddressText = UrlHelper.ForDisplay(Url);
            IsLoading = false;
            CloseFind();
            RefreshIcon();
            return;
        }

        Kind = BrowserTabKind.Web;
        NeedsWebView = true;
        Url = InternalPages.FromNavigationTarget(target);
        AddressText = UrlHelper.ForDisplay(Url);

        var navigationTarget = InternalPages.ToNavigationTarget(target);
        if (_host is { IsReady: true } host)
        {
            host.Navigate(navigationTarget);
        }
        else
        {
            PendingUrl = navigationTarget;
        }
    }

    public void ReportNavigationStarting(string uri)
    {
        IsLoading = true;
        ErrorMessage = null;
        SyncUrlFromBrowser(uri);
    }

    public void ReportSourceChanged(string uri) => SyncUrlFromBrowser(uri);

    public void ReportNavigationCompleted(bool success, string? failureText)
    {
        IsLoading = false;
        ErrorMessage = success ? null : failureText;

        if (success && !IsPrivate)
        {
            AppServices.History.Record(Url, Title);
        }

        if (IsFindOpen && !string.IsNullOrEmpty(FindQuery))
        {
            _ = RunFindAsync(SearchCall());
        }
    }

    public void ReportTitleChanged(string? title)
    {
        Title = InternalPages.IsNewTab(Url)
            ? "New tab"
            : string.IsNullOrWhiteSpace(title) ? UrlHelper.HostLabel(Url) : title;

        if (!IsPrivate)
        {
            AppServices.History.UpdateTitle(Url, Title);
        }
    }

    public void ReportHistoryChanged(bool canGoBack, bool canGoForward)
    {
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
    }

    public void ReportFavicon(ImageSource? image)
    {
        FaviconImage = image;
        RefreshIcon();
    }

    public void ReportStatusText(string? text) => StatusText = text ?? string.Empty;

    public void ReportAudioState(bool playing, bool muted)
    {
        IsAudioPlaying = playing;
        IsMuted = muted;
    }

    // -------------------------------------------------------------------- commands

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => _host?.GoBack();

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => _host?.GoForward();

    [RelayCommand]
    private void ReloadOrStop()
    {
        if (IsNativePage)
        {
            return;
        }

        if (IsLoading)
        {
            _host?.Stop();
            IsLoading = false;
        }
        else
        {
            _host?.Reload(bypassCache: false);
        }
    }

    [RelayCommand]
    private void HardReload() => _host?.Reload(bypassCache: true);

    [RelayCommand]
    private void GoHome() => Navigate(AppServices.Settings.Current.HomePage);

    [RelayCommand]
    private void ToggleBookmark()
    {
        if (string.IsNullOrWhiteSpace(Url) || IsNewTabPage)
        {
            return;
        }

        IsBookmarked = AppServices.Bookmarks.Toggle(Url, Title);
    }

    [RelayCommand]
    private void ZoomIn() => ZoomFactor = NextZoom(ZoomFactor, 1);

    [RelayCommand]
    private void ZoomOut() => ZoomFactor = NextZoom(ZoomFactor, -1);

    [RelayCommand]
    private void ResetZoom() => ZoomFactor = 1.0;

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        _host?.SetMuted(IsMuted);
    }

    [RelayCommand]
    private void OpenDevTools() => _host?.OpenDevTools();

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (_host is { IsReady: true } host)
        {
            await host.PrintAsync();
        }
    }

    [RelayCommand]
    private void CopyLink()
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(Url);
        Clipboard.SetContent(package);
    }

    [RelayCommand]
    private void Duplicate() => _shell.NewTab(Url, select: true);

    [RelayCommand]
    private void Close() => _shell.CloseTab(this);

    [RelayCommand]
    private void CloseOthers() => _shell.CloseOtherTabs(this);

    [RelayCommand]
    private void CloseToTheRight() => _shell.CloseTabsToTheRight(this);

    [RelayCommand]
    private void OpenFind()
    {
        if (IsNativePage)
        {
            return;
        }

        IsFindOpen = true;
    }

    [RelayCommand]
    private void CloseFind()
    {
        if (!IsFindOpen)
        {
            return;
        }

        IsFindOpen = false;
        FindQuery = string.Empty;
        FindCount = 0;
        FindIndex = 0;
        _ = RunFindAsync("window.__winserFind && window.__winserFind.clear()");
    }

    [RelayCommand]
    private Task FindNextAsync() => RunFindAsync("window.__winserFind && window.__winserFind.next()");

    [RelayCommand]
    private Task FindPreviousAsync() => RunFindAsync("window.__winserFind && window.__winserFind.previous()");

    // --------------------------------------------------------------- address bar

    /// <summary>Rebuilds the address bar dropdown for the text the user has typed so far.</summary>
    public void UpdateSuggestions(string? query)
    {
        Suggestions.Clear();

        var text = query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var engine = AppServices.Settings.SearchEngine;
        Suggestions.Add(UrlHelper.LooksNavigable(text)
            ? new AddressSuggestion(SuggestionKind.Navigate, text, "Open this address",
                UrlHelper.Resolve(text, engine))
            : new AddressSuggestion(SuggestionKind.Search, text, $"Search with {engine.Name}",
                engine.BuildSearchUrl(text)));

        foreach (var bookmark in AppServices.Bookmarks.Items
                     .Where(b => b.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                 b.Url.Contains(text, StringComparison.OrdinalIgnoreCase))
                     .Take(3))
        {
            Suggestions.Add(new AddressSuggestion(
                SuggestionKind.Bookmark, bookmark.Title, bookmark.Url, bookmark.Url));
        }

        if (IsPrivate)
        {
            return;
        }

        foreach (var entry in AppServices.History.Suggest(text))
        {
            if (Suggestions.Any(s => string.Equals(s.Target, entry.Url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Suggestions.Add(new AddressSuggestion(
                SuggestionKind.History, entry.Title, entry.Url, entry.Url));
        }
    }

    public void RefreshBookmarkState() => IsBookmarked = AppServices.Bookmarks.Contains(Url);

    // --------------------------------------------------------------------- private

    partial void OnUrlChanged(string value)
    {
        RefreshBookmarkState();
        RefreshIcon();
    }

    partial void OnZoomFactorChanged(double value)
    {
        if (!_syncingZoom && _host is { IsReady: true } host)
        {
            host.ZoomFactor = value;
        }
    }

    partial void OnFindQueryChanged(string value) => _ = RunFindAsync(SearchCall());

    partial void OnFindMatchCaseChanged(bool value) => _ = RunFindAsync(SearchCall());

    partial void OnIsFindOpenChanged(bool value)
    {
        if (value)
        {
            FindSupported = true;
        }
    }

    private string SearchCall() =>
        $"window.__winserFind && window.__winserFind.search({JsonSerializer.Serialize(FindQuery)}, {(FindMatchCase ? "true" : "false")})";

    private async Task RunFindAsync(string script)
    {
        if (_host is not { IsReady: true } host)
        {
            return;
        }

        string json;
        try
        {
            json = await host.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        if (string.IsNullOrEmpty(json) || json is "null" or "false")
        {
            FindCount = 0;
            FindIndex = 0;
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            FindCount = root.TryGetProperty("count", out var count) ? count.GetInt32() : 0;
            FindIndex = root.TryGetProperty("active", out var active) ? active.GetInt32() : 0;
            FindSupported = !root.TryGetProperty("supported", out var supported) || supported.GetBoolean();
        }
        catch (JsonException)
        {
            FindCount = 0;
            FindIndex = 0;
        }
    }

    private void SyncUrlFromBrowser(string uri)
    {
        Url = InternalPages.FromNavigationTarget(uri);
        if (!IsAddressFocused)
        {
            AddressText = UrlHelper.ForDisplay(Url);
        }
    }

    private void RefreshIcon()
    {
        TabIcon = Kind switch
        {
            BrowserTabKind.Settings => GlyphIcon(Glyphs.Settings),
            BrowserTabKind.History => GlyphIcon(Glyphs.History),
            BrowserTabKind.Downloads => GlyphIcon(Glyphs.Download),
            BrowserTabKind.Bookmarks => GlyphIcon(Glyphs.FavoriteList),
            _ when FaviconImage is { } image => new ImageIconSource { ImageSource = image },
            _ when IsNewTabPage => GlyphIcon(Glyphs.Add),
            _ => GlyphIcon(Glyphs.Globe),
        };
    }

    private static double NextZoom(double current, int direction)
    {
        if (direction > 0)
        {
            foreach (var step in ZoomLadder)
            {
                if (step > current + 0.001)
                {
                    return step;
                }
            }

            return ZoomLadder[^1];
        }

        for (var i = ZoomLadder.Length - 1; i >= 0; i--)
        {
            if (ZoomLadder[i] < current - 0.001)
            {
                return ZoomLadder[i];
            }
        }

        return ZoomLadder[0];
    }

    private static IconSource GlyphIcon(string glyph) => new FontIconSource
    {
        Glyph = glyph,
        FontFamily = SymbolFont,
    };

    /// <summary>The theme's icon font ("Segoe Fluent Icons" on Win11, MDL2 Assets on Win10).</summary>
    private static FontFamily SymbolFont
    {
        get
        {
            if (_symbolFont is not null)
            {
                return _symbolFont;
            }

            var resources = Application.Current?.Resources;
            _symbolFont = resources is not null &&
                          resources.TryGetValue("SymbolThemeFontFamily", out var value) &&
                          value is FontFamily family
                ? family
                : new FontFamily("Segoe Fluent Icons");
            return _symbolFont;
        }
    }
}
