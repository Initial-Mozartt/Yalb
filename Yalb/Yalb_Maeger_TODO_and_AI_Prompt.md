# Yalb Maeger — Minimal Browser TODO & AI Prompt

> **Project:** Yalb Maeger — The minimal variant of Yalb. No terminal, no bloat, no "AI-y" look.  
> **Inspiration:** Helium Browser (helium.computer) — compact, frameless, warm, crafted.  
> **Rule:** Keep changes small and surgical. Never rewrite whole files. Log everything.

---

## What Helium Does (From the Video & Docs)

Helium's frameless/minimal tricks:
- **True frameless window** — No Windows title bar at all. Not even a pixel. The app draws its own 32px compact toolbar directly at the top edge.
- **WM_NCHITTEST + WM_NCCALCSIZE override** — This is how native apps remove the title bar while keeping aero-snap, resizing, and shadow. Helium likely uses Chromium's built-in frameless support (`--enable-features=Windows10CustomTitlebar` or custom `HWND` handling), but for WinForms we do it via `WndProc`.
- **Compact chrome** — Toolbar is only ~32px. Tabs are physically connected to the toolbar (no gap). Address bar is seamlessly integrated.
- **Zen mode** — One shortcut hides ALL chrome. Hover top edge to reveal.
- **Warm palette** — Not generic `#1e1e1e`. It's `#1c1c1f` with muted blue accent `#5b8def`.
- **No noise** — No emoji icons, no giant padding, no stock-looking buttons.

---

## Current Maeger File Map

```
Maeger/
  BrowserForm.cs          (66KB) — main form, tabs, shortcuts, layout
  OriginSettings.cs       (4KB)  — JSON settings persistence
  YalbSettings.cs         (2KB)  — settings model
  SplashForm.cs           (1KB)  — splash screen
  Program.cs              (1KB)  — entry point
  YalbLogger.cs           (NEW)  — file logging
  Yalb.Maeger.csproj      — project file
  chrome-ui/
    index.html            — tab strip + toolbar markup
    style.css             — dark theme, tab styling, layout
    app.js                — WebView2 message bridge, tab rendering
  pages/                  — internal pages (newtab, settings)
  Splash/                 — splash assets
```

**NOT in Maeger:** `TerminalPanel.cs` — intentionally excluded. Do not add.

---

## P0 — CRITICAL (Do These First)

### P0.1 Create `YalbLogger.cs`
- Static class, thread-safe, zero-dependency
- Log path: `%LocalAppData%\OriginBrowser\logs\yalb-{date}.log`
- Levels: DEBUG, INFO, WARN, ERROR, FATAL
- Format: `[2026-05-31 14:32:01.234] [INFO] [Class.Method] message`
- `Time(string label, Action action)` — logs elapsed ms
- `Error(string ctx, Exception ex)` — logs full stack trace
- Auto-delete logs older than 7 days, max 5MB per file

### P0.2 Fix the Top Bar / Title Bar ("Bar thing on top")
**This is the #1 complaint.**

In `BrowserForm.cs`:
- [ ] When `FramelessWindow=true` (or by default in Maeger), set `FormBorderStyle = FormBorderStyle.None`
- [ ] Set `ControlBox = false`, `Text = string.Empty`
- [ ] Override `WndProc` to handle:
  - `WM_NCCALCSIZE` (0x83) — Return 0 to remove non-client area completely (no 1px DWM line)
  - `WM_NCHITTEST` (0x84) — Return edge constants so window is still resizable from borders/corners
  - `WM_NCACTIVATE` (0x86) — Pass through properly so window shadow stays
- [ ] Handle `WM_LBUTTONDOWN` on a draggable region (the toolbar area) to allow moving the window
- [ ] Custom minimize / maximize / close buttons must exist in `chrome-ui/index.html` and post messages back to C#
- [ ] Log every WndProc message during init to verify the title bar is actually gone
- [ ] Test on Windows 10 and 11 — DWM behavior differs slightly

**Key insight:** Just setting `FormBorderStyle = None` leaves a white/gray line on Windows 10/11. You MUST handle `WM_NCCALCSIZE` to tell Windows "my client area is the whole window" and `WM_NCHITTEST` to keep resize handles.

### P0.3 Fix Shortcut Conflicts
In `ProcessCmdKey()`:
- [ ] `Ctrl+Shift+Alt+F` → Toggle **Full Frameless** (hides title bar + chrome panel — Zen mode)
- [ ] `Ctrl+Shift+B` → Toggle **Chrome Panel** (tabs + address bar)
- [ ] `Ctrl+Shift+S` → Toggle **Sidebar** (if sidebar exists; skip if not implemented yet)
- [ ] Check `Alt` combo FIRST before simpler combos to avoid swallowing
- [ ] Log every shortcut invocation

### P0.4 Fix Extra Tab on Startup
- [ ] Add `bool _startupComplete` flag
- [ ] After session restore loop finishes, set `_startupComplete = true`
- [ ] Guard `AddNewTab()` so it never auto-creates a home-page tab if `_startupComplete == false` and session restore already ran
- [ ] Log which path created each startup tab

### P0.5 Fix "AI-y" Look — Warm Helium Palette
In `chrome-ui/style.css`:
```css
:root {
  --bg: #1c1c1f;
  --bg-light: #252528;
  --bg-lighter: #2e2e32;
  --bg-input: #1e1e22;
  --border: #3a3a3e;
  --text: #e6e6e8;
  --text-muted: #8a8a8e;
  --accent: #5b8def;
  --accent-hover: #7aa7f5;
  --tab-active: #323236;
  --tab-hover: #2a2a2e;
  --radius-sm: 4px;
  --radius-md: 8px;
  --toolbar-height: 32px;
  --tab-height: 32px;
  --font-family: "Segoe UI Variable", "Inter", -apple-system, sans-serif;
}
```
- [ ] Replace generic `#1e1e1e` / `#0a84ff` with the palette above
- [ ] Reduce toolbar height to 32px, tab height to 32px
- [ ] Remove giant emoji icons — use simple CSS shapes or SVG
- [ ] Tighten padding everywhere (Helium is compact, not spacious)

---

## P1 — HIGH IMPACT (Next)

### P1.1 Tab Animations & Favicons
- [ ] New tab: `scale(0.9) → scale(1)` + `opacity 0 → 1`, 150ms ease-out
- [ ] Close tab: `opacity 1 → 0` + width collapse, 120ms
- [ ] Active tab: 2px top accent border, connected to toolbar (no bottom gap)
- [ ] Favicon per tab — fetch from `https://www.google.com/s2/favicons?domain={host}` as fallback
- [ ] Close button (×) hidden by default, fades in on hover

### P1.2 Compact Chrome UI
- [ ] Address bar height: 28px, font 12.5px, seamless with toolbar
- [ ] Button size: 26×26px, 4px radius
- [ ] Button hover: `rgba(255,255,255,0.06)` — very subtle
- [ ] Button active: `transform: scale(0.96)`, 60ms
- [ ] Tab font: 11.5px, weight 500 active / 400 inactive
- [ ] No hard max-width on tabs — let them shrink naturally with `text-overflow: ellipsis`

### P1.3 New Tab Page (Minimal)
- [ ] `pages/newtab.html` — clean, not crowded
- [ ] Clock + date (subtle, muted color)
- [ ] Search bar in center (larger than normal, rounded)
- [ ] 8 shortcut tiles max, `+` tile to add current page
- [ ] Store shortcuts in `OriginSettings.HomeShortcuts`
- [ ] No wallpaper for now (keep it minimal) — solid `--bg` background

---

## P2 — MEDIUM (Later)

### P2.1 Pin Tabs
- [ ] Right-click tab → "Pin Tab" / "Unpin Tab"
- [ ] Pinned tabs: small (icon + truncated title), always leftmost, no close button
- [ ] `Ctrl+Shift+P` to pin/unpin current tab
- [ ] Persist `pinned: true` in session restore

### P2.2 Native !Bangs (Top 20 Only)
- [ ] Embedded `bangs.json` resource: `!w`, `!g`, `!gh`, `!yt`, `!r`, `!a`, `!maps`, `!img`, `!tr`, `!chatgpt`
- [ ] Resolved locally — no network call
- [ ] Dropdown suggestion when `!` typed in address bar
- [ ] Settings toggle to enable/disable

### P2.3 Left Sidebar (Narrow, Minimal)
- [ ] Width: 42px, background `--bg`, border-right `1px solid var(--border)`
- [ ] 5 icons max: Settings (top), Bookmarks (top), New Tab (+) (center), History (bottom), Downloads (bottom)
- [ ] `Ctrl+Shift+S` toggles visibility
- [ ] Click navigates active tab, middle-click opens new tab

---

## P3 — LOW / FUTURE (Don't Touch Yet)

- [ ] Split view — too complex for minimal
- [ ] Web app mode — not minimal enough
- [ ] Content blocking — can be added later
- [ ] Accent color picker — default `#5b8def` is fine for now
- [ ] Zen mode edge-hover reveal — implement AFTER full frameless works perfectly

---

## Build & Verify (After Every Change)

```bash
cd Maeger && dotnet build Yalb.Maeger.csproj
```

Manual checks:
- [ ] No title bar pixel visible at top when frameless
- [ ] Window still resizable from edges/corners
- [ ] Window can be dragged by toolbar
- [ ] Minimize / maximize / close buttons in chrome UI work
- [ ] `Ctrl+Shift+Alt+F` hides ALL chrome (Zen)
- [ ] `Ctrl+Shift+B` toggles chrome panel
- [ ] Startup restores correct tab count (no extra)
- [ ] Log file shows init timings
- [ ] No crash on close / last tab close

---

---

## AI PROMPT — Copy-Paste Ready for Maeger

```
You are working on Yalb Maeger — the MINIMAL variant of the Yalb Browser for Windows.  
Built with C# WinForms + Microsoft Edge WebView2.  
NO terminal. NO split view. NO web app mode. NO content blocking (for now).

INSPIRATION: Helium Browser (helium.computer) — ultra-compact, warm dark palette,  
true frameless window, "internet without interruptions."

CURRENT STATE:
- BrowserForm.cs (66KB) — main form with _chromeWebView (UI layer) and _contentPanel (tab webviews)
- chrome-ui/index.html + style.css + app.js — tab strip, address bar, toolbar
- OriginSettings.cs — JSON persistence to %LocalAppData%\OriginBrowser\settings.json
- NO YalbLogger.cs yet — create this FIRST before any other changes
- NO TerminalPanel.cs — do not add one

CRITICAL RULES:
1. NEVER rewrite entire files. Make surgical changes only.
2. ALWAYS add YalbLogger.Debug/Info/Error calls when modifying any method.
3. After every change: `cd Maeger && dotnet build Yalb.Maeger.csproj` must pass.
4. Keep UI minimal but polished. Avoid generic "AI-generated" looks.
5. If a feature adds noise, don't ship it. Every feature must earn its place.

COLOR PALETTE (Helium-inspired, already defined in CSS):
--bg: #1c1c1f; --bg-light: #252528; --bg-lighter: #2e2e32;
--border: #3a3a3e; --text: #e6e6e8; --text-muted: #8a8a8e;
--accent: #5b8def; --accent-hover: #7aa7f5;
--toolbar-height: 32px; --tab-height: 32px;

KEYBOARD SHORTCUTS (must not conflict):
- Ctrl+Shift+Alt+F: Full frameless / Zen mode toggle (hides title bar + chrome)
- Ctrl+Shift+B: Toggle chrome panel visibility (tabs + address bar)
- Ctrl+Shift+S: Toggle sidebar visibility
- Ctrl+Shift+P: Pin/unpin current tab
- Ctrl+T: New tab, Ctrl+W: Close tab, Ctrl+L: Focus address bar
- Ctrl+Tab / Ctrl+Shift+Tab: Next/previous tab

FRAMELESS WINDOW REQUIREMENT:
Maeger MUST have a true frameless window. The Windows title bar must be completely gone.
To achieve this in WinForms:
- Set FormBorderStyle = None when frameless
- Set ControlBox = false, Text = string.Empty
- Override WndProc:
  * WM_NCCALCSIZE (0x83): return 0 to eliminate non-client area (removes the 1px DWM line)
  * WM_NCHITTEST (0x84): return HTCAPTION/HTLEFT/HTRIGHT/HTTOP/etc. so window stays resizable and draggable
  * WM_NCACTIVATE (0x86): pass through for proper shadow
- Custom window controls (minimize, maximize, close) live in chrome-ui/index.html
- The chrome UI toolbar itself becomes the draggable region (postMessage "dragWindow" to C#)

LOGGING:
Before any change, ensure YalbLogger.cs exists with:
- Static methods: Debug(string msg), Info(string msg), Warn(string msg), Error(string msg, Exception? ex = null)
- Time(string label, Action action) that logs elapsed milliseconds
- Log file at %LocalAppData%\OriginBrowser\logs\yalb-{date}.log
- Thread-safe writes via lock or ConcurrentQueue

WHEN ASKED TO FIX A BUG:
1. First, add logging to understand current behavior.
2. Identify the exact line(s) causing the issue.
3. Make the smallest possible fix.
4. Add a log line confirming the fix path was taken.

WHEN ASKED TO ADD A FEATURE:
1. Check if partial code already exists.
2. Implement the minimal viable version first.
3. Add logging for all new code paths.
4. Ensure graceful degradation if something fails.
5. Ask: "Would Helium ship this?" If no, simplify or skip.

WHEN ASKED TO IMPROVE UI:
- Follow Helium's compact aesthetic: 32px chrome, maximum content area.
- Replace emoji icons with simple CSS/SVG or unicode arrows.
- Add subtle transitions (120-200ms) but never delay user actions.
- Active states must feel instant. Animations are for enter/exit only.
- Use varying border-radius: 4px buttons, 8px tabs, 0px panels.
- Avoid generic dark themes — use the warm Helium palette above.

BEFORE RESPONDING:
- State which file(s) you will modify.
- Show exact old code and exact new code (diff style).
- Mention where you added log lines.
- Confirm no other functionality is affected.
- If adding a feature, explain the minimal version you're implementing.
```

---

*Last updated: 2026-05-31*  
*Next step: Implement YalbLogger.cs, then fix the frameless title bar (WM_NCCALCSIZE + WM_NCHITTEST).*
