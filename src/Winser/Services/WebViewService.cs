using System.Diagnostics;
using System.Globalization;
using Microsoft.Web.WebView2.Core;

namespace Winser.Services;

/// <summary>
/// A CoreWebView2 environment plus, for InPrivate windows, the throwaway user data folder
/// it was created against.
/// </summary>
public sealed record WebViewProfile(CoreWebView2Environment Environment, string? EphemeralFolder)
{
    public bool IsPrivate => EphemeralFolder is not null;
}

/// <summary>
/// Owns the CoreWebView2 environments. Every normal tab shares one environment (and therefore
/// one cookie jar, cache and browser process group); each InPrivate window gets its own
/// environment over a temporary user data folder that is deleted when the window closes.
/// </summary>
public sealed class WebViewService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _ephemeralFolders = [];

    private WebViewProfile? _shared;

    /// <summary>
    /// The Evergreen runtime version, known once an environment has been created successfully;
    /// null before that, and null for good if creating one failed because it is not installed.
    /// </summary>
    public string? RuntimeVersion { get; private set; }

    public async Task<WebViewProfile> GetSharedAsync()
    {
        if (_shared is not null)
        {
            return _shared;
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            _shared ??= new WebViewProfile(await CreateEnvironmentAsync(AppPaths.Profile).ConfigureAwait(true), null);
        }
        finally
        {
            _gate.Release();
        }

        return _shared;
    }

    /// <summary>
    /// Builds an isolated environment for an InPrivate window. Because the data folder is
    /// unique and deleted on close, nothing the window does survives it — no controller-level
    /// InPrivate flag required.
    /// </summary>
    public async Task<WebViewProfile> CreatePrivateAsync()
    {
        var folder = Path.Combine(AppPaths.PrivateProfiles, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);
        lock (_ephemeralFolders)
        {
            _ephemeralFolders.Add(folder);
        }

        return new WebViewProfile(await CreateEnvironmentAsync(folder).ConfigureAwait(true), folder);
    }

    /// <summary>
    /// Deletes an InPrivate folder. The browser process may still hold it for a moment after
    /// the window closes, so failures are swallowed and retried at next launch.
    /// </summary>
    public void ReleasePrivate(WebViewProfile profile)
    {
        if (profile.EphemeralFolder is not { } folder)
        {
            return;
        }

        lock (_ephemeralFolders)
        {
            _ephemeralFolders.Remove(folder);
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Winser] InPrivate profile still locked, will clean up next launch: {folder}");
        }
    }

    /// <summary>
    /// The only Chromium switches Winser starts its browser process with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This reverses a position Winser used to hold outright - "no renderer flags" - and the
    /// reasoning behind that position still stands for almost everything on the list. Most of
    /// Chromium's memory and CPU switches buy their savings out of security or correctness, and
    /// none of those are here: not <c>--disable-gpu</c> (Microsoft is explicit that GPU use is
    /// what makes rendering fast in the first place), not SmartScreen, whose reputation check
    /// is deliberately switched on in WebContentView.ConfigureAsync, and not
    /// <c>--disable-background-timer-throttling</c> or <c>--disable-renderer-backgrounding</c>,
    /// which are popular in "make WebView2 faster" advice and are the exact opposite of what a
    /// browser trying to save power wants.
    /// </para>
    /// <para>
    /// What is left is the narrow set that costs neither. It is worth having because it reaches
    /// somewhere Winser's own freeze cannot: freezing applies to a whole tab that is not
    /// selected, so it never touches the timers running in cross-origin iframes and workers
    /// inside the tab that <em>is</em> selected, which is where a background chat widget or ad
    /// frame quietly spends a laptop's battery.
    /// </para>
    /// <para>
    /// The standing risk, and the reason this is one constant rather than scattered: Microsoft
    /// states plainly that production apps should not ship browser flags, because they may be
    /// altered or removed at any time and are not supported long-term. A flag that disappears
    /// here degrades to nothing rather than breaking anything, but the set wants re-checking
    /// whenever the Evergreen runtime moves. Written against runtime 140.
    /// </para>
    /// </remarks>
    private const string PowerBrowserArguments =
        // Chromium's own battery feature. Background pages normally get their timers coalesced
        // to once a minute only after five minutes out of sight; this starts that after ten
        // seconds instead. Google measured the intervention itself at a few percent of total
        // desktop CPU. Nothing is disabled - throttled timers still run, just less often - so a
        // page that misbehaves under it misbehaves under Chrome and Edge too.
        "--enable-features=IntensiveWakeUpThrottling:grace_period_seconds/10 " +

        // Cast/DIAL device discovery, which periodically puts mDNS traffic on the local network
        // and wakes the radio to do it. Winser has no casting UI, so nothing here can use it.
        // Whether WebView2 enables MediaRouter in the first place is unverified - if it does
        // not, this switch is simply a no-op rather than a mistake.
        "--disable-features=MediaRouter " +

        // Suppresses <a ping> beacons: a background network request per outbound link click,
        // for the sole benefit of whoever is counting the click.
        "--no-pings";

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync(string userDataFolder)
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            Language = CultureInfo.CurrentUICulture.Name,
            AllowSingleSignOnUsingOSPrimaryAccount = false,
            EnableTrackingPrevention = true,
            AdditionalBrowserArguments = PowerBrowserArguments,
        };

        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            browserExecutableFolder: string.Empty,
            userDataFolder: userDataFolder,
            options: options).AsTask().ConfigureAwait(true);

        RuntimeVersion = environment.BrowserVersionString;
        return environment;
    }
}
