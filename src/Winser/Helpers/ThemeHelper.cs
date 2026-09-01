using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;   // the Color struct; Colors itself only exists under Microsoft.UI
using Winser.Models;

namespace Winser.Helpers;

public static class ThemeHelper
{
    public static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>
    /// Applies the theme to a window's content and repaints the system caption buttons - hidden
    /// until hovered, then legible on top of the Mica backdrop - to match.
    /// </summary>
    public static void Apply(FrameworkElement root, AppWindow appWindow, AppTheme theme)
    {
        root.RequestedTheme = ToElementTheme(theme);
        UpdateCaptionButtons(root, appWindow);
    }

    public static void UpdateCaptionButtons(FrameworkElement root, AppWindow appWindow)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var bar = appWindow.TitleBar;
        var dark = root.ActualTheme == ElementTheme.Dark;
        var foreground = dark ? Colors.White : Colors.Black;
        var hover = dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
        var pressed = dark ? Color.FromArgb(45, 255, 255, 255) : Color.FromArgb(36, 0, 0, 0);

        bar.BackgroundColor = Colors.Transparent;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        // Transparent at rest - glyph included, not just background - so minimize/maximize/close
        // draw nothing until hovered. Windows still tracks the pointer over their hit-test area
        // regardless of this: that area lives in the non-client region, entirely outside the
        // XAML visual tree, so hover/press repainting keeps working with no code-behind needed.
        bar.ButtonForegroundColor = Colors.Transparent;
        bar.ButtonInactiveForegroundColor = Colors.Transparent;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = pressed;
        bar.ButtonPressedForegroundColor = foreground;
    }
}
