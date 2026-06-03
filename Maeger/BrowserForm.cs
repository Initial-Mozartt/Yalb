using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Yalb;

public partial class BrowserForm : Form
{
    private SplashForm? _splashForm;
    private bool _browserReadyHandled = false;

    private const int ChromeHeight = 64;
    private const int FramelessResizeBorder = 8;
    private const int HiddenChromeDragStripHeight = 44;
    private const int MinSidebarWidth = 42;
    private const int MaxSidebarWidth = 400;
    private const int TopTabHeight = 34;

    private WebView2 _chromeWebView = null!;
    private Panel _chromePanel = null!;
    private Panel _contentPanel = null!;
    private Panel? _verticalTabPanel;
    private FlowLayoutPanel? _verticalTabList;
    private Panel? _topTabPanel;
    private FlowLayoutPanel? _topTabList;
    private Button? _sidebarToggleHandle;
    private Panel? _addressBarFloat;
    private TextBox? _addressBarInput;
    private readonly BrowserVariant _variant = BrowserVariant.Maeger;

    private CoreWebView2Environment? _chromeEnv;
    private CoreWebView2Environment? _contentEnv;

    private readonly Dictionary<int, WebView2> _tabWebViews = new();
    private readonly Dictionary<int, string> _pendingUrls = new();
    private readonly HashSet<int> _pinnedTabs = new();
    private readonly List<int> _tabOrder = new();
    private int _activeTabId = -1;
    private int _tabIdCounter = 0;

    private readonly string _chromeUiPath;
    private readonly string _userDataFolder;
    private bool _isNavigatingFromUI;
    private bool _isFullScreen;
    private bool _zenModeActive;
    private DebugLogForm? _debugLogForm;
    private bool _isResizingSidebar;
    private int _resizeStartX;
    private int _resizeStartWidth;
    private FormBorderStyle _borderStyleBeforeFullScreen = FormBorderStyle.Sizable;
    private FormWindowState _windowStateBeforeFullScreen = FormWindowState.Normal;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // Window message constants for frameless handling
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCACTIVATE = 0x0086;

    private const int HTNOWHERE = 0;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    // Constructor that receives the splash form (called from Program.cs)
    public BrowserForm(SplashForm splashForm)
        : this()
    {
        _splashForm = splashForm;
    }

    // Default constructor – used by Designer and called by the constructor above
    // Default constructor – used by Designer and called by the constructor above
    public BrowserForm()
    {
        _userDataFolder = Path.Combine(Application.StartupPath, "YalbUserData");
        _chromeUiPath = Path.Combine(Application.StartupPath, "chrome-ui", "index.html");
        InitializeComponent();

        try
        {
            var candidates = new[]
            {
                Path.Combine(Application.StartupPath, "Assets", "Icon", "Yalb.Maeger.ico"),
                Path.Combine(Application.StartupPath, "..", "..", "..", "Assets", "Icon", "Yalb.Maeger.ico"),
                Path.Combine(Application.StartupPath, "..", "Assets", "Icon", "Yalb.Maeger.ico"),
            };
            foreach (var icoPath in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(icoPath);
                    if (File.Exists(full))
                    {
                        this.Icon = new System.Drawing.Icon(full);
                        break;
                    }
                }
                catch { }
            }
        }
        catch { }

        // Start minimized so the splash is the only window visible
        this.WindowState = FormWindowState.Minimized;

        this.Shown += async (s, e) =>
        {
            await InitializeAsync();
            AdjustContentLayout();

            // Wait until the first page has actually loaded before closing the splash
            WaitForBrowserReady();
        };
    }

    private async void WaitForBrowserReady()
    {
        if (_variant == BrowserVariant.Maeger)
        {
            await Task.Delay(1500);
            OnBrowserReady();
            return;
        }

        // Safety timeout: force the browser to appear after 10 seconds no matter what
        var timeout = Task.Delay(10_000);
        var minDisplay = Task.Delay(1500);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            while (!_browserReadyHandled)
            {
                var activeWv = GetActiveWebView();
                if (activeWv?.CoreWebView2 != null)
                {
                    // Check if the page is already loaded
                    string state = await activeWv.CoreWebView2.ExecuteScriptAsync(
                        "document.readyState");
                    if (state?.Trim('"') == "complete")
                    {
                        ready.TrySetResult();
                        break;
                    }

                    // Subscribe to the NavigationCompleted event for the active tab
                    activeWv.CoreWebView2.NavigationCompleted += (s, e) =>
                    {
                        if (!_browserReadyHandled && e.IsSuccess)
                            ready.TrySetResult();
                    };
                    break; // Wait for the event or timeout
                }

                await Task.Delay(200);
            }

            await System.Threading.Tasks.Task.WhenAll(
                minDisplay,
                System.Threading.Tasks.Task.WhenAny(ready.Task, timeout));
        }
        catch
        {
            await minDisplay;
        }

        // If the page never finished loading, force the browser to appear
        if (!_browserReadyHandled)
            OnBrowserReady();
    }

    private void OnBrowserReady()
    {
        if (_browserReadyHandled) return;
        _browserReadyHandled = true;
        YalbLogger.Info("Splash dismissed; showing browser", nameof(BrowserForm));


        // Close the splash and show the browser
        _splashForm?.Close();
        _splashForm = null;
        this.WindowState = FormWindowState.Normal;
        this.Activate();
    }

    private void InitializeComponent()
    {
        Text = string.Empty;

        // Remove default control box — we'll draw our own chrome when frameless
        ControlBox = false;

        Size = new System.Drawing.Size(1400, 900);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
        KeyPreview = true;

        // --- Top Chrome UI ---
        _chromePanel = new Panel
        {
            Location = new System.Drawing.Point(0, 0),
            Size = new System.Drawing.Size(ClientSize.Width, ChromeHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = System.Drawing.Color.FromArgb(30, 30, 30),
            Padding = new Padding(0),
            Margin = new Padding(0)
        };

        _chromeWebView = new WebView2
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        _chromeWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30);
        _chromePanel.Controls.Add(_chromeWebView);
        Controls.Add(_chromePanel);

        // --- Content Panel ---
        _contentPanel = new Panel
        {
            Location = new System.Drawing.Point(0, ChromeHeight),
            Size = new System.Drawing.Size(ClientSize.Width, ClientSize.Height - ChromeHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = System.Drawing.Color.FromArgb(30, 30, 30),
            BorderStyle = BorderStyle.None,
            Padding = new Padding(0),
            Margin = new Padding(0),
            AutoScroll = false
        };
        Controls.Add(_contentPanel);

        if (_variant == BrowserVariant.Maeger)
        {
            _chromePanel.Visible = true;
            InitializeVerticalTabs();
            InitializeTopTabBar();
            InitializeFloatingAddressBar();
        }

        // Bring content panel to front after chrome/sidebar controls are created.
        _contentPanel.BringToFront();

        _contentPanel.Resize += (s, e) => SnapActiveWebView();
        Resize += (s, e) => AdjustContentLayout();
        MouseDown += BrowserForm_MouseDown;
        KeyPress += BrowserForm_KeyPress;
    }

    

    private void InitializeVerticalTabs()
    {
        _verticalTabPanel = new Panel
        {
            BackColor = Color.FromArgb(24, 24, 27),
            Width = GetSidebarWidth()
        };
        _verticalTabPanel.MouseDown += Sidebar_MouseDown;
        _verticalTabPanel.MouseMove += Sidebar_MouseMove;
        _verticalTabPanel.MouseUp += Sidebar_MouseUp;

        _verticalTabList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(4, 8, 4, 6),
            BackColor = Color.FromArgb(24, 24, 27)
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 210,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4),
            BackColor = Color.FromArgb(24, 24, 27)
        };

        actions.Controls.Add(CreateRailButton("⚙", "Settings", () => NavigateActiveTab("yalb://settings")));
        actions.Controls.Add(CreateRailButton("☆", "Bookmarks", () => NavigateActiveTab("yalb://bookmarks")));
        actions.Controls.Add(CreateRailSpacer(58));
        actions.Controls.Add(CreateRailButton("+", "New Tab", () => AddNewTab("yalb://newtab", activate: true)));
        actions.Controls.Add(CreateRailSpacer(58));
        actions.Controls.Add(CreateRailButton("◷", "History", () => NavigateActiveTab("yalb://history")));
        actions.Controls.Add(CreateRailButton("⇩", "Downloads", () => NavigateActiveTab("yalb://downloads")));

        _verticalTabPanel.Controls.Add(_verticalTabList);
        _verticalTabPanel.Controls.Add(actions);
        Controls.Add(_verticalTabPanel);
        _verticalTabPanel.BringToFront();

        _sidebarToggleHandle = CreateRailButton("=", "Toggle Sidebar", ToggleSidebar);
        _sidebarToggleHandle.Width = 30;
        _sidebarToggleHandle.Visible = true;
        Controls.Add(_sidebarToggleHandle);
        _sidebarToggleHandle.BringToFront();
    }

    private void InitializeTopTabBar()
    {
        _topTabPanel = new Panel
        {
            BackColor = Color.FromArgb(18, 18, 18),
            Height = TopTabHeight
        };

        _topTabList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(42, 3, 6, 3),
            BackColor = Color.FromArgb(18, 18, 18)
        };

        _topTabPanel.Controls.Add(_topTabList);
        Controls.Add(_topTabPanel);
        _topTabPanel.BringToFront();
    }

    private void InitializeFloatingAddressBar()
    {
        _addressBarFloat = new Panel
        {
            BackColor = Color.FromArgb(18, 18, 18),
            Height = 44,
            Visible = false
        };

        _addressBarInput = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(26, 26, 26),
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 10f),
            Location = new Point(10, 10),
            Width = 520
        };
        _addressBarInput.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                NavigateActiveTab(_addressBarInput.Text);
                _addressBarFloat.Visible = false;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                _addressBarFloat.Visible = false;
                e.SuppressKeyPress = true;
            }
        };

        _addressBarFloat.Controls.Add(_addressBarInput);
        Controls.Add(_addressBarFloat);
        _addressBarFloat.BringToFront();
    }

    private static Button CreateRailButton(string text, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            Width = 40,
            Height = 40,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(24, 24, 27),
            ForeColor = Color.Gainsboro,
            TabStop = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(48, 48, 53);
        button.Click += (s, e) => onClick();
        new ToolTip().SetToolTip(button, tooltip);
        return button;
    }

    private static Control CreateRailSpacer(int height)
    {
        return new Panel
        {
            Width = 40,
            Height = height,
            BackColor = Color.FromArgb(24, 24, 27),
            Margin = Padding.Empty
        };
    }

    private void UpdateVerticalTabs()
    {
        if (_verticalTabList == null && _topTabList == null) return;

        string tabPosition = YalbSettings.Instance.TabPosition;
        bool useSidebarTabs = string.Equals(tabPosition, "sidebar", StringComparison.OrdinalIgnoreCase);
        bool useTopOrBottomTabs = !useSidebarTabs && !_chromePanel.Visible;

        if (_verticalTabList != null)
            _verticalTabList.Visible = useSidebarTabs;
        if (_topTabPanel != null)
            _topTabPanel.Visible = useTopOrBottomTabs;

        var targetList = useSidebarTabs ? _verticalTabList : _topTabList;
        if (targetList == null) return;

        targetList.SuspendLayout();
        targetList.Controls.Clear();

        foreach (int id in _tabOrder)
        {
            string title = _tabWebViews.TryGetValue(id, out var webView)
                ? webView.CoreWebView2?.DocumentTitle ?? webView.CoreWebView2?.Source ?? "Untitled"
                : "Untitled";

            var row = new Panel
            {
                Width = useSidebarTabs ? Math.Max(28, GetSidebarWidth() - 20) : 170,
                Height = 28,
                BackColor = id == _activeTabId ? Color.FromArgb(35, 35, 35) : Color.FromArgb(15, 15, 15),
                Margin = new Padding(0, 0, 4, 0)
            };

            var tabButton = new Button
            {
                Text = useSidebarTabs && !YalbSettings.Instance.SidebarShowLabels ? string.Empty : string.IsNullOrWhiteSpace(title) ? "Untitled" : title,
                TextAlign = ContentAlignment.MiddleLeft,
                Width = row.Width - 32,
                Height = 26,
                Location = new Point(0, 1),
                FlatStyle = FlatStyle.Flat,
                BackColor = row.BackColor,
                ForeColor = Color.Gainsboro,
                TabStop = false
            };
            tabButton.FlatAppearance.BorderSize = 0;
            tabButton.Click += (s, e) => SwitchToTab(id);

            var closeButton = new Button
            {
                Text = "x",
                Width = 26,
                Height = 26,
                Location = new Point(row.Width - 26, 1),
                FlatStyle = FlatStyle.Flat,
                BackColor = row.BackColor,
                ForeColor = Color.Gray,
                TabStop = false
            };
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.Click += (s, e) => CloseTabById(id);

            row.Controls.Add(tabButton);
            row.Controls.Add(closeButton);
            targetList.Controls.Add(row);
        }

        targetList.ResumeLayout();
    }

    private void LayoutFloatingAddressBar()
    {
        if (_addressBarFloat == null || _addressBarInput == null) return;

        int width = Math.Min(560, Math.Max(280, ClientSize.Width - 80));
        _addressBarFloat.SetBounds(
            Math.Max(GetSidebarWidth() + 20, (ClientSize.Width - width) / 2),
            24,
            width,
            44);
        _addressBarInput.Width = width - 20;
    }

    private int GetSidebarWidth()
    {
        return Math.Clamp(YalbSettings.Instance.SidebarWidth, MinSidebarWidth, MaxSidebarWidth);
    }

    private async System.Threading.Tasks.Task EnsureStartupTabAsync()
    {
        await Task.Delay(500);
        if (_tabOrder.Count > 0) return;

        var settings = YalbSettings.Instance;
        string startUrl = settings.HomePageUrl;
        if (string.IsNullOrWhiteSpace(startUrl))
        {
            startUrl = "yalb://newtab";
            settings.HomePageUrl = startUrl;
            settings.Save();
        }

        AddNewTab(startUrl, activate: true);
    }

    private void BrowserForm_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_variant == BrowserVariant.Maeger && !_chromePanel.Visible && e.Y <= 50)
        {
            ShowFloatingAddressBar();
            return;
        }

        // Hide floating address bar when clicking outside
        if (_addressBarFloat != null && _addressBarFloat.Visible)
        {
            var mousePoint = e.Location;
            if (!_addressBarFloat.Bounds.Contains(mousePoint))
            {
                _addressBarFloat.Visible = false;
                GetActiveWebView()?.Focus();
            }
        }
    }


    private void BrowserForm_KeyPress(object? sender, KeyPressEventArgs e)
    {
        if (_variant != BrowserVariant.Maeger || char.IsControl(e.KeyChar)) return;
        if (_addressBarInput?.Focused == true) return;
        if (ActiveControl is TextBox) return;

        ShowFloatingAddressBar(e.KeyChar.ToString());
        e.Handled = true;
    }

    private void ShowFloatingAddressBar(string? seed = null)
    {
        if (_addressBarFloat == null || _addressBarInput == null) return;
        if (!YalbSettings.Instance.FloatingAddressBar) return;

        LayoutFloatingAddressBar();
        _addressBarInput.Text = seed ?? GetActiveWebView()?.CoreWebView2?.Source ?? string.Empty;
        _addressBarFloat.Visible = true;
        _addressBarFloat.BringToFront();
        _addressBarInput.Focus();
        _addressBarInput.SelectionStart = _addressBarInput.Text.Length;
        _addressBarInput.SelectionLength = 0;
    }

    private void ToggleSidebar()
    {
        if (_verticalTabPanel == null || _sidebarToggleHandle == null) return;

        bool show = !_verticalTabPanel.Visible;
        _verticalTabPanel.Visible = show;
        _sidebarToggleHandle.Visible = true;
        _sidebarToggleHandle.Text = show ? "=" : ">";
        YalbSettings.Instance.ShowSidebar = show;
        YalbSettings.Instance.Save();
        AdjustContentLayout();
    }

    private void Sidebar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_verticalTabPanel == null || e.X < _verticalTabPanel.Width - 5) return;

        _isResizingSidebar = true;
        _resizeStartX = e.X;
        _resizeStartWidth = _verticalTabPanel.Width;
        _verticalTabPanel.Cursor = Cursors.SizeWE;
    }

    private void Sidebar_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_verticalTabPanel == null) return;

        if (_isResizingSidebar)
        {
            int newWidth = Math.Clamp(_resizeStartWidth + (e.X - _resizeStartX), MinSidebarWidth, MaxSidebarWidth);
            _verticalTabPanel.Width = newWidth;
            YalbSettings.Instance.SidebarWidth = newWidth;
            AdjustContentLayout();
            UpdateVerticalTabs();
            return;
        }

        _verticalTabPanel.Cursor = e.X >= _verticalTabPanel.Width - 5 ? Cursors.SizeWE : Cursors.Default;
    }

    private void Sidebar_MouseUp(object? sender, MouseEventArgs e)
    {
        if (!_isResizingSidebar) return;

        _isResizingSidebar = false;
        if (_verticalTabPanel != null)
            YalbSettings.Instance.SidebarWidth = _verticalTabPanel.Width;
        YalbSettings.Instance.Save();
    }

    private async System.Threading.Tasks.Task InitializeAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        YalbLogger.Info("InitializeAsync started", nameof(BrowserForm));
        try
        {
            YalbLogger.Info($"Variant={_variant}, userDataFolder={_userDataFolder}", nameof(BrowserForm));

            var options = new CoreWebView2EnvironmentOptions(
                additionalBrowserArguments:
                    "--disable-overscroll-scroll-edge-effects " +
                    "--disable-features=ElasticOverscroll,PullToRefresh");

            var envSw = System.Diagnostics.Stopwatch.StartNew();
            _chromeEnv = await CoreWebView2Environment.CreateAsync(
                null,
                Path.Combine(_userDataFolder, "Chrome"),
                options);
            envSw.Stop();
            YalbLogger.Info($"Chrome environment created in {envSw.ElapsedMilliseconds}ms", nameof(BrowserForm));

            envSw.Restart();
            _contentEnv = await CoreWebView2Environment.CreateAsync(
                null,
                Path.Combine(_userDataFolder, "Content"),
                options);
            envSw.Stop();
            YalbLogger.Info($"Content environment created in {envSw.ElapsedMilliseconds}ms", nameof(BrowserForm));

            envSw.Restart();
            await _chromeWebView.EnsureCoreWebView2Async(_chromeEnv);
            envSw.Stop();
            YalbLogger.Info($"Chrome WebView2 initialized in {envSw.ElapsedMilliseconds}ms", nameof(BrowserForm));

            _chromeWebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

            _chromeWebView.CoreWebView2.Navigate(_chromeUiPath);
            _chromeWebView.CoreWebView2.WebMessageReceived += ChromeWebView_WebMessageReceived;

            _chromeWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _chromeWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            var settings = YalbSettings.Instance;
            if (_variant == BrowserVariant.Maeger && _verticalTabPanel != null && _sidebarToggleHandle != null)
            {
                _verticalTabPanel.Width = GetSidebarWidth();
                _verticalTabPanel.Visible = settings.ShowSidebar;
                _sidebarToggleHandle.Visible = true;
                _sidebarToggleHandle.Text = settings.ShowSidebar ? "=" : ">";
                if (_topTabPanel != null)
                    _topTabPanel.Visible = !string.Equals(settings.TabPosition, "sidebar", StringComparison.OrdinalIgnoreCase);
                if (_verticalTabList != null)
                    _verticalTabList.Visible = string.Equals(settings.TabPosition, "sidebar", StringComparison.OrdinalIgnoreCase);
            }

            var sessionSw = System.Diagnostics.Stopwatch.StartNew();
            if (settings.RestoreLastSession && settings.LastSessionTabs.Count > 0)
            {
                YalbLogger.Info($"RestoreLastSession=true, restoring {settings.LastSessionTabs.Count} tabs", nameof(BrowserForm));
                int activeIndex = Math.Clamp(settings.ActiveTabIndex, 0, settings.LastSessionTabs.Count - 1);
                for (int i = 0; i < settings.LastSessionTabs.Count; i++)
                {
                    AddNewTab(settings.LastSessionTabs[i].Url, activate: i == activeIndex);
                    if (settings.LastSessionTabs[i].Pinned)
                        _pinnedTabs.Add(_tabIdCounter - 1);
                }
            }
            else
            {
                YalbLogger.Info($"RestoreLastSession=false or no saved tabs, creating new tab", nameof(BrowserForm));
                string startUrl = settings.HomePageUrl;
                if (string.IsNullOrWhiteSpace(startUrl))
                {
                    startUrl = "yalb://newtab";
                    settings.HomePageUrl = startUrl;
                    settings.Save();
                }
                AddNewTab(startUrl, activate: true);
            }
            sessionSw.Stop();
            YalbLogger.Info($"Session tabs restored in {sessionSw.ElapsedMilliseconds}ms", nameof(BrowserForm));

            // Apply frameless setting (title bar only)
            if (settings.FramelessWindow)
            {
                YalbLogger.Info($"Applying frameless window state", nameof(BrowserForm));
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                SetContentFramelessState(true);
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                SetContentFramelessState(false);
            }

            // Chrome visibility is independent of frameless
            ApplyChromeVisibility(settings.ChromeVisible, save: false);
            ApplyZenMode(settings.ZenModeActive, save: false);

            Invalidate();
            Refresh();

            // Prevent an extra startup tab if we already restored/created tabs above.
            // EnsureStartupTabAsync() is a fallback, not something we always want to run.
            if (_tabOrder.Count == 0 &&
                !(_variant == BrowserVariant.Maeger && settings.RestoreLastSession && settings.LastSessionTabs.Count > 0))
            {
                YalbLogger.Warn($"No tabs created, running EnsureStartupTabAsync as fallback", nameof(BrowserForm));
                _ = EnsureStartupTabAsync();
            }
        }
        catch (Exception ex)
        {
            YalbLogger.Error(nameof(BrowserForm), ex);
            MessageBox.Show($"Failed to initialize Yalb: {ex.Message}", "Yalb Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
        }
        finally
        {
            sw.Stop();
            YalbLogger.Info($"InitializeAsync completed in {sw.ElapsedMilliseconds}ms", nameof(BrowserForm));
            try { YalbLogger.Info($"=== YALB INIT END === {sw.ElapsedMilliseconds}ms"); } catch { }
        }
    }

    // … every other method from your original file (SnapActiveWebView through IsLikelyUrl)
    // stays exactly as you pasted them. They are unchanged, so I am not repeating them here.
    // The only changes are above: constructors merged, old Load handler removed, _loadingLabel removed.
    // ---------------------------------------------------------------------
    // Layout helper: force the active WebView2 to exactly fill the panel.
    // ---------------------------------------------------------------------
    private void SnapActiveWebView()
    {
        if (_activeTabId != -1 && _tabWebViews.TryGetValue(_activeTabId, out var wv))
        {
            // ClientRectangle is (0,0, ClientSize.Width, ClientSize.Height).
            // This guarantees zero gap and no viewport offset.
            wv.Bounds = _contentPanel.ClientRectangle;
        }
    }

    // ---------------------------------------------------------------------
    // Shortcut Injection (Ctrl+T/W/L/etc. from inside any page)
    // ---------------------------------------------------------------------
    private void InjectShortcutHandler(WebView2 webView)
    {
        const string script = @"
(function() {
    if (window.__yalbAccel) return;
    window.__yalbAccel = true;
    if (typeof window.__yalbTrueFrameless === 'undefined') window.__yalbTrueFrameless = false;

    document.addEventListener('keydown', function(e) {
        if (!e.ctrlKey) return;

        var action = null;
        switch (e.key) {
            case 'f': case 'F':
                if (e.altKey) {
                    if (e.shiftKey) action = 'fullFrameless';
                    else action = 'toggleFrameless';
                }
                break;
            case 'Tab':
                if (e.shiftKey) action = 'prevTab';
                else action = 'nextTab';
                break;
            case 't': case 'T':
                if (!e.shiftKey) action = 'newTab';
                break;
            case 'f': case 'F':
                if (e.altKey) action = 'toggleFrameless';
                break;
            case 'b': case 'B':
                if (e.shiftKey) action = 'toggleChromeVisibility';
                break;
            case 'w': case 'W': action = 'closeTab'; break;
            case 'l': case 'L': action = 'focusAddressBar'; break;
            case 'r': case 'R': action = 'reload'; break;
            case 'h': case 'H': action = 'home'; break;
            case 'ArrowLeft':  action = 'goBack'; break;
            case 'ArrowRight': action = 'goForward'; break;
            case '[': action = 'goBack'; break;
            case ']': action = 'goForward'; break;
        }

        if (action && window.chrome && window.chrome.webview) {
            e.preventDefault();
            e.stopImmediatePropagation();
            window.chrome.webview.postMessage({ type: action });
        }
    }, true);

    document.addEventListener('mousedown', function(e) {
        if (!window.__yalbTrueFrameless) return;
        if (e.button !== 0 || !window.chrome || !window.chrome.webview) return;

        var edge = 8;
        var dragStrip = 44;
        var x = e.clientX;
        var y = e.clientY;
        var w = window.innerWidth;
        var h = window.innerHeight;
        var left = x >= 0 && x < edge;
        var right = x <= w && x >= w - edge;
        var top = y >= 0 && y < edge;
        var bottom = y <= h && y >= h - edge;
        var hit = 0;

        if (top && left) hit = 13;
        else if (top && right) hit = 14;
        else if (bottom && left) hit = 16;
        else if (bottom && right) hit = 17;
        else if (left) hit = 10;
        else if (right) hit = 11;
        else if (top) hit = 12;
        else if (bottom) hit = 15;

        if (hit) {
            e.preventDefault();
            e.stopImmediatePropagation();
            window.chrome.webview.postMessage({ type: 'beginWindowResize', hitTest: hit });
            return;
        }

        if (y >= edge && y < dragStrip) {
            e.preventDefault();
            e.stopImmediatePropagation();
            window.chrome.webview.postMessage({ type: 'beginWindowDrag' });
        }
    }, true);
})();
";
        webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    private void ContentWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(message)) return;

            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "fullFrameless":
                    BeginInvoke(() => FullFramelessToggle());
                    break;
                case "nextTab":
                    BeginInvoke(() => { NextTab(); });
                    break;
                case "prevTab":
                    BeginInvoke(() => { PrevTab(); });
                    break;
                case "newTab":
                    BeginInvoke(() => AddNewTab(YalbSettings.Instance.HomePageUrl, activate: true));
                    break;
                case "closeTab":
                    BeginInvoke(() => CloseActiveTab());
                    break;
                case "focusAddressBar":
                    BeginInvoke(() => FocusAddressBar());
                    break;
                case "reload":
                    BeginInvoke(() => GetActiveWebView()?.CoreWebView2?.Reload());
                    break;
                case "home":
                    BeginInvoke(() => NavigateActiveTab(YalbSettings.Instance.HomePageUrl));
                    break;
                case "goBack":
                    BeginInvoke(() => GetActiveWebView()?.CoreWebView2?.GoBack());
                    break;
                case "goForward":
                    BeginInvoke(() => GetActiveWebView()?.CoreWebView2?.GoForward());
                    break;
                case "toggleFrameless":
                    BeginInvoke(() => ToggleFrameless());
                    break;
                case "toggleChromeVisibility":
                    BeginInvoke(() => ToggleChromeVisibility());
                    break;
                case "togglePinTab":
                    BeginInvoke(() => TogglePinActiveTab());
                    break;
                case "revealZenChrome":
                    BeginInvoke(() => RevealZenChrome());
                    break;
                case "revealZenSidebar":
                    BeginInvoke(() => RevealZenSidebar());
                    break;
                case "hideZenOverlays":
                    BeginInvoke(() => HideZenOverlays());
                    break;
                case "minimize":
                    BeginInvoke(() => WindowState = FormWindowState.Minimized);
                    break;
                case "maximize":
                    BeginInvoke(() => ToggleMaximize());
                    break;
                case "closeWindow":
                    BeginInvoke(Close);
                    break;
                case "beginWindowDrag":
                    BeginInvoke(() => BeginWindowDrag());
                    break;
                case "beginWindowResize":
                    int contentHitTest = GetInt(root, "hitTest");
                    BeginInvoke(() => BeginWindowResize(contentHitTest));
                    break;
                case "loadSettings":
                    BeginInvoke(() => _ = PushSettingsToActiveTabAsync());
                    break;
                case "saveSettings":
                    string homepage = GetString(root, "homepage");
                    string searchEngine = GetString(root, "searchEngine");
                    bool restoreSession = GetBool(root, "restoreSession");
                    bool frameless = GetBool(root, "frameless");
                    bool showSidebar = GetBool(root, "showSidebar");
                    bool floatingAddressBar = GetBool(root, "floatingAddressBar");
                    bool blockTrackers = GetBool(root, "blockTrackers");
                    bool blockAds = GetBool(root, "blockAds");
                    bool hardwareAcceleration = GetBool(root, "hardwareAcceleration");
                    string theme = GetString(root, "theme");
                    string downloadPath = GetString(root, "downloadPath");
                    string tabPosition = GetString(root, "tabPosition");
                    int sidebarWidth = GetInt(root, "sidebarWidth");
                    bool sidebarShowLabels = GetBool(root, "sidebarLabels");
                    BeginInvoke(() => SaveSettingsFromContent(
                        homepage, searchEngine, restoreSession, frameless,
                        showSidebar, floatingAddressBar, blockTrackers, blockAds,
                        hardwareAcceleration, theme, downloadPath,
                        tabPosition, sidebarWidth, sidebarShowLabels));
                    break;
                case "loadHistory":
                    BeginInvoke(() => _ = PushHistoryToActiveTabAsync());
                    break;
                case "loadDownloads":
                    BeginInvoke(() => _ = PushDownloadsToActiveTabAsync());
                    break;
                case "clearDownloads":
                    BeginInvoke(() =>
                    {
                        YalbSettings.Instance.Downloads.Clear();
                        YalbSettings.Instance.Save();
                        _ = PushDownloadsToActiveTabAsync();
                    });
                    break;
                case "clearHistory":
                    BeginInvoke(() =>
                    {
                        YalbSettings.Instance.History.Clear();
                        YalbSettings.Instance.Save();
                        _ = PushHistoryToActiveTabAsync();
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Content shortcut error: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------
    // Tab Management (Panel-based, explicit Bounds — no Dock on WebView2s)
    // ---------------------------------------------------------------------
    private async void AddNewTab(string url, bool activate = false)
    {
        YalbLogger.Debug($"AddNewTab called: url={url}, activate={activate}, currentTabCount={_tabOrder.Count}", nameof(BrowserForm));
        // Do NOT use Dock=Fill. Explicit Bounds prevents the 1-pixel white seam
        // and viewport offset that causes scroll bounce / hidden top.
        var webView = new WebView2
        {
            Visible = false,
            Margin = new Padding(0),
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30)
        };

        _contentPanel.Controls.Add(webView);

        // Size immediately so the first page loads with the correct viewport.
        webView.Bounds = _contentPanel.ClientRectangle;

        var tabId = _tabIdCounter++;
        await webView.EnsureCoreWebView2Async(_contentEnv);

        // Dark scrollbars for web content
        webView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

        InjectShortcutHandler(webView);
        webView.CoreWebView2.NewWindowRequested += (s, e) =>
        {
            e.Handled = true;
            BeginInvoke(() => AddNewTab(e.Uri, activate: true));
        };
        webView.CoreWebView2.WebMessageReceived += ContentWebView_WebMessageReceived;

        // Hook download starting to record downloads into settings and notify UI
        webView.CoreWebView2.DownloadStarting += (s, e) =>
        {
            try
            {
                var op = e.DownloadOperation;
                var entry = new DownloadEntry
                {
                    Url = op.Uri ?? string.Empty,
                    Filename = Path.GetFileName(op.ResultFilePath ?? string.Empty),
                    BytesReceived = op.BytesReceived,
                    TotalBytes = op.TotalBytesToReceive > 0 ? (long?)op.TotalBytesToReceive : null,
                    StartedAt = DateTime.UtcNow,
                    Status = "Starting"
                };

                YalbSettings.Instance.Downloads.Insert(0, entry);
                YalbSettings.Instance.Save();
                _ = PushDownloadsToActiveTabAsync();

                // Update entry progress on bytes changed
                op.BytesReceivedChanged += (ss, ee) =>
                {
                    try
                    {
                        entry.BytesReceived = op.BytesReceived;
                        entry.TotalBytes = op.TotalBytesToReceive > 0 ? (long?)op.TotalBytesToReceive : null;
                        entry.Status = op.State == CoreWebView2DownloadState.InProgress ? "Downloading" : entry.Status;
                        YalbSettings.Instance.Save();
                        _ = PushDownloadsToActiveTabAsync();
                    }
                    catch { }
                };

                op.StateChanged += (ss, ee) =>
                {
                    try
                    {
                        entry.Status = op.State.ToString();
                        YalbSettings.Instance.Save();
                        _ = PushDownloadsToActiveTabAsync();
                    }
                    catch { }
                };
            }
            catch (Exception ex)
            {
                YalbLogger.Error("DownloadStarting handler", ex);
            }
        };

        // Inject overscroll fix + Vim scroll script
        await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GetPageInjectionScript());

        webView.CoreWebView2.SourceChanged += (s, e) =>
        {
            if (_activeTabId == tabId && !_isNavigatingFromUI)
                Invoke(() => _ = UpdateChromeUIAsync());
        };

        webView.CoreWebView2.DocumentTitleChanged += (s, e) =>
        {
            Invoke(() => _ = UpdateChromeUIAsync());
        };

        webView.CoreWebView2.NavigationCompleted += (s, e) =>
        {
            _ = webView.ExecuteScriptAsync(
                $"window.__yalbTrueFrameless = {(FormBorderStyle == FormBorderStyle.None && !_isFullScreen ? "true" : "false")};");

            if (e.IsSuccess && webView.CoreWebView2.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var url = webView.CoreWebView2.Source;
                var title = webView.CoreWebView2.DocumentTitle ?? string.Empty;
                YalbSettings.Instance.AddHistoryEntry(url, title);
            }
        };

        _tabWebViews[tabId] = webView;
        _tabOrder.Add(tabId);
        YalbLogger.Debug($"Tab {tabId} created, total tabs now: {_tabOrder.Count}", nameof(BrowserForm));

        bool shouldActivate = activate || _activeTabId == -1;
        if (shouldActivate)
            ActivateTab(tabId);
        else
            _pendingUrls[tabId] = url;

        webView.CoreWebView2.Navigate(shouldActivate ? ResolveInternalUrl(url) : "about:blank");
        _ = UpdateChromeUIAsync();
        SaveSession();
    }

    private void ActivateTab(int tabId)
    {
        if (_activeTabId == tabId) return;

        // Hide previous
        if (_activeTabId != -1 && _tabWebViews.TryGetValue(_activeTabId, out var oldView))
        {
            oldView.Visible = false;
            _ = oldView.CoreWebView2?.TrySuspendAsync();
        }

        // Show new — snap to exact panel bounds before making visible.
        if (_tabWebViews.TryGetValue(tabId, out var newView))
        {
            newView.Bounds = _contentPanel.ClientRectangle;
            newView.Visible = true;
            newView.BringToFront();
            newView.Focus();
            if (_pendingUrls.Remove(tabId, out var pendingUrl))
                newView.CoreWebView2.Navigate(ResolveInternalUrl(pendingUrl));
            else
                newView.CoreWebView2?.Resume();
        }

        _activeTabId = tabId;
        SaveSession();
    }

    private void CloseActiveTab()
    {
        if (_activeTabId != -1)
        {
            if (_pinnedTabs.Contains(_activeTabId)) return;
            CloseTabById(_activeTabId);
        }
    }

    private void CloseTabById(int tabId)
    {
        if (!_tabWebViews.TryGetValue(tabId, out var webView)) 
        {
            YalbLogger.Warn($"CloseTabById: tab {tabId} not found", nameof(BrowserForm));
            return;
        }

        YalbLogger.Debug($"CloseTabById: closing tab {tabId}, total tabs before: {_tabOrder.Count}", nameof(BrowserForm));
        _contentPanel.Controls.Remove(webView);
        webView.Dispose();
        _tabWebViews.Remove(tabId);
        _pendingUrls.Remove(tabId);
        _pinnedTabs.Remove(tabId);

        int index = _tabOrder.IndexOf(tabId);
        _tabOrder.Remove(tabId);

        if (_activeTabId == tabId)
        {
            _activeTabId = -1;
            if (_tabOrder.Count > 0)
            {
                int newIndex = Math.Max(0, index - 1);
                ActivateTab(_tabOrder[newIndex]);
            }
            else
            {
                YalbLogger.Info($"No tabs remaining, creating new home tab", nameof(BrowserForm));
                AddNewTab(YalbSettings.Instance.HomePageUrl, activate: true);
                return;
            }
        }

        YalbLogger.Debug($"Tab {tabId} closed, total tabs after: {_tabOrder.Count}", nameof(BrowserForm));
        _ = UpdateChromeUIAsync();
        SaveSession();
    }

    private void SwitchToTab(int tabId)
    {
        if (_tabOrder.Contains(tabId))
        {
            YalbLogger.Debug($"SwitchToTab: from {_activeTabId} to {tabId}", nameof(BrowserForm));
            ActivateTab(tabId);
            _ = UpdateChromeUIAsync();
        }
        else
        {
            YalbLogger.Warn($"SwitchToTab: tab {tabId} not found in order", nameof(BrowserForm));
        }
    }

    private void NextTab()
    {
        if (_tabOrder.Count == 0) return;
        int currentIndex = _tabOrder.IndexOf(_activeTabId);
        int nextIndex = (currentIndex + 1) % _tabOrder.Count;
        ActivateTab(_tabOrder[nextIndex]);
        _ = UpdateChromeUIAsync();
    }

    private void PrevTab()
    {
        if (_tabOrder.Count == 0) return;
        int currentIndex = _tabOrder.IndexOf(_activeTabId);
        int prevIndex = (currentIndex - 1 + _tabOrder.Count) % _tabOrder.Count;
        ActivateTab(_tabOrder[prevIndex]);
        _ = UpdateChromeUIAsync();
    }

    private WebView2? GetActiveWebView()
    {
        if (_activeTabId != -1 && _tabWebViews.TryGetValue(_activeTabId, out var webView))
            return webView;
        return null;
    }

    private void NavigateActiveTab(string input)
    {
        var webView = GetActiveWebView();
        if (webView?.CoreWebView2 == null) return;

        if (input.StartsWith("yalb://", StringComparison.OrdinalIgnoreCase))
        {
            string pageName = input.Replace("yalb://", "", StringComparison.OrdinalIgnoreCase);
            string pagePath = Path.Combine(Application.StartupPath, "pages", pageName + ".html");
            if (File.Exists(pagePath))
                webView.CoreWebView2.Navigate(new Uri(pagePath).AbsoluteUri);
            return;
        }

        string url;
        if (IsLikelyUrl(input))
        {
            // Direct navigation
            if (!input.Contains("://") && !input.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                url = "https://" + input;
            else
                url = input;
        }
        else
        {
            // Omnibox search -> Google
            string query = Uri.EscapeDataString(input);
            url = $"https://www.google.com/search?q={query}";
        }

        _isNavigatingFromUI = true;
        webView.CoreWebView2.Navigate(url);
        _isNavigatingFromUI = false;
        _ = UpdateChromeUIAsync();
    }

    private static string ResolveInternalUrl(string input)
    {
        if (input.StartsWith("yalb://", StringComparison.OrdinalIgnoreCase))
        {
            string pageName = input.Replace("yalb://", "", StringComparison.OrdinalIgnoreCase);
                // If the startpage build was included in output, prefer it for the newtab/home page.
                if (string.Equals(pageName, "newtab", StringComparison.OrdinalIgnoreCase) || string.Equals(pageName, "home", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer local http server if running so browser features work correctly
                    if (StartpageServer.IsRunning)
                    {
                        return StartpageServer.BaseUrl + "/";
                    }

                    string startpageIndex = Path.Combine(Application.StartupPath, "startpage", "dist", "index.html");
                    if (File.Exists(startpageIndex))
                        return new Uri(startpageIndex).AbsoluteUri;
                }

                string pagePath = Path.Combine(Application.StartupPath, "pages", pageName + ".html");
                if (File.Exists(pagePath))
                    return new Uri(pagePath).AbsoluteUri;
        }

        return input;
    }

    // ---------------------------------------------------------------------
    // Chrome UI Communication
    // ---------------------------------------------------------------------
    private async System.Threading.Tasks.Task UpdateChromeUIAsync()
    {
        try
        {
            var webView = GetActiveWebView();
            if (webView?.CoreWebView2 == null) 
            {
                YalbLogger.Debug($"UpdateChromeUIAsync: no active webview", nameof(BrowserForm));
                return;
            }

            var url = webView.CoreWebView2.Source;
            var canGoBack = webView.CoreWebView2.CanGoBack;
            var canGoForward = webView.CoreWebView2.CanGoForward;
            var title = webView.CoreWebView2.DocumentTitle ?? "Untitled";

            var tabs = _tabOrder.Select(id => new
            {
                id,
                title = _tabWebViews.TryGetValue(id, out var wv)
                    ? (wv.CoreWebView2?.DocumentTitle ?? "Untitled")
                    : "Untitled",
                faviconUrl = GetFaviconUrl(id),
                pinned = _pinnedTabs.Contains(id),
                active = _activeTabId == id
            }).ToList();

            var payload = new { type = "state", url, canGoBack, canGoForward, title, tabs };
            var json = JsonSerializer.Serialize(payload);
            YalbLogger.Debug($"UpdateChromeUIAsync: posting state with {tabs.Count} tabs, payload size: {json.Length} bytes", nameof(BrowserForm));

            _chromeWebView.CoreWebView2?.PostWebMessageAsJson(json);
            BeginInvoke(() => UpdateVerticalTabs());

            // Update window title
            var currentTitle = _tabWebViews.TryGetValue(_activeTabId, out var activeWv)
                ? (activeWv.CoreWebView2?.DocumentTitle ?? "Untitled")
                : "Untitled";
            BeginInvoke(() => { Text = currentTitle == "Untitled" ? "Yalb" : $"Yalb — {currentTitle}"; });
        }
        catch (Exception ex)
        {
            YalbLogger.Error(nameof(BrowserForm), ex);
        }
    }

    private string GetFaviconUrl(int tabId)
    {
        try
        {
            if (_tabWebViews.TryGetValue(tabId, out var wv) && wv?.CoreWebView2 != null)
            {
                // Try to get favicon URI from WebView2
                var source = wv.CoreWebView2.Source;
                if (!string.IsNullOrEmpty(source) && Uri.TryCreate(source, UriKind.Absolute, out var uri))
                {
                    // Try standard favicon locations
                    return $"https://{uri.Host}/favicon.ico";
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private void ChromeWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(message)) return;

            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "fullFrameless":
                    BeginInvoke(() => FullFramelessToggle());
                    break;
                case "nextTab":
                    BeginInvoke(() => { NextTab(); });
                    break;
                case "prevTab":
                    BeginInvoke(() => { PrevTab(); });
                    break;
                case "navigate":
                    var url = root.GetProperty("url").GetString() ?? "";
                    NavigateActiveTab(url);
                    break;

                case "newTab":
                    var newUrl = root.TryGetProperty("url", out var nu) ? nu.GetString() : "yalb://newtab";
                    AddNewTab(newUrl ?? "yalb://newtab", activate: true);
                    break;

                case "closeTab":
            if (root.TryGetProperty("tabId", out var ctId))
            {
                if (_pinnedTabs.Contains(ctId.GetInt32())) return;
                CloseTabById(ctId.GetInt32());
            }
            else
                CloseActiveTab();
                    break;

                case "switchTab":
                    if (root.TryGetProperty("tabId", out var stId))
                        SwitchToTab(stId.GetInt32());
                    break;

                case "goBack":
                    GetActiveWebView()?.CoreWebView2?.GoBack();
                    break;

                case "goForward":
                    GetActiveWebView()?.CoreWebView2?.GoForward();
                    break;

                case "reload":
                    GetActiveWebView()?.CoreWebView2?.Reload();
                    break;

                case "home":
                    NavigateActiveTab(YalbSettings.Instance.HomePageUrl);
                    break;

                case "focusAddressBar":
                    FocusAddressBar();
                    break;
                case "toggleFrameless":
                    BeginInvoke(() => ToggleFrameless());
                    break;
                case "toggleChromeVisibility":
                    BeginInvoke(() => ToggleChromeVisibility());
                    break;
                case "beginWindowDrag":
                    BeginInvoke(() => BeginWindowDrag());
                    break;
                case "beginWindowResize":
                    int chromeHitTest = GetInt(root, "hitTest");
                    BeginInvoke(() => BeginWindowResize(chromeHitTest));
                    break;
                case "showHistory":
                    BeginInvoke(() => NavigateActiveTab("yalb://history"));
                    break;
                case "reorderTab":
                    int fromId = GetInt(root, "fromId");
                    int toId = GetInt(root, "toId");
                    BeginInvoke(() => ReorderTab(fromId, toId));
                    break;
                case "minimize":
                    BeginInvoke(() => WindowState = FormWindowState.Minimized);
                    break;
                case "maximize":
                    BeginInvoke(() => ToggleMaximize());
                    break;
                case "closeWindow":
                    BeginInvoke(() => Close());
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Chrome message error: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task PushHistoryToActiveTabAsync()
    {
        var history = YalbSettings.Instance.History
            .Take(50)
            .Select(h => new { h.Url, h.Title, h.VisitedAt })
            .ToList();
        var payload = new { type = "historyData", entries = history };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        GetActiveWebView()?.CoreWebView2?.PostWebMessageAsJson(json);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task PushSettingsToActiveTabAsync()
    {
        var settings = YalbSettings.Instance;
        var payload = new
        {
            type = "settingsData",
            homepage = settings.HomePageUrl,
            searchEngine = settings.SearchEngineUrl,
            restoreSession = settings.RestoreLastSession,
            frameless = settings.FramelessWindow,
            showSidebar = settings.ShowSidebar,
            floatingAddressBar = settings.FloatingAddressBar,
            blockTrackers = settings.BlockTrackers,
            blockAds = settings.BlockAds,
            hardwareAcceleration = settings.HardwareAcceleration,
            theme = settings.Theme,
            downloadPath = settings.DownloadPath,
            tabPosition = settings.TabPosition,
            sidebarWidth = settings.SidebarWidth,
            sidebarLabels = settings.SidebarShowLabels
        };
        GetActiveWebView()?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        await System.Threading.Tasks.Task.CompletedTask;
    }

        private async System.Threading.Tasks.Task PushDownloadsToActiveTabAsync()
        {
            var downloads = YalbSettings.Instance.Downloads
                .Take(100)
                .Select(d => new { d.Url, d.Filename, d.BytesReceived, d.TotalBytes, d.StartedAt, d.Status })
                .ToList();
            var payload = new { type = "downloadsData", entries = downloads };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            GetActiveWebView()?.CoreWebView2?.PostWebMessageAsJson(json);
            await System.Threading.Tasks.Task.CompletedTask;
        }

    private void SaveSettingsFromContent(
        string homepage,
        string searchEngine,
        bool restoreSession,
        bool frameless,
        bool showSidebar,
        bool floatingAddressBar,
        bool blockTrackers,
        bool blockAds,
        bool hardwareAcceleration,
        string theme,
        string downloadPath,
        string tabPosition,
        int sidebarWidth,
        bool sidebarShowLabels)
    {
        var settings = YalbSettings.Instance;
        settings.HomePageUrl = string.IsNullOrWhiteSpace(homepage) ? "yalb://newtab" : homepage;
        settings.SearchEngineUrl = string.IsNullOrWhiteSpace(searchEngine) ? "https://www.google.com/search?q={0}" : searchEngine;
        settings.RestoreLastSession = restoreSession;
        settings.FramelessWindow = frameless;
        settings.ShowSidebar = showSidebar;
        settings.FloatingAddressBar = floatingAddressBar;
        settings.BlockTrackers = blockTrackers;
        settings.BlockAds = blockAds;
        settings.HardwareAcceleration = hardwareAcceleration;
        settings.Theme = string.IsNullOrWhiteSpace(theme) ? "dark" : theme;
        settings.DownloadPath = downloadPath;
        settings.TabPosition = string.IsNullOrWhiteSpace(tabPosition) ? "top" : tabPosition;
        settings.SidebarWidth = Math.Clamp(sidebarWidth <= 0 ? MinSidebarWidth : sidebarWidth, MinSidebarWidth, MaxSidebarWidth);
        settings.SidebarShowLabels = sidebarShowLabels;
        settings.Save();

        if (_verticalTabPanel != null && _sidebarToggleHandle != null)
        {
            _verticalTabPanel.Width = settings.SidebarWidth;
            _verticalTabPanel.Visible = showSidebar;
            _sidebarToggleHandle.Visible = true;
            _sidebarToggleHandle.Text = showSidebar ? "=" : ">";
            UpdateVerticalTabs();
            AdjustContentLayout();
        }

        if (frameless && FormBorderStyle != FormBorderStyle.None)
            ToggleFrameless();
        else if (!frameless && FormBorderStyle == FormBorderStyle.None)
            ToggleFrameless();
    }

    private void ReorderTab(int fromId, int toId)
    {
        int fromIndex = _tabOrder.IndexOf(fromId);
        int toIndex = _tabOrder.IndexOf(toId);
        if (fromIndex < 0 || toIndex < 0) return;

        _tabOrder.RemoveAt(fromIndex);
        _tabOrder.Insert(toIndex, fromId);
        _ = UpdateChromeUIAsync();
        SaveSession();
    }

    private void TogglePinActiveTab()
    {
        if (_activeTabId == -1) return;

        if (!_pinnedTabs.Add(_activeTabId))
            _pinnedTabs.Remove(_activeTabId);

        _ = UpdateChromeUIAsync();
        UpdateVerticalTabs();
        SaveSession();
    }

    // ---------------------------------------------------------------------
    // Window-level shortcuts & WndProc
    // ---------------------------------------------------------------------
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11)
        {
            YalbLogger.Debug($"ProcessCmdKey: F11 pressed (ToggleFullScreen)", nameof(BrowserForm));
            ToggleFullScreen();
            return true;
        }

        if (keyData == (Keys.Control | Keys.L))
        {
            YalbLogger.Debug($"ProcessCmdKey: Ctrl+L pressed (ToggleAddressBar)", nameof(BrowserForm));
            ToggleAddressBar();
            return true;
        }

        // Title bar only toggle
        // Title bar (frameless) only toggle
        // Full frameless / Zen toggle must be checked first to avoid conflicts.
        if (keyData == (Keys.Control | Keys.Shift | Keys.Alt | Keys.F))
        {
            YalbLogger.Debug($"ProcessCmdKey: Ctrl+Shift+Alt+F pressed (FullFramelessToggle)", nameof(BrowserForm));
            FullFramelessToggle();
            return true;
        }

        // Chrome panel visibility toggle
        if (keyData == (Keys.Control | Keys.Shift | Keys.B))
        {
            YalbLogger.Debug($"ProcessCmdKey: Ctrl+Shift+B pressed (ToggleChromeVisibility)", nameof(BrowserForm));
            ToggleChromeVisibility();
            return true;
        }

        // Title bar only toggle (no longer uses Ctrl+Alt+F in the Maeger spec)
        if (keyData == (Keys.Control | Keys.Alt | Keys.F))
        {
            YalbLogger.Debug($"ProcessCmdKey: Ctrl+Alt+F pressed (ToggleFrameless legacy: mapping removed by spec)", nameof(BrowserForm));
            // Intentionally do nothing; Maeger uses Ctrl+Shift+Alt+F.
            return true;
        }


        if (keyData == (Keys.Control | Keys.Shift | Keys.P))
        {
            YalbLogger.Debug($"ProcessCmdKey: Ctrl+Shift+P pressed (TogglePinActiveTab)", nameof(BrowserForm));
            TogglePinActiveTab();
            return true;
        }

        // Sidebar open/close shortcut (Maeger)
        if (keyData == (Keys.Control | Keys.Shift | Keys.S))
        {
            if (_variant == BrowserVariant.Maeger)
            {
                YalbLogger.Debug($"ProcessCmdKey: Ctrl+Shift+S pressed (ToggleSidebar)", nameof(BrowserForm));
                ToggleSidebar();
            }
            return true;
        }

        // Toggle debug log window with Ctrl+` (Oem3)
        if (keyData == (Keys.Control | Keys.Oem3))
        {
            BeginInvoke(() =>
            {
                try
                {
                    if (_debugLogForm == null || _debugLogForm.IsDisposed)
                        _debugLogForm = new DebugLogForm();

                    if (!_debugLogForm.Visible)
                        _debugLogForm.Show(this);
                    else
                        _debugLogForm.BringToFront();
                }
                catch (Exception ex)
                {
                    YalbLogger.Error("ShowDebugLog", ex);
                }
            });
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ToggleChromeVisibility()
    {
        // Allow toggling even when frameless
        ApplyChromeVisibility(!_chromePanel.Visible, save: true);
    }

    private void ApplyChromeVisibility(bool visible, bool save)
    {
        YalbLogger.Debug($"ApplyChromeVisibility: visible={visible}, save={save}", nameof(BrowserForm));
        _chromePanel.Visible = visible;
        UpdateVerticalTabs();
        AdjustContentLayout();

        if (save)
        {
            var settings = YalbSettings.Instance;
            settings.ChromeVisible = visible;
            settings.Save();
            YalbLogger.Info($"Chrome visibility saved to settings: {visible}", nameof(BrowserForm));
        }
    }

    private void SetContentFramelessState(bool enabled)
    {
        YalbLogger.Debug($"SetContentFramelessState: enabled={enabled}, injecting into {_tabWebViews.Count} webviews", nameof(BrowserForm));
        string script = $"window.__yalbTrueFrameless = {(enabled ? "true" : "false")};";
        foreach (var webView in _tabWebViews.Values)
        {
            if (webView.CoreWebView2 != null)
            {
                _ = webView.ExecuteScriptAsync(script);
            }
        }
    }

    private void AdjustContentLayout()
    {
        int sidebarWidth = _verticalTabPanel?.Visible == true ? _verticalTabPanel.Width : 0;

        if (_chromePanel.Visible)
            _chromePanel.SetBounds(sidebarWidth, 0, Math.Max(0, ClientSize.Width - sidebarWidth), ChromeHeight);
        else
            _chromePanel.SetBounds(0, 0, ClientSize.Width, ChromeHeight);

        int contentTop = _chromePanel.Visible ? _chromePanel.Bottom : 0;
        bool topTabsVisible = _topTabPanel?.Visible == true;
        bool bottomTabs = string.Equals(YalbSettings.Instance.TabPosition, "bottom", StringComparison.OrdinalIgnoreCase);
        int topTabHeight = topTabsVisible && !bottomTabs ? TopTabHeight : 0;
        int bottomTabHeight = topTabsVisible && bottomTabs ? TopTabHeight : 0;
        int contentLeft = sidebarWidth;
        int contentHeight = Math.Max(0, ClientSize.Height - contentTop);

        _verticalTabPanel?.SetBounds(0, 0, GetSidebarWidth(), ClientSize.Height);
        _topTabPanel?.SetBounds(
            contentLeft,
            bottomTabs ? Math.Max(contentTop, ClientSize.Height - TopTabHeight) : contentTop,
            Math.Max(0, ClientSize.Width - contentLeft),
            TopTabHeight);
        _sidebarToggleHandle?.SetBounds(Math.Max(0, sidebarWidth), contentTop + 2, 34, 30);
        _contentPanel.SetBounds(contentLeft, contentTop + topTabHeight, Math.Max(0, ClientSize.Width - contentLeft), Math.Max(0, contentHeight - topTabHeight - bottomTabHeight));
        LayoutFloatingAddressBar();

        _contentPanel.BringToFront();
        _topTabPanel?.BringToFront();
        _verticalTabPanel?.BringToFront();
        _sidebarToggleHandle?.BringToFront();
        _addressBarFloat?.BringToFront();

        SnapActiveWebView();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        const int WM_NCCALCSIZE = 0x83;
        const int HTCLIENT = 1;
        const int HTCAPTION = 2;
        const int HTLEFT = 10;
        const int HTRIGHT = 11;
        const int HTTOP = 12;
        const int HTTOPLEFT = 13;
        const int HTTOPRIGHT = 14;
        const int HTBOTTOM = 15;
        const int HTBOTTOMLEFT = 16;
        const int HTBOTTOMRIGHT = 17;

        if (m.Msg == WM_NCCALCSIZE && FormBorderStyle == FormBorderStyle.None && !_isFullScreen)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WM_NCHITTEST)
        {
            if (FormBorderStyle == FormBorderStyle.None && !_isFullScreen)
            {
                var pt = GetClientPointFromLParam(m.LParam);
                int grip = FramelessResizeBorder;
                bool left = pt.X >= 0 && pt.X < grip;
                bool right = pt.X <= ClientSize.Width && pt.X >= ClientSize.Width - grip;
                bool top = pt.Y >= 0 && pt.Y < grip;
                bool bottom = pt.Y <= ClientSize.Height && pt.Y >= ClientSize.Height - grip;

                if (top && left) { m.Result = (IntPtr)HTTOPLEFT; return; }
                if (top && right) { m.Result = (IntPtr)HTTOPRIGHT; return; }
                if (bottom && left) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
                if (bottom && right) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
                if (left) { m.Result = (IntPtr)HTLEFT; return; }
                if (right) { m.Result = (IntPtr)HTRIGHT; return; }
                if (top) { m.Result = (IntPtr)HTTOP; return; }
                if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }

                if (!_chromePanel.Visible && pt.Y >= grip && pt.Y < HiddenChromeDragStripHeight)
                {
                    m.Result = (IntPtr)HTCAPTION;
                    return;
                }

                m.Result = (IntPtr)HTCLIENT;
                return;
            }

            base.WndProc(ref m);
            if (m.Result == (IntPtr)HTCAPTION && _contentPanel != null)
            {
                var pt = GetClientPointFromLParam(m.LParam);
                if (_contentPanel.Bounds.Contains(pt))
                    m.Result = (IntPtr)HTCLIENT;
            }
            return;
        }
        base.WndProc(ref m);
    }

    private Point GetClientPointFromLParam(IntPtr lParam)
    {
        long value = lParam.ToInt64();
        int x = unchecked((short)(value & 0xFFFF));
        int y = unchecked((short)((value >> 16) & 0xFFFF));
        return PointToClient(new Point(x, y));
    }

    private static int GetInt(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement element) && element.TryGetInt32(out int value)
            ? value
            : 0;
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement element)
            ? element.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool GetBool(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement element) && element.GetBoolean();
    }

    private void BeginWindowDrag()
    {
        if (FormBorderStyle != FormBorderStyle.None || _isFullScreen) return;

        const int WM_NCLBUTTONDOWN = 0x00A1;
        const int HTCAPTION = 2;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
    }

    private void BeginWindowResize(int hitTest)
    {
        if (FormBorderStyle != FormBorderStyle.None || _isFullScreen) return;
        if (hitTest < 10 || hitTest > 17) return;

        const int WM_NCLBUTTONDOWN = 0x00A1;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)hitTest, IntPtr.Zero);
    }

    private void ToggleAddressBar()
    {
        if (_variant != BrowserVariant.Maeger || _addressBarFloat == null || _addressBarInput == null) return;

        if (_addressBarFloat.Visible)
        {
            _addressBarFloat.Visible = false;
            // Return focus to webview
            GetActiveWebView()?.Focus();
            return;
        }

        // Show
        ShowFloatingAddressBar();
        _addressBarInput.SelectAll();
    }

    private void FocusAddressBar()
    {
        if (_variant == BrowserVariant.Maeger && _addressBarFloat != null && _addressBarInput != null)
        {
            ShowFloatingAddressBar();
            _addressBarInput.SelectAll();
            return;
        }

        _chromeWebView.Focus();
        _ = _chromeWebView.ExecuteScriptAsync(@"
            const bar = document.querySelector('#addressBarWrapper input');
            if (bar) { bar.focus(); bar.select(); }
        ");
    }


    private void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            WindowState = _windowStateBeforeFullScreen;
            FormBorderStyle = _borderStyleBeforeFullScreen;
            _isFullScreen = false;
        }
        else
        {
            _borderStyleBeforeFullScreen = FormBorderStyle;
            _windowStateBeforeFullScreen = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _isFullScreen = true;
        }
    }

    private void ToggleFrameless()
    {
        if (_isFullScreen)
            ToggleFullScreen();

        bool enableFrameless = FormBorderStyle != FormBorderStyle.None;
        Size previousSize = Size;
        Point previousClientScreenLocation = PointToScreen(Point.Empty);

        WindowState = FormWindowState.Normal;
        FormBorderStyle = enableFrameless ? FormBorderStyle.None : FormBorderStyle.Sizable;
        Size = previousSize;

        Point newClientScreenLocation = PointToScreen(Point.Empty);
        Location = new Point(
            Location.X - (newClientScreenLocation.X - previousClientScreenLocation.X),
            Location.Y - (newClientScreenLocation.Y - previousClientScreenLocation.Y));

        var settings = YalbSettings.Instance;
        settings.FramelessWindow = enableFrameless;
        settings.Save();

        SetContentFramelessState(enableFrameless);

        // Refresh layout — chrome visibility is preserved
        AdjustContentLayout();
        Invalidate();
        Refresh();
    }

    // Full frameless convenience toggle: hide both title bar and chrome
    private void FullFramelessToggle()
    {
        ApplyZenMode(!_zenModeActive, save: true);
    }

    private void ApplyZenMode(bool enabled, bool save)
    {
        if (_isFullScreen)
            ToggleFullScreen();

        _zenModeActive = enabled;

        if (enabled)
        {
            if (FormBorderStyle != FormBorderStyle.None)
                ToggleFrameless();
            ApplyChromeVisibility(false, save: false);
            if (_verticalTabPanel != null)
                _verticalTabPanel.Visible = false;
            if (_topTabPanel != null)
                _topTabPanel.Visible = false;
        }
        else
        {
            if (FormBorderStyle == FormBorderStyle.None)
                ToggleFrameless();
            ApplyChromeVisibility(true, save: false);
            if (_verticalTabPanel != null)
                _verticalTabPanel.Visible = YalbSettings.Instance.ShowSidebar;
            if (_topTabPanel != null)
                _topTabPanel.Visible = !string.Equals(YalbSettings.Instance.TabPosition, "sidebar", StringComparison.OrdinalIgnoreCase);
        }

        if (save)
        {
            var settings = YalbSettings.Instance;
            settings.ZenModeActive = enabled;
            settings.ChromeVisible = !enabled;
            settings.Save();
        }

        AdjustContentLayout();
        SetContentFramelessState(FormBorderStyle == FormBorderStyle.None);
    }

    private void RevealZenChrome()
    {
        if (!_zenModeActive) return;
        ApplyChromeVisibility(true, save: false);
    }

    private void RevealZenSidebar()
    {
        if (!_zenModeActive || _verticalTabPanel == null) return;
        _verticalTabPanel.Visible = true;
        AdjustContentLayout();
    }

    private void HideZenOverlays()
    {
        if (!_zenModeActive) return;
        ApplyChromeVisibility(false, save: false);
        if (_verticalTabPanel != null)
            _verticalTabPanel.Visible = false;
        AdjustContentLayout();
    }

    // ---------------------------------------------------------------------
    // Page injection: disable overscroll bounce + dark bg + Vim shortcuts
    // ---------------------------------------------------------------------
    private static string GetPageInjectionScript()
    {
        return @"
(function() {
    if (window.__yalbInjected) return;
    window.__yalbInjected = true;

    // 1. Kill elastic overscroll / rubber-band and set a dark fallback background
    //    so the edge-flash is never white, even on pages without a background.
    var style = document.createElement('style');
    style.textContent = `
        html, body { 
            overscroll-behavior: none !important; 
            background-color: #1e1e1e !important; 
        }
        ::-webkit-scrollbar { width: 10px; height: 10px; }
        ::-webkit-scrollbar-track { background: #1a1a1a; }
        ::-webkit-scrollbar-thumb { background: #444; border-radius: 5px; }
        ::-webkit-scrollbar-thumb:hover { background: #555; }
    `;
    document.head.appendChild(style);

    // 2. Vim-style scroll shortcuts
    document.addEventListener('keydown', function(e) {
        var tag = e.target.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA' || e.target.isContentEditable) return;

        switch(e.key) {
            case 'j':
                if (!e.ctrlKey && !e.altKey && !e.metaKey) {
                    window.scrollBy({ top: 60, behavior: 'smooth' });
                    e.preventDefault();
                }
                break;
            case 'k':
                if (!e.ctrlKey && !e.altKey && !e.metaKey) {
                    window.scrollBy({ top: -60, behavior: 'smooth' });
                    e.preventDefault();
                }
                break;
            case 'g':
                if (e.shiftKey) {
                    window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' });
                } else {
                    window.scrollTo({ top: 0, behavior: 'smooth' });
                }
                e.preventDefault();
                break;
            case 'd':
                if (e.ctrlKey) {
                    window.scrollBy({ top: window.innerHeight / 2, behavior: 'smooth' });
                    e.preventDefault();
                }
                break;
            case 'u':
                if (e.ctrlKey) {
                    window.scrollBy({ top: -window.innerHeight / 2, behavior: 'smooth' });
                    e.preventDefault();
                }
                break;
        }
    });
})();
";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        YalbLogger.Info($"OnFormClosing: saving session with {_tabOrder.Count} tabs", nameof(BrowserForm));
        try
        {
            SaveSession();

            foreach (var kvp in _tabWebViews.ToList())
            {
                try { kvp.Value.Dispose(); } catch { }
            }
            _tabWebViews.Clear();
            YalbLogger.Info($"Browser closed successfully", nameof(BrowserForm));
        }
        catch (Exception ex)
        {
            YalbLogger.Error(nameof(BrowserForm), ex);
        }
        base.OnFormClosing(e);
    }

    private void SaveSession()
    {
        var sessionTabs = _tabOrder
            .Select(id => _tabWebViews.TryGetValue(id, out var wv) ? wv : null)
            .Where(wv => wv?.CoreWebView2 != null)
            .Select(wv => new TabSession
            {
                Url = wv!.CoreWebView2.Source,
                Title = wv.CoreWebView2.DocumentTitle ?? string.Empty
            })
            .ToList();

        int activeIndex = _tabOrder.IndexOf(_activeTabId);
        YalbSettings.Instance.RecordSession(sessionTabs, Math.Max(0, activeIndex));
    }

    /// <summary>
    /// Omnibox heuristic: determines whether raw user input is a URL
    /// or a search query. Mimics Chrome's address-bar logic.
    /// </summary>
    private bool IsLikelyUrl(string input)
    {
        input = input.Trim();
        if (string.IsNullOrEmpty(input)) return false;

        // Explicit scheme (https://, ftp://, file://, etc.)
        if (input.Contains("://")) return true;
        if (input.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;

        // Extract host part (ignore path/query/fragment for the check)
        int hostEnd = input.IndexOfAny(new[] { '/', '?', '#' });
        string host = hostEnd >= 0 ? input.Substring(0, hostEnd) : input;

        // Strip port if present (e.g. localhost:8080)
        int portIdx = host.LastIndexOf(':');
        string hostNoPort = portIdx >= 0 ? host.Substring(0, portIdx) : host;

        // localhost
        if (hostNoPort.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        // IPv4 address (with or without port)
        string[] ipParts = hostNoPort.Split('.');
        if (ipParts.Length == 4 &&
            ipParts.All(p => p.Length > 0 && p.Length <= 3 && p.All(char.IsDigit)))
            return true;

        // Contains spaces -> definitely a search query
        if (hostNoPort.Contains(' ')) return false;

        // Domain-like: has a dot and the TLD is 2+ letters
        int lastDot = hostNoPort.LastIndexOf('.');
        if (lastDot > 0 && lastDot < hostNoPort.Length - 1)
        {
            string tld = hostNoPort.Substring(lastDot + 1);
            if (tld.Length >= 2 && tld.All(char.IsLetter))
                return true;
        }

        return false;
    }
}
