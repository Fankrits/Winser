using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;
using Winser.Helpers;
using Winser.Models;
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

    /// <summary>
    /// Gap between the expanded pane and the window's top, left and bottom edges, so it reads as
    /// a card floating over the page rather than a panel welded to the frame - the rounded
    /// corners only look intentional with a gap for them to show against.
    /// </summary>
    private const double VerticalTabsPaneMargin = 8;

    /// <summary>
    /// Narrowest window in which a pinned pane docks beside the page, the way Arc and Zen dock
    /// theirs. Below it a docked pane would leave the page a sliver, so the pinned pane floats
    /// over the page instead - still open, just not taking the room.
    /// </summary>
    private const double MinWidthToDockVerticalTabs = 640;

    /// <summary>
    /// How long the pointer may be away from both the left edge and the auto-hidden pane before
    /// the pane slides back out. Long enough to forgive overshooting the pane's edge on the way
    /// to a tab, or skimming past it on the way back - short enough that leaving still feels
    /// like the pane following the hand rather than lingering.
    /// </summary>
    private const int VerticalTabsHideDelayMs = 250;

    /// <summary>
    /// The slowest the cursor poll may back off to while an auto-hidden pane is waiting to be
    /// summoned in the foreground window. See <see cref="AdjustCursorPollRate"/>.
    /// </summary>
    private const int ArmedCursorPollMs = 150;

    /// <summary>Slide durations for <see cref="SidebarAnimationSpeed"/>; exits run at 3/4 of these.</summary>
    private const int FastPaneAnimationMs = 150;

    private const int NormalPaneAnimationMs = 240;

    private const int RelaxedPaneAnimationMs = 360;

    /// <summary>
    /// How often the cursor is checked against the zones below while it is actually moving.
    /// Fast enough that reaching one feels immediate.
    /// </summary>
    private const int CursorPollMs = 100;

    /// <summary>
    /// The rate once the cursor has held still for <see cref="CursorRestingAfterMs"/>, and the
    /// slower one once it has held still for <see cref="CursorIdleAfterMs"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The work per tick really is trivial - one GetCursorPos and two rectangle tests - but that
    /// was never the cost worth counting. A precise 10 Hz timer is 600 thread wake-ups a minute
    /// that Windows cannot coalesce, and it holds the CPU package out of its deeper sleep states
    /// for as long as a window is open, whether or not anyone is at the machine. That is charged
    /// to battery even though it barely registers as CPU time.
    /// </para>
    /// <para>
    /// A cursor that is not moving cannot be approaching a zone, which makes stillness a safe
    /// thing to back off on - and stillness is exactly the state a laptop left alone is in. The
    /// backing off costs nothing in feel because the tick that notices movement drops straight
    /// back to <see cref="CursorPollMs"/> and evaluates both zones on that same tick, so a hand
    /// travelling to a screen edge - which takes a few hundred milliseconds of continuous motion -
    /// is caught in flight rather than on arrival.
    /// </para>
    /// </remarks>
    private const int RestingCursorPollMs = 500;

    private const int IdleCursorPollMs = 2000;

    /// <summary>How long the cursor must hold still before each slower rate takes over.</summary>
    private const int CursorRestingAfterMs = 3_000;

    private const int CursorIdleAfterMs = 15_000;

    /// <summary>
    /// How far around the caption buttons the pointer counts as being at them, in pixels at 100%
    /// scaling. The zone is the buttons plus this - deliberately forgiving, because nothing is
    /// drawn there to aim at until the aiming has already worked.
    /// </summary>
    private const double CaptionRevealPadding = 16;

    /// <summary>
    /// How far back out it then has to go. Larger than <see cref="CaptionRevealPadding"/> so that
    /// a cursor resting on the boundary cannot sample its way in and out and flicker the chrome.
    /// </summary>
    private const double CaptionRevealExitPadding = 28;

    /// <summary>
    /// Extra height the zone keeps below the bar once the buttons are showing, so that hovering
    /// maximize and reaching down into the Windows 11 snap layout flyout - which drops straight
    /// down out of that button - does not paint the buttons out from under the pointer.
    /// </summary>
    private const double CaptionSnapLayoutReach = 220;

    /// <summary>
    /// Stands in for <c>TitleBar.RightInset</c> until the system has reported one - roughly three
    /// caption buttons wide. Winser starts collapsed, and a collapsed title bar reserves nothing,
    /// so the real inset is not knowable until the first reveal.
    /// </summary>
    private const double FallbackCaptionInset = 138;

    /// <summary>
    /// Height of the bar the caption buttons come back on in vertical tabs mode - the height of
    /// the buttons themselves at <see cref="TitleBarHeightOption.Standard"/>, so the bar is no
    /// taller than what it carries.
    /// </summary>
    private const double VerticalCaptionBarHeight = 32;

    private bool _isClosing;
    private bool _isWindowActive = true;
    private bool _isMinimized;

    /// <summary>The one cursor poll both hover behaviours share; see the cursor watch section.</summary>
    private DispatcherTimer? _cursorWatchTimer;

    /// <summary>Where the cursor was on the previous tick, for deciding whether it has moved.</summary>
    private POINT _lastCursorPoint;

    private long _cursorMovedAtTicks;

    /// <summary>The interval currently on <see cref="_cursorWatchTimer"/>, to avoid restarting it.</summary>
    private int _cursorPollMs = CursorPollMs;

    /// <summary>
    /// When the pointer was last seen leaving both the edge zone and the pane, or 0 while it is
    /// still on one of them. Drives <see cref="VerticalTabsHideDelayMs"/>.
    /// </summary>
    private long _paneHoverLostAtTicks;

    /// <summary>
    /// The state the pane was last told to animate towards - which is not the same as
    /// Popup.IsOpen: the popup stays open for the length of the exit animation.
    /// </summary>
    private bool _isVerticalTabsPaneShown;

    /// <summary>Bumped per pane animation, so a superseded exit cannot close the popup late.</summary>
    private int _paneAnimationGeneration;

    /// <summary>
    /// False until the window has been activated once: the pane starting pinned should simply be
    /// there when the window appears, not slide in as it opens.
    /// </summary>
    private bool _hasActivated;

    /// <summary>For the system's "Animation effects" switch, which the pane's slide respects.</summary>
    private readonly UISettings _uiSettings = new();

    /// <summary>Whether the caption buttons and the bar they sit on are currently showing.</summary>
    private bool _isCaptionRevealed;

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
        ApplyCaptionReveal();

        PrivateBadge.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;
        VerticalPrivateBadge.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;

        // The pane slides on its composition Translation rather than a RenderTransform, so the
        // motion runs on the compositor thread and stays smooth even while the UI thread is busy
        // with a page loading or a tab switching.
        ElementCompositionPreview.SetIsTranslationEnabled(VerticalTabsCard, true);

        ApplyTheme();
        UpdateChromeLayout();
        AppServices.Settings.Changed += OnSettingsChanged;

        RootGrid.ActualThemeChanged += OnActualThemeChanged;
        RootGrid.PreviewKeyDown += OnPreviewKeyDown;
        RootGrid.SizeChanged += (_, _) =>
        {
            UpdateCaptionInset();

            // The popup is not laid out in this Grid, so nothing resizes it on the window's
            // behalf the way layout would for an inline child - it has to be told, every resize.
            // The page's inset follows the pane's width, and whether there is room to dock.
            UpdateVerticalTabsFlyout();
            UpdatePageInset();
        };

        // A windowed popup cannot open before there is a XamlRoot for it to belong to, so a pane
        // that starts out pinned is only actually opened once the content has loaded.
        RootGrid.Loaded += (_, _) => UpdateChromeLayout();

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
            // Collapsed, the pane (and this address bar inside it) is not visible and cannot
            // accept focus at all. Flagging it open takes effect through a binding, which needs
            // a layout pass to actually happen before Focus() has anything to land on - the same
            // reason BrowserTabPage.xaml.cs's own FindBar focus defers through the dispatcher.
            ViewModel.IsVerticalTabsAddressBarFocused = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                VerticalAddressBox.Focus(FocusState.Programmatic);
                VerticalAddressBox.FindDescendant<TextBox>()?.SelectAll();
            });
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

        // Only from here on does the pane animate: whatever state it starts in, it is simply in.
        _hasActivated = true;
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

    private double _measuredPageTop;

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

        // Nothing reserves layout space for vertical tabs: the expanded pane is a windowed Popup
        // floating over the page, and the hover trigger is the cursor's position rather than an
        // element (see the cursor watch section).
        UpdateCursorWatch();

        UpdateVerticalTabsFlyout();
        UpdateVerticalTabsPane();
        UpdatePageInset();

        // The vertical mode bar only exists while the caption buttons are out; see
        // UpdateVerticalCaptionBar.
        UpdateVerticalCaptionBar();
        UpdateWindowBorder(vertical);

        // In vertical mode CustomDragRegion is inside that now-hidden strip, so dragging and the
        // caption buttons need to be re-anchored to VerticalModeDragRegion instead.
        SetTitleBar(vertical ? VerticalModeDragRegion : CustomDragRegion);
    }

    /// <summary>
    /// Where the page goes. TabView renders the strip and the content in one control, so the only
    /// way to hide just the strip - for full screen, or because the vertical pane is standing in
    /// for it - is to slide it above the client area. A negative top margin on a stretched child
    /// grows it by the same amount, so it still fills its cell either way. This only fully hides
    /// the strip because TabStrip's own cell is anchored at y=0 in both modes (see the XAML
    /// comments) - were it pushed down by a reserved row instead, the strip would still occupy
    /// part of the now-visible band above that row, no matter the margin used.
    /// </summary>
    /// <remarks>
    /// The left margin is the docked sidebar: pinned, the pane sits in a column of window
    /// background beside the page instead of covering its left edge, the way Arc's and Zen's do.
    /// Unpinned, the page takes the full width and the pane slides in over it on demand. The
    /// pane itself is the same floating popup either way; docking only moves the page out from
    /// under it.
    /// </remarks>
    private void UpdatePageInset()
    {
        var top = ViewModel.IsFullScreen || ViewModel.UseVerticalTabs ? -PageTopInStrip : 0;

        // The caption bar in vertical tabs mode takes its own row while it is out, pushing the
        // page down, rather than being drawn over the top of the site.
        if (IsVerticalChrome && _isCaptionRevealed)
        {
            top += VerticalCaptionBarHeight;
        }

        var left = IsVerticalTabsDocked ? VerticalTabsPaneWidth + (VerticalTabsPaneMargin * 2) : 0;
        TabStrip.Margin = new Thickness(left, top, 0, 0);
    }

    /// <summary>
    /// How far down inside TabStrip the page actually starts - the distance the strip has to be
    /// pushed up by for the page's first pixel to land on the window's first pixel.
    /// </summary>
    /// <remarks>
    /// Measured from TabView's own content presenter rather than taken as the footer's height
    /// (<see cref="TabStripHeight"/>). The two are meant to agree, but anything in the template
    /// between the strip row and the content - padding, the strip's bottom border line - would
    /// make the footer a few pixels short, and those pixels would show as a sliver of hidden
    /// strip along the top of every page. This is exact whatever the template does. Falls back
    /// to the footer height if the presenter is not there (a future template renaming it).
    /// </remarks>
    private double PageTopInStrip
    {
        get
        {
            if (_measuredPageTop <= 0 &&
                TabStrip.FindDescendantByName("TabContentPresenter") is { ActualHeight: > 0 } presenter)
            {
                var offset = presenter.TransformToVisual(TabStrip).TransformPoint(new Point(0, 0)).Y;
                if (offset > 0)
                {
                    _measuredPageTop = offset;
                    DiagnosticLog.Write($"chrome: page top in strip={offset}, footer height={TabStripHeight}");
                }
            }

            return _measuredPageTop > 0 ? _measuredPageTop : TabStripHeight;
        }
    }

    private bool IsVerticalChrome => ViewModel.UseVerticalTabs && !ViewModel.IsFullScreen;

    /// <summary>
    /// In vertical tabs mode the page runs to the top of the window, so the bar the caption
    /// buttons sit on is either fully there or fully gone - never a transparent strip lying over
    /// the site. Hidden, it is zero height: no tint, no buttons, and no drag region either, so
    /// the top of the page takes clicks like the rest of it. Shown, it is a real row the page is
    /// pushed down below (see <see cref="UpdatePageInset"/>), and the whole of it drags the
    /// window. Horizontal mode needs none of this: there the buttons sit in the tab strip, which
    /// is never over the page.
    /// </summary>
    private void UpdateVerticalCaptionBar()
    {
        // Resizing the element SetTitleBar was given is enough: WinUI re-derives the drag
        // rectangle from its bounds, so at zero height there is no drag region at all.
        VerticalModeDragRegion.Height = _isCaptionRevealed ? VerticalCaptionBarHeight : 0;
        UpdatePageInset();
    }

    /// <summary>The border state last applied, so the DWM call is only made on a change.</summary>
    private bool? _isWindowBorderHidden;

    /// <summary>
    /// Takes away the thin line Windows 11 draws round every window while vertical tabs is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DWM paints that border on top of the client area, not outside it. Against a title bar it
    /// is invisible, but in vertical tabs mode the page runs to the window's very top edge with
    /// the caption bar hidden, and there it shows as a light edge across the top of the site -
    /// exactly the one piece of non-website the mode is meant not to have.
    /// <c>DWMWA_COLOR_NONE</c> removes it; going back to the tab strip restores the system
    /// default. The window keeps its shadow and rounded corners either way, so it still reads
    /// as a window against the desktop.
    /// </para>
    /// <para>
    /// The attribute is Windows 11 (22000) only. Earlier, the call fails and nothing changes,
    /// which is fine - Windows 10 draws no such border over the client area.
    /// </para>
    /// </remarks>
    private void UpdateWindowBorder(bool hide)
    {
        if (_isWindowBorderHidden == hide)
        {
            return;
        }

        _isWindowBorderHidden = hide;
        var color = hide ? DwmColorNone : DwmColorDefault;
        var result = DwmSetWindowAttribute(WindowHandle, DwmwaBorderColor, ref color, sizeof(uint));
        if (result != 0)
        {
            DiagnosticLog.Write($"chrome: border colour not applied (hr=0x{result:X8})");
        }
    }

    private bool IsVerticalTabsDocked =>
        ViewModel.IsVerticalTabsPaneVisible &&
        ViewModel.IsVerticalTabsPinned &&
        RootGrid.ActualWidth >= MinWidthToDockVerticalTabs;

    /// <summary>
    /// Clamped to the window's own size: on a narrow window a fixed 240px card would otherwise
    /// hang off the edge rather than shrinking to fit inside it.
    /// </summary>
    private double VerticalTabsPaneWidth =>
        Math.Min(VerticalTabsExpandedWidth, Math.Max(0, RootGrid.ActualWidth - (VerticalTabsPaneMargin * 2)));

    /// <summary>
    /// Sizes and places the floating pane. A Popup does not stretch to anything, and its offsets
    /// are relative to where it sits in the tree - the client area's top-left - so both are set
    /// here rather than in XAML, and re-set whenever the window resizes.
    /// </summary>
    private void UpdateVerticalTabsFlyout()
    {
        if (RootGrid.ActualHeight <= 0)
        {
            return;
        }

        VerticalTabsFlyout.HorizontalOffset = VerticalTabsPaneMargin;
        VerticalTabsFlyout.VerticalOffset = VerticalTabsPaneMargin;

        var width = VerticalTabsPaneWidth;
        var height = Math.Max(0, RootGrid.ActualHeight - (VerticalTabsPaneMargin * 2));

        VerticalTabsHost.Width = width;
        VerticalTabsHost.Height = height;
        VerticalTabsCard.Width = width;
        VerticalTabsCard.Height = height;

        // The card leaves by sliding its own width to the left; this keeps it from being drawn
        // outside the pane's slot - past the window's edge - on the way.
        VerticalTabsHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
    }

    /// <summary>
    /// Slides the pane in or out to match <see cref="BrowserViewModel.IsVerticalTabsPaneExpanded"/>.
    /// Idempotent and retargetable: called again mid-flight - the pointer coming back while the
    /// pane is still leaving - it turns around from wherever the card currently is, rather than
    /// jumping to either end first.
    /// </summary>
    /// <remarks>
    /// Entrance and exit use the two Fluent motion curves: decelerate in, so the pane arrives
    /// fast and settles; accelerate out, so it gets out of the way without hanging around. Both
    /// slide and fade together, the fade being what keeps a half-way frame from reading as a
    /// rendering glitch. The popup is only closed once the exit has actually finished.
    /// </remarks>
    private void UpdateVerticalTabsPane()
    {
        // Too early to open a windowed popup; RootGrid.Loaded calls back in once it is not.
        if (!RootGrid.IsLoaded || RootGrid.XamlRoot is null)
        {
            return;
        }

        var show = ViewModel.IsVerticalTabsPaneExpanded;
        if (show == _isVerticalTabsPaneShown)
        {
            return;
        }

        _isVerticalTabsPaneShown = show;
        var generation = ++_paneAnimationGeneration;

        var visual = ElementCompositionPreview.GetElementVisual(VerticalTabsCard);
        var compositor = visual.Compositor;
        var hidden = new Vector3(-(float)VerticalTabsPaneWidth, 0, 0);
        var duration = PaneAnimationDuration(show);

        if (show && !VerticalTabsFlyout.IsOpen)
        {
            // Coming from fully closed: put the card off-screen first, so the popup's first
            // frame is the start of the slide rather than the finished pane flashing up.
            SetPaneVisualState(visual, duration > 0 ? hidden : Vector3.Zero, duration > 0 ? 0 : 1);
            VerticalTabsFlyout.IsOpen = true;
        }

        if (duration <= 0)
        {
            SetPaneVisualState(visual, show ? Vector3.Zero : hidden, show ? 1 : 0);
            VerticalTabsFlyout.IsOpen = show;
            return;
        }

        var easing = show
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f))
            : compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0.0f), new Vector2(1.0f, 0.5f));
        var length = TimeSpan.FromMilliseconds(duration);

        // No keyframe at 0 on purpose: a composition animation without one starts from the
        // property's current value, which is what makes reversing mid-slide seamless.
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1.0f, show ? Vector3.Zero : hidden, easing);
        slide.Duration = length;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1.0f, show ? 1.0f : 0.0f, easing);
        fade.Duration = length;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Translation", slide);
        visual.StartAnimation("Opacity", fade);
        batch.End();

        if (!show)
        {
            // Completed also fires when a newer animation cuts this one short, which is what the
            // generation check is for: only the exit that actually ran to the end may close.
            batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (generation == _paneAnimationGeneration && !_isVerticalTabsPaneShown)
                {
                    VerticalTabsFlyout.IsOpen = false;
                }
            });
        }
    }

    private static void SetPaneVisualState(Visual visual, Vector3 translation, float opacity)
    {
        visual.StopAnimation("Translation");
        visual.StopAnimation("Opacity");
        visual.Properties.InsertVector3("Translation", translation);
        visual.Opacity = opacity;
    }

    /// <summary>
    /// The slide's length in milliseconds, or 0 for none: before the window has first appeared,
    /// when the user has turned the slide off, or when Windows' own "Animation effects" is off.
    /// </summary>
    private int PaneAnimationDuration(bool show)
    {
        if (!_hasActivated || !_uiSettings.AnimationsEnabled)
        {
            return 0;
        }

        var milliseconds = AppServices.Settings.Current.VerticalTabsAnimationSpeed switch
        {
            SidebarAnimationSpeed.Off => 0,
            SidebarAnimationSpeed.Fast => FastPaneAnimationMs,
            SidebarAnimationSpeed.Relaxed => RelaxedPaneAnimationMs,
            _ => NormalPaneAnimationMs,
        };

        // Exits are quicker than entrances: the pane arriving is something to look at, the pane
        // leaving is just getting out of the way.
        return show ? milliseconds : milliseconds * 3 / 4;
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
    // Two pieces of chrome appear on the cursor reaching an edge of the window: the vertical tabs
    // pane at the left, and the caption buttons with the bar they sit on at the top right. Both
    // are decided by polling GetCursorPos rather than by PointerEntered on an element parked
    // there, and neither could work any other way.
    //
    // The pane tried the element approach first and failed twice over: it needed a band of the
    // window kept clear of WebView2 to receive pointer events at all, which was visible as a
    // strip of window background beside every page; and once the pane itself became a windowed
    // Popup, the strip and the pane were separate hit-test surfaces, so crossing between them
    // produced an exit and an enter with no ordering guarantee and the pane flickered.
    //
    // The caption zone has no element to attach to even in principle. SetTitleBar hands that
    // whole band to the system as a caption region, so it is non-client and raises no XAML
    // pointer events at all, and the buttons inside it are drawn by WinUI rather than by
    // anything in this tree. InputNonClientPointerSource reports non-client regions, but the
    // ones it reports are those an app registers for buttons it draws itself.
    //
    // Polling has neither problem. Nothing can swallow a cursor position, so the page keeps every
    // pixel including the ones under these zones, and every question is answered from the same
    // tick - there is no hand-off between surfaces to race, so no debounce is needed either.

    /// <summary>
    /// Runs the poll unless the window is minimized, which is the one state where neither zone
    /// can be pointed at. Idempotent, so every caller that changes the window's shape can just
    /// call it.
    /// </summary>
    private void UpdateCursorWatch()
    {
        if (_isClosing)
        {
            _cursorWatchTimer?.Stop();
            return;
        }

        if (_isMinimized)
        {
            _cursorWatchTimer?.Stop();
            _paneHoverLostAtTicks = 0;
            ViewModel.IsVerticalTabsPointerOver = false;
            SetCaptionRevealed(false);
            return;
        }

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
            // Both zones are evaluated on every tick whatever the rate, so backing off changes
            // only how soon a change is noticed - never whether it is.
            UpdateVerticalTabsHover();
            UpdateCaptionReveal();
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

        // A zone that is currently open is being pointed at right now, and leaving it has to
        // feel as prompt as reaching it did.
        if (_isCaptionRevealed || ViewModel.IsVerticalTabsPointerOver)
        {
            SetCursorPollRate(CursorPollMs);
            return;
        }

        var still = Environment.TickCount64 - _cursorMovedAtTicks;
        var rate = still >= CursorIdleAfterMs ? IdleCursorPollMs
            : still >= CursorRestingAfterMs ? RestingCursorPollMs
            : CursorPollMs;

        // An auto-hidden pane is the only way to reach the tabs, and backing off to the idle rate
        // would make the first reach for it after a pause - reading a page, say - wait up to two
        // seconds before anything happened. So while it is armed in the window being used, the
        // back-off stops short. Not foreground, or pinned, and the full saving applies as before.
        if (_isWindowActive && ViewModel.IsVerticalTabsPaneVisible && !ViewModel.IsVerticalTabsPinned)
        {
            rate = Math.Min(rate, ArmedCursorPollMs);
        }

        SetCursorPollRate(rate);
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
    /// One evaluation of "should the pane be peeking": the cursor is in the strip down the left
    /// of this window's client area, or the pane is out and the cursor is over it - or over a
    /// menu it opened, such as a tab's context menu, which hangs outside the pane's bounds and
    /// must not have the pane slide away from under it.
    /// </summary>
    /// <remarks>
    /// Evaluated whether or not the pane is pinned, so that unpinning it with the pointer still
    /// on it (which is where the pin button is) leaves it out until the pointer actually goes,
    /// rather than sliding it away and straight back on the next tick. Leaving only starts
    /// <see cref="VerticalTabsHideDelayMs"/>; it is the delay running out that hides the pane.
    /// </remarks>
    private void UpdateVerticalTabsHover()
    {
        // Another app in front: its window is over these coordinates, so the cursor being within
        // our edge band says nothing about what the user is pointing at.
        if (!ViewModel.IsVerticalTabsPaneVisible || GetForegroundWindow() != WindowHandle)
        {
            _paneHoverLostAtTicks = 0;
            ViewModel.IsVerticalTabsPointerOver = false;
            return;
        }

        var hovering = IsCursorAtLeftEdge() ||
            (_isVerticalTabsPaneShown && (IsCursorOverPane() || HasOpenPaneMenu()));

        if (hovering)
        {
            _paneHoverLostAtTicks = 0;
            ViewModel.IsVerticalTabsPointerOver = true;
            return;
        }

        if (!ViewModel.IsVerticalTabsPointerOver)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (_paneHoverLostAtTicks == 0)
        {
            _paneHoverLostAtTicks = now;
        }
        else if (now - _paneHoverLostAtTicks >= VerticalTabsHideDelayMs)
        {
            _paneHoverLostAtTicks = 0;
            ViewModel.IsVerticalTabsPointerOver = false;
        }
    }

    /// <summary>
    /// Whether a menu or other popup is open in this window - a tab's context menu, the address
    /// bar's suggestions - other than the pane itself and tooltips. The pane is only ever peeking
    /// when this is asked, so any such popup is one the user is in the middle of using.
    /// </summary>
    private bool HasOpenPaneMenu()
    {
        if (RootGrid.XamlRoot is not { } root)
        {
            return false;
        }

        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup != VerticalTabsFlyout && popup.Child is not ToolTip)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Brings the caption buttons back, along with the bar they sit on in vertical tabs mode,
    /// while the cursor is in the window's top-right corner - and takes both away again as soon
    /// as it leaves.
    /// </summary>
    /// <remarks>
    /// Unlike the pane above this deliberately does not check for the foreground window. Reaching
    /// for the close button of a window you have not focused yet is an ordinary way to close it,
    /// and buttons that stayed gone until after a click would be worse than no auto-hide at all.
    /// A window that is genuinely behind another one has its corner covered by that window, so
    /// restoring its own buttons under there costs nothing and shows nothing.
    /// </remarks>
    private void UpdateCaptionReveal() => SetCaptionRevealed(IsCursorInCaptionZone());

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
        UpdateVerticalCaptionBar();
    }

    private bool IsCursorInCaptionZone()
    {
        // Full screen has no caption buttons and no bar under them, so there is nothing the zone
        // could reveal - and TitleBar reports no inset to anchor it to either.
        if (ViewModel.IsFullScreen)
        {
            return false;
        }

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

        // Anchored to the client area rather than to AppWindow.Position, which is the outer rect:
        // a maximized window's outer rect hangs off every screen edge by the width of its
        // invisible resize border, and a corner measured from it would sit that far out.
        var padding = (int)Math.Round((_isCaptionRevealed ? CaptionRevealExitPadding : CaptionRevealPadding) * scale);
        var reach = _isCaptionRevealed ? (int)Math.Round(CaptionSnapLayoutReach * scale) : 0;

        var clientWidth = (int)Math.Round(RootGrid.ActualWidth * scale);
        var inset = (int)Math.Round(_captionInset * scale);
        var vertical = IsVerticalChrome;
        var barHeight = (int)Math.Round((vertical ? VerticalCaptionBarHeight : TabStripHeight) * scale);

        var left = origin.X + clientWidth - inset - padding;
        var top = origin.Y - padding;

        // In vertical tabs mode the revealed bar is the window's only title bar - the one place
        // to drag the window from - so once it is out it stays out while the pointer is anywhere
        // along it, not just near the buttons that summoned it.
        if (vertical && _isCaptionRevealed &&
            cursor.X >= origin.X - padding && cursor.X <= origin.X + clientWidth + padding &&
            cursor.Y >= top && cursor.Y <= origin.Y + barHeight + padding)
        {
            return true;
        }

        return cursor.X >= left && cursor.X <= origin.X + clientWidth + padding &&
               cursor.Y >= top && cursor.Y <= origin.Y + barHeight + reach + padding;
    }

    private bool IsCursorAtLeftEdge() =>
        TryGetCursorInClient(out var x, out var y) &&
        x >= 0 && x < VerticalTabsHoverZoneWidth &&
        y >= 0 && y < RootGrid.ActualHeight;

    /// <summary>
    /// Whether the cursor is within the pane's slot. Asked of the cursor position rather than
    /// tracked from PointerEntered/PointerExited on the card: the card is in its own popup HWND
    /// and the page in WebView2's, and an exit crossing between the two is not reliably raised -
    /// a missed one would have left the pane stuck open.
    /// </summary>
    private bool IsCursorOverPane() =>
        TryGetCursorInClient(out var x, out var y) &&
        x >= VerticalTabsPaneMargin && x < VerticalTabsPaneMargin + VerticalTabsCard.Width &&
        y >= VerticalTabsPaneMargin && y < VerticalTabsPaneMargin + VerticalTabsCard.Height;

    /// <summary>The cursor's position relative to the client area's top-left, in DIPs.</summary>
    private bool TryGetCursorInClient(out double x, out double y)
    {
        x = y = 0;

        if (!GetCursorPos(out var cursor))
        {
            return false;
        }

        // Anchored to the client area rather than to AppWindow.Position, which is the outer
        // rect - see IsCursorInCaptionZone.
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

        x = (cursor.X - origin.X) / scale;
        y = (cursor.Y - origin.Y) / scale;
        return true;
    }

    /// <summary>
    /// The one pointer event the pane still uses, purely so that it reacts the moment the pointer
    /// lands on it rather than on the next poll tick. Leaving is the poll's call alone.
    /// </summary>
    private void OnVerticalTabsPanePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _paneHoverLostAtTicks = 0;
        ViewModel.IsVerticalTabsPointerOver = true;
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

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private const int DwmwaBorderColor = 34;

    private const uint DwmColorNone = 0xFFFFFFFE;

    private const uint DwmColorDefault = 0xFFFFFFFF;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);

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

    private void OnVerticalAddressGotFocus(object sender, RoutedEventArgs e)
    {
        // Also covers clicking straight into an already hover-revealed address bar (not just
        // Ctrl+L, which sets this itself before focus can even land): either way, the pane needs
        // to stay open regardless of hover for as long as this box is being edited.
        ViewModel.IsVerticalTabsAddressBarFocused = true;

        if (ViewModel.SelectedTab is { } tab)
        {
            tab.IsAddressFocused = true;
        }
    }

    private void OnVerticalAddressLostFocus(object sender, RoutedEventArgs e)
    {
        ViewModel.IsVerticalTabsAddressBarFocused = false;

        if (ViewModel.SelectedTab is not { } tab)
        {
            return;
        }

        tab.IsAddressFocused = false;

        // Put back whatever the page's real address is if the edit was abandoned.
        VerticalAddressBox.Text = UrlHelper.ForDisplay(tab.Url);
    }
}
