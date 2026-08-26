using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Winser.Models;

namespace Winser.Services;

/// <summary>
/// Remembers per-origin permission decisions (camera, microphone, geolocation, notifications,
/// clipboard read) so a site is asked once rather than on every visit, and so a grant can be
/// listed and revoked later from <c>winser://settings</c>.
/// </summary>
public sealed class SitePermissionService : IDisposable
{
    private readonly JsonStore<List<SitePermission>> _store =
        new("permissions.json", WinserJsonContext.Default.ListSitePermission, () => []);

    private readonly Dictionary<(string Origin, SitePermissionKind Kind), SitePermission> _byKey =
        new();

    public SitePermissionService()
    {
        Items = new ObservableCollection<SitePermission>(_store.Load());
        foreach (var item in Items)
        {
            _byKey[(item.Origin, item.Kind)] = item;
        }

        Items.CollectionChanged += OnCollectionChanged;
    }

    public ObservableCollection<SitePermission> Items { get; }

    public SitePermissionState? TryGet(string origin, SitePermissionKind kind) =>
        _byKey.TryGetValue((origin, kind), out var found) ? found.State : null;

    /// <summary>Records a decision, replacing any earlier one for the same origin and kind.</summary>
    public void Set(string origin, SitePermissionKind kind, SitePermissionState state)
    {
        if (_byKey.TryGetValue((origin, kind), out var existing))
        {
            Items.Remove(existing);
        }

        Items.Add(new SitePermission { Origin = origin, Kind = kind, State = state });
    }

    public void Revoke(SitePermission permission) => Items.Remove(permission);

    public void Dispose() => _store.Dispose();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SitePermission item in e.OldItems)
            {
                _byKey.Remove((item.Origin, item.Kind));
            }
        }

        if (e.NewItems is not null)
        {
            foreach (SitePermission item in e.NewItems)
            {
                _byKey[(item.Origin, item.Kind)] = item;
            }
        }

        Persist();
    }

    private void Persist() => _store.Save([.. Items]);
}
