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

Winser builds **unpackaged and self-contained**: the output is a plain `Winser.exe` that carries
its own copy of .NET and the Windows App SDK, so it runs without installing a runtime first.
For a much smaller build that depends on the machine having the Windows App SDK runtime
installed, set both `WindowsAppSDKSelfContained` and `SelfContained` to `false` in
`src/Winser/Winser.csproj`.

`winser.exe https://example.com` opens that URL on launch.

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
