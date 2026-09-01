using System.Text.Json.Serialization;
using Winser.Helpers;

namespace Winser.Models;

public sealed class HistoryEntry
{
    private string? _hostLabel;

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset LastVisitedUtc { get; set; } = DateTimeOffset.UtcNow;

    public int VisitCount { get; set; } = 1;

    /// <summary>
    /// <see cref="UrlHelper.HostLabel"/> for <see cref="Url"/>, parsed once and kept.
    /// </summary>
    /// <remarks>
    /// Address-bar ranking asks every candidate for its host on every keystroke, and answering
    /// means constructing a <see cref="Uri"/> - up to a couple of hundred of them per character
    /// typed, all re-parsing strings that never change. <see cref="Url"/> is written once when
    /// the entry is created and never reassigned afterwards (visits only touch the title, the
    /// count and the timestamp), so caching it is safe as well as worthwhile.
    /// </remarks>
    [JsonIgnore]
    public string HostLabel => _hostLabel ??= UrlHelper.HostLabel(Url);
}
