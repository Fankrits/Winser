namespace Winser.Models;

public sealed class Bookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>Optional single-level folder. Bookmarks with no folder sit directly on the bar.</summary>
    public string? Folder { get; set; }

    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
}
