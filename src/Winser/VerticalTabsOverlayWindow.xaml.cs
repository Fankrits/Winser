using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Winser.Helpers;
using Winser.ViewModels;

namespace Winser;

/// <summary>
/// The expanded vertical tabs pane, in its own borderless top-level window owned by the
/// <see cref="MainWindow"/> it belongs to. See VerticalTabsOverlayWindow.xaml for why this pane
/// cannot simply live inline in that window (short version: WebView2's child HWND paints over any
/// XAML sibling, so an inline pane is invisible the moment it overlaps the page).
///
/// <see cref="MainWindow"/> owns every decision about when this is shown and where; this class
/// only knows how to place itself and how to report the pointer arriving and leaving.
/// </summary>
public sealed partial class VerticalTabsOverlayWindow : Window
{
    private const int GWLP_HWNDPARENT = -8;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUNDSMALL = 3;

    private readonly MainWindow _owner;

    public VerticalTabsOverlayWindow(MainWindow owner, BrowserViewModel viewModel)
    {
        _owner = owner;
        ViewModel = viewModel;

        InitializeComponent();

        VerticalPrivateBadge.Visibility = owner.IsPrivate ? Visibility.Visible : Visibility.Collapsed;

        var presenter = (OverlappedPresenter)AppWindow.Presenter;

        // No border and no title bar: this is a floating pane, not a window the user manages.
        // Also not resizable/minimizable/maximizable - MainWindow drives its bounds entirely.
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;

        AppWindow.IsShownInSwitchers = false;

        // Making this an *owned* window (rather than just moving it to the top of the z-order once)
        // is what keeps it reliably above the page: an owned window always renders above its owner
        // and every child HWND inside it, follows the owner when it is minimized or restored, and
        // is destroyed with it. A one-shot MoveInZOrderAboveWindow would come undone the first time
        // anything else reordered the two.
        SetWindowLongPtrW(WindowHandle, GWLP_HWNDPARENT, owner.WindowHandle);

        // Rounds all four corners at the DWM compositor level - the same mechanism Windows 11
        // uses for its own flyouts and context menus, and ROUNDSMALL is Microsoft's own guidance
        // for exactly that class of window (DWMWA_WINDOW_CORNER_PREFERENCE docs: "flyouts or
        // context menus"). This call simply fails on Windows 10, where the attribute does not
        // exist - harmless, since the result is never checked, and there is nothing to fall back
        // to that would not need its own separate XAML-side clipping.
        var cornerPreference = DWMWCP_ROUNDSMALL;
        DwmSetWindowAttribute(WindowHandle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

        AppWindow.Hide();
    }

    public BrowserViewModel ViewModel { get; }

    public nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>Applies the app theme, which this window does not inherit from its owner.</summary>
    public void ApplyTheme(ElementTheme theme) => OverlayRoot.RequestedTheme = theme;

    /// <summary>
    /// Lines the pane up with the left edge of its owner's client area. Bounds are in physical
    /// pixels while the caller thinks in DIPs, so the owner's rasterization scale is applied here;
    /// the client origin comes from ClientToScreen rather than AppWindow.Position because the
    /// latter is the outer frame, which includes a resize border this pane must sit inside of.
    /// </summary>
    public void SyncBounds(double widthDips, double heightDips, double scale)
    {
        var origin = default(POINT);
        if (!ClientToScreen(_owner.WindowHandle, ref origin))
        {
            return;
        }

        var width = (int)Math.Round(widthDips * scale);
        var height = (int)Math.Round(heightDips * scale);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        AppWindow.MoveAndResize(new RectInt32(origin.X, origin.Y, width, height));
    }

    /// <summary>
    /// The pane's rectangle in screen pixels, or null while it is hidden. <see cref="MainWindow"/>
    /// uses this to check where the pointer actually is before collapsing, rather than trusting
    /// enter/leave events to arrive in a sensible order across two separate windows.
    /// </summary>
    public RectInt32? ScreenBounds => AppWindow.IsVisible
        ? new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height)
        : null;

    public bool IsShown => AppWindow.IsVisible;

    /// <summary>
    /// Shown without activation by default: hovering the edge should reveal the pane without the
    /// owner's title bar greying out as if the user had switched windows. Ctrl+L passes
    /// <paramref name="activate"/> instead, because the address bar cannot take keyboard input in
    /// a window that never came forward.
    /// </summary>
    public void ShowOverlay(bool activate)
    {
        AppWindow.Show(activate);

        if (activate)
        {
            Activate();
        }
    }

    public void HideOverlay() => AppWindow.Hide();

    /// <summary>Moves focus into the address bar, opening the pane first if it is not up yet.</summary>
    public void FocusAddressBar()
    {
        ShowOverlay(activate: true);

        // Focus needs a laid-out target: if this call is what just brought the window up, the box
        // does not exist as far as focus is concerned until a layout pass has run. Same reason
        // BrowserTabPage.xaml.cs defers its own FindBar focus through the dispatcher.
        DispatcherQueue.TryEnqueue(() =>
        {
            VerticalAddressBox.Focus(FocusState.Programmatic);
            VerticalAddressBox.FindDescendant<TextBox>()?.SelectAll();
        });
    }

    // ------------------------------------------------------------------------- hover

    private void OnPointerEnteredOverlay(object sender, PointerRoutedEventArgs e) =>
        _owner.NotifyVerticalTabsPointerEntered();

    private void OnPointerExitedOverlay(object sender, PointerRoutedEventArgs e) =>
        _owner.NotifyVerticalTabsPointerExited();

    // -------------------------------------------------------------------- tab list

    /// <summary>
    /// SelectedItem binds one-way rather than two-way, and writes back from here instead. Two
    /// TwoWay x:Bind bindings onto the same source property is a shape known to trip x:Bind
    /// codegen (microsoft-ui-xaml#8441); MainWindow's own TabStrip already owns the TwoWay one.
    /// </summary>
    private void OnVerticalTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as ListView)?.SelectedItem is BrowserTabViewModel tab)
        {
            ViewModel.SelectedTab = tab;
        }
    }

    // ------------------------------------------------------------------ address bar
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

        // Hand the keyboard back to the page, which lives in the owner window - so this pane stops
        // being the foreground window too, and a hover-opened pane can collapse again.
        _owner.Activate();
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

    // ----------------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hWnd, ref POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint hWnd, int index, nint value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
