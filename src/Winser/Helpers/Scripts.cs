namespace Winser.Helpers;

/// <summary>JavaScript Winser injects into every page it loads.</summary>
public static class Scripts
{
    /// <summary>
    /// Find-in-page, built on the CSS Custom Highlight API so matches are painted without
    /// mutating the DOM (no wrapper spans, so no broken layouts and nothing for a page's own
    /// scripts to trip over). Registered with AddScriptToExecuteOnDocumentCreatedAsync and
    /// driven from <c>BrowserTabViewModel</c> through ExecuteScriptAsync.
    /// </summary>
    public const string FindInPage = """
        (() => {
          if (window.__winserFind) { return; }

          const ALL = 'winser-find';
          const ACTIVE = 'winser-find-active';
          const LIMIT = 2000;

          let ranges = [];
          let active = -1;
          let styleEl = null;

          const supported = () => typeof CSS !== 'undefined' && !!CSS.highlights;

          function ensureStyle() {
            if (styleEl && styleEl.isConnected) { return; }
            styleEl = document.createElement('style');
            styleEl.textContent =
              '::highlight(winser-find){background-color:#ffe27a;color:#101010}' +
              '::highlight(winser-find-active){background-color:#ff9f1c;color:#101010}';
            (document.head || document.documentElement).appendChild(styleEl);
          }

          function report() {
            return { count: ranges.length, active: ranges.length ? active + 1 : 0, supported: supported() };
          }

          function clear() {
            ranges = [];
            active = -1;
            if (supported()) {
              CSS.highlights.delete(ALL);
              CSS.highlights.delete(ACTIVE);
            }
            return report();
          }

          function textNodes() {
            if (!document.body) { return []; }
            const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
              acceptNode(node) {
                if (!node.nodeValue || !node.nodeValue.trim()) { return NodeFilter.FILTER_REJECT; }
                const parent = node.parentElement;
                if (!parent) { return NodeFilter.FILTER_REJECT; }
                const tag = parent.tagName;
                if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'TEXTAREA') {
                  return NodeFilter.FILTER_REJECT;
                }
                const style = getComputedStyle(parent);
                if (style.display === 'none' || style.visibility === 'hidden') {
                  return NodeFilter.FILTER_REJECT;
                }
                return NodeFilter.FILTER_ACCEPT;
              },
            });
            const found = [];
            let node;
            while ((node = walker.nextNode())) { found.push(node); }
            return found;
          }

          function paint() {
            if (!supported()) { return; }
            const others = ranges.filter((_, i) => i !== active);
            CSS.highlights.set(ALL, new Highlight(...others));
            if (active >= 0) {
              const one = new Highlight(ranges[active]);
              one.priority = 1;
              CSS.highlights.set(ACTIVE, one);
            } else {
              CSS.highlights.delete(ACTIVE);
            }
          }

          function reveal() {
            if (active < 0) { return; }
            const target = ranges[active].startContainer.parentElement;
            if (target && target.scrollIntoView) {
              target.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
            }
          }

          function search(query, matchCase) {
            clear();
            if (!query || !supported()) { return report(); }
            ensureStyle();

            const needle = matchCase ? query : query.toLowerCase();
            outer:
            for (const node of textNodes()) {
              const hay = matchCase ? node.nodeValue : node.nodeValue.toLowerCase();
              let at = hay.indexOf(needle);
              while (at !== -1) {
                const range = document.createRange();
                range.setStart(node, at);
                range.setEnd(node, at + needle.length);
                ranges.push(range);
                if (ranges.length >= LIMIT) { break outer; }
                at = hay.indexOf(needle, at + needle.length);
              }
            }

            if (ranges.length) {
              active = 0;
              paint();
              reveal();
            }
            return report();
          }

          function step(delta) {
            if (!ranges.length) { return report(); }
            active = (active + delta + ranges.length) % ranges.length;
            paint();
            reveal();
            return report();
          }

          window.__winserFind = {
            search,
            next: () => step(1),
            previous: () => step(-1),
            clear,
          };
        })();
        """;

    /// <summary>
    /// Forwards browser-level keyboard shortcuts out of the page. WinUI's KeyboardAccelerators
    /// never fire while WebView2 has focus, so the page itself has to hand the keys back.
    /// Only the combinations the shell owns are intercepted; editing shortcuts are left alone.
    /// </summary>
    public const string ShortcutBridge = """
        (() => {
          if (window.__winserKeys) { return; }
          window.__winserKeys = true;

          // Re-asserted by the shell on every navigation and full-screen toggle, since a fresh
          // document gets a fresh global scope; see WebContentView.SyncFullScreenFlag.
          if (typeof window.__winserFullScreen === 'undefined') { window.__winserFullScreen = false; }

          const OWNED = new Set(['t', 'w', 'n', 'l', 'd', 'f', 'h', 'j', 'o', 'p', 'r', 'b',
                                 '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
                                 '+', '-', '=', '_']);
          const NAMED = new Set(['Tab', 'ArrowLeft', 'ArrowRight', 'Home']);

          addEventListener('keydown', (e) => {
            // Escape is handled on its own: forwarding it unconditionally would swallow every
            // page's own use of it (closing a modal, a dropdown, ...), so it only ever leaves
            // the page when Winser is actually in full screen - matching how Escape universally
            // exits full screen in every other browser, page handling or not.
            if (e.key === 'Escape') {
              if (!window.__winserFullScreen) { return; }
              window.chrome.webview.postMessage(JSON.stringify({
                t: 'key', key: 'Escape', ctrl: false, shift: false, alt: false,
              }));
              e.preventDefault();
              e.stopPropagation();
              return;
            }

            const fn = /^F([1-9]|1[0-2])$/.test(e.key);
            if (!fn && !e.ctrlKey && !e.altKey) { return; }

            const key = e.key.length === 1 ? e.key.toLowerCase() : e.key;
            if (!fn && !OWNED.has(key) && !NAMED.has(key)) { return; }

            window.chrome.webview.postMessage(JSON.stringify({
              t: 'key', key, ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey,
            }));
            e.preventDefault();
            e.stopPropagation();
          }, true);

          // WebView2's built-in Ctrl+scroll zoom is switched off so the shell stays the single
          // source of truth for the zoom level shown in the toolbar.
          addEventListener('wheel', (e) => {
            if (!e.ctrlKey) { return; }
            e.preventDefault();
            window.chrome.webview.postMessage(JSON.stringify({
              t: 'zoom', d: e.deltaY < 0 ? 1 : -1,
            }));
          }, { capture: true, passive: false });
        })();
        """;

    /// <summary>
    /// One-shot check run immediately before discarding an idle tab (see
    /// <c>BrowserTabViewModel.TryDiscardAsync</c>), not injected persistently like the two
    /// scripts above. A page exposes no API for "is there unsaved data here" in general, so
    /// this only looks at the currently focused field - a filled, focused input or textarea is
    /// the one case actually worth protecting, not a complete audit of the page's state.
    /// </summary>
    public const string HasUnsavedFormInput = """
        (() => {
          const el = document.activeElement;
          if (!el) { return false; }
          const tag = el.tagName;
          if (tag === 'TEXTAREA') { return el.value.length > 0; }
          if (tag === 'INPUT') {
            const type = (el.type || 'text').toLowerCase();
            const skip = new Set(['button', 'submit', 'reset', 'checkbox', 'radio', 'file', 'image', 'hidden']);
            return !skip.has(type) && el.value.length > 0;
          }
          if (el.isContentEditable) { return el.textContent.trim().length > 0; }
          return false;
        })()
        """;
}
