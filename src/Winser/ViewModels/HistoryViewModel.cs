using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winser.Helpers;
using Winser.Models;
using Winser.Services;

namespace Winser.ViewModels;

/// <summary>
/// A day's worth of history. Deriving from List keeps it compatible with the grouped-ListView
/// pattern, which hands the group object straight to the header template.
/// </summary>
public sealed class HistoryGroup : List<HistoryEntry>
{
    public HistoryGroup(string key, IEnumerable<HistoryEntry> items)
        : base(items) => Key = key;

    public string Key { get; }
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private const int MaxResults = 500;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    public HistoryViewModel() => Refresh();

    public ObservableCollection<HistoryGroup> Groups { get; } = [];

    public void Refresh()
    {
        Groups.Clear();

        foreach (var group in AppServices.History
                     .Search(Query, MaxResults)
                     .GroupBy(entry => entry.LastVisitedUtc.ToLocalTime().Date))
        {
            Groups.Add(new HistoryGroup(Format.DayBucket(group.Key), group));
        }

        IsEmpty = Groups.Count == 0;
    }

    public void Remove(HistoryEntry entry) => AppServices.History.Remove(entry);

    /// <summary>
    /// Starts listening for history changes. Paired with <see cref="Detach"/> on the view's
    /// load/unload rather than done once in the constructor: TabView unloads a page when the
    /// user switches tabs, so a one-way teardown would leave the list frozen on return.
    /// </summary>
    public void Attach()
    {
        AppServices.History.Changed -= OnHistoryChanged;
        AppServices.History.Changed += OnHistoryChanged;
        Refresh();
    }

    public void Detach() => AppServices.History.Changed -= OnHistoryChanged;

    [RelayCommand]
    private void ClearAll() => AppServices.History.Clear();

    partial void OnQueryChanged(string value) => Refresh();

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();
}
