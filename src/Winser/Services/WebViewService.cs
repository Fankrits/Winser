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

    /// <summary>The Evergreen runtime version, or null when WebView2 is not installed.</summary>
    public string? RuntimeVersion
    {
        get
        {
            try
            {
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return string.IsNullOrEmpty(version) ? null : version;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }
    }

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

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync(string userDataFolder)
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            Language = CultureInfo.CurrentUICulture.Name,
            AllowSingleSignOnUsingOSPrimaryAccount = false,
            EnableTrackingPrevention = true,
        };

        return CoreWebView2Environment.CreateWithOptionsAsync(
            browserExecutableFolder: string.Empty,
            userDataFolder: userDataFolder,
            options: options).AsTask();
    }
}
