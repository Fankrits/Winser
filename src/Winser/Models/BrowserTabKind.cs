namespace Winser.Models;

/// <summary>
/// What a tab is showing. Everything except <see cref="Web"/> is a native XAML page rendered
/// in place of the WebView2, the same way <c>edge://settings</c> is not a real web page.
/// </summary>
public enum BrowserTabKind
{
    Web = 0,
    Settings = 1,
    History = 2,
    Downloads = 3,
    Bookmarks = 4,
}
