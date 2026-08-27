using Winser.Helpers;

namespace Winser.Models;

public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>Mirrors CoreWebView2TrackingPreventionLevel.</summary>
public enum TrackingPrevention
{
    Off = 0,
    Basic = 1,
    Balanced = 2,
    Strict = 3,
}

public enum StartupBehavior
{
    NewTabPage = 0,
    HomePage = 1,
    RestorePreviousSession = 2,
}

/// <summary>
/// Everything the user can change in <c>winser://settings</c>. Persisted as JSON in the
/// Winser data folder; see <see cref="Services.SettingsService"/>.
/// </summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public StartupBehavior Startup { get; set; } = StartupBehavior.RestorePreviousSession;

    public string HomePage { get; set; } = InternalPages.NewTab;

    public string SearchEngineId { get; set; } = SearchEngine.DefaultId;

    public bool ShowBookmarksBar { get; set; } = true;

    /// <summary>Tabs in a collapsible pane on the side instead of a strip across the top.</summary>
    public bool UseVerticalTabs { get; set; }

    /// <summary>
    /// When vertical tabs are on: whether the pane stays expanded, rather than collapsing to an
    /// icon-only rail that only expands while the pointer is over it.
    /// </summary>
    public bool VerticalTabsPinned { get; set; } = true;

    /// <summary>Open <c>window.open</c> popups as tabs instead of letting sites spawn windows.</summary>
    public bool OpenPopupsAsTabs { get; set; } = true;

    public TrackingPrevention TrackingPrevention { get; set; } = TrackingPrevention.Balanced;

    /// <summary>
    /// Freeze a tab's browser process while the tab is in the background, handing its memory
    /// back until the tab is looked at again. Coming back is a resume, not a reload, so
    /// nothing on the page is lost - but a frozen page's timers and scripts are stopped, so
    /// anything that expects to keep working out of sight (a chat, a live dashboard) stays
    /// quiet until it is on screen. Tabs that are audibly playing something are never frozen.
    /// </summary>
    public bool SleepBackgroundTabs { get; set; } = true;

    /// <summary>
    /// Minutes a background tab may sit unwatched before its renderer is discarded outright,
    /// freeing its memory completely rather than just freezing it. Revisiting a discarded tab
    /// is a fresh page load, not a resume - scroll position and anything unsubmitted are lost -
    /// which is why this is a separate, much coarser setting than
    /// <see cref="SleepBackgroundTabs"/>. 0 means never discard.
    /// </summary>
    public int DiscardIdleTabsAfterMinutes { get; set; } = 30;

    public bool EnableDevTools { get; set; } = true;

    public bool EnableJavaScript { get; set; } = true;

    /// <summary>
    /// Let WebView2 remember and suggest form field values and passwords. On by default, the
    /// same as every other browser out of the box - but explicit and switchable here, rather
    /// than silently on with no UI to see or turn it off.
    /// </summary>
    public bool EnableAutofill { get; set; } = true;

    public bool EnableStatusBar { get; set; } = true;

    public bool AskWhereToSaveDownloads { get; set; }

    public string? DownloadFolder { get; set; }

    public double DefaultZoomFactor { get; set; } = 1.0;

    /// <summary>Drop browsing history when the last window closes.</summary>
    public bool ClearHistoryOnExit { get; set; }

    public int HistoryRetentionDays { get; set; } = 90;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
