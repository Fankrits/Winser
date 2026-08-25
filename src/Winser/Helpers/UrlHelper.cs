using System.Text.RegularExpressions;
using Winser.Models;

namespace Winser.Helpers;

public enum PageSecurity
{
    /// <summary>Nothing loaded yet.</summary>
    None,

    /// <summary>An app page, a local file, or the new tab page.</summary>
    Local,

    /// <summary>Served over https.</summary>
    Secure,

    /// <summary>Plain http (or another scheme with no transport security).</summary>
    Insecure,
}

/// <summary>
/// The address bar's brain: decides whether what the user typed is somewhere to go or
/// something to search for, and formats URLs for display.
/// </summary>
public static partial class UrlHelper
{
    private static readonly HashSet<string> NavigableSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "file", "ftp", "about", "data", "blob", "view-source",
        "mailto", "tel", InternalPages.Scheme,
    };

    /// <summary>
    /// The far narrower set a *page* may steer the browser to. <see cref="NavigableSchemes"/>
    /// is what the person at the keyboard is allowed to type; this is what web content is
    /// allowed to ask for. The gap is the point: <c>winser://</c> reaches Winser's own
    /// settings, history and downloads UI, and <c>file://</c> reaches the local disk, so
    /// neither may be opened on a page's say-so.
    /// </summary>
    private static readonly HashSet<string> WebRequestableSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https",
    };

    /// <summary>True when web content may ask Winser to open this URL.</summary>
    public static bool IsWebRequestable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && WebRequestableSchemes.Contains(uri.Scheme);

    /// <summary>host[:port][/path] — a dotted name, a bracketed IPv6 literal, or bare "localhost".</summary>
    [GeneratedRegex(
        @"^(?:\[[0-9A-Fa-f:]+\]|[\w\-]+(?:\.[\w\-]+)+|localhost)(?::\d{1,5})?(?:[/?#].*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex HostLike();

    [GeneratedRegex(@"^(?:[A-Za-z]:[\\/]|\\\\)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathLike();

    /// <summary>
    /// Turns raw address-bar text into something navigable, falling back to a web search.
    /// </summary>
    public static string Resolve(string? input, SearchEngine engine)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return InternalPages.NewTab;
        }

        if (InternalPages.IsInternal(text))
        {
            return InternalPages.Normalize(text);
        }

        // Drive letters would otherwise parse as a URI scheme ("c:").
        if (WindowsPathLike().IsMatch(text))
        {
            try
            {
                return new Uri(text).AbsoluteUri;
            }
            catch (UriFormatException)
            {
                return engine.BuildSearchUrl(text);
            }
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) &&
            NavigableSchemes.Contains(absolute.Scheme))
        {
            return text;
        }

        // Protocol-relative paste, e.g. "//example.com/x".
        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + text;
        }

        if (!ContainsWhitespace(text) && HostLike().IsMatch(text))
        {
            return "https://" + text;
        }

        return engine.BuildSearchUrl(text);
    }

    /// <summary>True when the text should navigate rather than search.</summary>
    public static bool LooksNavigable(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || ContainsWhitespace(text))
        {
            return false;
        }

        return InternalPages.IsInternal(text)
            || WindowsPathLike().IsMatch(text)
            || (Uri.TryCreate(text, UriKind.Absolute, out var uri) && NavigableSchemes.Contains(uri.Scheme))
            || HostLike().IsMatch(text);
    }

    /// <summary>What the address bar should show for a given loaded URL.</summary>
    public static string ForDisplay(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || InternalPages.IsNewTab(url) ||
            url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return InternalPages.FromNavigationTarget(url);
    }

    /// <summary>Short host label for the security chip, e.g. "github.com".</summary>
    public static string HostLabel(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (InternalPages.IsInternal(url))
        {
            return "Winser";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        if (uri.IsFile)
        {
            return "Local file";
        }

        var host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    public static PageSecurity Security(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return PageSecurity.None;
        }

        if (InternalPages.IsInternal(url) || InternalPages.IsNewTab(url))
        {
            return PageSecurity.Local;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return PageSecurity.None;
        }

        if (uri.IsFile)
        {
            return PageSecurity.Local;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? PageSecurity.Secure
            : PageSecurity.Insecure;
    }

    /// <summary>A stable per-origin key, used for grouping history and per-site zoom.</summary>
    public static string OriginKey(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile
            ? uri.GetLeftPart(UriPartial.Authority)
            : url ?? string.Empty;

    private static bool ContainsWhitespace(string value)
    {
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                return true;
            }
        }

        return false;
    }
}
