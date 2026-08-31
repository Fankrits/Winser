using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Winser.Helpers;
using Winser.Services;
using Winser.ViewModels;

namespace Winser;

/// <summary>
/// A browser window: a tab strip that doubles as the title bar, and one
/// <see cref="Views.BrowserTabPage"/> per tab.
/// </summary>
public sealed partial class MainWindow : Window, IShellWindow
{
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 860;
    private const int CascadeStep = 28;

    // 16, not something thinner: Windows reserves roughly the outer 8px of every edge of a
    // resizable window for its own invisible resize-drag hit-testing (WM_NCHITTEST), handled
    // before a pointer event ever reaches XAML. A hover-zone at or below that width can end up
    // entirely inside the OS's resize border, where PointerEntered never fires at all - which
    // is indistinguishable from "vertical tabs doesn't work" from the pointer's own vantage
    // point. The extra margin is free: this strip is fully transparent regardless of width.
    private const double VerticalTabsHoverZoneWidth = 16;
    private const double VerticalTabsExpandedWidth = 240;

    /// <summary>
    /// Grace period between the pointer leaving the vertical tabs hover surface and the pane
    /// actually collapsing - long enough to cover the gap while the pointer crosses from the
    /// strip into the pane's own window, which are two separate windows exchanging leave and
    /// enter events with no ordering guarantee. See NotifyVerticalTabsPointerExited.
    /// </summary>
    private const int VerticalTabsCollapseDelayMs = 250;

    private bool _isClosing;
    private bool _isWindowActive = true;

    /// <summary>Created on first use: only vertical tabs mode ever needs it.</summary>
    private VerticalTabsOverlayWindow? _overlay;

    private DispatcherTimer? _verticalTabsCollapseTimer;

    public MainWindow(bool isPrivate = false, string? initialUrl = null)
    {
        IsPrivate = isPrivate;
        ViewModel = new BrowserViewModel(isPrivate);

        InitializeComponent();

        ViewModel.AttachWindow(this);
        WindowManager.Register(this);

        Title = ViewModel.WindowTitle;
        TrySetWindowIcon();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(CustomDragRegion);

        // The vertical pane's own copy of this badge is set by VerticalTabsOverlayWindow, which
        // reads IsPrivate off this window when it is created.
        PrivateBadge.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;

        ApplyTheme();
        UpdateChromeLayout();
        AppServices.Settings.Changed += OnSettingsChanged;

        RootGrid.ActualThemeChanged += OnActualThemeChanged;
        RootGrid.PreviewKeyDown += OnPreviewKeyDown;
        RootGrid.SizeChanged += (_, _) =>
        {
            UpdateCaptionInset();

            // The pane is a free-floating window, so nothing repositions it on the owner's behalf
            // the way layout would for an inline child - it has to be told, on every resize.
            UpdateVerticalTabsOverlay();
        };

        RestorePlacement();
        Activated += OnFirstActivated;
        Activated += OnActivationChanged;
        AppWindow.Changed += OnAppWindowChanged;
        Closed += OnClosed;

        ViewModel.StartUp(initialUrl);
    }

    public BrowserViewModel ViewModel { get; }

    public bool IsPrivate { get; }

    public bool IsFullScreen => ViewModel.IsFullScreen;

    public nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    XamlRoot? IShellWindow.XamlRoot => RootGrid.XamlRoot;

    // ------------------------------------------------------------------ IShellWindow

    public void SetFullScreen(bool fullScreen)
    {
        AppWindow.SetPresenter(fullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Overlapped);

        ViewModel.IsFullScreen = fullScreen;
        UpdateChromeLayout();
    }

    public void RefreshTabChrome() => UpdateChromeLayout();

    public void FocusAddressBar()
    {
        if (ViewModel.UseVerticalTabs)
        {
            // Collapsed, the pane's window is hidden and the address bar inside it cannot accept
            // focus at all. Flagging it open puts the pane up (via UpdateChromeLayout below) and,
            // unlike a hover peek, activates it - keyboard input needs a foreground window.
            ViewModel.IsVerticalTabsAddressBarFocused = true;
            UpdateVerticalTabsOverlay(activate: true);
            _overlay?.FocusAddressBar();
        }
        else
        {
            ViewModel.SelectedTab?.RequestAddressFocus();
        }
    }

    public void CloseWindow() => Close();

    // --------------------------------------------------------------------- lifecycle

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        UpdateCaptionInset();

        // The constructor's own UpdateChromeLayout() call runs before the first layout pass, so
        // TabStripHeight was still falling back to FallbackTabStripHeight then. If vertical tabs
        // was already on at startup (a persisted setting, not something toggled just now), that
        // stale guess would otherwise never get corrected: nothing else changes UseVerticalTabs/
        // IsFullScreen/pinned/hover state on its own just because layout finished. Once first
        // activated, a real layout pass has already run, so this re-applies the actual measured
        // height instead.
        UpdateChromeLayout();
    }

    private void OnActivationChanged(object sender, WindowActivatedEventArgs args)
    {
        _isWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        UpdateMemoryPressure();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange)
        {
            UpdateMemoryPressure();
        }

        // Dragging the window moves its client area out from under the pane, which - being its own
        // top-level window - does not come along by itself.
        if (args.DidPositionChange || args.DidSizeChange)
        {
            UpdateVerticalTabsOverlay();
        }
    }

    /// <summary>
    /// Re-evaluates from both signals together rather than reacting to each in isolation:
    /// minimizing normally deactivates the window too, and handling them independently would
    /// mean the second event undoing what the first just did with a now-stale view of the
    /// other one.
    /// </summary>
    private void UpdateMemoryPressure()
    {
        var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        ViewModel.SetAllTabsMemoryPressure(minimized || !_isWindowActive);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;

        if (WindowManager.IsLastNormalWindow(this))
        {
            CapturePlacement();
            ViewModel.SaveSession();
        }

        AppServices.Settings.Changed -= OnSettingsChanged;
        RootGrid.ActualThemeChanged -= OnActualThemeChanged;
        RootGrid.PreviewKeyDown -= OnPreviewKeyDown;
        Activated -= OnActivationChanged;
        AppWindow.Changed -= OnAppWindowChanged;

        _verticalTabsCollapseTimer?.Stop();
        _verticalTabsCollapseTimer = null;

        // Owned windows are destroyed with their owner anyway, but closing it explicitly keeps
        // that from depending on teardown order - and stops a stray pane outliving its window.
        _overlay?.Close();
        _overlay = null;

        ViewModel.Detach();
        WindowManager.Unregister(this);
    }

    /// <summary>A missing or unreadable icon file is not worth failing a window over.</summary>
    private void TrySetWindowIcon()
    {
        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Winser.ico"));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Winser] Window icon could not be set: {ex.Message}");
        }
    }

    private void RestorePlacement()
    {
        var state = AppServices.Session.State;
        var isFirstWindow = WindowManager.Windows.Count <= 1;

        var width = isFirstWindow && state.WindowWidth > 400 ? state.WindowWidth : DefaultWidth;
        var height = isFirstWindow && state.WindowHeight > 300 ? state.WindowHeight : DefaultHeight;

        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);
        AppWindow.Resize(new SizeInt32(width, height));

        if (isFirstWindow && state.WindowLeft != int.MinValue)
        {
            var left = Math.Clamp(state.WindowLeft, work.X, Math.Max(work.X, work.X + work.Width - width));
            var top = Math.Clamp(state.WindowTop, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
            AppWindow.Move(new PointInt32(left, top));
        }
        else if (!isFirstWindow)
        {
            var offset = (WindowManager.Windows.Count - 1) * CascadeStep;
            AppWindow.Move(new PointInt32(
                work.X + Math.Min(offset, Math.Max(0, work.Width - width)),
                work.Y + Math.Min(offset, Math.Max(0, work.Height - height))));
        }

        if (isFirstWindow && state.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void CapturePlacement()
    {
        var state = AppServices.Session.State;
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        state.IsMaximized = maximized;

        if (maximized)
        {
            return;
        }

        state.WindowWidth = AppWindow.Size.Width;
        state.WindowHeight = AppWindow.Size.Height;
        state.WindowLeft = AppWindow.Position.X;
        state.WindowTop = AppWindow.Position.Y;
    }

    // ------------------------------------------------------------------- appearance

    /// <summary>
    /// Fallback used only before the first layout pass has measured the real strip height.
    /// </summary>
    private const double FallbackTabStripHeight = 40;

    private double _measuredTabStripHeight;

    /// <summary>
    /// The tab strip's rendered height, used to slide it off-screen in full screen or vertical
    /// tabs mode (see <see cref="UpdateChromeLayout"/>). <see cref="CustomDragRegion"/> is the
    /// TabView's TabStripFooter, which the control renders inline in the same row as the tab
    /// headers, so its ActualHeight after layout is the real strip height — measuring it beats
    /// guessing at an internal theme resource key that may not exist or may not match.
    /// </summary>
    private double TabStripHeight
    {
        get
        {
            if (CustomDragRegion.ActualHeight > 0)
            {
                _measuredTabStripHeight = CustomDragRegion.ActualHeight;
            }

            return _measuredTabStripHeight > 0 ? _measuredTabStripHeight : FallbackTabStripHeight;
        }
    }

    /// <summary>
    /// Re-derives the vertical tabs chrome from the view model's current state: idempotent, so
    /// every trigger (full screen, the setting flipping from another tab, the pin button, a
    /// hover peek) can just call this rather than duplicating the layout math.
    /// </summary>
    private void UpdateChromeLayout()
    {
        var vertical = ViewModel.UseVerticalTabs && !ViewModel.IsFullScreen;

        // Constant at the hover-zone width whenever vertical tabs is on: the expanded pane is a
        // separate window floating above this one (see UpdateVerticalTabsOverlay), so nothing in
        // this Grid ever has to make room for it and the page never resizes when it opens.
        VerticalTabsColumnDef.Width = vertical ? new GridLength(VerticalTabsHoverZoneWidth) : new GridLength(0);

        // TabView renders the strip and the content in one control, so the only way to hide
        // just the strip - for full screen, or because the vertical pane is standing in for it -
        // is to slide it above the client area. A negative top margin on a stretched child grows
        // it by the same amount, so it still fills its cell either way. This only fully hides the
        // strip because TabStrip's own cell is anchored at y=0 in both modes (see the XAML
        // comments) - were it pushed down by a reserved row instead, the strip would still
        // occupy part of the now-visible band above that row, no matter the margin used.
        TabStrip.Margin = ViewModel.IsFullScreen || ViewModel.UseVerticalTabs
            ? new Thickness(0, -TabStripHeight, 0, 0)
            : new Thickness(0);

        // The vertical mode drag region overlays the same top strip TabStrip just vacated,
        // rather than reserving it, so it needs to match that same height to look intentional.
        // It needs no left-edge offset to keep clear of the pane's header (app icon, pin button):
        // the expanded pane is a window of its own sitting above this one, so it takes that input
        // itself rather than competing with a drag region registered underneath it.
        VerticalModeDragRegion.Height = TabStripHeight;

        // In vertical mode CustomDragRegion is inside that now-hidden strip, so dragging and the
        // caption buttons need to be re-anchored to VerticalModeDragRegion instead.
        SetTitleBar(vertical ? VerticalModeDragRegion : CustomDragRegion);

        UpdateVerticalTabsOverlay();
    }

    /// <summary>
    /// Brings the floating pane up, puts it where it belongs, or takes it away - whichever the
    /// view model's current state calls for. Safe to call for any reason at any time; it derives
    /// everything and does nothing if the pane should not be showing.
    /// </summary>
    private void UpdateVerticalTabsOverlay(bool activate = false)
    {
        if (!ViewModel.IsVerticalTabsPaneExpanded)
        {
            _overlay?.HideOverlay();
            return;
        }

        // Before the first layout pass there is no client rectangle to line the pane up with, so
        // there is nothing meaningful to place yet - and showing it anyway would put an unpositioned
        // window on screen. OnFirstActivated re-runs this once real bounds exist.
        if (RootGrid.ActualHeight <= 0)
        {
            return;
        }

        var overlay = EnsureOverlay();
        overlay.ApplyTheme(RootGrid.RequestedTheme);

        // Clamped to the owner's own width, not just the constant: on a narrow or snapped window
        // (or a small-screen device) a fixed 240px pane would otherwise hang off the right edge
        // of the window itself rather than shrinking to fit it.
        var width = Math.Min(VerticalTabsExpandedWidth, RootGrid.ActualWidth);
        overlay.SyncBounds(width, RootGrid.ActualHeight, RasterizationScale);
        overlay.ShowOverlay(activate);
    }

    private VerticalTabsOverlayWindow EnsureOverlay() =>
        _overlay ??= new VerticalTabsOverlayWindow(this, ViewModel);

    private double RasterizationScale
    {
        get
        {
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            return scale > 0 ? scale : 1.0;
        }
    }

    private void ApplyTheme() =>
        ThemeHelper.Apply(RootGrid, AppWindow, AppServices.Settings.Current.Theme);

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplyTheme();

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ThemeHelper.UpdateCaptionButtons(RootGrid, AppWindow);

        // A separate window does not inherit the owner's theme, so it has to be told.
        _overlay?.ApplyTheme(RootGrid.RequestedTheme);
    }

    /// <summary>Keeps the tab strip from sliding underneath the system caption buttons.</summary>
    private void UpdateCaptionInset()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        CustomDragRegion.MinWidth = (AppWindow.TitleBar.RightInset / scale) + 16;
    }

    // ----------------------------------------------------------------------- input

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var ctrl = IsKeyDown(VirtualKey.Control);
        var shift = IsKeyDown(VirtualKey.Shift);
        var alt = IsKeyDown(VirtualKey.Menu);

        if (ViewModel.HandleShortcut(e.Key, ctrl, shift, alt))
        {
            e.Handled = true;
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ------------------------------------------------------------------ tab strip

    private void OnAddTabButtonClick(TabView sender, object args) => ViewModel.NewTab();

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is BrowserTabViewModel tab)
        {
            ViewModel.CloseTab(tab);
        }
    }

    // ------------------------------------------------------------- vertical tabs hover
    //
    // The hover zone and the pane it reveals live in two different windows, so the pointer
    // crossing from one to the other produces a leave on the first and an enter on the second
    // with no ordering guarantee between them - and, if the pane appears directly underneath a
    // stationary cursor, possibly no enter at all until the mouse next moves. Collapsing straight
    // off a leave event would therefore flicker, or oscillate: pane hides, cursor is back over the
    // strip, pane shows, repeat.
    //
    // So a leave only ever *schedules* a collapse, and the collapse itself checks where the
    // pointer actually is at that moment rather than trusting the events to have told the whole
    // story.

    /// <summary>Peeks the collapsed vertical tabs pane open while the pointer is over it.</summary>
    private void OnVerticalTabsPanePointerEntered(object sender, PointerRoutedEventArgs e) =>
        NotifyVerticalTabsPointerEntered();

    private void OnVerticalTabsPanePointerExited(object sender, PointerRoutedEventArgs e) =>
        NotifyVerticalTabsPointerExited();

    /// <summary>Called by the hover strip here and by the pane's own window.</summary>
    internal void NotifyVerticalTabsPointerEntered()
    {
        _verticalTabsCollapseTimer?.Stop();
        ViewModel.IsVerticalTabsPointerOver = true;
    }

    /// <summary>Called by the hover strip here and by the pane's own window.</summary>
    internal void NotifyVerticalTabsPointerExited()
    {
        _verticalTabsCollapseTimer ??= CreateVerticalTabsCollapseTimer();
        _verticalTabsCollapseTimer.Stop();
        _verticalTabsCollapseTimer.Start();
    }

    private DispatcherTimer CreateVerticalTabsCollapseTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(VerticalTabsCollapseDelayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // Still hovering something that counts? Then the leave that scheduled this was just
            // the pointer moving between the strip and the pane, and there is nothing to do.
            if (IsPointerOverVerticalTabs())
            {
                timer.Start();
                return;
            }

            ViewModel.IsVerticalTabsPointerOver = false;
        };

        return timer;
    }

    /// <summary>
    /// Whether the cursor is inside either half of the vertical tabs hover surface - the pane's
    /// own window, or the strip down the owner's left edge - asked of the OS directly rather than
    /// inferred from enter/leave events that cross a window boundary.
    /// </summary>
    private bool IsPointerOverVerticalTabs()
    {
        if (!GetCursorPos(out var cursor))
        {
            // Can't tell, so don't collapse something the user may well be pointing at; a later
            // tick will decide once the answer is knowable again.
            return true;
        }

        if (_overlay?.ScreenBounds is { } pane &&
            cursor.X >= pane.X && cursor.X < pane.X + pane.Width &&
            cursor.Y >= pane.Y && cursor.Y < pane.Y + pane.Height)
        {
            return true;
        }

        var origin = default(POINT);
        if (!ClientToScreen(WindowHandle, ref origin))
        {
            return false;
        }

        var scale = RasterizationScale;
        var stripWidth = (int)Math.Round(VerticalTabsHoverZoneWidth * scale);
        var stripHeight = (int)Math.Round(RootGrid.ActualHeight * scale);

        return cursor.X >= origin.X && cursor.X < origin.X + stripWidth &&
               cursor.Y >= origin.Y && cursor.Y < origin.Y + stripHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hWnd, ref POINT point);
}
