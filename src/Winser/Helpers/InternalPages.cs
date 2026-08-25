using Winser.Models;

namespace Winser.Helpers;

/// <summary>
/// The <c>winser://</c> pseudo-scheme. Native pages (settings, history, ...) never reach
/// WebView2 at all; the new tab page is real HTML served off a virtual host that is mapped
/// to the app's <c>Assets\Web</c> folder, which keeps it on a normal https origin so the
/// page can use fetch, localStorage and the rest without file:// restrictions.
/// </summary>
public static class InternalPages
{
    public const string Scheme = "winser";

    public const string NewTab = "winser://newtab";
    public const string Settings = "winser://settings";
    public const string History = "winser://history";
    public const string Downloads = "winser://downloads";
    public const string Bookmarks = "winser://bookmarks";

    /// <summary>Host name mapped to <c>Assets\Web</c> via SetVirtualHostNameToFolderMapping.</summary>
    public const string VirtualHost = "assets.winser";

    public const string NewTabUri = "https://assets.winser/newtab.html";

    public static bool IsInternal(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower-cases and trims a <c>winser://</c> URL so comparisons are stable.</summary>
    public static string Normalize(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        return IsInternal(trimmed) ? trimmed.ToLowerInvariant() : trimmed;
    }

    public static BrowserTabKind ResolveKind(string? url) => Normalize(url ?? string.Empty) switch
    {
        Settings => BrowserTabKind.Settings,
        History => BrowserTabKind.History,
        Downloads => BrowserTabKind.Downloads,
        Bookmarks => BrowserTabKind.Bookmarks,
        _ => BrowserTabKind.Web,
    };

    public static string Title(BrowserTabKind kind) => kind switch
    {
        BrowserTabKind.Settings => "Settings",
        BrowserTabKind.History => "History",
        BrowserTabKind.Downloads => "Downloads",
        BrowserTabKind.Bookmarks => "Bookmarks",
        _ => "New tab",
    };

    /// <summary>Maps a user-facing URL onto what WebView2 should actually load.</summary>
    public static string ToNavigationTarget(string url) =>
        string.Equals(Normalize(url), NewTab, StringComparison.OrdinalIgnoreCase) ? NewTabUri : url;

    /// <summary>Maps a WebView2 URL back onto what the address bar should show.</summary>
    public static string FromNavigationTarget(string url) =>
        url.StartsWith(NewTabUri, StringComparison.OrdinalIgnoreCase) ? NewTab : url;

    public static bool IsNewTab(string? url) =>
        url is not null &&
        (string.Equals(Normalize(url), NewTab, StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith(NewTabUri, StringComparison.OrdinalIgnoreCase));
}
