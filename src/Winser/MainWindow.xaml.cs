using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    // How close to the window's left edge the cursor has to get to open the pane. This is a band
    // measured against the client rect, not an element - nothing occupies it, so the page keeps
    // those pixels and stays clickable there. 16 rather than something thinner because the OS
    // reserves roughly the outer 8px of a resizable window for its own resize-drag hit-testing,
    // and a band inside that is awkward to hit deliberately.
    private const double VerticalTabsHoverZoneWidth = 16;
    private const double VerticalTabsExpandedWidth = 240;
    private const double CompactToolbarWidth = 760;
    private const double StackedToolbarWidth = 520;

    /// <summary>
    /// Gap between the expanded pane and the window's top, left and bottom edges, so it reads as
    /// a card floating over the page rather than a panel welded to the frame - the rounded
    /// corners only look intentional with a gap for them to show against.
    /// </summary>
    private const double VerticalTabsPaneMargin = 8;

    /// <summary>
    /// How often the cursor is checked against the vertical pane edge while it is moving.
    /// </summary>
    private const int CursorPollMs = 100;

    /// <summary>The slower rate once the cursor has held still for <see cref="CursorIdleAfterMs"/>.</summary>
    /// <remarks>
    /// <para>
    /// The work per tick really is trivial - one GetCursorPos and an edge check - but that
    /// was never the cost worth counting. A precise 10 Hz timer is 600 thread wake-ups a minute
    /// that Windows cannot coalesce, and it holds the CPU package out of its deeper sleep states
    /// for as long as a window is open, whether or not anyone is at the machine. That is charged
    /// to battery even though it barely registers as CPU time.
    /// </para>
    /// <para>
    /// A cursor that is not moving cannot be approaching a zone, which makes stillness a safe
    /// thing to back off on - and stillness is exactly the state a laptop left alone is in. The
    /// backing off costs nothing in feel because the tick that notices movement drops straight
    /// back to <see cref="CursorPollMs"/> and evaluates the edge on that same tick, so a hand
    /// travelling to a screen edge - which takes a few hundred milliseconds of continuous motion -
    /// is caught in flight rather than on arrival.
    /// </para>
    /// </remarks>
    private const int IdleCursorPollMs = 2000;

    private const int CursorIdleAfterMs = 15_000;

    /// <summary>
    /// Stands in for <c>TitleBar.RightInset</c> until the system has reported one - roughly three
    /// caption buttons wide. Winser starts collapsed, and a collapsed title bar reserves nothing,
    /// so the real inset is not knowable until the first reveal.
    /// </summary>
    private const double FallbackCaptionInset = 138;

    private bool _isClosing;
    private bool _isWindowActive = true;
    private bool _isMinimized;

    /// <summary>The cursor poll used for the vertical pane peek; see the cursor watch section.</summary>
    private DispatcherTimer? _cursorWatchTimer;

    /// <summary>Where the cursor was on the previous tick, for deciding whether it has moved.</summary>
    private POINT _lastCursorPoint;

    private long _cursorMovedAtTicks;

    /// <summary>The interval currently on <see cref="_cursorWatchTimer"/>, to avoid restarting it.</summary>
    private int _cursorPollMs = CursorPollMs;

    private bool _pointerOverVerticalTabsCard;

    /// <summary>Whether the caption buttons and the bar they sit on are currently showing.</summary>
    private bool _isCaptionRevealed = true;

    /// <summary>
    /// The caption inset in DIPs, remembered across the reveal. <c>TitleBar.RightInset</c> reads
    /// 0 while the title bar is collapsed, and reserving 0 would let the tab strip slide under
    /// buttons that are one hover away from coming back - so the last real width is kept and the
    /// reserved space never moves.
    /// </summary>
    private double _captionInset = FallbackCaptionInset;

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
        HideWindowBorder();
        ApplyCaptionReveal();

        PrivateBadge.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;
        VerticalPrivateBadge.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;

        ApplyTheme();
        UpdateChromeLayout();
        AppServices.Settings.Changed += OnSettingsChanged;

        RootGrid.ActualThemeChanged += OnActualThemeChanged;
        RootGrid.PreviewKeyDown += OnPreviewKeyDown;
        RootGrid.SizeChanged += (_, _) =>
        {
            UpdateVerticalToolbarLayout();
            UpdateCaptionInset();
            UpdateTopBarInsets();
            UpdateTopBarPassthroughRegions();

            // The popup is not laid out in this Grid, so nothing resizes it on the window's
            // behalf the way layout would for an inline child - it has to be told, every resize.
            UpdateVerticalTabsFlyout();
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

    private void HideWindowBorder()
    {
        const int DwmwaBorderColor = 34;
        var color = -2;
        var result = DwmSetWindowAttribute(WindowHandle, DwmwaBorderColor, ref color, sizeof(int));
        if (result < 0)
        {
            DiagnosticLog.Write($"window border: DwmSetWindowAttribute failed with HRESULT 0x{result:X8}");
        }
    }

    public void RefreshTabChrome()
    {
        if (ViewModel.UseVerticalTabs)
        {
            var addressFocused = IsFocusWithin(TopAddressBox);
            foreach (var tab in ViewModel.Tabs)
            {
                tab.IsAddressFocused = ReferenceEquals(tab, ViewModel.SelectedTab) && addressFocused;
                if (!ReferenceEquals(tab, ViewModel.SelectedTab))
                {
                    tab.ClearSuggestions();
                }
            }
        }

        UpdateChromeLayout();
    }

    public void FocusAddressBar()
    {
        var wasFullScreen = ViewModel.IsFullScreen;
        if (wasFullScreen)
        {
            SetFullScreen(false);
        }

        if (wasFullScreen || ViewModel.UseVerticalTabs)
        {
            DispatcherQueue.TryEnqueue(FocusVisibleAddressBar);
        }
        else
        {
            FocusVisibleAddressBar();
        }
    }

    private void FocusVisibleAddressBar()
    {
        if (ViewModel.UseVerticalTabs)
        {
            TopAddressBox.Focus(FocusState.Programmatic);
            TopAddressBox.FindDescendant<TextBox>()?.SelectAll();
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
        HideWindowBorder();
        UpdateCaptionInset();

        // Winser starts with the title bar collapsed, so an inset and height of 0 here is the
        // confirmation that it took - and a customization flag of False is the one reason it
        // cannot, in which case Windows keeps drawing the buttons itself and no amount of
        // reveal logic will move them.
        DiagnosticLog.Write(
            $"caption: customization={AppWindowTitleBar.IsCustomizationSupported()}, " +
            $"inset={AppWindow.TitleBar.RightInset}, height={AppWindow.TitleBar.Height}");

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
            HideWindowBorder();
            UpdateMemoryPressure();
        }
    }

    /// <summary>Whether this window is currently minimized. Read by <see cref="WindowManager"/>.</summary>
    internal bool IsMinimized => _isMinimized;

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

        _isMinimized = minimized;
        UpdateCursorWatch();

        // Deliberately driven by the minimized half of that signal alone, not by
        // "minimized or deactivated" as the line above is. A window that has merely lost focus
        // is still on screen and still has to paint - a video, an animation, a page finishing
        // its load - and scheduling the process for efficiency there would be visible
        // sluggishness in exchange for nothing. Only a window nobody can see is free.
        WindowManager.UpdateProcessPowerState();
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

        _cursorWatchTimer?.Stop();
        _cursorWatchTimer = null;

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
        UpdateVerticalToolbarLayout();
        var vertical = ViewModel.UseVerticalTabs && !ViewModel.IsFullScreen;

        // The browser bar stays visible during normal browsing; fullscreen is the only state
        // that removes the title bar and its controls.
        SetCaptionRevealed(!ViewModel.IsFullScreen);
        // Nothing reserves layout space for vertical tabs: the expanded pane is a windowed Popup
        // floating over the page, and the hover trigger is the cursor's position rather than an
        // element (see the cursor watch section).
        UpdateCursorWatch();

        UpdateVerticalTabsFlyout();

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

        // The vertical mode drag region covers the complete responsive toolbar, including its
        // address row at narrow widths; the pane floats above it in its own popup.
        // In vertical mode CustomDragRegion is inside that now-hidden strip, so dragging and the
        // caption buttons need to be re-anchored to VerticalModeDragRegion instead.
        SetTitleBar(vertical ? VerticalModeDragRegion : CustomDragRegion);
    }

    private void UpdateVerticalToolbarLayout()
    {
        var width = RootGrid.ActualWidth;
        var compact = width > 0 && width < CompactToolbarWidth;
        var stacked = width > 0 && width < StackedToolbarWidth;

        TopForwardButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TopNewTabButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TopPinButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TopActionBarGrid.Height = stacked ? 72 : 40;
        TopActionBarGrid.RowDefinitions[1].Height = new GridLength(stacked ? 32 : 0);
        Grid.SetRow(TopAddressBox, stacked ? 1 : 0);
        Grid.SetColumn(TopAddressBox, stacked ? 1 : 4);
        Grid.SetColumnSpan(TopAddressBox, stacked ? 10 : 1);

        var vertical = ViewModel.UseVerticalTabs && !ViewModel.IsFullScreen;
        VerticalToolbarRow.Height = new GridLength(vertical ? TopActionBarGrid.Height : 0);
        VerticalModeDragRegion.Height = vertical ? TopActionBarGrid.Height : TabStripHeight;
    }

    /// <summary>
    /// Sizes and places the floating pane. A Popup does not stretch to anything, and its offsets
    /// are relative to where it sits in the tree - the hover strip at the client area's top-left -
    /// so both are set here rather than in XAML, and re-set whenever the window resizes.
    /// </summary>
    private void UpdateVerticalTabsFlyout()
    {
        if (RootGrid.ActualHeight <= 0)
        {
            return;
        }

        VerticalTabsFlyout.HorizontalOffset = VerticalTabsPaneMargin;
        var topOffset = VerticalTabsPaneMargin + (ViewModel.UseVerticalTabs && !ViewModel.IsFullScreen
            ? VerticalToolbarRow.Height.Value
            : 0);
        VerticalTabsFlyout.VerticalOffset = topOffset;

        // Clamped to the window's own size: on a narrow or short window a fixed 240px card would
        // otherwise hang off the edge rather than shrinking to fit inside it.
        VerticalTabsCard.Width = Math.Min(
            VerticalTabsExpandedWidth,
            Math.Max(0, RootGrid.ActualWidth - (VerticalTabsPaneMargin * 2)));
        VerticalTabsCard.Height = Math.Max(0, RootGrid.ActualHeight - topOffset - VerticalTabsPaneMargin);
    }

    private void OnTabViewClipHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        TabViewClipGeometry.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
    }

    private void ApplyTheme() =>
        ThemeHelper.Apply(RootGrid, AppWindow, AppServices.Settings.Current.Theme);

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplyTheme();

    private void OnActualThemeChanged(FrameworkElement sender, object args) =>
        ThemeHelper.UpdateCaptionButtons(RootGrid, AppWindow);

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

        // Kept rather than read straight through: see _captionInset. A collapsed title bar
        // reports nothing, and this runs on every resize, most of which happen collapsed.
        if (AppWindow.TitleBar.RightInset > 0)
        {
            _captionInset = AppWindow.TitleBar.RightInset / scale;
        }

        CustomDragRegion.MinWidth = _captionInset + 16;
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

    // --------------------------------------------------------------- cursor watch
    //
    // The pane peeks when the cursor reaches the window's left edge. The position is polled rather
    // than received from an element, because a hit-test element there would cover page content.
    //
    // The pane tried the element approach first and failed twice over: it needed a band of the
    // window kept clear of WebView2 to receive pointer events at all, which was visible as a
    // strip of window background beside every page; and once the pane itself became a windowed
    // Popup, the strip and the pane were separate hit-test surfaces, so crossing between them
    // produced an exit and an enter with no ordering guarantee and the pane flickered.
    //
    /// <summary>
    /// Runs the poll only while the vertical pane can peek. Idempotent, so every caller that
    /// changes the window's shape can just call it.
    /// </summary>
    private void UpdateCursorWatch()
    {
        if (_isClosing)
        {
            _cursorWatchTimer?.Stop();
            return;
        }

        if (_isMinimized || !ViewModel.IsVerticalTabsPaneVisible)
        {
            _cursorWatchTimer?.Stop();
            _pointerOverVerticalTabsCard = false;
            ViewModel.IsVerticalTabsListFocused = false;
            ViewModel.IsVerticalTabsPointerOver = false;
            SetCaptionRevealed(!_isMinimized && !ViewModel.IsFullScreen);
            return;
        }

        SetCaptionRevealed(true);

        _cursorWatchTimer ??= CreateCursorWatchTimer();

        // Start() on a running timer restarts its interval, which a burst of activation changes
        // could otherwise use to starve the tick indefinitely.
        if (!_cursorWatchTimer.IsEnabled)
        {
            // Whatever changed the window's shape may well have been the user, so come back at
            // full rate and let stillness earn the slower one again.
            _cursorMovedAtTicks = Environment.TickCount64;
            SetCursorPollRate(CursorPollMs);
            _cursorWatchTimer.Start();
        }
    }

    private DispatcherTimer CreateCursorWatchTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_cursorPollMs) };
        timer.Tick += (_, _) =>
        {
            UpdateVerticalTabsHover();
            AdjustCursorPollRate();
        };
        return timer;
    }

    /// <summary>
    /// Slows the poll down while the cursor holds still, and restores it the moment it moves.
    /// </summary>
    private void AdjustCursorPollRate()
    {
        if (GetCursorPos(out var cursor))
        {
            if (cursor.X != _lastCursorPoint.X || cursor.Y != _lastCursorPoint.Y)
            {
                _lastCursorPoint = cursor;
                _cursorMovedAtTicks = Environment.TickCount64;
            }
        }
        else
        {
            // No answer is not evidence of stillness; treat it as movement and stay responsive.
            _cursorMovedAtTicks = Environment.TickCount64;
        }

        // Leaving the pane should feel as prompt as reaching it did.
        if (GetForegroundWindow() == WindowHandle &&
            (_pointerOverVerticalTabsCard || IsCursorAtLeftEdge()))
        {
            SetCursorPollRate(CursorPollMs);
            return;
        }

        var still = Environment.TickCount64 - _cursorMovedAtTicks;
        SetCursorPollRate(still >= CursorIdleAfterMs ? IdleCursorPollMs : CursorPollMs);
    }

    /// <summary>
    /// Changes the tick rate, and only when it actually differs: assigning
    /// <see cref="DispatcherTimer.Interval"/> restarts the timer, so writing the value it
    /// already holds on every tick would push the next tick out forever.
    /// </summary>
    private void SetCursorPollRate(int milliseconds)
    {
        if (_cursorPollMs == milliseconds)
        {
            return;
        }

        _cursorPollMs = milliseconds;
        if (_cursorWatchTimer is { } timer)
        {
            timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        }
    }

    /// <summary>
    /// One evaluation of both halves of "should the pane be open": the cursor is in the strip
    /// down the left of this window's client area, or it is over the pane itself.
    /// </summary>
    private void UpdateVerticalTabsHover()
    {
        // Another app in front: its window is over these coordinates, so the cursor being within
        // our edge band says nothing about what the user is pointing at.
        if (!ViewModel.IsVerticalTabsPaneVisible || GetForegroundWindow() != WindowHandle)
        {
            ViewModel.IsVerticalTabsPointerOver = false;
            return;
        }

        ViewModel.IsVerticalTabsPointerOver = _pointerOverVerticalTabsCard || IsCursorAtLeftEdge();
    }

    private void SetCaptionRevealed(bool revealed)
    {
        if (_isCaptionRevealed == revealed)
        {
            return;
        }

        _isCaptionRevealed = revealed;
        ApplyCaptionReveal();

        // Only now is there an inset to read: a collapsed title bar reserves nothing and reports
        // 0, so the real width can only be picked up while the buttons are actually there.
        if (revealed)
        {
            UpdateCaptionInset();
            UpdateTopBarInsets();
            UpdateTopBarPassthroughRegions();
        }
    }

    /// <summary>
    /// Takes the caption buttons and the bar behind them in and out of existence together.
    /// </summary>
    /// <remarks>
    /// Height, not colour. The buttons are drawn by the system and
    /// <see cref="AppWindowTitleBar"/> ignores the alpha channel on their foreground colours
    /// while content is extended into the title bar, so painting a glyph transparent paints it
    /// opaque white instead - see <see cref="ThemeHelper.UpdateCaptionButtons"/>.
    /// <see cref="TitleBarHeightOption.Collapsed"/> takes the reserved area to zero height, which
    /// removes the buttons outright rather than trying to make them invisible in place; going
    /// back to <see cref="TitleBarHeightOption.Standard"/> hands back the system's own buttons
    /// with everything that comes with them - snap layouts, tooltips, hit-testing, narration.
    /// </remarks>
    private void ApplyCaptionReveal()
    {
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.PreferredHeightOption = _isCaptionRevealed
                ? TitleBarHeightOption.Standard
                : TitleBarHeightOption.Collapsed;
        }

        TopChromeTint.Opacity = _isCaptionRevealed ? 1 : 0;
        TopChromeTint.IsHitTestVisible = _isCaptionRevealed;
        TopAddressBox.IsTabStop = _isCaptionRevealed;
        TopBackButton.IsTabStop = _isCaptionRevealed;
        TopForwardButton.IsTabStop = _isCaptionRevealed;
        TopReloadButton.IsTabStop = _isCaptionRevealed;
        TopNewTabButton.IsTabStop = _isCaptionRevealed;
        TopShowTabsButton.IsTabStop = _isCaptionRevealed;
        TopPinButton.IsTabStop = _isCaptionRevealed;
        if (TopSiteInfoButton.FindDescendant<Button>() is { } siteInfoButton)
        {
            siteInfoButton.IsTabStop = _isCaptionRevealed;
        }
        if (TopMenuButton.FindDescendant<Button>() is { } menuButton)
        {
            menuButton.IsTabStop = _isCaptionRevealed;
        }
        UpdateTopBarInsets();
        UpdateTopBarPassthroughRegions();
    }

    private void OnTopActionBarLayoutChanged(object sender, RoutedEventArgs e) => UpdateTopBarPassthroughRegions();

    private void OnTopActionBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTopBarPassthroughRegions();

    private void UpdateTopBarInsets()
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;
        TopLeftCaptionPadding.Width = new GridLength(AppWindow.TitleBar.LeftInset > 0
            ? AppWindow.TitleBar.LeftInset / scale : 0);
        TopRightCaptionPadding.Width = new GridLength(_isCaptionRevealed ? _captionInset : FallbackCaptionInset);
    }

    private void UpdateTopBarPassthroughRegions()
    {
        if (RootGrid.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        if (ViewModel.IsFullScreen || !_isCaptionRevealed)
        {
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(NonClientRegionKind.Passthrough, Array.Empty<RectInt32>());
            return;
        }

        var scale = xamlRoot.RasterizationScale;
        var regions = new FrameworkElement?[]
            {
                TopAddressBox, TopBackButton, TopForwardButton, TopReloadButton, TopNewTabButton,
                TopShowTabsButton, TopPinButton,
                TopSiteInfoButton.FindDescendant<Button>(), TopMenuButton.FindDescendant<Button>()
            }
            .OfType<FrameworkElement>()
            .Where(element => element.Visibility == Visibility.Visible)
            .Select(element =>
            {
                var bounds = element.TransformToVisual(null).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                return new RectInt32(
                    (int)Math.Round(bounds.X * scale),
                    (int)Math.Round(bounds.Y * scale),
                    (int)Math.Round(bounds.Width * scale),
                    (int)Math.Round(bounds.Height * scale));
            })
            .ToArray();
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, regions);
    }

    private bool IsCursorAtLeftEdge()
    {
        if (!GetCursorPos(out var cursor))
        {
            return false;
        }

        var origin = default(POINT);
        if (!ClientToScreen(WindowHandle, ref origin))
        {
            return false;
        }

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var width = (int)Math.Round(VerticalTabsHoverZoneWidth * scale);
        var height = (int)Math.Round(RootGrid.ActualHeight * scale);

        return cursor.X >= origin.X && cursor.X < origin.X + width &&
               cursor.Y >= origin.Y && cursor.Y < origin.Y + height;
    }

    // The pane's own hover, folded into the poll above rather than driving the view model
    // directly - so that leaving the pane for the page does not close it a tick before the poll
    // would have agreed, and re-entering does not fight it.

    private void OnVerticalTabsPanePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverVerticalTabsCard = true;
        ViewModel.IsVerticalTabsPointerOver = true;
    }

    private void OnVerticalTabsPanePointerExited(object sender, PointerRoutedEventArgs e) =>
        _pointerOverVerticalTabsCard = false;

    private void OnVerticalTabsFlyoutOpened(object sender, object e)
    {
        ScrollSelectedVerticalTabIntoView();
        if (ViewModel.IsVerticalTabsManuallyRevealed)
        {
            VerticalTabList.Focus(FocusState.Programmatic);
        }
    }

    private void OnVerticalTabContextFlyoutOpened(object sender, object e) =>
        ViewModel.IsVerticalTabsManuallyRevealed = true;

    private void OnVerticalTabContextFlyoutClosed(object sender, object e) =>
        ViewModel.IsVerticalTabsManuallyRevealed = false;

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

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    /// <summary>
    /// SelectedItem binds one-way rather than two-way: this list and TabStrip both need to
    /// react to ViewModel.SelectedTab changing elsewhere, but two TwoWay x:Bind bindings to the
    /// same property in one Window's binding scope is exactly the shape that trips known x:Bind
    /// codegen bugs (microsoft-ui-xaml#8441 and others) - so only TabStrip's original binding
    /// stays TwoWay, and this list writes back explicitly instead.
    /// </summary>
    private void OnVerticalTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as ListView)?.SelectedItem is BrowserTabViewModel tab)
        {
            ViewModel.SelectedTab = tab;
            VerticalTabList.ScrollIntoView(tab);
        }
    }

    private void OnVerticalTabListGotFocus(object sender, RoutedEventArgs e)
    {
        ViewModel.IsVerticalTabsListFocused = true;
        ViewModel.IsVerticalTabsManuallyRevealed = false;
    }

    private void OnVerticalTabListLostFocus(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() => ViewModel.IsVerticalTabsListFocused = IsFocusWithin(VerticalTabList));

    private void OnVerticalTabListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _pointerOverVerticalTabsCard = false;
            ViewModel.IsVerticalTabsPointerOver = false;
            ViewModel.IsVerticalTabsManuallyRevealed = false;
            ViewModel.IsVerticalTabsListFocused = false;
            FocusVisibleAddressBar();
        }
    }

    private bool IsFocusWithin(DependencyObject element)
    {
        if (RootGrid.XamlRoot is not { } xamlRoot)
        {
            return false;
        }

        for (var focused = FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;
             focused is not null;
             focused = VisualTreeHelper.GetParent(focused))
        {
            if (ReferenceEquals(focused, element))
            {
                return true;
            }
        }

        return false;
    }

    private void ScrollSelectedVerticalTabIntoView()
    {
        if (ViewModel.SelectedTab is { } tab)
        {
            VerticalTabList.ScrollIntoView(tab);
        }
    }

    // ------------------------------------------------------- vertical tabs address bar
    //
    // Mirrors BrowserTabPage.xaml.cs's OnAddress* handlers exactly, retargeted from a fixed
    // per-tab ViewModel to ViewModel.SelectedTab, since this one address bar serves whichever
    // tab is currently selected rather than belonging to a single tab's own page.

    private void OnVerticalAddressTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.SelectedTab?.UpdateSuggestions(sender.Text);
        }
    }

    private void OnVerticalAddressQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (ViewModel.SelectedTab is not { } tab)
        {
            return;
        }

        if (args.ChosenSuggestion is AddressSuggestion suggestion)
        {
            tab.NavigateResolved(suggestion.Target);
        }
        else
        {
            tab.Navigate(args.QueryText);
        }

        tab.IsAddressFocused = false;
        tab.ClearSuggestions();
        tab.FocusWebContent();
    }

    private void OnVerticalAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel.SelectedTab is { } tab && sender is AutoSuggestBox addressBox)
        {
            tab.ClearSuggestions();
            addressBox.Text = UrlHelper.ForDisplay(tab.Url);
            e.Handled = true;
        }
    }

    private void OnVerticalAddressGotFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTab is { } tab)
        {
            tab.IsAddressFocused = true;
        }
    }

    private void OnVerticalAddressLostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTab is not { } tab)
        {
            return;
        }

        tab.IsAddressFocused = false;

        // Put back whatever the page's real address is if the edit was abandoned.
        if (sender is AutoSuggestBox addressBox)
        {
            addressBox.Text = UrlHelper.ForDisplay(tab.Url);
        }
    }
}
