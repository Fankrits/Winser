using System.Collections.ObjectModel;
using Microsoft.Web.WebView2.Core;
using Winser.Models;
using Winser.ViewModels;

namespace Winser.Services;

public sealed class DownloadService : IDisposable
{
    private readonly JsonStore<List<DownloadRecord>> _store =
        new("downloads.json", WinserJsonContext.Default.ListDownloadRecord, () => []);

    public DownloadService()
    {
        var records = _store.Load();

        // Anything still marked in-progress belongs to a process that is gone.
        foreach (var record in records)
        {
            if (record.State == DownloadState.InProgress)
            {
                record.State = DownloadState.Interrupted;
            }
        }

        Items = new ObservableCollection<DownloadItem>(
            records.Select(r => new DownloadItem(r, operation: null, Persist)));
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<DownloadItem> Items { get; }

    /// <summary>Raised so the toolbar can flash its downloads button.</summary>
    public event EventHandler<DownloadItem>? Started;

    public bool HasActiveDownloads => Items.Any(i => i.IsInProgress);

    public DownloadItem Track(CoreWebView2DownloadOperation operation)
    {
        var record = new DownloadRecord
        {
            SourceUri = operation.Uri,
            FilePath = operation.ResultFilePath,
            TotalBytes = operation.TotalBytesToReceive,
            ReceivedBytes = operation.BytesReceived,
            State = DownloadState.InProgress,
        };

        var item = new DownloadItem(record, operation, Persist);
        Items.Insert(0, item);
        Persist();
        Started?.Invoke(this, item);
        return item;
    }

    public void Remove(DownloadItem item)
    {
        Items.Remove(item);
        Persist();
    }

    /// <summary>Clears the list without touching the files on disk.</summary>
    public void ClearList()
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!Items[i].IsInProgress)
            {
                Items.RemoveAt(i);
            }
        }

        Persist();
    }

    public void Dispose() => _store.Dispose();

    private void Persist() => _store.Save([.. Items.Select(i => i.Record)]);
}
