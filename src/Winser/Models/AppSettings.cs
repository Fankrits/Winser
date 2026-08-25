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

    /// <summary>Open <c>window.open</c> popups as tabs instead of letting sites spawn windows.</summary>
    public bool OpenPopupsAsTabs { get; set; } = true;

    public TrackingPrevention TrackingPrevention { get; set; } = TrackingPrevention.Balanced;

    public bool EnableDevTools { get; set; } = true;

    public bool EnableJavaScript { get; set; } = true;

    public bool EnableStatusBar { get; set; } = true;

    public bool AskWhereToSaveDownloads { get; set; }

    public string? DownloadFolder { get; set; }

    public double DefaultZoomFactor { get; set; } = 1.0;

    /// <summary>Drop browsing history when the last window closes.</summary>
    public bool ClearHistoryOnExit { get; set; }

    public int HistoryRetentionDays { get; set; } = 90;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
