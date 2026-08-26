namespace Winser.ViewModels;

/// <summary>
/// The slice of WebView2 a tab view model is allowed to touch. Keeping it behind an interface
/// means the view model owns navigation policy while the control owns the CoreWebView2 lifetime.
/// </summary>
public interface IWebViewHost
{
    bool IsReady { get; }

    double ZoomFactor { get; set; }

    void Navigate(string url);

    void GoBack();

    void GoForward();

    void Reload(bool bypassCache);

    void Stop();

    void FocusContent();

    void SetMuted(bool muted);

    void OpenDevTools();

    Task PrintAsync();

    Task<string> ExecuteScriptAsync(string script);

    /// <summary>Re-reads app settings that map onto CoreWebView2 options.</summary>
    void ApplyPreferences();

    /// <summary>
    /// Tells the page's injected script whether Winser itself is in full screen, so Escape
    /// can exit it even while the page has keyboard focus. See Scripts.ShortcutBridge.
    /// </summary>
    void SyncFullScreenFlag(bool isFullScreen);

    /// <summary>Clears cookies, cache and site data for the whole profile this tab uses.</summary>
    Task ClearBrowsingDataAsync();

    /// <summary>
    /// Tears down the underlying browser - called both when the tab goes away for good and
    /// when an idle tab is discarded to free its memory (see
    /// <see cref="BrowserTabViewModel.TryDiscardAsync"/>). Safe to call again later: a
    /// subsequent navigation on the same tab creates a fresh browser rather than reusing this
    /// one, which <see cref="IsReady"/> turning false after this call is what signals.
    /// </summary>
    void Release();
}
