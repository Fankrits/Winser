using Winser.Models;

namespace Winser.Services;

public sealed class SettingsService : IDisposable
{
    private readonly JsonStore<AppSettings> _store =
        new("settings.json", WinserJsonContext.Default.AppSettings, () => new AppSettings());

    public SettingsService()
    {
        Current = _store.Load();
        Current.DownloadFolder ??= AppPaths.DefaultDownloadFolder();
    }

    /// <summary>Raised after any call to <see cref="Commit"/> so open windows can react.</summary>
    public event EventHandler? Changed;

    public AppSettings Current { get; }

    public SearchEngine SearchEngine => SearchEngine.Resolve(Current.SearchEngineId);

    public string EffectiveDownloadFolder =>
        string.IsNullOrWhiteSpace(Current.DownloadFolder)
            ? AppPaths.DefaultDownloadFolder()
            : Current.DownloadFolder;

    /// <summary>Persists the current values and notifies listeners.</summary>
    public void Commit()
    {
        _store.Save(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _store.Dispose();
}
