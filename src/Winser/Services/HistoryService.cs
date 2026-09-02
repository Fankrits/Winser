using Winser.Helpers;
using Winser.Models;

namespace Winser.Services;

/// <summary>
/// Browsing history, newest first, de-duplicated by URL. Small enough to keep in memory and
/// flush to a single JSON file; the cap keeps both bounded.
/// </summary>
public sealed class HistoryService : IDisposable
{
    private const int MaxEntries = 10_000;

    /// <summary>
    /// History is written far more often than anything else Winser persists, and it is by some
    /// distance the largest of those documents: every completed navigation, and every late title
    /// change after one, re-serializes the whole capped 10,000-entry list and replaces the file.
    /// At the shared 750ms debounce, ordinary browsing turns that into a full rewrite every
    /// couple of seconds - all of it disk traffic and CPU spent recording something no one is
    /// waiting to read. Twenty seconds collapses a browsing session into far fewer of them.
    /// </summary>
    /// <remarks>
    /// Nothing is risked on a clean exit: <see cref="Dispose"/> flushes whatever is queued, and
    /// AppServices.Shutdown disposes every store - including from the unhandled-exception
    /// handler. A hard kill loses at most the last twenty seconds of history, which is the right
    /// thing to trade for not rewriting a megabyte of JSON every few seconds.
    /// </remarks>
    private const int HistoryDebounceMilliseconds = 20_000;

    private readonly JsonStore<List<HistoryEntry>> _store =
        new("history.json", WinserJsonContext.Default.ListHistoryEntry, () => [],
            HistoryDebounceMilliseconds);

    private readonly List<HistoryEntry> _entries;
    private readonly Dictionary<string, HistoryEntry> _byUrl;
    private readonly SettingsService _settings;

    public HistoryService(SettingsService settings)
    {
        _settings = settings;
        _entries = _store.Load();
        _entries.Sort(static (a, b) => b.LastVisitedUtc.CompareTo(a.LastVisitedUtc));
        Prune();
        _byUrl = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            _byUrl[entry.Url] = entry;
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    /// <summary>Records a visit. Internal pages and blank navigations are ignored.</summary>
    public void Record(string url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            InternalPages.IsInternal(url) ||
            InternalPages.IsNewTab(url) ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_byUrl.TryGetValue(url, out var existing))
        {
            existing.VisitCount++;
            existing.LastVisitedUtc = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(title))
            {
                existing.Title = title;
            }

            _entries.Remove(existing);
            _entries.Insert(0, existing);
        }
        else
        {
            var entry = new HistoryEntry
            {
                Url = url,
                Title = string.IsNullOrWhiteSpace(title) ? UrlHelper.HostLabel(url) : title,
                LastVisitedUtc = DateTimeOffset.UtcNow,
            };
            _entries.Insert(0, entry);
            _byUrl[url] = entry;

            if (_entries.Count > MaxEntries)
            {
                foreach (var dropped in _entries.GetRange(MaxEntries, _entries.Count - MaxEntries))
                {
                    _byUrl.Remove(dropped.Url);
                }

                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            }
        }

        Persist();
    }

    /// <summary>Updates the stored title for an already recorded URL (titles arrive late).</summary>
    public void UpdateTitle(string url, string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || !_byUrl.TryGetValue(url, out var entry))
        {
            return;
        }

        if (entry.Title == title)
        {
            return;
        }

        entry.Title = title;
        Persist();
    }

    public IEnumerable<HistoryEntry> Search(string? query, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _entries.Take(max);
        }

        return _entries
            .Where(e => e.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(max);
    }

    /// <summary>
    /// How many matches <see cref="Suggest"/> scores and ranks before taking its top few.
    /// Scoring parses each candidate's host out of its URL, which is not free to do for every
    /// match in a 10,000-entry history on every keystroke - and unnecessary, since the entry
    /// list is already newest-first and recency already counts toward the score, so the
    /// entries a wide query would drop past this cap are the ones least likely to have won
    /// regardless.
    /// </summary>
    private const int SuggestionCandidateLimit = 200;

    /// <summary>Address-bar suggestions: prefix/substring matches ranked by visits then recency.</summary>
    public IEnumerable<HistoryEntry> Suggest(string query, int max = 6)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        // Read once and passed down rather than sampled inside Score: it is the same instant for
        // every candidate by definition, and asking the clock two hundred times per keystroke to
        // be told so is pure overhead.
        var now = DateTimeOffset.UtcNow;

        return _entries
            .Where(e => e.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(SuggestionCandidateLimit)
            .OrderByDescending(e => Score(e, query, now))
            .Take(max);
    }

    /// <summary>
    /// How many ranked origins are kept in the cache below - comfortably more than any caller
    /// asks for, so one cached ranking serves every <paramref name="count"/> without a rebuild.
    /// </summary>
    private const int MaxTopSites = 32;

    private List<HistoryEntry>? _topSites;

    /// <summary>Most-used sites, one per origin, for the new tab page.</summary>
    /// <remarks>
    /// Cached because the work is out of all proportion to how often the answer changes:
    /// grouping the entire history by origin means parsing a <see cref="Uri"/> per entry, up to
    /// 10,000 of them, and it ran on every single new tab page that finished loading. The cache
    /// is dropped whenever history changes, so opening ten new tabs in a row now costs one
    /// ranking rather than ten.
    /// </remarks>
    public IEnumerable<HistoryEntry> TopSites(int count = 8)
    {
        _topSites ??=
        [
            .. _entries
                .GroupBy(e => UrlHelper.OriginKey(e.Url), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(e => e.VisitCount).First())
                .OrderByDescending(e => e.VisitCount)
                .ThenByDescending(e => e.LastVisitedUtc)
                .Take(MaxTopSites),
        ];

        return _topSites.Take(count);
    }

    public void Remove(HistoryEntry entry)
    {
        if (_entries.Remove(entry))
        {
            _byUrl.Remove(entry.Url);
            Persist();
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _byUrl.Clear();
        Persist();
    }

    public void Dispose() => _store.Dispose();

    private static double Score(HistoryEntry entry, string query, DateTimeOffset now)
    {
        var score = Math.Log(entry.VisitCount + 1) * 2;

        // A match at the start of the host is far more likely to be what was meant.
        // HistoryEntry.HostLabel is parsed once per entry and kept, rather than rebuilt from the
        // URL for every candidate on every keystroke.
        if (entry.HostLabel.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 6;
        }
        else if (entry.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        var ageDays = (now - entry.LastVisitedUtc).TotalDays;
        score += Math.Max(0, 4 - (ageDays / 7));
        return score;
    }

    private void Prune()
    {
        var days = _settings.Current.HistoryRetentionDays;
        if (days <= 0)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var removed = _entries.RemoveAll(e => e.LastVisitedUtc < cutoff);
        if (removed > 0)
        {
            _topSites = null;
            _store.Save(_entries);
        }
    }

    private void Persist()
    {
        _topSites = null;
        _store.Save(_entries);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
