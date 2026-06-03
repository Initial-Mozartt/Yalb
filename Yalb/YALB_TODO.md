# Yalb Browser — Development Roadmap & AI Prompt

> **Project:** Yalb (Maeger) — A minimal, keyboard-driven WinForms + WebView2 browser for Windows.  
> **Repo:** `https://github.com/Initial-Mozartt/Yalb`  
> **Rule:** Keep changes small and surgical. Never rewrite whole files. Log everything.

---

## Table of Contents

1. [Project State](#1-project-state)
2. [Logging Infrastructure (Do First)](#2-logging-infrastructure-do-first)
3. [Critical Bugs](#3-critical-bugs)
4. [Polish & Feel (Fix the "AI-y" Look)](#4-polish--feel-fix-the-ai-y-look)
5. [Features (Minimal but Complete)](#5-features-minimal-but-complete)
6. [Build & Verify Checklist](#6-build--verify-checklist)
7. [AI Prompt (Copy-Paste Ready)](#7-ai-prompt-copy-paste-ready)
8. [App Icon](# App Icon)

---

## 1. Project State

### Current Architecture
- **C# WinForms + WebView2** — two separate WebView2 environments: `Chrome` (UI) and `Content` (pages)
- **Chrome UI** rendered from `chrome-ui/index.html` + `style.css` + `app.js`
- **Terminal** integrated via xterm.js (`TerminalPanel.cs`)
- **Settings** persisted to JSON in `%LocalAppData%\OriginBrowser\settings.json`
- **Splash screen** (`SplashForm.cs`) shows during init

### File Map (Local `Maeger/` folder)
```
Maeger/
  BrowserForm.cs          (66KB) — main form, tab mgmt, shortcuts, layout
  OriginSettings.cs       (4KB)  — JSON settings persistence
  YalbSettings.cs         (2KB)  — settings model
  SplashForm.cs           (1KB)  — splash screen
  Program.cs              (1KB)  — entry point
  chrome-ui/
    index.html            — tab strip + toolbar markup
    style.css             — dark theme, tab styling, layout
    app.js                — WebView2 message bridge, tab rendering
  pages/                  — (new) internal pages
```

---

## 2. LOGGING INFRASTRUCTURE (Do First)

> **Purpose:** Every action, error, timing measurement, and state change must be logged to a file. This is the foundation for fixing everything else.

### 2.1 Create `YalbLogger.cs`

```csharp
// New file: Maeger/YalbLogger.cs
// Simple, zero-dependency file logger with timestamps and log levels.
// Log path: %LocalAppData%\OriginBrowser\logs\yalb-{date}.log
// Auto-creates directory, auto-deletes logs older than 7 days.
```

**Log levels:** `DEBUG` | `INFO` | `WARN` | `ERROR` | `FATAL`  
**Log format:** `[2026-05-31 14:32:01.234] [INFO] [BrowserForm.InitializeAsync] WebView2 chrome environment created in 847ms`  
**Thread-safe:** Yes, using `lock` or `ConcurrentQueue` + background flush.

**Implementation rules:**
- [x] Static class, no instance needed: `YalbLogger.Info("message")`

- [ ] Include source context (class/method name) in every log line
- [ ] `Stopwatch` integration: `YalbLogger.Time("Label", () => { ... })` logs elapsed ms
- [ ] `Exception` overload: `YalbLogger.Error("context", ex)` logs full stack trace
- [ ] Keep last N lines in memory for a "Debug Console" feature later
- [ ] Max log file size: 5MB, then rotate to `.log.1`

### 2.2 Add `[Log]` calls to every existing method

Priority order (most impactful first):

| Method | What to log |
|--------|-------------|
| `InitializeAsync()` | Start/end of each phase with Stopwatch timing |
| `CoreWebView2Environment.CreateAsync` | Time the call, log userDataFolder path |
| `EnsureCoreWebView2Async` | Time the call |
| `_chromeWebView.CoreWebView2.Navigate` | Log chrome UI load start |
| Chrome UI `NavigationCompleted` | Log chrome UI load complete |
| Session restore loop | Log `LastSessionTabs.Count`, time the loop |
| `AddNewTab()` | Log tab ID, URL, total tab count after add |
| `CloseTab()` | Log tab ID, total tab count after close |
| `SwitchToTab()` | Log from-tab, to-tab |
| `UpdateChromeUIAsync()` | Log payload size (char count), time the call |
| `ProcessCmdKey()` | Log shortcut pressed and action taken |
| `ApplyChromeVisibility()` | Log new visibility state |
| `SetContentFramelessState()` | Log frameless boolean |
| `WaitForBrowserReady()` | Log each readyState check, log timeout vs natural |
| `OnBrowserReady()` | Log splash dismissal |
| `FormClosing` | Log settings save success/fail |
| Every `catch` block | Log exception details |

### 2.3 Add runtime "Debug Log" panel (optional but recommended)

- [ ] Add a `Ctrl+Shift+D` shortcut to open a debug overlay
- [ ] Overlay is a small WebView2 panel showing last 200 log lines
- [ ] Auto-scrolls to bottom
- [ ] Color-coded by level (yellow=WARN, red=ERROR, gray=DEBUG)
- [ ] Close with `Escape` or same shortcut

---

## 3. CRITICAL BUGS

### BUG 1 — Extra Tab on Startup (Partial Fix, Needs Confirm)

**Problem:** When `RestoreLastSession=true` and `LastSessionTabs` has entries, the session restore loop creates tabs, but then a fallback `AddNewTab(HomePageUrl)` might also run.

**Current code (line ~249 in GitHub version):**
```csharp
if (settings.RestoreLastSession && settings.LastSessionTabs.Count > 0)
{
    foreach (var tab in settings.LastSessionTabs)
        AddNewTab(tab.Url);
}
else
{
    AddNewTab(settings.HomePageUrl);   // <-- BUG: else is correct, but check
                                      // if AddNewTab is called elsewhere
}
```

- [x] Verify the `else` guard is actually correct in the **local** 66KB `BrowserForm.cs`
- [ ] Check if `AddNewTab()` is called from `OnBrowserReady()` or `WaitForBrowserReady()` after the session restore
- [ ] Check if `settings.LastSessionTabs` is deserialized correctly (non-null)
- [ ] Add `YalbLogger.Info` after every `AddNewTab` call during init to trace which path created it
- [ ] **Fix:** If a second call path exists, add a `bool _startupTabsRestored` flag to block it

### BUG 2 — App is Slow

**Phase 1: Measure (requires Logger from Section 2)**

Add `YalbLogger.Time()` wrappers around these blocks in `InitializeAsync()`:

```csharp
// Pseudocode — wrap existing code:
YalbLogger.Time("Env.CreateAsync.Chrome", () => {
    _chromeEnv = await CoreWebView2Environment.CreateAsync(...);
});
YalbLogger.Time("Env.CreateAsync.Content", () => {
    _contentEnv = await CoreWebView2Environment.CreateAsync(...);
});
YalbLogger.Time("EnsureCoreWebView2Async", () => {
    await _chromeWebView.EnsureCoreWebView2Async(_chromeEnv);
});
YalbLogger.Time("ChromeUINavigate", () => {
    _chromeWebView.CoreWebView2.Navigate(_chromeUiPath);
});
YalbLogger.Time("SessionRestore", () => {
    foreach (var tab in settings.LastSessionTabs)
        AddNewTab(tab.Url);   // time each AddNewTab too
});
```

**Phase 2: Fix based on measurements**

| If slow step is... | Fix |
|-------------------|-----|
| `Env.CreateAsync` | Move user data to `%LocalAppData%\Yalb\UserData` (faster disk) |
| `EnsureCoreWebView2Async` | Pre-warm environment in `Program.cs` before showing splash |
| `AddNewTab` loop (many tabs) | Lazy-load: restore only first tab, keep rest as `about:blank` stubs |
| `UpdateChromeUIAsync` | Debounce: add 50ms timer, coalesce rapid updates |
| Overall | Defer terminal panel init, sidebar init until after first paint |

### BUG 3 — Remove Top Title Bar ("Origin — Name of tab")

**Problem:** The WinForms `Text` property still shows in the Windows title bar.

- [x] In `InitializeComponent()`, change `Text = "Origin"` to `Text = ""`

- [ ] Verify `FormBorderStyle = None` is set when `FramelessWindow=true`
- [ ] Ensure `DWM` (Desktop Window Manager) doesn't render a title bar anyway:
  - Use `this.ControlBox = false` when frameless
  - Use `this.Text = string.Empty` when frameless
- [ ] If using `SetWindowPos` or `WM_NCCALCSIZE` hacks, log them and verify on Windows 10/11
- [ ] Custom window controls (minimize, maximize, close) must still work via chrome UI

### BUG 4 — Shortcut Conflicts (Ctrl+Shift+Alt+F, Ctrl+Shift+B, Ctrl+Shift+S)

**Required mapping:**

| Shortcut | Action |
|----------|--------|
| `Ctrl+Shift+Alt+F` | **Full frameless toggle** — hides BOTH title bar AND chrome panel |
| `Ctrl+Shift+B` | Toggle chrome panel visibility (tabs + address bar) |
| `Ctrl+Shift+S` | Toggle sidebar visibility |

- [ ] In `ProcessCmdKey()`, add the exact check order:
  1. `if (keyData == (Keys.Control | Keys.Shift | Keys.Alt | Keys.F))` -> `ToggleFullFrameless()`
  2. `if (keyData == (Keys.Control | Keys.Shift | Keys.B))` -> `ToggleChromePanel()`
  3. `if (keyData == (Keys.Control | Keys.Shift | Keys.S))` -> `ToggleSidebar()`
- [ ] The `Alt` key in combo #1 must be checked BEFORE the simpler combos
- [ ] Log every shortcut invocation with `YalbLogger.Debug()`
- [ ] After toggling, call `UpdateChromeUIAsync()` to reflect state change

---

## 4. POLISH & FEEL (Fix the "AI-y" Look)

> **Principle:** The browser currently looks like a template — generic dark theme, stock buttons, no personality. We want it to feel **crafted**, not generated.

### 4.1 Tab Design — Make Tabs Feel Physical

**Current problem:** Tabs are flat rectangles with no depth. They don't feel like tabs.

**CSS changes in `chrome-ui/style.css`:**

- [ ] **Active tab:** Add subtle top border (2px accent color), slightly lighter background, connected to toolbar below (no gap)
- [ ] **Hover:** Slight lift — `transform: translateY(-1px)` with transition
- [ ] **Inactive tabs:** Slightly darker than active, rounded top corners only
- [ ] **Close button (x):** Only show on hover, fade in with `opacity` transition
- [ ] **Tab text:** Add `text-overflow: ellipsis`, remove hardcoded `max-width` constraints
- [ ] **New tab animation:** `scale(0.9) -> scale(1)` with `opacity 0 -> 1`, 150ms ease-out
- [ ] **Close tab animation:** `opacity 1 -> 0` + `width` collapse, 120ms, then remove from DOM
- [ ] **Tab drag ghost:** Semi-transparent clone with `box-shadow` while dragging

### 4.2 Toolbar — Less Clutter, More Purpose

- [ ] **Remove emoji icons** (`🔍` in address bar) — use a simple SVG icon or CSS-only magnifying glass
- [ ] **Button style:** Flat, no borders. Hover = subtle background color change (`rgba(255,255,255,0.05)`)
- [ ] **Active button state:** Slightly different background when action is toggled on
- [ ] **Address bar:** Rounded, seamless with toolbar. Focus = subtle border glow (1px accent)
- [ ] **Add subtle divider** between nav buttons and address bar (`border-left: 1px solid var(--border)`)

### 4.3 Color Palette — More Sophisticated

Replace the generic dark theme:

```css
/* Current — too generic */
--bg: #1e1e1e;
--bg-light: #2d2d2d;
--accent: #0a84ff;

/* Target — warmer, more crafted */
--bg: #1a1a1f;           /* Slightly warm dark */
--bg-light: #252529;      /* Warm gray */
--bg-lighter: #2e2e33;    /* Elevated surfaces */
--border: #3a3a40;        /* Softer borders */
--accent: #5b8def;        /* Muted blue, less saturated */
--accent-hover: #7aa7f5;  /* Lighter on interaction */
--text: #e8e8e8;          /* Slightly warm white */
--text-muted: #888892;    /* Softer muted text */
--tab-active: #32323a;    /* Active tab bg */
--toolbar-height: 38px;   /* Slightly taller for breathing room */
```

### 4.4 Typography — Smaller, Tighter

- [ ] Reduce font sizes: tabs to `11.5px`, toolbar buttons to `12px`, address bar to `12.5px`
- [ ] Use `"Segoe UI Variable"` or `"Inter"` if available, fall back to system fonts
- [ ] Add `-webkit-font-smoothing: antialiased` equivalent (`-webkit-font-smoothing` doesn't work in WebView2, use proper font stack)
- [ ] Tab titles: `font-weight: 500` for active, `400` for inactive

### 4.5 Micro-Interactions

- [ ] **Button press:** `transform: scale(0.96)` on `:active`, 60ms
- [ ] **Tab hover:** Background lightens, close button fades in
- [ ] **Page load:** Address bar shows subtle progress shimmer (CSS animation)
- [ ] **Focus ring:** Only on keyboard navigation (use `:focus-visible`), 1px accent color

### 4.6 No "AI-y" Patterns to Avoid

| Don't | Do Instead |
|-------|-----------|
| Giant emoji icons | Simple, consistent SVG or CSS icons |
| Generic blue `#0a84ff` everywhere | Muted accent used sparingly |
| Perfectly centered everything | Left-aligned tabs, natural reading flow |
| `border-radius: 6px` on everything | Vary slightly: 4px for buttons, 8px for tabs, 0px for panels |
| Pure black `#000` shadows | Subtle, warm shadows with opacity |
| Stock sans-serif font stack | Curated font stack with fallbacks |

---

## 5. FEATURES (Minimal but Complete)

### FEATURE 1 — Sidebar Buttons (2 Top, 2 Bottom, Center)

**Layout:**
```
+------------------+
| [Settings]       |  <- top
| [Bookmarks]      |  <- top
|                  |
|    [ + ]         |  <- center (new tab)
|                  |
| [History]        |  <- bottom
| [Downloads]      |  <- bottom
+------------------+
```

- [ ] Update `chrome-ui/index.html` — add sidebar `<div id="sidebar">`
- [ ] Update `chrome-ui/style.css` — sidebar positioning, flex layout with `justify-content: space-between`
- [ ] Center the `+` button with `margin: auto`
- [ ] Each button: icon-only, 32x32px, tooltip on hover with title
- [ ] Sidebar width: 44px, collapsible via `Ctrl+Shift+S`
- [ ] Sidebar background: `--bg` with right border `1px solid var(--border)`
- [ ] Buttons send `postMessage` to C#: `openSettings`, `openBookmarks`, `newTab`, `openHistory`, `openDownloads`

### FEATURE 2 — Tab Animations (CSS-only)

See Section 4.1 for the CSS transitions. Keep them all GPU-accelerated:

```css
.tab {
    will-change: transform, opacity;
    transition: transform 120ms cubic-bezier(0.25, 0.1, 0.25, 1),
                opacity 100ms ease-out,
                background-color 80ms ease;
}
```

- [ ] New tab: `opacity 0 -> 1` + `translateY(4px) -> translateY(0)`
- [ ] Close tab: `opacity 1 -> 0` + `scale(1) -> scale(0.95)`, then `display: none`
- [ ] Active switch: instant background change (no animation — must feel responsive)
- [ ] Hover: `background-color` transition only, 80ms

### FEATURE 3 — Favicons on Tabs

- [ ] In `UpdateChromeUIAsync()`, include `faviconUrl` per tab in the JSON payload
- [ ] Fetch favicon in C# via `webView.CoreWebView2.FaviconUri` property
- [ ] Fallback chain: `FaviconUri` -> `https://domain.com/favicon.ico` -> generic globe SVG
- [ ] In `chrome-ui/app.js` `renderTabs()`, add `<img class="tab-favicon">` before the title
- [ ] CSS: favicon 14x14px, slightly rounded (2px), `opacity: 0.8` for inactive tabs
- [ ] If favicon fails to load, show CSS-only globe icon as fallback (no broken image)

### FEATURE 4 — Bookmarks Bar (Below Address Bar)

**Not in sidebar — a horizontal bar below the address bar, like classic browsers.**

- [ ] Add `#bookmark-bar` HTML in `chrome-ui/index.html` between toolbar and content
- [ ] CSS: horizontal flex row, height `28px`, same background as toolbar, border-top `1px solid var(--border)`
- [ ] Store bookmarks in `OriginSettings.Bookmarks` (list of `{title, url}`)
- [ ] Each bookmark: favicon + truncated title, click to navigate
- [ ] `Ctrl+Shift+B` toggles bookmark bar visibility (separate from chrome panel toggle)
- [ ] Right-click a bookmark: context menu with "Edit", "Delete", "Open in new tab"
- [ ] "Add bookmark" via address bar star icon or `Ctrl+D` shortcut

### FEATURE 5 — Opera GX Features (Placeholder Only)

These stay as **future placeholders**. Do NOT implement now.

- [ ] Add `GX` section in settings JSON model (commented out in code)
- [ ] GX Design: `AccentColor` field (string, hex), default `"#fa1e4e"`
- [ ] GX Sound: `EnableKeySounds` bool, default `false`
- [ ] GX Control: `EnableHibernation` bool, default `false`
- [ ] Keep UI placeholders minimal — a disabled menu item is enough

---

## 6. BUILD & VERIFY CHECKLIST

Run this after **every** change:

```bash
cd Maeger && dotnet build Yalb.Maeger.csproj
```

### Manual Regression Tests

- [ ] **Startup:** Opens with correct number of restored tabs (no extra tab)
- [ ] **Startup timing:** Check log file for init phase timings — identify slowest step
- [ ] **Title bar:** Not visible when `FramelessWindow=true` in settings
- [ ] **Chrome panel:** `Ctrl+Shift+B` toggles tab strip + address bar visibility
- [ ] **Full frameless:** `Ctrl+Shift+Alt+F` toggles both title bar AND chrome panel
- [ ] **Sidebar:** `Ctrl+Shift+S` toggles sidebar visibility
- [ ] **Tab switch:** Click tabs, use `Ctrl+Tab` / `Ctrl+Shift+Tab` — all smooth
- [ ] **New tab:** `Ctrl+T` creates tab, animation plays
- [ ] **Close tab:** `Ctrl+W` closes tab, animation plays, no crash on last tab
- [ ] **Navigation:** Address bar works, back/forward work, reload works
- [ ] **Terminal:** `Ctrl+Shift+T` toggles terminal panel, can resize
- [ ] **Log file:** Check `%LocalAppData%\OriginBrowser\logs\` for complete startup log
- [ ] **No crash:** Close browser via X button, close via `Alt+F4`, close last tab — all stable

---

## 7. AI PROMPT (Copy-Paste Ready)

Copy the block below and paste it into any AI coding assistant. It gives the AI full context to make surgical, correct changes.

---

```
You are working on Yalb Browser — a minimal, keyboard-driven desktop web browser for Windows built with C# WinForms and Microsoft Edge WebView2. The browser renders its own chrome UI (tabs, address bar, toolbar) using an HTML/CSS/JS layer inside a WebView2, separate from the content WebView2 that displays web pages.

PROJECT RULES:
- NEVER rewrite entire files. Make surgical changes only.
- ALWAYS add YalbLogger.Debug/Info/Error calls when modifying any method.
- Keep the UI minimal but polished. Avoid generic "AI-generated" looks.
- After every code change, the project must still build with `dotnet build`.
- Prefer small, testable changes over large refactors.

ARCHITECTURE:
- BrowserForm.cs: Main WinForms form. Contains _chromeWebView (WebView2 for UI), _contentPanel (hosts content WebViews per tab), _terminalPanel, tab management dictionaries.
- InitializeAsync(): Creates two CoreWebView2Environment instances (Chrome and Content), initializes the chrome WebView, restores session tabs, applies frameless/chrome settings.
- UpdateChromeUIAsync(): Serializes tab state to JSON and posts it to the chrome WebView via WebMessageReceived.
- ProcessCmdKey(): Handles all keyboard shortcuts.
- chrome-ui/index.html: Tab strip (#tabs-bar) + toolbar with nav buttons and address bar.
- chrome-ui/style.css: Dark theme using CSS custom properties. Tab styling, button styling, layout.
- chrome-ui/app.js: Receives state updates from C#, renders tabs, handles click events, posts messages back to C#.
- OriginSettings.cs: Singleton JSON settings manager. Persists to %LocalAppData%\OriginBrowser\settings.json.

CURRENT COLOR PALETTE (in style.css):
--bg: #1e1e1e; --bg-light: #2d2d2d; --bg-lighter: #3a3a3a; --bg-input: #252525;
--border: #444; --text: #ffffff; --text-muted: #aaa; --accent: #0a84ff;
--radius: 6px; --toolbar-height: 34px;

KEYBOARD SHORTCUTS (must not conflict):
- Ctrl+Shift+Alt+F: Full frameless toggle (title bar + chrome panel)
- Ctrl+Shift+B: Toggle chrome panel visibility
- Ctrl+Shift+S: Toggle sidebar visibility
- Ctrl+Shift+T: Toggle terminal
- Ctrl+T: New tab, Ctrl+W: Close tab, Ctrl+L: Focus address bar

LOGGING:
Before any change, ensure YalbLogger.cs exists with:
- Static methods: Debug(string msg), Info(string msg), Warn(string msg), Error(string msg, Exception? ex = null)
- Time(string label, Action action) that logs elapsed milliseconds
- Log file at %LocalAppData%\OriginBrowser\logs\yalb-{date}.log
- Thread-safe writes

WHEN ASKED TO FIX A BUG:
1. First, add logging to understand the current behavior.
2. Identify the exact line(s) causing the issue.
3. Make the smallest possible fix.
4. Add a log line confirming the fix path was taken.

WHEN ASKED TO ADD A FEATURE:
1. Check if the feature already has partial code.
2. Implement the minimal viable version first.
3. Add logging for all new code paths.
4. Ensure the feature degrades gracefully if something fails.

WHEN ASKED TO IMPROVE UI:
- Avoid generic dark themes. Use a warmer, more crafted palette.
- Replace emoji icons with simple CSS/SVG icons.
- Add subtle transitions (120-200ms) but never delay user actions.
- Active states must feel instant. Animations are for enter/exit only.
- Use varying border-radius values (not all 6px) for visual hierarchy.
- Favicons on tabs are the highest-priority visual improvement.

BEFORE RESPONDING:
- State which file(s) you will modify.
- Show the exact old code and the exact new code (diff style).
- Mention where you added log lines.
- Confirm no other functionality is affected.
```

---

## Appendix A: Quick Reference — File Responsibilities

| File | Responsibility | When to Edit |
|------|---------------|-------------|
| `BrowserForm.cs` | Window, tabs, shortcuts, layout, WebView2 mgmt | Bugs, features needing C# logic |
| `OriginSettings.cs` | Settings model, persistence | New settings fields |
| `YalbSettings.cs` | Settings constants, defaults | Default value changes |
| `YalbLogger.cs` | **(NEW)** File logging, timing | Create first, then use everywhere |
| `chrome-ui/index.html` | Chrome UI markup | New UI elements (sidebar, bookmarks) |
| `chrome-ui/style.css` | Chrome UI styling | Visual polish, animations, colors |
| `chrome-ui/app.js` | Chrome UI JS bridge | Tab rendering, event handling, favicons |
| `SplashForm.cs` | Splash screen | Only if init flow changes |
| `Program.cs` | Entry point | Pre-warming, logging init |

## Appendix B: Opera GX Features — Future Only

| Feature | Status | Notes |
|---------|--------|-------|
| GX Accent Color Picker | Placeholder | Add `AccentColor` to settings model |
| GX Keyboard Sounds | Placeholder | Add `EnableKeySounds` bool |
| GX Resource Monitor | Placeholder | Add `EnableHibernation` bool |
| GX Cleaner | Placeholder | Menu item disabled |

---

# App Icon
The app icon for Yalb.Maeger specifically is in Yalb/Assests/Icon/Lambda512.1.ico

[ ] Implement into the app ASAP.

*Last updated: 2026-05-31*  
*Next step: Implement Section 2 (YalbLogger.cs) first, then pick bugs in Section 3.*

# List (Trackable TODO)

## Progress Tracker
- Total checklist items: 0
- Done: 0
- Remaining: 0


## Task Groups (checkboxes kept unchanged)

### [T0] Logging Infrastructure (Do First)
- (Section 2.1) Create `YalbLogger.cs`
- (Section 2.2) Add `[Log]` calls to every existing method
- (Section 2.3) Add runtime "Debug Log" panel (optional)

### [T1] Critical Bugs
- (BUG 1) Extra Tab on Startup (Partial Fix, Needs Confirm)
- (BUG 2) App is Slow
- (BUG 3) Remove Top Title Bar ("Origin — Name of tab")
- (BUG 4) Shortcut Conflicts

### [T2] Polish & Feel (Fix the "AI-y" Look)
- (4.1) Tab Design
- (4.2) Toolbar
- (4.3) Color Palette
- (4.4) Typography
- (4.5) Micro-Interactions

### [T3] Features (Minimal but Complete)
- (FEATURE 1) Sidebar Buttons
- (FEATURE 2) Tab Animations (CSS-only)
- (FEATURE 3) Favicons on Tabs
- (FEATURE 4) Bookmarks Bar

### [T4] Build & Verify Checklist
- (6) Run this after every change + manual regression tests

