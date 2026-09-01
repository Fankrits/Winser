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
    /// Applies the theme to a window's content and repaints the system caption buttons so they
    /// stay legible on top of the Mica backdrop.
    /// </summary>
    public static void Apply(FrameworkElement root, AppWindow appWindow, AppTheme theme)
    {
        root.RequestedTheme = ToElementTheme(theme);
        UpdateCaptionButtons(root, appWindow);
    }

    /// <summary>Recolours minimize/maximize/close for the current theme.</summary>
    /// <remarks>
    /// Colour cannot hide these buttons, and two commits tried before this comment existed.
    /// While content is extended into the title bar, <see cref="AppWindowTitleBar"/> honours the
    /// alpha channel on the button *background* colours and ignores it on every other one - so
    /// <c>ButtonForegroundColor = Colors.Transparent</c> is not a transparent glyph, it is an
    /// opaque white one, and the buttons stay exactly as visible as they were. Hiding them is
    /// <c>TitleBarHeightOption.Collapsed</c>, which takes the whole reserved area to zero;
    /// see <c>MainWindow.ApplyCaptionReveal</c>. This method only ever paints them visible,
    /// because that is the only state in which they exist.
    /// </remarks>
    public static void UpdateCaptionButtons(FrameworkElement root, AppWindow appWindow)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var bar = appWindow.TitleBar;
        var dark = root.ActualTheme == ElementTheme.Dark;
        var foreground = dark ? Colors.White : Colors.Black;
        var muted = dark ? Color.FromArgb(255, 155, 155, 155) : Color.FromArgb(255, 105, 105, 105);
        var hover = dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
        var pressed = dark ? Color.FromArgb(45, 255, 255, 255) : Color.FromArgb(36, 0, 0, 0);

        bar.BackgroundColor = Colors.Transparent;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = muted;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = pressed;
        bar.ButtonPressedForegroundColor = foreground;
    }
}
