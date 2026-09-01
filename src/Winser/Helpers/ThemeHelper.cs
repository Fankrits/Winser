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
    /// Applies the theme to a window's content and repaints the system caption buttons to match.
    /// </summary>
    public static void Apply(FrameworkElement root, AppWindow appWindow, AppTheme theme, bool captionButtonsVisible)
    {
        root.RequestedTheme = ToElementTheme(theme);
        UpdateCaptionButtons(root, appWindow, captionButtonsVisible);
    }

    /// <summary>
    /// Paints the system caption buttons legibly on top of the Mica backdrop, or - when
    /// <paramref name="visible"/> is false - paints them out of sight entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Colour is the only lever there is. Once <c>ExtendsContentIntoTitleBar</c> is on, WinUI
    /// draws minimize/maximize/close itself and <see cref="AppWindowTitleBar"/> exposes their
    /// colours and no visibility at all - so the hidden state is every one of those colours set
    /// transparent. That is also what makes it safe: hit-testing is untouched, so clicks, snap
    /// layouts and screen readers carry on working while only the pixels go away.
    /// </para>
    /// <para>
    /// The hover and pressed colours are part of the hidden state rather than left visible.
    /// Leaving them set was the earlier attempt at this (1c1c013) and is what made the buttons
    /// light up one at a time under the cursor and never as a group: Windows repaints only the
    /// button the pointer is actually on. Which one shows is now decided by
    /// <c>MainWindow.UpdateCaptionReveal</c>, from a zone rather than from a single button.
    /// </para>
    /// </remarks>
    public static void UpdateCaptionButtons(FrameworkElement root, AppWindow appWindow, bool visible)
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
        bar.ButtonForegroundColor = visible ? foreground : Colors.Transparent;
        bar.ButtonInactiveForegroundColor = visible ? muted : Colors.Transparent;
        bar.ButtonHoverBackgroundColor = visible ? hover : Colors.Transparent;
        bar.ButtonHoverForegroundColor = visible ? foreground : Colors.Transparent;
        bar.ButtonPressedBackgroundColor = visible ? pressed : Colors.Transparent;
        bar.ButtonPressedForegroundColor = visible ? foreground : Colors.Transparent;
    }
}
