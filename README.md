<p align="center">
  <img src="src/Winser/Assets/Winser.png" width="96" alt="Winser">
</p>

<h1 align="center">Winser</h1>

<p align="center">A native Windows web browser built with WinUI 3, .NET 10 and WebView2.</p>

---

Winser is a real browser shell — tabs in the title bar, an omnibox that searches or navigates,
bookmarks, history, downloads, find-on-page, InPrivate windows — written as an ordinary
WinUI 3 desktop app. Page rendering is handled by the Microsoft Edge WebView2 runtime
(Chromium); everything around it is XAML.

## Features

**Browsing**
- Tab strip integrated into the window title bar, with drag-to-reorder, duplicate, close-others
  and close-to-the-right
- Omnibox that decides between navigating and searching, with suggestions from history and
  bookmarks, and six switchable search engines
- Back / forward / reload / stop / home, and per-tab zoom (menu, keyboard or Ctrl+scroll)
  with a zoom chip in the toolbar
- Per-tab audio indicator and mute
- Link preview in the bottom-left corner, like every other browser
- Session restore, with window placement remembered

**Find on page** — implemented with the CSS Custom Highlight API, so matches are painted
without injecting wrapper elements into the page. Match count, next/previous, and case
sensitivity.

**Winser's own pages** — `winser://settings`, `winser://history`, `winser://downloads` and
`winser://bookmarks` are native XAML, rendered in place of the WebView2 inside a normal tab.
The new tab page is real HTML served from a virtual host mapped to the app's `Assets\Web`
folder, so it runs on a proper `https` origin.

**Downloads** — tracked in-app with progress, pause/resume, cancel, open, reveal in Explorer,
and an optional "ask where to save" picker.

**Privacy** — WebView2 tracking prevention (off / basic / balanced / strict), InPrivate windows
that get their own throwaway CoreWebView2 environment (deleted when the window closes), and an
explicit "clear cookies, cache and site data" action.

**Sleeping background tabs** — a tab that is not on screen has its browser process frozen, which
hands its memory back until you look at it again. Coming back is a resume, not a reload, so the
page is exactly as you left it. Tabs playing audio are never slept, and the whole thing is a
switch in settings. See [Memory](#memory).

**Fit and finish** — Mica backdrop, light/dark/system theme that also drives the page colour
scheme, high-contrast theme resources, and full keyboard shortcuts.

## Keyboard shortcuts

| | |
|---|---|
| `Ctrl+T` / `Ctrl+W` | New tab / close tab |
| `Ctrl+Shift+T` | Reopen the last closed tab |
| `Ctrl+N` / `Ctrl+Shift+N` | New window / new InPrivate window |
| `Ctrl+L` or `Alt+D` | Focus the address bar |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab |
| `Ctrl+1`…`Ctrl+8` / `Ctrl+9` | Jump to tab / last tab |
| `Alt+←` / `Alt+→` / `Alt+Home` | Back / forward / home |
| `Ctrl+R` or `F5` / `Ctrl+Shift+R` | Reload / reload ignoring cache |
| `Ctrl+F` | Find on page (`Enter` / `Shift+Enter` to step) |
| `Ctrl+D` | Bookmark this page |
| `Ctrl+H` / `Ctrl+J` / `Ctrl+Shift+O` | History / downloads / bookmarks |
| `Ctrl+Shift+B` | Toggle the bookmarks bar |
| `Ctrl+P` | Print |
| `Ctrl+ +` / `Ctrl+ -` / `Ctrl+0` | Zoom in / out / reset |
| `F11` / `F12` | Full screen / developer tools |

Shortcuts work while a page has focus: WinUI keyboard accelerators never fire once WebView2
owns the keyboard, so Winser injects a small script that hands the browser-level combinations
back to the shell and leaves editing shortcuts alone.

## Requirements

- Windows 10 version 1809 (17763) or later — Windows 11 recommended for Mica
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — pinned in `global.json`
  for consistent restore/tooling behavior across machines
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
  — preinstalled on Windows 11 and on up-to-date Windows 10

Visual Studio is *not* required. WinUI 3 compiles its XAML into a `resources.pri` via MRT/PRI
build tasks that normally ship only with Visual Studio's MSBuild, not the .NET SDK — a known,
unresolved gap for plain `dotnet build`
([microsoft/WindowsAppSDK#3939](https://github.com/microsoft/WindowsAppSDK/issues/3939),
[#4889](https://github.com/microsoft/WindowsAppSDK/issues/4889)). The project sets
`EnableMsixTooling` to route around it, confirmed working in CI (see `.github/workflows/build.yml`'s
`dotnet-build` job) — so `dotnet build`/`dotnet run` work directly with just the SDK above.
If you'd still rather use Visual Studio 2022 (17.8+, *.NET Desktop Development* workload) or the
standalone [Build Tools](https://visualstudio.microsoft.com/downloads/) with `msbuild`, both keep
working too; CI builds with `msbuild` as the primary, most-tested path.

## Build and run

```powershell
git clone https://github.com/Fankrits/Winser.git
cd Winser
dotnet build src/Winser/Winser.csproj -c Release -p:Platform=x64
dotnet run  --project src/Winser/Winser.csproj -c Release -p:Platform=x64
```

Or open `Winser.sln` in Visual Studio, pick the `x64` platform, and press F5. Or, from a
Developer PowerShell:

```powershell
msbuild Winser.sln -t:Restore -p:Configuration=Release -p:Platform=x64
msbuild Winser.sln -p:Configuration=Release -p:Platform=x64
.\src\Winser\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\Winser.exe
```

`winser.exe https://example.com` opens that URL on launch.

## Creating a release

Day-to-day, Winser stays unpackaged (`WindowsPackageType=None` in `Winser.csproj`) - that's what
every build above, and all four jobs in `.github/workflows/build.yml`, produce. For a release
someone can just download and install, `.github/workflows/release.yml` packages a self-contained,
signed `.msix` instead, overriding `WindowsPackageType` to `MSIX` for that one build via
[single-project MSIX packaging](https://learn.microsoft.com/windows/apps/windows-app-sdk/single-project-msix) -
nothing else changes, since this repo has no separate packaging project.

### One-time setup: a signing certificate

MSIX packages must be signed, and the signature's certificate `Subject` must exactly match
`Package.appxmanifest`'s `Publisher` (currently `CN=Fankrits`). In an elevated PowerShell
prompt, on any Windows machine:

```powershell
$cert = New-SelfSignedCertificate -Type Custom -KeyUsage DigitalSignature `
  -Subject "CN=Fankrits" -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
  -FriendlyName "Winser release signing"

$password = ConvertTo-SecureString -String "<choose a password>" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath WinserSigning.pfx -Password $password
[Convert]::ToBase64String([IO.File]::ReadAllBytes("WinserSigning.pfx")) | Set-Clipboard
```

If you use a different `-Subject`, change `Package.appxmanifest`'s `Publisher` to match -
identically, including spacing.

Add two repository secrets under **Settings → Secrets and variables → Actions**:

| Secret | Value |
|---|---|
| `WINSER_PFX_BASE64` | The clipboard contents from the last command above |
| `WINSER_PFX_PASSWORD` | The password you chose |

This certificate is self-signed, not issued by a public CA - the trade-off documented in
[Sign your MSIX package](https://learn.microsoft.com/windows/msix/package/sign-msix-package-guide):
free and immediate, but every machine that installs the result needs the one-time trust step
below. A certificate from a CA, or [Azure Artifact Signing](https://learn.microsoft.com/windows/msix/package/signing-package-overview),
removes that step; switching later only means replacing these two secrets and re-signing, not
any code change here.

### Cutting a release

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The `release` workflow builds and signs `Winser-v1.0.0-x64.msix` and attaches it to a GitHub
Release under that tag. To test the pipeline without publishing a real version, run it manually
from the Actions tab (**Run workflow**) - it still builds and uploads the `.msix` as a workflow
artifact, it just skips creating a Release.

## Installing a release

Because the certificate above is self-signed, Windows won't trust the package until that same
certificate is trusted on the installing machine - once per machine, not per update. Every
release publishes three files together for exactly this reason:

| File | What it is |
|---|---|
| `Winser-<tag>-x64.msix` | The app |
| `Winser-<tag>.cer` | The public half of the signing certificate (no private key - safe to hand out) |
| `install-winser.ps1` | Trusts the certificate, then installs the package |

Download all three into the same folder, then right-click `install-winser.ps1` → **Run with
PowerShell** (as Administrator) - it finds the `.cer` and `.msix` next to itself, imports the
certificate to `Cert:\LocalMachine\TrustedPeople`, and installs the package in one step. No
other setup needed on the machine installing it.

If you'd rather see (or run) each step by hand instead of trusting a script, this is exactly
what `install-winser.ps1` does:

```powershell
Import-Certificate -FilePath WinserSigning.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path Winser-<tag>-x64.msix
```

Got the "publisher certificate could not be verified" error (`0x800B010A`) from double-clicking
the `.msix` directly, without the certificate step first? That's this trust step, not run yet -
either run `install-winser.ps1`, or the two commands above. If you no longer have the `.cer`
file, pull the certificate straight back out of the `.msix` instead of hunting for it:

```powershell
$sig = Get-AuthenticodeSignature -FilePath Winser-<tag>-x64.msix
$sig.SignerCertificate | Export-Certificate -FilePath WinserSigning.cer
```

Once trusted, Winser installs and uninstalls like any other Windows app - Start menu entry,
normal removal from Settings, no SmartScreen "unrecognized publisher" prompt, since that check
is for standalone EXE/MSI downloads and MSIX installation verifies the package signature
instead.

## Size

Winser builds **unpackaged and self-contained**: the output is a plain `Winser.exe` carrying its
own copy of .NET *and* of the Windows App SDK, so it runs on a clean machine with nothing
installed first. Those two copies are essentially the whole download — Winser's own code is a
rounding error next to them, and the Chromium that actually renders pages is not in there at all
(it is the shared WebView2 runtime already on the machine).

CI measures this on every push rather than estimating it — the **Publish size** job in
`.github/workflows/build.yml` publishes both deployment shapes and prints their totals and the
25 largest files to the run summary. As of `x64` Release:

| Shape | Size | Files |
|---|---:|---:|
| Self-contained (what ships) | **215.0 MB** | 456 |
| Framework-dependent | **51.8 MB** | 38 |

Three quarters of the download is the two bundled runtimes.

Where it goes: `Microsoft.Windows.SDK.NET.dll` alone is **52.8 MB**, a quarter of the app — it
is the C# projection of the entire Windows API surface. WinUI itself is next
(`Microsoft.WinUI.dll` 15.7 MB, `Microsoft.ui.xaml.dll` 14.6 MB, plus the rest of the Windows App
SDK), then the .NET runtime (`System.Private.CoreLib.dll` 15.3 MB, `coreclr.dll` 4.4 MB).
Winser's own assembly does not make the top 25.

What can still be done, honestly:

- **Drop the self-contained runtimes** — set `WindowsAppSDKSelfContained` and `SelfContained` to
  `false` in `src/Winser/Winser.csproj`. By far the biggest lever: **215.0 MB down to 51.8 MB**,
  456 files down to 38. The cost is that the machine then needs the .NET Desktop Runtime and the
  Windows App SDK runtime installed, which is exactly the prerequisite the shipped shape exists
  to avoid. CI publishes both, so that trade stays a measured number rather than a guess.
- **.NET 9/10 do *not* split up `Microsoft.Windows.SDK.NET.dll`** the way earlier notes here
  assumed — that was an unverified claim, and measuring .NET 10 directly disproved it: the file
  drops a marginal 2.4% (54.1 → 52.8 MB). A framework upgrade bought nothing on its own here.
- **The Windows App SDK's sub-package split was worth doing, but almost went the other way.**
  The `Microsoft.WindowsAppSDK` 2.4.0 meta-package pulls in nine sub-packages; Winser needs five.
  Referencing the meta-package directly (briefly, on this branch) measured **268.8 MB** self-
  contained — `onnxruntime.dll` (20.7 MB) and `DirectML.dll` (17.8 MB) came along for on-device
  AI features Winser never calls, plus `Microsoft.Windows.Search.dll` and
  `Microsoft.Windows.Widgets.dll`. Referencing only `.Base`, `.Foundation`,
  `.InteractiveExperiences`, `.WinUI`, `.DWrite` and `.Runtime` directly — after checking that
  none of those five sub-packages reach back into AI/ML/Search/Widgets on their own — dropped
  all four unused packages and landed at the 215.0 MB above: a ~5% increase over this branch's
  original .NET 8 / Windows App SDK 1.6 baseline (203.8 MB) for being on current versions of
  both, not the ~30% increase blindly following the meta-package would have shipped.
- **Trimming is out.** WinUI 3 activates XAML types by name at run time, so `PublishTrimmed`
  breaks it — which is why obvious dead weight (`System.Private.Xml.dll` at 7.4 MB,
  `System.Data.Common.dll`) stays in the output despite Winser never touching any of it.
- **`InvariantGlobalization` buys nothing here.** It is the standard advice for shrinking a
  self-contained .NET app, and on Windows it is wrong: .NET uses the ICU that ships *with the
  OS*, so there is no bundled `icudt.dat` to delete. The measurement above is what caught that.

`<SatelliteResourceLanguages>en</SatelliteResourceLanguages>` is set, dropping the Windows App
SDK's translated resource assemblies for the forty-odd languages Winser's own UI does not speak.

## Memory

Winser renders with WebView2, so its floor is Chromium's floor: a browser process, a GPU
process, and a renderer per site. Nothing in a WinUI shell changes that, and this app will not
undercut Edge or Chrome on a single open tab. What it can control is what happens to the tabs
you are *not* looking at, and that is where nearly all of a browser's memory actually goes.

- **Background tabs are frozen immediately.** `TabView` shows one tab at a time, so switching
  away unloads the page that was showing; Winser takes that as the cue to call
  `CoreWebView2.TrySuspendAsync` and drop the tab to `CoreWebView2MemoryUsageTargetLevel.Low`.
  Chromium freezes the process rather than throwing it away, so switching back resumes it with
  scroll position, form contents and page state intact. The cost is that a frozen page stops
  running — a chat or a live dashboard goes quiet until it is on screen again — so it is a
  setting, and tabs that are audibly playing something are skipped.
- **Background tabs are discarded outright after sitting idle.** A freeze keeps the renderer
  process around, just quiesced; a discard closes it completely, which is the difference between
  handing memory *back* and handing *most* of it back. Set in minutes in `winser://settings`
  (default 30, 0 to never discard), it applies only to a tab that is not selected, is not playing
  audio, and whose currently focused field does not look like it holds something typed and not
  yet submitted — the one case a discard could plausibly cost you something you'd notice.
  Revisiting a discarded tab is a fresh navigation, not a resume: `BrowserTabViewModel.PendingUrl`
  carries the address across, so it lands back on the same page, just reloaded rather than woken
  up. The underlying WebView2 element itself is created in code (`WebContentView.CreateBrowserElement`),
  not declared in XAML, because `WebView2.Close()` leaves that specific instance permanently
  unusable — a discarded tab gets a brand new element the next time it is shown.
- **Every tab is asked to trim its footprint when the window itself is minimized or loses
  focus** — the one moment even the *selected* tab is not actually being looked at, which the
  freeze above can never reach on its own, since it only ever applies to a tab that is not
  selected. Lighter than a freeze: it only sets `CoreWebView2.MemoryUsageTargetLevel`, without
  touching visibility or calling `TrySuspendAsync`.
- **One CoreWebView2 per tab, sharing one environment**, so every tab in a window shares a
  single browser and GPU process rather than starting its own.
- **No renderer flags.** Most of Chromium's memory switches buy their savings out of security or
  correctness, so Winser sets none of them.

Winser's own managed footprint is small and stays bounded: history is capped at 10,000 entries,
and everything persisted is a plain JSON file written by a debounced atomic replace.

### Measured

Real numbers, not estimates: 10 tabs opened on a fresh profile, `Get-Process msedgewebview2 |
Measure-Object WorkingSet64 -Sum` sampled at each stage, discard threshold temporarily set to
1 minute so the run finishes in CI time rather than the real 30-minute default.

| Checkpoint | MB | msedgewebview2 processes |
|---|---:|---:|
| 10 tabs open | 484.7 | 15 |
| Background nine frozen | 453.3 | 15 |
| Background nine discarded | 193.9 | 6 |
| Returned to a discarded tab | 261.4 | 7 |

Discard closed exactly the nine processes it should have and cut memory by 57%. Freeze's ~6%
is a smaller number than the feature is capable of by construction: all ten tabs loaded the
same static, script-light page, which has little to suspend - a real page doing continuous
work (a timer, a video, a live feed) has more to give back. Returning to a discarded tab costs
one fresh renderer process, matching the documented "reload, not resume" behavior above.

Measured by driving a real, unattended Winser instance on a GitHub Actions `windows-latest`
runner via `.github/workflows/diagnose.yml`, which also screenshots Mica, the find bar, zoom,
and full screen, and prints `diagnostics.log` to the job summary - useful again the next time
a change in this area needs the same kind of answer.

## Security

The new tab page runs as web content, not as part of the shell, so everything it can ask
Winser to do goes through the same message boundary a hostile site would have to get through
too - there is no separate, more-trusted path for Winser's own pages beyond the origin check
below. In `Controls/WebContentView.xaml.cs`:

- **`navigate`/`newtab` messages** (`OnWebMessageReceived`) are only honored from
  `https://assets.winser` - `InternalPages.IsTrustedOrigin` checks scheme and host exactly.
  `newtab` is checked again against `UrlHelper.IsWebRequestable` (http/https only) even from
  that trusted origin, so it can't be used to open `file://` or `winser://` in a new tab.
- **`window.open()`** (`OnNewWindowRequested`) goes through `IsWebRequestable` regardless of
  origin, so no page - trusted or not - can pop `winser://settings` or a local file.
- **Camera, microphone, location, notifications and clipboard-read** are mediated
  (`OnPermissionRequested`): decisions are remembered per origin and listed/revocable in
  Settings, and an InPrivate window denies all of them outright rather than prompting.
  **Script dialogs** cap at 10 per navigation (`MaxScriptDialogsPerNavigation`); further
  `alert()`/`confirm()`/`prompt()` calls are dismissed without ever showing.
- **Accelerator keys** (`AreBrowserAcceleratorKeysEnabled = false`) are owned solely by
  Winser's own shortcut bridge, not also handled by WebView2 itself, and forwarded shortcuts
  are rate-limited (`ShortcutBurstLimit`) so a scripted key flood can't out-open a person.
- **Restored session URLs** go through `UrlHelper.IsWebRequestable` before reopening, so a
  tampered `session.json` can't hand a tab a `file://` or `winser://` address on launch.

`tools/security-check.html` exercises all of the above from an actual untrusted origin - open
its local path directly in Winser's address bar (not by copying it into `Assets/Web`, which
would make it trusted and defeat its own point) and it reports which of these still hold.

## Where Winser keeps things

Everything lives under `%LOCALAPPDATA%\Winser`:

| Path | Contents |
|---|---|
| `Data\settings.json` | Preferences |
| `Data\bookmarks.json` | Bookmarks |
| `Data\history.json` | Browsing history |
| `Data\downloads.json` | Download list (not the files) |
| `Data\permissions.json` | Remembered camera/microphone/location/notification/clipboard decisions |
| `Data\session.json` | Open tabs and window placement |
| `Profile\` | The WebView2 user data folder: cookies, cache, storage |
| `Private\` | Throwaway InPrivate profiles, deleted when their window closes |

There is no `ApplicationData.Current` because the app is unpackaged; every write is a debounced,
atomic replace of a JSON file.

## How it is put together

```
src/Winser/
├── App.xaml(.cs)              Application entry point, theme resources, converters
├── MainWindow.xaml(.cs)       One browser window: title-bar tab strip, placement, shortcuts
├── Controls/
│   └── WebContentView         The only class that talks to WebView2
├── Views/
│   ├── BrowserTabPage         Toolbar, bookmarks bar, find bar, content switching
│   ├── SettingsView           winser://settings
│   ├── HistoryView            winser://history
│   ├── DownloadsView          winser://downloads
│   └── BookmarksView          winser://bookmarks
├── ViewModels/
│   ├── BrowserViewModel       A window: tabs, bookmarks bar, window-level commands
│   ├── BrowserTabViewModel    A tab: address, title, favicon, zoom, find, security
│   ├── IWebViewHost           The slice of WebView2 a tab is allowed to touch
│   └── …
├── Services/                  Settings, history, bookmarks, downloads, session, WebView2 envs
├── Models/                    Plain persisted records
├── Helpers/                   URL parsing, injected scripts, formatting, theming
└── Assets/Web/newtab.html     The new tab page
```

Two boundaries are worth calling out:

**`IWebViewHost`** keeps navigation *policy* in the view model and the CoreWebView2 *lifetime*
in the control. The tab view model never sees a `CoreWebView2`; `WebContentView` translates
browser events into `Report…` calls on the tab.

**Internal pages are not web pages.** A tab has a `Kind`; anything other than `Web` renders a
native XAML view in place of the WebView2. A tab that has ever shown web content keeps its
WebView2 alive, so a detour through `winser://settings` does not throw away back/forward history.

**Pages talk back, and none of it is trusted.** WinUI's WebView2 element exposes no
`CoreWebView2Controller`, so there is no `AcceleratorKeyPressed` and a focused page swallows XAML
keyboard accelerators outright. The only way to get Ctrl+T back is to inject a script that posts
it out — which means `window.chrome.webview.postMessage` is reachable from every site's own
JavaScript too, and every message arriving on that channel has to be read as attacker-controlled.
So `WebContentView` splits them by what they can do:

| Message | Who may send it | Why |
|---|---|---|
| `navigate`, `newtab` | `https://assets.winser/` only | These steer the browser. Gated on the sender's origin *and* on the target scheme being one web content may ask for, so no site can walk a tab into `winser://settings` or `file:///`. |
| `key`, `zoom` | any page | Their whole purpose is to work from any page, and a forged one is indistinguishable from a real keypress. Instead they are rate-limited to a burst no hand can exceed, which is what stops a script from spinning up tabs until the machine gives out. |

`window.open` gets the same scheme check before it becomes a tab, and the `assets.winser`
virtual host is mapped `DenyCors` so no site can read the app's own files out of it.

**Permissions are mediated, not left to WebView2's own prompt.** Camera, microphone,
geolocation, notifications and clipboard-read go through Winser's own inline prompt (the same
overlay pattern as the find bar), and the decision is remembered per origin in
`Data\permissions.json` — listed and revocable from `winser://settings`. InPrivate windows deny
all of these outright rather than prompting, since a profile that could still hand out the
camera would not really be private. Everything else WebView2 can ask for (autoplay, local
fonts, window management, ...) is left to its own default, since Winser has no UI yet to explain
or revoke it.

**A page cannot wedge the tab open with dialogs.** `alert`/`confirm`/`prompt` show normally, but
past ten on the same navigation `WebContentView` starts dismissing them unseen — taking the
`ScriptDialogOpening` deferral and completing it without calling `Accept()`, which resolves the
dialog exactly as if it were closed unanswered. A script cannot use a modal loop to freeze the
tab.

**Restored tabs go through the same scheme check as a typed address.** `session.json` is
Winser's own file, but it is still a file on disk; `UrlHelper.IsRestorable` keeps a corrupted or
tampered copy from handing a tab a scheme nobody chose, the same way `IsWebRequestable` keeps a
page from doing it over `postMessage`.

`AreBrowserAcceleratorKeysEnabled` is off, so the injected bridge is the *only* path a shortcut
takes rather than a second, invisible one inside WebView2 itself; `IsReputationCheckingRequired`
(SmartScreen) is set explicitly rather than left to whatever WebView2 defaults to; and autofill
has its own settings toggle instead of being silently on with nothing to see or turn it off.

## Known limitations

- Popups opened with `window.open` become tabs but lose their `window.opener` back-reference,
  because Winser handles `NewWindowRequested` itself rather than parenting a second WebView2.
- Find on page needs the CSS Custom Highlight API. Every current Chromium build has it; the find
  bar says so when a page does not.
- The WinUI 3 WebView2 element exposes no `CoreWebView2Controller`, so there is no real
  `ZoomFactor` to set. Zoom is emulated with the CSS `zoom` property on the document element,
  reapplied on every `DOMContentLoaded`. It reflows pages the way browser zoom does, but it does
  not reach into cross-origin iframes, and a page that sets `zoom` on `<html>` itself wins.
- Tab drag-and-drop between windows is not implemented — reordering within a window is.
- Only the empty strip to the right of the tabs is a window drag region, which is the limit of
  what `Window.SetTitleBar` accepts.
- `DownloadService` is one list shared by every window. An InPrivate download never reaches
  `downloads.json` and disappears once removed from the list, but while its window is still
  open it is live in every other window's downloads flyout too, not just its own — scoping
  downloads per window the way `WebViewService` already scopes profiles per window would fix
  this, but is a larger change than the audit that found the gap.

## License

MIT — see [LICENSE](LICENSE).
