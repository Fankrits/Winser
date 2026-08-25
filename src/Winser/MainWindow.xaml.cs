using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
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

    private bool _isClosing;

    public MainWindow(bool isPrivate = false, string? initialUrl = null)
    {
        IsPrivate = isPrivate;
        ViewModel = new BrowserViewModel(isPrivate);

        InitializeComponent();

        ViewModel.AttachWindow(this);
        WindowManager.Register(this);

        Title = ViewModel.WindowTitle;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Winser.ico"));

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(CustomDragRegion);

        PrivateBadge.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;

        ApplyTheme();
        AppServices.Settings.Changed += OnSettingsChanged;

        RootGrid.ActualThemeChanged += OnActualThemeChanged;
        RootGrid.PreviewKeyDown += OnPreviewKeyDown;
        RootGrid.SizeChanged += (_, _) => UpdateCaptionInset();

        RestorePlacement();
        Activated += OnFirstActivated;
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

        // TabView renders the strip and the content in one control, so the only way to hide
        // just the strip is to slide it above the client area. A negative top margin on a
        // stretched child grows it by the same amount, so the page still fills the window.
        TabStrip.Margin = fullScreen
            ? new Thickness(0, -TabStripHeight, 0, 0)
            : new Thickness(0);

        ViewModel.IsFullScreen = fullScreen;
    }

    public void FocusAddressBar() => ViewModel.SelectedTab?.RequestAddressFocus();

    public void CloseWindow() => Close();

    // --------------------------------------------------------------------- lifecycle

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        UpdateCaptionInset();
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

        ViewModel.Detach();
        WindowManager.Unregister(this);
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

    private double TabStripHeight =>
        Application.Current.Resources.TryGetValue("TabViewItemHeight", out var value) && value is double height
            ? height
            : 40;

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
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // ------------------------------------------------------------------ tab strip

    private void OnAddTabButtonClick(TabView sender, object args) => ViewModel.NewTab();

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is BrowserTabViewModel tab)
        {
            ViewModel.CloseTab(tab);
        }
    }
}
