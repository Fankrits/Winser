using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Winser.Helpers;
using Winser.Models;

namespace Winser.Services;

/// <summary>
/// A flat, ordered bookmark list with an optional single folder level. The collection is
/// observable so the bookmarks bar and the manager page stay in sync for free.
/// </summary>
public sealed class BookmarkService : IDisposable
{
    private readonly JsonStore<List<Bookmark>> _store =
        new("bookmarks.json", WinserJsonContext.Default.ListBookmark, () => []);

    private readonly Dictionary<string, Bookmark> _byUrl = new(StringComparer.OrdinalIgnoreCase);

    public BookmarkService()
    {
        Items = new ObservableCollection<Bookmark>(_store.Load());
        foreach (var item in Items)
        {
            _byUrl[item.Url] = item;
        }

        Items.CollectionChanged += OnCollectionChanged;
    }

    public ObservableCollection<Bookmark> Items { get; }

    /// <summary>Bookmarks pinned directly to the bar (no folder).</summary>
    public IEnumerable<Bookmark> BarItems => Items.Where(b => string.IsNullOrEmpty(b.Folder));


    public bool Contains(string? url) => !string.IsNullOrEmpty(url) && _byUrl.ContainsKey(url);

    public Bookmark? Find(string? url) =>
        url is not null && _byUrl.TryGetValue(url, out var found) ? found : null;

    public Bookmark Add(string url, string? title, string? folder = null)
    {
        if (_byUrl.TryGetValue(url, out var existing))
        {
            return existing;
        }

        var bookmark = new Bookmark
        {
            Url = url,
            Title = string.IsNullOrWhiteSpace(title) ? UrlHelper.HostLabel(url) : title,
            Folder = string.IsNullOrWhiteSpace(folder) ? null : folder,
        };

        Items.Add(bookmark);
        return bookmark;
    }

    public void Remove(string url)
    {
        if (_byUrl.TryGetValue(url, out var bookmark))
        {
            Items.Remove(bookmark);
        }
    }

    /// <summary>Adds or removes the URL. Returns true when the page ends up bookmarked.</summary>
    public bool Toggle(string url, string? title)
    {
        if (Contains(url))
        {
            Remove(url);
            return false;
        }

        Add(url, title);
        return true;
    }

    /// <summary>
    /// Applies an edit. Bookmark is a plain POCO so lists would not see in-place mutations;
    /// taking it out and putting it back at the same index refreshes every binding and keeps
    /// the URL index correct in one move.
    /// </summary>
    public void Update(Bookmark bookmark, string title, string url, string? folder)
    {
        var index = Items.IndexOf(bookmark);
        if (index >= 0)
        {
            Items.RemoveAt(index);
        }

        bookmark.Title = string.IsNullOrWhiteSpace(title) ? UrlHelper.HostLabel(url) : title;
        bookmark.Url = url;
        bookmark.Folder = string.IsNullOrWhiteSpace(folder) ? null : folder;

        if (index >= 0)
        {
            Items.Insert(index, bookmark);
        }
        else
        {
            Items.Add(bookmark);
        }
    }

    public void Dispose() => _store.Dispose();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (Bookmark item in e.OldItems)
            {
                _byUrl.Remove(item.Url);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (Bookmark item in e.NewItems)
            {
                _byUrl[item.Url] = item;
            }
        }

        Persist();
    }

    private void Persist() => _store.Save([.. Items]);
}
