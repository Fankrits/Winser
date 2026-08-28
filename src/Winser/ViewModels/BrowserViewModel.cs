using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Windows.System;
using Winser.Helpers;
using Winser.Models;
using Winser.Services;

namespace Winser.ViewModels;

/// <summary>A bookmarks-bar folder and the bookmarks inside it.</summary>
public sealed record BookmarkFolder(string Name, IReadOnlyList<Bookmark> Items);

/// <summary>
/// One browser window: its tabs, its bookmarks bar, and the window-level commands the menu
/// and keyboard accelerators fire.
/// </summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    private const int MaxRecentlyClosed = 25;

    /// <summary>VK_OEM_PLUS — the unshifted "=" key, which Ctrl+ zoom-in lands on.</summary>
    private const VirtualKey PlusKey = (VirtualKey)187;

    /// <summary>VK_OEM_MINUS.</summary>
    private const VirtualKey MinusKey = (VirtualKey)189;

    /// <summary>
    /// One per window rather than one per tab: a tab is cheap to check (a DateTimeOffset
    /// comparison), so paying for a whole timer's worth of overhead per tab just to save
    /// overhead elsewhere would be backwards.
    /// </summary>
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromMinutes(1);

    private readonly List<ClosedTab> _closedTabs = [];
    private readonly DispatcherTimer _idleSweepTimer;

    private IShellWindow? _window;
    private Task<WebViewProfile>? _profileTask;

    [ObservableProperty]
    public partial BrowserTabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullScreenGlyph), nameof(FullScreenTooltip))]
    [NotifyPropertyChangedFor(nameof(IsBookmarksBarVisible))]
    [NotifyPropertyChangedFor(nameof(IsVerticalTabsPaneVisible), nameof(VerticalTabsPaneVisibility), nameof(IsToolbarVisible))]
    [NotifyPropertyChangedFor(nameof(IsVerticalTabsPaneExpanded))]
    public partial bool IsFullScreen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBookmarksBarVisible))]
    public partial bool ShowBookmarksBar { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVerticalTabsPaneVisible), nameof(VerticalTabsPaneVisibility), nameof(IsToolbarVisible))]
    [NotifyPropertyChangedFor(nameof(IsVerticalTabsPaneExpanded))]
    public partial bool UseVerticalTabs { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVerticalTabsExpanded))]
    [NotifyPropertyChangedFor(nameof(PinGlyph), nameof(PinTooltip))]
    [NotifyPropertyChangedFor(nameof(IsVerticalTabsPaneExpanded))]
    public partial bool IsVerticalTabsPinned { get; set; }

    /// <summary>True while the pointer is over the collapsed vertical tabs hover-zone, expanding the pane as a peek.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVerticalTabsExpanded))]
    [NotifyPropertyChangedFor(nameof(IsVerticalTabsPaneExpanded))]
    public partial bool IsVerticalTabsPointerOver { get; set; }

    /// <summary>
    /// True while the vertical pane's own address bar has focus. Folded into
    /// <see cref="IsVerticalTabsExpanded"/> so the pane stays open across a hover ending
    /// mid-edit, and so Ctrl+L can reach an address bar that starts out fully collapsed:
    /// focusing it needs it visible first, and hovering is not itself a keyboard-reachable action.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVerticalTabsExpanded))]
    [NotifyPropertyChangedFor(nameof(IsVerticalTabsPaneExpanded))]
    public partial bool IsVerticalTabsAddressBarFocused { get; set; }

    [ObservableProperty]
    public partial bool HasActiveDownloads { get; set; }

    public BrowserViewModel(bool isPrivate)
    {
        IsPrivate = isPrivate;
        ShowBookmarksBar = AppServices.Settings.Current.ShowBookmarksBar && !isPrivate;
        UseVerticalTabs = AppServices.Settings.Current.UseVerticalTabs;
        IsVerticalTabsPinned = AppServices.Settings.Current.VerticalTabsPinned;

        AppServices.Bookmarks.Items.CollectionChanged += OnBookmarksChanged;
        AppServices.Downloads.Items.CollectionChanged += OnDownloadsChanged;
        AppServices.Downloads.Started += OnDownloadStarted;
        AppServices.Settings.Changed += OnSettingsChanged;

        HasActiveDownloads = AppServices.Downloads.HasActiveDownloads;

        RebuildBookmarkBar();

        _idleSweepTimer = new DispatcherTimer { Interval = IdleSweepInterval };
        _idleSweepTimer.Tick += OnIdleSweepTick;
        _idleSweepTimer.Start();
    }

    public bool IsPrivate { get; }

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];

    /// <summary>Bookmarks pinned straight onto the bar (no folder).</summary>
    public ObservableCollection<Bookmark> BookmarkBarItems { get; } = [];

    public ObservableCollection<BookmarkFolder> BookmarkBarFolders { get; } = [];

    public DownloadService Downloads => AppServices.Downloads;

    public AppSettings Settings => AppServices.Settings.Current;

    public bool CanReopenClosedTab => _closedTabs.Count > 0;

    public string WindowTitle => IsPrivate ? "Winser — InPrivate" : "Winser";

    /// <summary>Full screen hides all chrome, the bookmarks bar included.</summary>
    public bool IsBookmarksBarVisible => ShowBookmarksBar && !IsFullScreen;

    /// <summary>Full screen hides all chrome, the vertical tab pane included.</summary>
    public bool IsVerticalTabsPaneVisible => UseVerticalTabs && !IsFullScreen;

    /// <summary>
    /// The per-tab toolbar (nav buttons, address bar, zoom/mute/bookmark/downloads/menu) is
    /// redundant with the vertical pane's own nav row and address bar, so it hides the same way
    /// full screen already hides it.
    /// </summary>
    public bool IsToolbarVisible => !IsFullScreen && !UseVerticalTabs;

    /// <summary>Pinned open, actively being peeked at via hover, or its address bar has focus.</summary>
    public bool IsVerticalTabsExpanded => IsVerticalTabsPinned || IsVerticalTabsPointerOver || IsVerticalTabsAddressBarFocused;

    /// <summary>
    /// Whether the pane's actual chrome - header, nav row, address bar, tab list - should be on
    /// screen at all. That chrome lives in its own window (VerticalTabsOverlayWindow), so this
    /// drives whether that window is shown, not a Visibility binding. Collapsed, vertical tabs
    /// shows nothing whatsoever, not even an icon rail; all that is left in MainWindow itself is a
    /// transparent strip down the left edge, there purely to notice the pointer arriving.
    /// </summary>
    public bool IsVerticalTabsPaneExpanded => IsVerticalTabsPaneVisible && IsVerticalTabsExpanded;

    // x:Bind cannot use a Converter anywhere in a Window's own binding scope: the compiler emits
    // a call to SetConverterLookupRoot(this), and Window - unlike Page or UserControl - is not a
    // FrameworkElement, so that call fails to compile (microsoft-ui-xaml#5902, #6369). Exposing
    // the already-converted Visibility here is the documented workaround.
    public Visibility VerticalTabsPaneVisibility => IsVerticalTabsPaneVisible ? Visibility.Visible : Visibility.Collapsed;

    public string FullScreenGlyph => IsFullScreen ? Glyphs.BackToWindow : Glyphs.FullScreen;

    public string FullScreenTooltip => IsFullScreen ? "Exit full screen (F11)" : "Full screen (F11)";

    public string PinGlyph => IsVerticalTabsPinned ? Glyphs.Unpin : Glyphs.Pin;

    public string PinTooltip => IsVerticalTabsPinned ? "Unpin the tab pane" : "Keep the tab pane open";

    public void AttachWindow(IShellWindow window) => _window = window;

    /// <summary>
    /// Called when the window is minimized or loses focus: even the selected tab is off-screen
    /// then, which <see cref="AppSettings.SleepBackgroundTabs"/>'s per-tab freeze never reaches,
    /// since it only ever applies to a tab that is not selected.
    /// </summary>
    public void SetAllTabsMemoryPressure(bool constrained)
    {
        foreach (var tab in Tabs)
        {
            tab.SetMemoryPressure(constrained);
        }
    }

    partial void OnIsFullScreenChanged(bool value)
    {
        foreach (var tab in Tabs)
        {
            tab.SyncFullScreenFlag();
        }
    }

    // All four drive window-level chrome - the hover-zone column, the drag region, whether the
    // native strip is hidden, and whether the pane's own window is on screen - none of which is
    // a binding this view model can satisfy by raising PropertyChanged alone. Routing every write
    // through RefreshTabChrome, rather than only the ones a window event handler starts, is what
    // covers UseVerticalTabs flipping from winser://settings in a different tab entirely, and
    // IsVerticalTabsAddressBarFocused being set by Ctrl+L before any pointer is involved.
    partial void OnUseVerticalTabsChanged(bool value) => _window?.RefreshTabChrome();

    partial void OnIsVerticalTabsPinnedChanged(bool value) => _window?.RefreshTabChrome();

    partial void OnIsVerticalTabsPointerOverChanged(bool value) => _window?.RefreshTabChrome();

    partial void OnIsVerticalTabsAddressBarFocusedChanged(bool value) => _window?.RefreshTabChrome();

    /// <summary>The owning window's HWND, for WinRT pickers that need an owner.</summary>
    public nint WindowHandle => _window?.WindowHandle ?? IntPtr.Zero;

    /// <summary>
    /// The CoreWebView2 environment every tab in this window shares. Normal windows use the
    /// app-wide profile; an InPrivate window gets a throwaway one that is deleted on close.
    /// Cached as a Task so concurrently initialising tabs cannot create two environments.
    /// </summary>
    public Task<WebViewProfile> GetProfileAsync() =>
        _profileTask ??= IsPrivate
            ? AppServices.WebView.CreatePrivateAsync()
            : AppServices.WebView.GetSharedAsync();

    // ------------------------------------------------------------------ tab management

    public BrowserTabViewModel NewTab(string? url = null, bool select = true, int? index = null)
    {
        var tab = new BrowserTabViewModel(this, url);

        if (index is { } at && at >= 0 && at <= Tabs.Count)
        {
            Tabs.Insert(at, tab);
        }
        else
        {
            Tabs.Add(tab);
        }

        if (select)
        {
            SelectedTab = tab;
        }

        return tab;
    }

    /// <summary>Opens a link. Background tabs land immediately after the current one.</summary>
    public BrowserTabViewModel OpenInNewTab(string url, bool background = false)
    {
        var index = SelectedTab is null ? Tabs.Count : Tabs.IndexOf(SelectedTab) + 1;
        return NewTab(url, select: !background, index: index);
    }

    public void CloseTab(BrowserTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        if (!tab.IsNewTabPage && !string.IsNullOrWhiteSpace(tab.Url))
        {
            _closedTabs.Add(new ClosedTab(tab.Url, tab.Title, index));
            if (_closedTabs.Count > MaxRecentlyClosed)
            {
                _closedTabs.RemoveAt(0);
            }

            OnPropertyChanged(nameof(CanReopenClosedTab));
            ReopenClosedTabCommand.NotifyCanExecuteChanged();
        }

        Tabs.RemoveAt(index);
        tab.DetachHost();

        if (Tabs.Count == 0)
        {
            _window?.CloseWindow();
            return;
        }

        if (SelectedTab is null || ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    public void CloseOtherTabs(BrowserTabViewModel keep)
    {
        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList())
        {
            CloseTab(tab);
        }
    }

    public void CloseTabsToTheRight(BrowserTabViewModel from)
    {
        var index = Tabs.IndexOf(from);
        if (index < 0)
        {
            return;
        }

        foreach (var tab in Tabs.Skip(index + 1).ToList())
        {
            CloseTab(tab);
        }
    }

    public void SelectTabAt(int index)
    {
        if (index >= 0 && index < Tabs.Count)
        {
            SelectedTab = Tabs[index];
        }
    }

    /// <summary>Ctrl+9 jumps to the last tab, matching every other browser.</summary>
    public void SelectLastTab()
    {
        if (Tabs.Count > 0)
        {
            SelectedTab = Tabs[^1];
        }
    }

    public void CycleTab(int delta)
    {
        if (Tabs.Count == 0 || SelectedTab is null)
        {
            return;
        }

        var index = (Tabs.IndexOf(SelectedTab) + delta + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[index];
    }

    // ------------------------------------------------------------------------ session

    /// <summary>Fills the window with the startup tabs, restoring the last session if asked.</summary>
    public void StartUp(string? initialUrl = null)
    {
        var settings = AppServices.Settings.Current;

        if (initialUrl is not null)
        {
            NewTab(initialUrl);
            return;
        }

        if (!IsPrivate && settings.Startup == StartupBehavior.RestorePreviousSession)
        {
            var state = AppServices.Session.State;
            foreach (var tab in state.Tabs)
            {
                // session.json is Winser's own file, but it is still a file on disk: validate
                // before trusting it the same way a freshly typed address would not skip
                // scheme checks just because it looks well-formed.
                if (UrlHelper.IsRestorable(tab.Url))
                {
                    NewTab(tab.Url, select: false);
                }
            }

            if (Tabs.Count > 0)
            {
                SelectedTab = Tabs[Math.Clamp(state.SelectedIndex, 0, Tabs.Count - 1)];
            }
        }

        if (Tabs.Count == 0)
        {
            var start = !IsPrivate && settings.Startup == StartupBehavior.HomePage
                ? settings.HomePage
                : InternalPages.NewTab;
            NewTab(start);
        }
    }

    /// <summary>
    /// Clears cookies, cache and site data. Any live tab can do it — they all share one
    /// CoreWebView2 profile — so the first one with a running browser wins.
    /// </summary>
    public async Task<bool> ClearSiteDataAsync()
    {
        foreach (var tab in Tabs)
        {
            if (await tab.TryClearBrowsingDataAsync())
            {
                return true;
            }
        }

        return false;
    }

    public void SaveSession()
    {
        if (IsPrivate)
        {
            return;
        }

        var state = AppServices.Session.State;
        state.Tabs = [.. Tabs
            .Where(t => !t.IsNewTabPage)
            .Select(t => new SessionTab { Url = t.Url, Title = t.Title })];
        state.SelectedIndex = SelectedTab is null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab));
        AppServices.Session.Save();
    }

    public void Detach()
    {
        _idleSweepTimer.Stop();
        _idleSweepTimer.Tick -= OnIdleSweepTick;

        AppServices.Bookmarks.Items.CollectionChanged -= OnBookmarksChanged;
        AppServices.Downloads.Items.CollectionChanged -= OnDownloadsChanged;
        AppServices.Downloads.Started -= OnDownloadStarted;
        AppServices.Settings.Changed -= OnSettingsChanged;

        foreach (var tab in Tabs)
        {
            tab.DetachHost();
        }

        ReleasePrivateProfile();
    }

    /// <summary>
    /// Discards a background tab's renderer once it has sat unwatched past the configured
    /// threshold. A snapshot of <see cref="Tabs"/> rather than a live enumeration: closing this
    /// window's tabs is a user action that can happen at any time, including while this loop is
    /// suspended on the await inside TryDiscardAsync, and mutating an ObservableCollection out
    /// from under an in-progress foreach throws.
    /// </summary>
    private async void OnIdleSweepTick(object? sender, object e)
    {
        var minutes = AppServices.Settings.Current.DiscardIdleTabsAfterMinutes;

        // Downloads aren't tracked per tab (DownloadRecord has no owning-tab reference), so the
        // precise rule - never discard the tab a running download belongs to - isn't something
        // this sweep can check. Skipping the whole window while any download is active is the
        // coarser rule the data model actually supports, and it fails on the safe side: a few
        // extra idle tabs stay resident for the minutes a download takes, rather than risking a
        // renderer closing under a transfer that turns out to depend on it.
        if (minutes <= 0 || HasActiveDownloads)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-minutes);
        foreach (var tab in Tabs.ToList())
        {
            if (ReferenceEquals(tab, SelectedTab) || tab.LastActiveUtc > cutoff)
            {
                continue;
            }

            await tab.TryDiscardAsync();
        }
    }

    /// <summary>Drops the InPrivate data folder once the window's browsers are gone.</summary>
    private void ReleasePrivateProfile()
    {
        if (!IsPrivate || _profileTask is not { IsCompletedSuccessfully: true } task)
        {
            return;
        }

        _profileTask = null;
        AppServices.WebView.ReleasePrivate(task.Result);
    }

    // ---------------------------------------------------------------------- shortcuts

    /// <summary>
    /// The single place browser keyboard shortcuts are interpreted. Both the window's
    /// PreviewKeyDown and WebView2's AcceleratorKeyPressed funnel through here, because a page
    /// with focus swallows XAML keyboard accelerators outright.
    /// </summary>
    public bool HandleShortcut(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        var tab = SelectedTab;

        switch (key)
        {
            case VirtualKey.T when ctrl && shift:
                ReopenClosedTab();
                return true;
            case VirtualKey.T when ctrl:
                NewTab();
                return true;
            case VirtualKey.W when ctrl:
                CloseSelectedTab();
                return true;
            case VirtualKey.N when ctrl && shift:
                NewPrivateWindow();
                return true;
            case VirtualKey.N when ctrl:
                NewWindow();
                return true;
            case VirtualKey.L when ctrl:
            case VirtualKey.D when alt:
                tab?.RequestAddressFocus();
                return true;
            case VirtualKey.D when ctrl:
                tab?.ToggleBookmarkCommand.Execute(null);
                return true;
            case VirtualKey.F when ctrl:
                tab?.OpenFindCommand.Execute(null);
                return true;
            case VirtualKey.H when ctrl:
                OpenHistory();
                return true;
            case VirtualKey.J when ctrl:
                OpenDownloads();
                return true;
            case VirtualKey.O when ctrl && shift:
                OpenBookmarks();
                return true;
            case VirtualKey.B when ctrl && shift:
                ToggleBookmarksBar();
                return true;
            case VirtualKey.P when ctrl:
                tab?.PrintCommand.Execute(null);
                return true;
            case VirtualKey.R when ctrl && shift:
            case VirtualKey.F5 when ctrl:
                tab?.HardReloadCommand.Execute(null);
                return true;
            case VirtualKey.R when ctrl:
            case VirtualKey.F5:
                tab?.ReloadOrStopCommand.Execute(null);
                return true;
            case VirtualKey.F11:
                ToggleFullScreen();
                return true;
            case VirtualKey.F12:
                tab?.OpenDevToolsCommand.Execute(null);
                return true;
            case VirtualKey.Tab when ctrl:
                CycleTab(shift ? -1 : 1);
                return true;
            case VirtualKey.Number9 when ctrl:
            case VirtualKey.NumberPad9 when ctrl:
                SelectLastTab();
                return true;
            case >= VirtualKey.Number1 and <= VirtualKey.Number8 when ctrl:
                SelectTabAt(key - VirtualKey.Number1);
                return true;
            case >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad8 when ctrl:
                SelectTabAt(key - VirtualKey.NumberPad1);
                return true;
            case VirtualKey.Left when alt:
            case VirtualKey.GoBack:
                tab?.GoBackCommand.Execute(null);
                return true;
            case VirtualKey.Right when alt:
            case VirtualKey.GoForward:
                tab?.GoForwardCommand.Execute(null);
                return true;
            case VirtualKey.Home when alt:
                tab?.GoHomeCommand.Execute(null);
                return true;
            case VirtualKey.Number0 when ctrl:
            case VirtualKey.NumberPad0 when ctrl:
                tab?.ResetZoomCommand.Execute(null);
                return true;
            case VirtualKey.Add when ctrl:
            case PlusKey when ctrl:
                tab?.ZoomInCommand.Execute(null);
                return true;
            case VirtualKey.Subtract when ctrl:
            case MinusKey when ctrl:
                tab?.ZoomOutCommand.Execute(null);
                return true;
            case VirtualKey.Escape when IsFullScreen:
                ToggleFullScreen();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Called when a page enters or leaves its own (video) full screen.</summary>
    public void SetContentFullScreen(bool fullScreen)
    {
        if (fullScreen != IsFullScreen)
        {
            _window?.SetFullScreen(fullScreen);
        }
    }

    // ----------------------------------------------------------------------- commands

    [RelayCommand]
    private void AddTab() => NewTab();

    [RelayCommand]
    private void CloseSelectedTab()
    {
        if (SelectedTab is { } tab)
        {
            CloseTab(tab);
        }
    }

    [RelayCommand(CanExecute = nameof(CanReopenClosedTab))]
    private void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0)
        {
            return;
        }

        var closed = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        NewTab(closed.Url, select: true, index: Math.Min(closed.Index, Tabs.Count));

        OnPropertyChanged(nameof(CanReopenClosedTab));
        ReopenClosedTabCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void NewWindow() => WindowManager.CreateWindow();

    [RelayCommand]
    private void NewPrivateWindow() => WindowManager.CreateWindow(isPrivate: true);

    [RelayCommand]
    private void OpenSettings() => ShowNativePage(InternalPages.Settings);

    [RelayCommand]
    private void OpenHistory() => ShowNativePage(InternalPages.History);

    [RelayCommand]
    private void OpenDownloads() => ShowNativePage(InternalPages.Downloads);

    [RelayCommand]
    private void OpenBookmarks() => ShowNativePage(InternalPages.Bookmarks);

    [RelayCommand]
    private void ToggleBookmarksBar()
    {
        ShowBookmarksBar = !ShowBookmarksBar;
        if (!IsPrivate)
        {
            AppServices.Settings.Current.ShowBookmarksBar = ShowBookmarksBar;
            AppServices.Settings.Commit();
        }
    }

    /// <summary>
    /// Whether the vertical pane stays expanded is not privacy-sensitive the way the bookmarks
    /// bar is, so unlike <see cref="ToggleBookmarksBar"/> this persists for InPrivate windows too.
    /// </summary>
    [RelayCommand]
    private void ToggleVerticalTabsPinned()
    {
        IsVerticalTabsPinned = !IsVerticalTabsPinned;
        AppServices.Settings.Current.VerticalTabsPinned = IsVerticalTabsPinned;
        AppServices.Settings.Commit();
    }

    [RelayCommand]
    private void ToggleFullScreen() => _window?.SetFullScreen(!IsFullScreen);

    [RelayCommand]
    private void FocusAddressBar() => _window?.FocusAddressBar();

    [RelayCommand]
    private void CloseWindow() => _window?.CloseWindow();

    [RelayCommand]
    private void NavigateSelectedTab(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        (SelectedTab ?? NewTab()).NavigateResolved(url);
    }

    // ------------------------------------------------------------------------ private

    /// <summary>Focuses an existing tab for a native page instead of opening a duplicate.</summary>
    private void ShowNativePage(string url)
    {
        var kind = InternalPages.ResolveKind(url);
        var existing = Tabs.FirstOrDefault(t => t.Kind == kind);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        if (SelectedTab is { IsNewTabPage: true, IsLoading: false } blank)
        {
            blank.NavigateResolved(url);
            return;
        }

        NewTab(url);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!IsPrivate)
        {
            ShowBookmarksBar = AppServices.Settings.Current.ShowBookmarksBar;
        }

        UseVerticalTabs = AppServices.Settings.Current.UseVerticalTabs;

        foreach (var tab in Tabs)
        {
            tab.ApplyPreferences();
        }
    }

    private void OnBookmarksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildBookmarkBar();
        foreach (var tab in Tabs)
        {
            tab.RefreshBookmarkState();
        }
    }

    private void OnDownloadsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (DownloadItem item in e.NewItems)
            {
                item.PropertyChanged += OnDownloadItemChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (DownloadItem item in e.OldItems)
            {
                item.PropertyChanged -= OnDownloadItemChanged;
            }
        }

        HasActiveDownloads = AppServices.Downloads.HasActiveDownloads;
    }

    private void OnDownloadItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItem.State))
        {
            HasActiveDownloads = AppServices.Downloads.HasActiveDownloads;
        }
    }

    private void OnDownloadStarted(object? sender, DownloadItem e) => HasActiveDownloads = true;

    private void RebuildBookmarkBar()
    {
        BookmarkBarItems.Clear();
        foreach (var bookmark in AppServices.Bookmarks.Items.Where(b => string.IsNullOrEmpty(b.Folder)))
        {
            BookmarkBarItems.Add(bookmark);
        }

        BookmarkBarFolders.Clear();
        foreach (var group in AppServices.Bookmarks.Items
                     .Where(b => !string.IsNullOrEmpty(b.Folder))
                     .GroupBy(b => b.Folder!, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            BookmarkBarFolders.Add(new BookmarkFolder(group.Key, [.. group]));
        }
    }

    private sealed record ClosedTab(string Url, string Title, int Index);
}
