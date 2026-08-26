using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winser.Helpers;
using Winser.Models;
using Winser.Services;

namespace Winser.ViewModels;

/// <summary>
/// A bindable face over <see cref="AppSettings"/>. Every setter writes straight through and
/// commits, so there is no Save button and no chance of the UI and the file disagreeing.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _service = AppServices.Settings;

    public IReadOnlyList<string> ThemeOptions { get; } =
        ["Use system setting", "Light", "Dark"];

    public IReadOnlyList<string> StartupOptions { get; } =
        ["Open the new tab page", "Open the home page", "Continue where I left off"];

    public IReadOnlyList<string> TrackingOptions { get; } =
        ["Off", "Basic", "Balanced (recommended)", "Strict"];

    public IReadOnlyList<SearchEngine> SearchEngines { get; } = SearchEngine.All;

    private AppSettings Values => _service.Current;

    public int ThemeIndex
    {
        get => (int)Values.Theme;
        set => Set(value, (int)Values.Theme, v => Values.Theme = (AppTheme)v);
    }

    public int StartupIndex
    {
        get => (int)Values.Startup;
        set => Set(value, (int)Values.Startup, v => Values.Startup = (StartupBehavior)v);
    }

    public int TrackingIndex
    {
        get => (int)Values.TrackingPrevention;
        set => Set(value, (int)Values.TrackingPrevention, v => Values.TrackingPrevention = (TrackingPrevention)v);
    }

    public int SearchEngineIndex
    {
        get
        {
            var index = SearchEngines.ToList().FindIndex(e => e.Id == Values.SearchEngineId);
            return index < 0 ? 0 : index;
        }

        set
        {
            if (value < 0 || value >= SearchEngines.Count || value == SearchEngineIndex)
            {
                return;
            }

            Values.SearchEngineId = SearchEngines[value].Id;
            _service.Commit();
            OnPropertyChanged();
        }
    }

    public string HomePage
    {
        get => Values.HomePage;
        set => Set(value ?? string.Empty, Values.HomePage, v => Values.HomePage = v);
    }

    public bool ShowBookmarksBar
    {
        get => Values.ShowBookmarksBar;
        set => Set(value, Values.ShowBookmarksBar, v => Values.ShowBookmarksBar = v);
    }

    public bool OpenPopupsAsTabs
    {
        get => Values.OpenPopupsAsTabs;
        set => Set(value, Values.OpenPopupsAsTabs, v => Values.OpenPopupsAsTabs = v);
    }

    public bool SleepBackgroundTabs
    {
        get => Values.SleepBackgroundTabs;
        set => Set(value, Values.SleepBackgroundTabs, v => Values.SleepBackgroundTabs = v);
    }

    public bool EnableJavaScript
    {
        get => Values.EnableJavaScript;
        set => Set(value, Values.EnableJavaScript, v => Values.EnableJavaScript = v);
    }

    public bool EnableDevTools
    {
        get => Values.EnableDevTools;
        set => Set(value, Values.EnableDevTools, v => Values.EnableDevTools = v);
    }

    public bool EnableAutofill
    {
        get => Values.EnableAutofill;
        set => Set(value, Values.EnableAutofill, v => Values.EnableAutofill = v);
    }

    /// <summary>Sites with a remembered camera/microphone/location/notification/clipboard decision.</summary>
    public ObservableCollection<SitePermission> GrantedPermissions =>
        AppServices.Permissions.Items;

    public bool AskWhereToSaveDownloads
    {
        get => Values.AskWhereToSaveDownloads;
        set => Set(value, Values.AskWhereToSaveDownloads, v => Values.AskWhereToSaveDownloads = v);
    }

    public bool ClearHistoryOnExit
    {
        get => Values.ClearHistoryOnExit;
        set => Set(value, Values.ClearHistoryOnExit, v => Values.ClearHistoryOnExit = v);
    }

    public double HistoryRetentionDays
    {
        get => Values.HistoryRetentionDays;
        set
        {
            // An emptied NumberBox reports NaN.
            if (double.IsNaN(value))
            {
                return;
            }

            Set((int)Math.Clamp(value, 0, 3650), Values.HistoryRetentionDays, v => Values.HistoryRetentionDays = v);
        }
    }

    public double DefaultZoomPercent
    {
        get => Math.Round(Values.DefaultZoomFactor * 100);
        set
        {
            if (double.IsNaN(value))
            {
                return;
            }

            var factor = Math.Clamp(value, 25, 500) / 100.0;
            if (Math.Abs(factor - Values.DefaultZoomFactor) < 0.001)
            {
                return;
            }

            Values.DefaultZoomFactor = factor;
            _service.Commit();
            OnPropertyChanged();
        }
    }

    public string DownloadFolder
    {
        get => _service.EffectiveDownloadFolder;
        set => Set(value, Values.DownloadFolder ?? string.Empty, v => Values.DownloadFolder = v);
    }

    public string AppVersion
    {
        get
        {
            var version = typeof(SettingsViewModel).Assembly.GetName().Version;
            return version is null ? "1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string RuntimeDescription =>
        AppServices.WebView.RuntimeVersion is { } version
            ? $"Rendering with the Microsoft Edge WebView2 Runtime {version}"
            : "The WebView2 Runtime version appears here once a web page has loaded.";

    public string DataFolder => AppPaths.Root;

    [RelayCommand]
    private void OpenDataFolder() => SystemShell.OpenFolder(AppPaths.Root);

    [RelayCommand]
    private void UseNewTabAsHomePage() => HomePage = InternalPages.NewTab;

    private void Set<T>(T value, T current, Action<T> apply, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
        {
            return;
        }

        apply(value);
        _service.Commit();
        OnPropertyChanged(property);
    }
}
