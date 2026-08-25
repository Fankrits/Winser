namespace Winser.Models;

public sealed class HistoryEntry
{
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset LastVisitedUtc { get; set; } = DateTimeOffset.UtcNow;

    public int VisitCount { get; set; } = 1;
}
