namespace Winser.Models;

public enum DownloadState
{
    InProgress = 0,
    Completed = 1,
    Canceled = 2,
    Interrupted = 3,
}

/// <summary>The persisted half of a download. Live progress lives on the view model.</summary>
public sealed class DownloadRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string SourceUri { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public long TotalBytes { get; set; }

    public long ReceivedBytes { get; set; }

    public DownloadState State { get; set; } = DownloadState.InProgress;

    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string FileName => Path.GetFileName(FilePath);
}
