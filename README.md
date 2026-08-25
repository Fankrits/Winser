<p align="center">
  <img src="src/Winser/Assets/Winser.png" width="96" alt="Winser">
</p>

<h1 align="center">Winser</h1>

<p align="center">A native Windows web browser built with WinUI 3, .NET 8 and WebView2.</p>

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
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — pinned in `global.json` for
  consistent restore/tooling behavior across machines
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
.\src\Winser\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Winser.exe
```

`winser.exe https://example.com` opens that URL on launch.

## Size

Winser builds **unpackaged and self-contained**: the output is a plain `Winser.exe` carrying its
own copy of .NET *and* of the Windows App SDK, so it runs on a clean machine with nothing
installed first. Those two copies are essentially the whole download — Winser's own code is a
rounding error next to them, and the Chromium that actually renders pages is not in there at all
(it is the shared WebView2 runtime already on the machine).

CI measures the real number on every push rather than estimating it: see the **Publish size**
job in `.github/workflows/build.yml`, which prints the total and the 25 largest files to the run
summary.

Two levers, in order of how much they give back:

- **Drop the self-contained runtimes.** Set `WindowsAppSDKSelfContained` and `SelfContained` to
  `false` in `src/Winser/Winser.csproj`. This is the big one — most of the output is those two
  runtimes — in exchange for the machine needing the .NET 8 Desktop Runtime and the Windows App
  SDK runtime installed.
- **`<InvariantGlobalization>true</InvariantGlobalization>`.** Deletes ICU (`icudt.dat`), the
  single largest file in a self-contained .NET app. Deliberately *not* enabled: it makes every
  culture behave like the invariant one, so dates, sorting and case rules stop being correct
  outside English. A browser is the wrong place to trade that away for disk.

`<SatelliteResourceLanguages>en</SatelliteResourceLanguages>` is already set, which drops the
Windows App SDK's translated resource assemblies for the forty-odd languages Winser's own UI
does not speak.

## Memory

Winser renders with WebView2, so its floor is Chromium's floor: a browser process, a GPU
process, and a renderer per site. Nothing in a WinUI shell changes that, and this app will not
undercut Edge or Chrome on a single open tab. What it can control is what happens to the tabs
you are *not* looking at, and that is where nearly all of a browser's memory actually goes.

- **Background tabs are frozen.** `TabView` shows one tab at a time, so switching away unloads
  the page that was showing; Winser takes that as the cue to call `CoreWebView2.TrySuspendAsync`
  and drop the tab to `CoreWebView2MemoryUsageTargetLevel.Low`. Chromium freezes the process
  rather than throwing it away, so switching back resumes it with scroll position, form contents
  and page state intact. The cost is that a frozen page stops running — a chat or a live
  dashboard goes quiet until it is on screen again — so it is a setting, and tabs that are
  audibly playing something are skipped.
- **One CoreWebView2 per tab, sharing one environment**, so every tab in a window shares a
  single browser and GPU process rather than starting its own.
- **No renderer flags.** Most of Chromium's memory switches buy their savings out of security or
  correctness, so Winser sets none of them.

Winser's own managed footprint is small and stays bounded: history is capped at 10,000 entries,
and everything persisted is a plain JSON file written by a debounced atomic replace.

## Where Winser keeps things

Everything lives under `%LOCALAPPDATA%\Winser`:

| Path | Contents |
|---|---|
| `Data\settings.json` | Preferences |
| `Data\bookmarks.json` | Bookmarks |
| `Data\history.json` | Browsing history |
| `Data\downloads.json` | Download list (not the files) |
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

## License

MIT — see [LICENSE](LICENSE).
