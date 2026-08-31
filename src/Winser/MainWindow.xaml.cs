using System.Diagnostics;
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
    /// Gap between the expanded pane and the window's top, left and bottom edges, so it reads as
    /// a card floating over the page rather than a panel welded to the frame - the rounded
    /// corners only look intentional with a gap for them to show against.
    /// </summary>
    private const double VerticalTabsPaneMargin = 8;

    /// <summary>
    /// Grace period between the pointer leaving one half of the hover surface and the pane
    /// actually collapsing - long enough to cover the hand-off while the pointer crosses from the
    /// collapsed strip into the expanded card, which are separate hit-test surfaces (the card is
    /// a windowed Popup). See OnVerticalTabsPanePointerExited.
    /// </summary>
    private const int VerticalTabsCollapseDelayMs = 150;

    private bool _isClosing;
    private bool _isWindowActive = true;

    /// <summary>Created on first hover-out; see OnVerticalTabsPanePointerExited.</summary>
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

        PrivateBadge.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;
        VerticalPrivateBadge.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;

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

        // Constant at the hover-zone width whenever vertical tabs is on: the expanded chrome is a
        // windowed Popup (see the XAML), which is not laid out in this Grid at all, so nothing
        // here has to make room for it and the page never shifts when the pane opens. An earlier
        // attempt did widen this column to match, which avoided the airspace problem by keeping
        // the two rectangles from overlapping - at the cost of reflowing the page every time the
        // pane was hovered. The popup gets its own HWND above WebView2's instead, so overlapping
        // is fine now.
        VerticalTabsColumnDef.Width = vertical ? new GridLength(VerticalTabsHoverZoneWidth) : new GridLength(0);
        VerticalTabsPane.Width = VerticalTabsHoverZoneWidth;

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

        // The vertical mode drag region overlays the same top strip TabStrip just vacated,
        // rather than reserving it, so it needs to match that same height to look intentional.
        // No left-edge offset is needed here: VerticalModeDragRegion is Grid.Column="1", and
        // that column now starts wherever VerticalTabsColumnDef ends, so it already clears the
        // pane's header (app icon, pin button) whether collapsed or expanded without any
        // compensating Margin.
        VerticalModeDragRegion.Height = TabStripHeight;

        // In vertical mode CustomDragRegion is inside that now-hidden strip, so dragging and the
        // caption buttons need to be re-anchored to VerticalModeDragRegion instead.
        SetTitleBar(vertical ? VerticalModeDragRegion : CustomDragRegion);
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
        VerticalTabsFlyout.VerticalOffset = VerticalTabsPaneMargin;

        // Clamped to the window's own size: on a narrow or short window a fixed 240px card would
        // otherwise hang off the edge rather than shrinking to fit inside it.
        VerticalTabsCard.Width = Math.Min(
            VerticalTabsExpandedWidth,
            Math.Max(0, RootGrid.ActualWidth - (VerticalTabsPaneMargin * 2)));
        VerticalTabsCard.Height = Math.Max(0, RootGrid.ActualHeight - (VerticalTabsPaneMargin * 2));
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

    /// <summary>Peeks the collapsed vertical tabs pane open while the pointer is over it.</summary>
    // The hover surface is two elements, not one: the collapsed strip lives in this window, and
    // the expanded card lives in a windowed Popup with its own HWND. Crossing from one to the
    // other therefore fires an exit on the first and an enter on the second with no ordering
    // guarantee between them, and acting on the exit directly makes the pane flicker or oscillate
    // - close, pointer is over the strip again, open, repeat. So a leave only ever *schedules* a
    // collapse, and any enter within the grace period cancels it. (The inline pane this replaced
    // needed none of this: it was a single continuous element, so there was no hand-off at all.)

    private void OnVerticalTabsPanePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _verticalTabsCollapseTimer?.Stop();
        ViewModel.IsVerticalTabsPointerOver = true;
    }

    private void OnVerticalTabsPanePointerExited(object sender, PointerRoutedEventArgs e)
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
            ViewModel.IsVerticalTabsPointerOver = false;
        };

        return timer;
    }

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
        tab.Suggestions.Clear();
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
