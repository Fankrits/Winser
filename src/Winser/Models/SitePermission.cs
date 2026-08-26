using Winser.Helpers;

namespace Winser.Models;

/// <summary>The permission kinds Winser mediates and remembers. WebView2 has a few more
/// (autoplay, local fonts, window management, ...); those are left to its own defaults
/// since Winser has no UI to explain or revoke them yet.</summary>
public enum SitePermissionKind
{
    Camera = 0,
    Microphone = 1,
    Geolocation = 2,
    Notifications = 3,
    ClipboardRead = 4,
}

public enum SitePermissionState
{
    Allow = 0,
    Deny = 1,
}

/// <summary>One remembered permission decision for one origin.</summary>
public sealed class SitePermission
{
    public string Origin { get; set; } = string.Empty;

    public SitePermissionKind Kind { get; set; }

    public SitePermissionState State { get; set; }

    public DateTimeOffset DecidedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string HostLabel => UrlHelper.HostLabel(Origin);

    public string KindLabel => Kind switch
    {
        SitePermissionKind.Camera => "Camera",
        SitePermissionKind.Microphone => "Microphone",
        SitePermissionKind.Geolocation => "Location",
        SitePermissionKind.Notifications => "Notifications",
        SitePermissionKind.ClipboardRead => "Clipboard",
        _ => Kind.ToString(),
    };

    public string StateLabel => State == SitePermissionState.Allow ? "Allowed" : "Blocked";
}
