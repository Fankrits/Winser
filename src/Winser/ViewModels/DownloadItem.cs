using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Core;
using Winser.Helpers;
using Winser.Models;

namespace Winser.ViewModels;

/// <summary>
/// One row in the downloads list. Wraps the persisted <see cref="DownloadRecord"/> and, while
/// the transfer is live, the CoreWebView2 operation driving it.
/// </summary>
public sealed partial class DownloadItem : ObservableObject
{
    private readonly Action _onChanged;

    private CoreWebView2DownloadOperation? _operation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private long _receivedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInProgress))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DownloadState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isPaused;

    public DownloadItem(DownloadRecord record, CoreWebView2DownloadOperation? operation, Action onChanged)
    {
        Record = record;
        _onChanged = onChanged;
        _receivedBytes = record.ReceivedBytes;
        _totalBytes = record.TotalBytes;
        _state = record.State;

        if (operation is not null)
        {
            Attach(operation);
        }
    }

    public DownloadRecord Record { get; }

    public string FileName => Record.FileName;

    public string FilePath => Record.FilePath;

    public string SourceHost => UrlHelper.HostLabel(Record.SourceUri);

    public bool IsInProgress => State == DownloadState.InProgress;

    public bool IsCompleted => State == DownloadState.Completed;

    public bool IsIndeterminate => State == DownloadState.InProgress && TotalBytes <= 0;

    public double ProgressPercent => TotalBytes > 0
        ? Math.Clamp(ReceivedBytes * 100.0 / TotalBytes, 0, 100)
        : 0;

    public string StatusText => State switch
    {
        DownloadState.Completed => Format.Bytes(TotalBytes > 0 ? TotalBytes : ReceivedBytes),
        DownloadState.Canceled => "Canceled",
        DownloadState.Interrupted => "Failed — the transfer was interrupted",
        _ when IsPaused => $"Paused — {Format.Bytes(ReceivedBytes)} of {Format.Bytes(TotalBytes)}",
        _ when TotalBytes > 0 => $"{Format.Bytes(ReceivedBytes)} of {Format.Bytes(TotalBytes)}",
        _ => Format.Bytes(ReceivedBytes),
    };

    public bool FileExists => File.Exists(Record.FilePath);

    private void Attach(CoreWebView2DownloadOperation operation)
    {
        _operation = operation;
        TotalBytes = operation.TotalBytesToReceive;
        ReceivedBytes = operation.BytesReceived;

        operation.BytesReceivedChanged += OnBytesReceivedChanged;
        operation.StateChanged += OnStateChanged;
    }

    private void Detach()
    {
        if (_operation is null)
        {
            return;
        }

        _operation.BytesReceivedChanged -= OnBytesReceivedChanged;
        _operation.StateChanged -= OnStateChanged;
        _operation = null;
    }

    private void OnBytesReceivedChanged(CoreWebView2DownloadOperation sender, object args)
    {
        ReceivedBytes = sender.BytesReceived;
        TotalBytes = sender.TotalBytesToReceive;
        Record.ReceivedBytes = ReceivedBytes;
        Record.TotalBytes = TotalBytes;
    }

    private void OnStateChanged(CoreWebView2DownloadOperation sender, object args)
    {
        State = sender.State switch
        {
            CoreWebView2DownloadState.Completed => DownloadState.Completed,
            CoreWebView2DownloadState.Interrupted =>
                sender.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled
                    ? DownloadState.Canceled
                    : DownloadState.Interrupted,
            _ => DownloadState.InProgress,
        };

        Record.State = State;
        Record.FilePath = sender.ResultFilePath;
        Record.ReceivedBytes = sender.BytesReceived;
        Record.TotalBytes = sender.TotalBytesToReceive;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FilePath));

        if (State != DownloadState.InProgress)
        {
            Detach();
        }

        CancelCommand.NotifyCanExecuteChanged();
        TogglePauseCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        _onChanged();
    }

    [RelayCommand(CanExecute = nameof(IsInProgress))]
    private void Cancel()
    {
        try
        {
            _operation?.Cancel();
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Winser] Could not cancel download: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(IsInProgress))]
    private void TogglePause()
    {
        if (_operation is null)
        {
            return;
        }

        try
        {
            if (IsPaused)
            {
                _operation.Resume();
                IsPaused = false;
            }
            else
            {
                _operation.Pause();
                IsPaused = true;
            }
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Winser] Could not pause/resume download: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(IsCompleted))]
    private void Open()
    {
        if (!FileExists)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(Record.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"[Winser] Could not open {Record.FilePath}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        try
        {
            var target = FileExists
                ? $"/select,\"{Record.FilePath}\""
                : $"\"{Path.GetDirectoryName(Record.FilePath)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"[Winser] Could not reveal {Record.FilePath}: {ex.Message}");
        }
    }
}
