using Winser.Models;

namespace Winser.Services;

/// <summary>Remembers open tabs and window placement across restarts.</summary>
public sealed class SessionService : IDisposable
{
    private readonly JsonStore<SessionState> _store =
        new("session.json", WinserJsonContext.Default.SessionState, () => new SessionState());

    public SessionService() => State = _store.Load();

    public SessionState State { get; }

    public void Save() => _store.Save(State);

    public void Dispose() => _store.Dispose();
}
