using System.Diagnostics;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using RemoteAccessHub.Core;
using RemoteAccessHub.Router;
using RemoteAccessHub.SelfTest;
using RemoteAccessHub.Services;
using RemoteAccessHub.UI.Controls;

namespace RemoteAccessHub.UI;

/// <summary>
/// 메인 창.
/// 제목과 상태 배지 → 4단계 표시 → 현재 안내 → 동작 버튼 → 자세한 기록(접기 가능).
/// 공유기 화면(WebView2)은 별도 창(RouterWindow)에 있고, 닫아도 컨트롤을 버리지 않고 숨기기만 해 로그인 세션을 유지한다.
/// 자동화 동작(세션 판정·WOL·접속)은 Router/Services 계층에 있고 이 창은 상태 표시와 사용자 입력만 맡는다.
/// </summary>
public sealed class MainForm : Form
{
    private const int LogicalBrowserWidth = 1180;
    private const int LogicalBrowserHeight = 720;
    /// <summary>메인 창 너비(공유기 화면이 빠져 더 좁아도 된다).</summary>
    private const int LogicalMainWidth = 1180;
    private const int LogicalLogHeight = 150;

    private readonly LaunchOptions _options;
    private readonly AppLog _log;
    private readonly string _settingsPath;
    private AppSettings _settings;

    private readonly IVpnService _vpn;
    private readonly IPortProbe _portProbe;
    private readonly IRdpLauncher _rdp;
    private readonly ICrdLauncher _crd;
    private readonly PowerWatcher _power;
    /// <summary>PC가 켜져 있을 때 [PC 접속]을 서서히 밝아졌다 어두워지게 하는 타이머.</summary>
    private readonly System.Windows.Forms.Timer _blinkTimer = new() { Interval = 40 };
    private readonly System.Diagnostics.Stopwatch _blinkClock = new();
    private bool _blinkConnectOn;
    private bool _blinkRouterOn;
    /// <summary>한 번 밝아졌다 어두워지는 데 걸리는 시간.</summary>
    private static readonly TimeSpan BlinkPeriod = TimeSpan.FromSeconds(2.2);

    private readonly WebView2 _webView = new();
    /// <summary>공유기 로그인·관리 화면은 메인 창 안이 아니라 별도 창에 띄운다.</summary>
    private readonly RouterWindow _routerWindow;
    private readonly RouterBrowser _browser;
    private readonly WolAutomation _wol;
    private readonly ConnectWorkflow _connect;
    private readonly FlowTracker _flow = new();
    private readonly System.Windows.Forms.Timer _sessionTimer = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private MockRouterServer? _mock;
    private readonly TaskCompletionSource<bool> _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _opCts;
    private CancellationTokenSource? _settleCts;
    private bool _busy;
    private bool _browserExpanded = true;
    private bool _logVisible;
    private bool _initDone;
    private bool _vpnConnecting;
    private bool _wakeRunning;
    private bool _exiting;
    private bool _exitApproved;
    private ModePopup? _modePopup;
    private int _uiTicks;
    private bool? _vpnConnectedCache;

    // --- 화면 구성 요소
    private readonly Panel _topArea = new();
    private readonly Panel _header = new();
    private readonly Label _title = new();
    private readonly FlowLayoutPanel _pills = new();
    private readonly StatusPill _routerPill = new();
    private readonly StatusPill _vpnPill = new();
    private readonly StatusPill _powerPill = new();
    private readonly FlatButton _btnSettings = new();
    private readonly FlatButton _btnMore = new();
    private readonly FlatButton _btnExit = new();
    private readonly CardPanel _stepCard = new();
    private readonly StepperControl _stepper = new();
    private readonly StatusBanner _banner = new();
    private readonly Panel _actions = new();
    private readonly FlatButton _btnWakeConnect = new();
    private readonly FlatButton _btnWake = new();
    private readonly FlatButton _btnConnect = new();
    private readonly FlatButton _btnCancel = new();
    private readonly FlatButton _btnToggle = new();
    private readonly FlatButton _btnLog = new();
    private readonly Panel _logPanel = new();
    private readonly TextBox _txtLog = new();
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly ToolStripMenuItem _miReopen = new("공유기 창 다시 열기");
    private readonly ToolStripMenuItem _miWakeHere = new("현재 화면에서 PC 켜기");
    private readonly ToolStripMenuItem _miDiag = new("진단 내보내기...");
    private readonly ToolStripMenuItem _miLogs = new("기록 폴더 열기");
    private readonly ToolStripMenuItem _miTheme = new("테마");
    private readonly ToolStripMenuItem _miSetup = new("시작 설정 다시 하기...");
    private readonly ToolTip _tips = new() { InitialDelay = 400, ReshowDelay = 200 };
    private readonly CursorGuard _cursorGuard;
    private bool _preparingAdmin;

    // --- 외부(자체검사·진단)에서 쓰는 상태
    public RouterBrowser Browser => _browser;
    public WolAutomation Wol => _wol;
    public AppSettings Settings => _settings;
    public MockRouterServer? Mock => _mock;
    public FlowTracker Flow => _flow;
    public bool IsBrowserExpanded => _routerWindow.Visible;
    public bool IsLogVisible => _logVisible;
    public bool WakeEnabled => _btnWake.Enabled;
    public bool WakeConnectEnabled => _btnWakeConnect.Enabled;
    public bool ConnectEnabled => _btnConnect.Enabled;
    /// <summary>[PC 접속]이 지금 깜빡이는 중인지(자체검사용).</summary>
    public bool ConnectBlinking => _blinkTimer.Enabled && _blinkConnectOn;
    /// <summary>[공유기 화면]이 로그인 필요 알림으로 깜빡이는 중인지(자체검사용).</summary>
    public bool RouterBlinking => _blinkTimer.Enabled && _blinkRouterOn;
    public double RouterHighlight => _btnToggle.AttentionLevel;
    public Color RouterBlinkColor => _btnToggle.AttentionColor;
    public double ConnectHighlight => _btnConnect.AttentionLevel;
    public bool ConnectHighlighted => _btnConnect.AttentionLevel > 0.5;
    public bool IsBusy => _busy;
    public string StatusText => _banner.Text;
    public BannerKind StatusKind => _banner.Kind;
    public string WolClickText => _flow.WolClickText;
    public string WolRouterText => _flow.WolRouterText;
    public string WolBootText => _flow.WolBootText;
    public string RouterPillText => _routerPill.Text;
    public string VpnPillText => _vpnPill.Visible ? _vpnPill.Text : "";
    public string PowerPillText => _powerPill.Visible ? _powerPill.Text : "";
    public PcPowerStatus PcPowerStatus => _power.Status;
    /// <summary>자체검사용: 전원 상태를 지금 확인한다.</summary>
    public Task CheckPowerNowAsync() => _power.CheckNowAsync();
    public int AttentionCount { get; private set; }
    public string WakeConnectButtonText => _btnWakeConnect.Text;
    public bool ConnectButtonsHaveArrow => _btnConnect.ShowArrow || _btnConnect.SplitWidth > 0 || _btnWakeConnect.ShowArrow || _btnWakeConnect.SplitWidth > 0;
    public bool IsExiting => _exiting;
    public bool IsPreparingAdmin => _preparingAdmin;

    /// <summary>자체검사용: 페이지 대화상자를 묻지 않고 수락한다.</summary>
    public bool ScriptDialogAutoAccept { get; set; }
    public int ScriptDialogCount { get; private set; }
    /// <summary>자체검사용: MessageBox·파일 대화상자를 띄우지 않는다.</summary>
    public bool SuppressDialogs { get; set; }

    public Task<bool> Initialized => _initTcs.Task;
    public NavResult? LastSettle { get; private set; }

    public MainForm(LaunchOptions options, AppLog log, IVpnService? vpn = null, IPortProbe? probe = null, IRdpLauncher? rdp = null, ICrdLauncher? crd = null)
    {
        _options = options;
        _log = log;
        _settingsPath = options.SettingsPath
            ?? (options.SelfTest ? Path.Combine(Path.GetTempPath(), "RemoteAccessHub-selftest-settings.json")
            : options.Mock ? Path.Combine(AppPaths.SettingsDirectory, "settings-mock.json")
            : AppPaths.SettingsFile);
        _settings = options.SelfTest ? new AppSettings() : AppSettings.Load(_settingsPath, log);
        SuppressDialogs = options.SelfTest;
        ScriptDialogAutoAccept = options.SelfTest;
        _logVisible = _settings.ShowLog;

        _vpn = vpn ?? new VpnService(log);
        _portProbe = probe ?? new TcpPortProbe();
        _rdp = rdp ?? new RdpLauncher(log);
        _crd = crd ?? new CrdLauncher(log);

        _routerWindow = new RouterWindow(_webView,
            new Size(L(LogicalBrowserWidth), L(LogicalBrowserHeight)),
            new Size(L(900), L(560)));
        _routerWindow.VisibleChanged += (_, _) => SafeInvoke(RefreshUi);
        _browser = new RouterBrowser(_webView, log, () => _settings);
        _wol = new WolAutomation(_browser, log, () => _settings);
        _connect = new ConnectWorkflow(_vpn, _portProbe, _rdp, _crd, log);
        // 전원 배지: 설정한 주기로 PC 포트 응답만 확인한다. VPN을 스스로 연결하지 않고, 작업 중에는 쉰다.
        _power = new PowerWatcher(_portProbe, log, () => _settings,
            vpnConnected: () => _vpnConnectedCache == true,
            routerLoggedIn: () => _browser.Session.IsLoggedIn,
            paused: () => _busy || _exiting);
        _power.Changed += _ =>
        {
            if (IsDisposed) return;
            // 이미 화면 스레드면 바로 갱신한다(배지가 한 박자 늦게 바뀌지 않도록).
            if (InvokeRequired) BeginInvoke(RefreshPills);
            else RefreshPills();
        };

        _cursorGuard = new CursorGuard(this, _routerWindow.BrowserHost, log);
        Application.AddMessageFilter(_cursorGuard);
        Theme.Apply(Theme.ParseMode(_settings.Theme));
        BuildUi();
        BuildMenus();
        HookEvents();
        ApplyTheme();
        RefreshUi();
    }

    private int L(int logical) => LogicalToDeviceUnits(logical);

    // ================================================================== 화면 구성

    private void BuildUi()
    {
        Text = "RemoteAccessHub" + (_options.Mock ? " [모의 공유기]" : _options.SelfTest ? " [자체검사]" : "");
        AppIcon.ApplyTo(this);
        Font = Theme.UiFont(9.5f);
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        // --- 머리글: 제목 · 상태 배지 · 설정/더보기
        _header.Dock = DockStyle.Top;
        _title.Text = "RemoteAccessHub";
        _title.Font = Theme.UiFont(13f, FontStyle.Bold);
        _title.AutoSize = true;
        _title.Location = new Point(0, L(9));
        _title.Tag = "keep";

        _btnExit.Text = "종료";
        _btnExit.Glyph = Theme.Glyph.Exit;
        _btnExit.Variant = ButtonVariant.Secondary;
        _btnExit.Size = new Size(L(84), L(36));
        _btnExit.AccessibleName = "공유기 로그아웃 후 종료";
        _btnExit.Click += (_, _) => _ = ExitWithLogoutAsync();
        _tips.SetToolTip(_btnExit, "공유기 관리 세션을 로그아웃한 뒤 프로그램을 종료합니다. VPN 연결은 끊지 않습니다. (Ctrl+Q)");

        foreach (var (b, glyph, name) in new[] { (_btnSettings, Theme.Glyph.Settings, "설정"), (_btnMore, Theme.Glyph.More, "더 보기") })
        {
            b.IconOnly = true;
            b.Glyph = glyph;
            b.Variant = ButtonVariant.Ghost;
            b.AccessibleName = name;
            b.Text = name;
            b.Size = new Size(L(38), L(36));
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        }
        _btnSettings.Click += (_, _) => OpenSettings();
        _btnMore.Click += (_, _) => _moreMenu.Show(_btnMore, new Point(_btnMore.Width - _moreMenu.PreferredSize.Width, _btnMore.Height));
        _tips.SetToolTip(_btnSettings, "설정");
        _tips.SetToolTip(_btnMore, "공유기 창 다시 열기 · 현재 화면에서 PC 켜기 · 진단 · 테마");

        _pills.FlowDirection = FlowDirection.RightToLeft;
        _pills.WrapContents = false;
        _pills.AutoSize = false;
        _pills.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _pills.Controls.Add(_powerPill);
        _pills.Controls.Add(_vpnPill);
        _pills.Controls.Add(_routerPill);
        _powerPill.Cursor = Cursors.Hand;
        _powerPill.Click += (_, _) => _ = _power.CheckNowAsync();
        _tips.SetToolTip(_powerPill, "PC 전원 상태(눌러서 지금 확인)");
        foreach (var pill in new[] { _routerPill, _vpnPill, _powerPill })
        {
            pill.Height = L(30);
            pill.Margin = new Padding(L(8), L(3), 0, 0);
        }

        _header.Controls.AddRange(new Control[] { _title, _pills, _btnMore, _btnSettings, _btnExit });
        _header.Resize += (_, _) => LayoutHeader();

        // --- 단계 표시
        _stepCard.Dock = DockStyle.Top;
        _stepCard.Padding = new Padding(L(4), L(6), L(4), L(4));
        _stepper.Dock = DockStyle.Fill;
        _stepCard.Controls.Add(_stepper);

        // --- 안내 줄
        _banner.Dock = DockStyle.Top;

        // --- 동작 버튼
        _actions.Dock = DockStyle.Top;
        _btnWakeConnect.Variant = ButtonVariant.Primary;
        _btnWakeConnect.Glyph = Theme.Glyph.Play;
        _btnWakeConnect.Text = "PC 켜고 접속";
        _btnWakeConnect.Click += (_, _) => ShowModePopup(wakeFirst: true);
        _tips.SetToolTip(_btnWakeConnect, "누르면 일반/VPN 접속을 고릅니다. 고른 뒤 PC를 켜고 부팅을 기다려 원격 데스크톱까지 한 번에 진행합니다. (Ctrl+Enter)");

        _btnWake.Text = "PC 켜기";
        _btnWake.Glyph = Theme.Glyph.Power;
        _btnWake.Click += (_, _) => _ = RunWakeAsync(skipNavigation: false);
        _tips.SetToolTip(_btnWake, "공유기 WOL로 PC만 켭니다.");

        _btnConnect.Text = "PC 접속";
        _btnConnect.Glyph = Theme.Glyph.Connect;
        _btnConnect.Click += (_, _) => ShowModePopup(wakeFirst: false);
        // 사인 곡선이라 양 끝에서 머물고 가운데에서 빨라져 '숨 쉬듯' 보인다.
        _blinkTimer.Tick += (_, _) =>
        {
            var level = (1 - Math.Cos(2 * Math.PI * (_blinkClock.Elapsed.TotalSeconds / BlinkPeriod.TotalSeconds))) / 2;
            if (_blinkConnectOn) _btnConnect.AttentionLevel = level;
            if (_blinkRouterOn) _btnToggle.AttentionLevel = level;
        };
        _tips.SetToolTip(_btnConnect, "이미 켜진 PC에 바로 접속합니다. 누르면 일반/VPN 접속을 고릅니다. (Ctrl+Shift+Enter)");

        _btnCancel.Text = "취소";
        _btnCancel.Glyph = Theme.Glyph.Cancel;
        _btnCancel.Variant = ButtonVariant.Danger;
        _btnCancel.Click += (_, _) => CancelOperation();
        _tips.SetToolTip(_btnCancel, "진행 중인 작업을 멈춥니다. VPN 연결은 끊지 않습니다. (Esc)");

        _btnToggle.Variant = ButtonVariant.Ghost;
        _btnToggle.AttentionColor = Theme.Current.Warning; // 로그인이 풀렸다는 알림
        _btnToggle.Click += (_, _) => SetBrowserExpanded(!_browserExpanded);
        _tips.SetToolTip(_btnToggle, "공유기 화면 창을 띄우거나 닫습니다. 닫아도 로그인은 유지됩니다. (Ctrl+B)");

        _btnLog.Variant = ButtonVariant.Ghost;
        _btnLog.Glyph = Theme.Glyph.Log;
        _btnLog.Click += (_, _) => SetLogVisible(!_logVisible);
        _tips.SetToolTip(_btnLog, "자세한 기록을 보이거나 숨깁니다. (Ctrl+L)");

        _actions.Controls.AddRange(new Control[] { _btnWakeConnect, _btnWake, _btnConnect, _btnCancel, _btnToggle, _btnLog });
        _actions.Resize += (_, _) => LayoutActions();

        // --- 위쪽 묶음(Dock=Top은 나중에 추가한 것이 위로 온다)
        _topArea.Dock = DockStyle.Top;
        _topArea.Controls.Add(_actions);
        _topArea.Controls.Add(Spacer(10));
        _topArea.Controls.Add(_banner);
        _topArea.Controls.Add(Spacer(10));
        _topArea.Controls.Add(_stepCard);
        _topArea.Controls.Add(Spacer(8));
        _topArea.Controls.Add(_header);

        // --- 자세한 기록
        _logPanel.Dock = DockStyle.Top;
        _logPanel.Padding = new Padding(L(16), 0, L(16), L(10));
        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.Dock = DockStyle.Fill;
        _txtLog.Font = new Font("Consolas", 9f);
        _txtLog.WordWrap = true;
        _logPanel.Controls.Add(_txtLog);

        Controls.Add(_logPanel);
        Controls.Add(_topArea);

        ApplyLayoutMetrics();
        ClientSize = new Size(L(LogicalMainWidth), ClientSize.Height);
        ApplyWindowHeight();
        RestoreWindowPosition();
    }

    private Control Spacer(int logicalHeight) => new Panel { Dock = DockStyle.Top, Height = L(logicalHeight), Tag = "spacer" };

    private void ApplyLayoutMetrics()
    {
        _topArea.Padding = new Padding(L(16), L(12), L(16), L(10));
        _header.Height = L(44);
        _stepCard.Height = L(84);
        _banner.Height = L(54);
        _actions.Height = L(40);
        _logPanel.Height = L(LogicalLogHeight);
        foreach (Control c in _topArea.Controls)
            if (c.Tag as string == "spacer") c.Height = c.Height; // 이미 L() 적용
        var h = _topArea.Padding.Vertical;
        foreach (Control c in _topArea.Controls) h += c.Height;
        _topArea.Height = h;
        _logPanel.Visible = _logVisible;
    }

    /// <summary>메인 창 내용 높이(위쪽 묶음 + 보이는 경우 기록 창).</summary>
    private int ContentHeight => _topArea.Height + (_logVisible ? _logPanel.Height : 0);

    /// <summary>
    /// 창 높이를 현재 표시 상태에 맞춘다.
    /// 공유기 화면이 별도 창으로 빠져 메인 창은 늘 내용 높이에 고정된다.
    /// </summary>
    private void ApplyWindowHeight()
    {
        if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
        var frame = Height - ClientSize.Height;
        var contentWindowHeight = ContentHeight + frame;
        SuspendLayout();
        // 크기 제한을 먼저 풀어야 새 높이가 적용된다.
        MaximumSize = Size.Empty;
        MinimumSize = new Size(L(1040), contentWindowHeight);
        ClientSize = new Size(ClientSize.Width, ContentHeight);
        // 너비는 제한하지 않는다(0을 넣으면 WinForms가 최소 창 너비로 고정한다).
        MaximumSize = new Size(short.MaxValue, contentWindowHeight);
        ResumeLayout(true);
    }

    /// <summary>자체검사용: 공유기 화면이 실제로 차지하는 높이.</summary>
    public int BrowserAreaHeight => _routerWindow.Visible ? _routerWindow.ClientSize.Height : 0;
    internal CursorGuard CursorGuard => _cursorGuard;
    internal IntPtr ActionsHandle => _actions.Handle;
    internal IntPtr BrowserControlHandle => _webView.Handle;

    private void LayoutHeader()
    {
        var right = _header.Width;
        _btnExit.Size = new Size(Math.Max(L(84), _btnExit.PreferredWidth()), L(36));
        _btnExit.Location = new Point(right - _btnExit.Width, L(4));
        _btnMore.Location = new Point(_btnExit.Left - _btnMore.Width - L(8), L(4));
        _btnSettings.Location = new Point(_btnMore.Left - _btnSettings.Width - L(2), L(4));
        var pillsWidth = _routerPill.Width + (_vpnPill.Visible ? _vpnPill.Width + L(8) : 0)
            + (_powerPill.Visible ? _powerPill.Width + L(8) : 0) + L(12);
        _pills.Size = new Size(pillsWidth, L(40));
        _pills.Location = new Point(_btnSettings.Left - pillsWidth - L(8), L(2));
    }

    private void LayoutActions()
    {
        var gap = L(8);
        var h = _actions.Height;
        var x = 0;
        void Place(FlatButton b, int minWidth = 0)
        {
            if (!b.Visible) return;
            b.Size = new Size(Math.Max(minWidth, b.PreferredWidth()), h);
            b.Location = new Point(x, 0);
            x += b.Width + gap;
        }
        Place(_btnWakeConnect, L(200));
        Place(_btnWake, L(104));
        Place(_btnConnect, L(112));

        var rx = _actions.Width;
        void PlaceRight(FlatButton b)
        {
            if (!b.Visible) return;
            b.Size = new Size(b.PreferredWidth(), h);
            rx -= b.Width;
            b.Location = new Point(rx, 0);
            rx -= gap;
        }
        PlaceRight(_btnLog);
        PlaceRight(_btnToggle);
        PlaceRight(_btnCancel);
    }

    private void BuildMenus()
    {
        foreach (var m in new[] { _moreMenu })
        {
            m.Renderer = new ThemedMenuRenderer();
            m.Font = Theme.UiFont(9.5f);
            m.ShowImageMargin = true;
            m.ShowCheckMargin = false;
        }

        _miReopen.ShortcutKeyDisplayString = "F5";
        _miReopen.Click += (_, _) => _ = NavigateToRouterAsync(expand: true);
        _miWakeHere.Click += (_, _) => _ = RunWakeAsync(skipNavigation: true);
        _miDiag.Click += (_, _) => _ = ExportDiagnosticsAsync();
        _miLogs.Click += (_, _) => OpenLogFolder();
        foreach (var (label, mode) in new[] { ("Windows 설정 따르기", ThemeMode.System), ("어둡게", ThemeMode.Dark), ("밝게", ThemeMode.Light) })
        {
            var item = new ToolStripMenuItem(label) { Tag = mode };
            item.Click += (_, _) => SetThemeMode(mode, save: true);
            _miTheme.DropDownItems.Add(item);
        }
        _miTheme.DropDown.Renderer = new ThemedMenuRenderer();
        _miSetup.Click += (_, _) => RunSetupWizard();
        _moreMenu.Items.AddRange(new ToolStripItem[] { _miReopen, _miWakeHere, new ToolStripSeparator(), _miSetup, _miDiag, _miLogs, new ToolStripSeparator(), _miTheme });
        _moreMenu.Opening += (_, _) =>
        {
            foreach (ToolStripMenuItem t in _miTheme.DropDownItems) t.Checked = (ThemeMode)t.Tag! == Theme.Mode;
        };
    }

    /// <summary>
    /// 접속 방식 선택 팝업을 연다([PC 켜고 접속]·[PC 접속]을 누를 때마다).
    /// 방식마다 필요한 설정만 검사하며, 설정이 비어 있는 방식은 선택할 수 없게 표시한다.
    /// </summary>
    public ModePopup? ShowModePopup(bool wakeFirst)
    {
        if (_busy || _exiting) return null;
        if (wakeFirst && !_browser.Session.IsLoggedIn) return null;
        if (_preparingAdmin && _browser.Session.IsLoggedIn) return null;
        _modePopup?.Close();
        var anchor = wakeFirst ? _btnWakeConnect : _btnConnect;
        var heading = wakeFirst ? "PC를 켠 뒤 접속할 방식" : "접속 방식";
        var popup = new ModePopup(heading, ModeOptions.For(_settings), _settings.LastMode);
        popup.Chosen += mode =>
        {
            _settings.LastConnectMode = mode switch { ConnectMode.Vpn => "vpn", ConnectMode.Crd => "crd", _ => "direct" };
            if (!_options.SelfTest) TrySaveSettings();
            if (wakeFirst) _ = RunWakeAndConnectAsync(mode);
            else _ = RunConnectAsync(mode);
        };
        popup.FormClosed += (_, _) => { if (ReferenceEquals(_modePopup, popup)) _modePopup = null; };
        _modePopup = popup;
        popup.ShowBelow(this, anchor);
        return popup;
    }

    public ModePopup? CurrentModePopup => _modePopup;

    /// <summary>접속 방식 선택지 상태(자체검사에서도 사용).</summary>
    public ModeOption ModeOptionState(ConnectMode mode) => ModeOptions.Build(_settings, mode);

    /// <summary>지금 설정에서 보이는 접속 방식 수(자체검사에서도 사용).</summary>
    public int ModeOptionCount => ModeOptions.For(_settings).Count;

    private void HookEvents()
    {
        _log.Appended += e =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => AppendLog(e))); } catch { /* closing */ }
        };
        _flow.Changed += () => SafeInvoke(() => _stepper.SetSteps(_flow.Steps));
        _sessionTimer.Tick += async (_, _) => { if (_initDone && !_busy) await ProbeSessionNowAsync(); };
        _uiTimer.Tick += (_, _) => OnUiTick();
        Theme.Changed += OnThemeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        HandleCreated += (_, _) => Theme.ApplyTitleBar(this);
        Shown += (_, _) => { _uiTimer.Start(); _ = InitializeAsync(); };
        DpiChanged += (_, _) => { ApplyLayoutMetrics(); LayoutHeader(); LayoutActions(); ApplyWindowHeight(); };
        FormClosing += (_, e) =>
        {
            // 창의 X로 닫을 때: 작업 중이면 한 번 묻는다. (공유기 로그아웃은 [종료] 버튼에서만 한다)
            if (!_exitApproved && _busy && !SuppressDialogs && e.CloseReason == CloseReason.UserClosing)
            {
                var r = MessageBox.Show(this, "작업이 진행 중입니다. 종료하면 작업을 멈춥니다(VPN 연결은 유지).\n\n종료할까요?",
                    "RemoteAccessHub", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            _modePopup?.Close();
            _sessionTimer.Stop();
            _uiTimer.Stop();
            _opCts?.Cancel();
            _settleCts?.Cancel();
            SaveWindowPosition();
            _log.Info("프로그램 종료 (VPN 연결은 그대로 둡니다)");
            _blinkTimer.Stop();
            _power.Dispose();
            _routerWindow.CloseForReal();
            _mock?.Dispose();
        };
        FormClosed += (_, _) =>
        {
            Application.RemoveMessageFilter(_cursorGuard);
            Theme.Changed -= OnThemeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Enter when _btnWakeConnect.Enabled:
                ShowModePopup(wakeFirst: true);
                return true;
            case Keys.Control | Keys.Shift | Keys.Enter when _btnConnect.Enabled:
                ShowModePopup(wakeFirst: false);
                return true;
            case Keys.Control | Keys.Q:
                _ = ExitWithLogoutAsync();
                return true;
            case Keys.Escape when _busy:
                CancelOperation();
                return true;
            case Keys.F5 when !_busy:
                _ = NavigateToRouterAsync(expand: true);
                return true;
            case Keys.Control | Keys.B:
                SetBrowserExpanded(!_browserExpanded);
                return true;
            case Keys.Control | Keys.L:
                SetLogVisible(!_logVisible);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ================================================================== 테마

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            SafeInvoke(Theme.RefreshFromSystem);
    }

    private void OnThemeChanged() => SafeInvoke(ApplyTheme);

    public void SetThemeMode(ThemeMode mode, bool save)
    {
        _settings.Theme = Theme.ToSetting(mode);
        if (save) TrySaveSettings();
        Theme.Apply(mode);
        ApplyTheme(); // 같은 팔레트로 바뀌어도 한 번은 적용
    }

    private void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Background;
        ForeColor = p.Text;
        _topArea.BackColor = p.Background;
        _header.BackColor = p.Background;
        _actions.BackColor = p.Background;
        _pills.BackColor = p.Background;
        _logPanel.BackColor = p.Background;
        _routerWindow.ApplyTheme();
        _btnToggle.AttentionColor = p.Warning;
        foreach (Control c in _topArea.Controls) if (c.Tag as string == "spacer") c.BackColor = p.Background;
        _title.ForeColor = p.Text;
        _txtLog.BackColor = p.LogBackground;
        _txtLog.ForeColor = p.LogText;
        _txtLog.BorderStyle = BorderStyle.FixedSingle;
        _stepCard.ApplyTheme();
        _moreMenu.BackColor = p.Surface;
        Theme.ApplyTitleBar(this);
        RefreshPills();
        Invalidate(true);
    }

    // ================================================================== 기록

    private void AppendLog(LogEntry e)
    {
        if (e.Level == LogLevel.Debug) return;
        if (_txtLog.TextLength > 200_000) _txtLog.Clear();
        _txtLog.AppendText(e.Format() + Environment.NewLine);
    }

    public void SetLogVisible(bool visible)
    {
        if (_logVisible == visible) return;
        _logVisible = visible;
        _settings.ShowLog = visible;
        if (!_options.SelfTest) TrySaveSettings();
        _logPanel.Visible = visible;
        ApplyWindowHeight();
        RefreshUi();
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var psi = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), UseShellExecute = false };
            psi.ArgumentList.Add(AppPaths.LogDirectory);
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            SetStatus("기록 폴더를 열지 못했습니다: " + ex.Message, BannerKind.Warning);
        }
    }

    // ================================================================== 상태 표시

    private void SetStatus(string text, BannerKind kind = BannerKind.Info) => _banner.Set(text, kind);

    /// <summary>사용자 조작이 필요할 때: 창이 뒤에 있으면 작업 표시줄 단추를 깜박인다.</summary>
    private void NotifyAttention()
    {
        AttentionCount++;
        if (!SuppressDialogs) NativeMethods.FlashUntilForeground(this);
    }

    private void RefreshUi()
    {
        var loggedIn = _browser.Session.IsLoggedIn;
        var idle = !_busy && !_exiting;
        var gate = ActionGate.Compute(loggedIn, _busy, _exiting, _preparingAdmin);
        _btnWake.Enabled = gate.Wake;
        _btnWakeConnect.Enabled = gate.WakeConnect;
        _btnConnect.Enabled = gate.Connect;
        _btnSettings.Enabled = idle;
        _btnMore.Enabled = !_exiting;
        _btnExit.Enabled = !_exiting;
        _btnExit.Text = _exiting ? "종료 중" : "종료";
        _miWakeHere.Enabled = gate.Wake;
        _miReopen.Enabled = idle;
        _miSetup.Enabled = idle;
        _btnCancel.Visible = _busy && !_exiting;
        _btnToggle.Text = _routerWindow.Visible ? "공유기 창 닫기" : "공유기 화면";
        _btnToggle.Glyph = _routerWindow.Visible ? Theme.Glyph.Cancel : Theme.Glyph.Router;
        _btnLog.Text = _logVisible ? "기록 숨기기" : "기록";
        RefreshPills();
        LayoutActions();
        _stepper.SetSteps(_flow.Steps);
    }

    /// <summary>
    /// 알림이 필요한 버튼을 천천히 깜빡인다.
    /// PC가 켜진 것이 확인되면 [PC 접속](강조색), 공유기 로그인이 풀리면 [공유기 화면](주황).
    /// 조건이 사라지거나 버튼이 잠기면 바로 멈추고 원래 색으로 돌아간다.
    /// </summary>
    private void RefreshAttentionBlink()
    {
        var setting = _settings.BlinkAttentionButtons;
        _blinkConnectOn = ActionGate.ShouldBlinkConnect(setting, _power.Status.State, _btnConnect.Enabled, _busy, _exiting);
        _blinkRouterOn = ActionGate.ShouldBlinkRouter(setting, _browser.Session.State, _routerWindow.Visible, _busy, _exiting);
        if (!_blinkConnectOn) _btnConnect.AttentionLevel = 0;
        if (!_blinkRouterOn) _btnToggle.AttentionLevel = 0;
        var any = _blinkConnectOn || _blinkRouterOn;
        if (any == _blinkTimer.Enabled) return;
        if (any)
        {
            _blinkClock.Restart(); // 늘 어두운 쪽에서 시작한다
            _blinkTimer.Start();
        }
        else
        {
            _blinkTimer.Stop();
            _blinkClock.Reset();
        }
    }

    private void RefreshPills()
    {
        var p = Theme.Current;
        switch (_browser.Session.State)
        {
            case SessionState.LoggedIn when _flow.Login.State == StepState.Warning:
                _routerPill.Set("공유기 · 관리도구 선택 필요", p.Warning, Theme.Glyph.Router);
                break;
            case SessionState.LoggedIn when _preparingAdmin:
                _routerPill.Set("공유기 · 관리 화면 준비 중", p.Info, Theme.Glyph.Router);
                break;
            case SessionState.LoggedIn:
                _routerPill.Set("공유기 로그인됨", p.Success, Theme.Glyph.Router);
                break;
            case SessionState.LoggedOut:
                _routerPill.Set("공유기 로그인 필요", p.Warning, Theme.Glyph.Router);
                break;
            default:
                _routerPill.Set("공유기 확인 전", p.Muted, Theme.Glyph.Router);
                break;
        }

        var vpnConfigured = InputRules.IsValidVpnName(_settings.VpnName);
        _vpnPill.Visible = vpnConfigured;
        if (vpnConfigured)
        {
            if (_vpnConnecting) _vpnPill.Set("VPN 연결 중", p.Info, Theme.Glyph.Lock);
            else if (_vpnConnectedCache == true) _vpnPill.Set("VPN 연결됨", p.Success, Theme.Glyph.Lock);
            else if (_vpnConnectedCache == false) _vpnPill.Set("VPN 연결 안 됨", p.Muted, Theme.Glyph.Lock);
            else _vpnPill.Set("VPN 확인 전", p.Muted, Theme.Glyph.Lock);
        }
        var power = _power.Status;
        _powerPill.Visible = power.State != PcPowerState.Disabled;
        if (_powerPill.Visible)
        {
            var color = power.State switch
            {
                PcPowerState.On => p.Success,
                PcPowerState.Checking => p.Info,
                _ => p.Muted,
            };
            _powerPill.Set(power.PillText, color, Theme.Glyph.Power);
            _tips.SetToolTip(_powerPill, power.Detail + " (눌러서 지금 확인)");
        }
        RefreshAttentionBlink();
        LayoutHeader();
    }

    private void OnUiTick()
    {
        _uiTicks++;
        _flow.Tick(DateTimeOffset.Now);
        if (_uiTicks % 5 == 1) PollVpn();
        // 자체검사는 포트 확인 횟수를 세므로 주기 확인을 끄고 필요한 곳에서 직접 부른다.
        if (!_options.SelfTest) _power.Tick();
    }

    private void PollVpn()
    {
        if (!InputRules.IsValidVpnName(_settings.VpnName)) return;
        bool connected;
        try { connected = _vpn.IsConnected(_settings.VpnName); }
        catch { return; }
        if (_vpnConnectedCache != connected)
        {
            _vpnConnectedCache = connected;
            RefreshPills();
        }
    }

    // ================================================================== 초기화

    private async Task InitializeAsync()
    {
        try
        {
            if (_options.Mock || _options.SelfTest)
            {
                _mock = new MockRouterServer(_log);
                _mock.Start();
                _settings.RouterUrl = _mock.BaseUrl + "ui/";
                _settings.PublicHost = "127.0.0.1";
                _settings.PublicRdpPort = _mock.FakeRdpPort;
                _settings.BootWaitSeconds = 30;
                if (_settings.WolPcName.Length == 0) _settings.WolPcName = MockRouterServer.DefaultTargetName;
                _log.Info("모의 공유기 서버: " + _mock.BaseUrl);
            }

            SetStatus("내장 브라우저를 준비하는 중...", BannerKind.Progress);
            // 공유기 화면은 별도 창에 있다. 창을 먼저 띄워야 핸들이 생겨 브라우저를 준비할 수 있다.
            SetBrowserExpanded(true);
            await _browser.InitializeAsync(CancellationToken.None);
            _browser.ScriptDialogHandler = OnScriptDialog;
            _browser.Session.StateChanged += (o, n, r) => SafeInvoke(() => OnSessionStateChanged(o, n, r));
            _browser.Network.CallCompleted += c => SafeInvoke(() => OnApiCall(c));
            _browser.NavigationFinished += ok => SafeInvoke(() => { if (ok) _ = ProbeSessionNowAsync(); });
            _browser.UrlChanged += _ => SafeInvoke(RefreshUi);
            _wol.StageChanged += (s, st, m) => SafeInvoke(() => OnWolStage(s, st, m));
            _wol.DialogAppeared = () => { SafeInvoke(() => { SetBrowserExpanded(true); NotifyAttention(); }); return Task.CompletedTask; };
            _wol.UserActionNeeded = () => { SafeInvoke(() => { SetBrowserExpanded(true); NotifyAttention(); }); return Task.CompletedTask; };

            _sessionTimer.Interval = Math.Max(5, _settings.SessionProbeIntervalSeconds) * 1000;
            _sessionTimer.Start();
            _initDone = true;
            _initTcs.TrySetResult(true);
            PollVpn();

            var errors = _settings.ValidateRouter();
            if (ShouldRunSetup(_settings, _options) && !SuppressDialogs)
            {
                // 첫 실행: 시작 설정으로 꼭 필요한 값을 받는다(저장하면 공유기 화면이 열림)
                if (!RunSetupWizard()) SetStatus(SetupSkippedMessage, BannerKind.Warning);
            }
            else if (errors.Count > 0)
            {
                SetStatus("설정(⚙)에서 공유기 주소와 WOL 대상 PC 이름을 먼저 입력하세요.", BannerKind.Warning);
                _log.Warn(string.Join(" / ", errors));
                if (!SuppressDialogs) OpenSettings();
            }
            else
            {
                await NavigateToRouterAsync(expand: true);
            }
        }
        catch (Exception ex)
        {
            _initTcs.TrySetResult(false);
            SetStatus("내장 브라우저를 시작하지 못했습니다: " + ex.Message, BannerKind.Error);
            if (!SuppressDialogs)
            {
                MessageBox.Show(this, "WebView2 런타임을 초기화하지 못했습니다.\n" + ex.Message +
                    "\n\nMicrosoft Edge WebView2 런타임이 설치되어 있는지 확인하세요.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void SafeInvoke(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(a); } catch { /* closing */ } }
        else a();
    }

    private bool OnScriptDialog(string kind, string message)
    {
        ScriptDialogCount++;
        if (ScriptDialogAutoAccept) return true;
        SetBrowserExpanded(true);
        NotifyAttention();
        if (string.Equals(kind, "Alert", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, message, "공유기 페이지 알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        var r = MessageBox.Show(this, "공유기 페이지가 확인을 요청합니다:\n\n" + message, "공유기 페이지 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        return r == DialogResult.Yes;
    }

    // ================================================================== 세션

    public async Task<SessionProbeDetail> ProbeSessionNowAsync()
    {
        if (!_browser.IsReady) return new SessionProbeDetail(SessionProbeResult.Unavailable, "브라우저 준비 안 됨", 0, null);
        SessionProbeDetail d;
        try
        {
            d = await _browser.RefreshSessionAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            d = new SessionProbeDetail(SessionProbeResult.Unavailable, ex.Message, 0, null);
            _browser.Session.Apply(d.Result, d.Reason);
        }
        if (d.Result == SessionProbeResult.Unavailable) _log.Debug("세션 확인 불가(상태 유지): " + d.Reason);
        else if (_browser.Session.State == SessionState.LoggedIn) _flow.OnSession(SessionState.LoggedIn, _browser.Session.LastConfirmedAt);
        RefreshUi();
        return d;
    }

    private void OnSessionStateChanged(SessionState oldState, SessionState newState, string reason)
    {
        _flow.OnSession(newState, _browser.Session.LastConfirmedAt);
        RefreshUi();
        if (_exiting)
        {
            // [종료] 중의 로그아웃은 의도한 것이므로 만료 경고·화면 펼침·알림을 하지 않는다.
            return;
        }
        if (newState == SessionState.LoggedIn && oldState == SessionState.LoggedOut && !_busy)
        {
            // 새로 로그인하면 이전 실행의 PC 켜기·부팅·접속 결과를 지운다(지난 결과가 "완료"로 남아 헷갈리지 않도록).
            _flow.ResetForNewSession();
        }
        if (newState == SessionState.LoggedIn)
        {
            _log.Info("공유기 로그인 확인됨 → [PC 켜기] 활성화 (" + reason + ")");
            SetStatus("공유기 로그인 확인. 관리 화면을 준비하는 중...", BannerKind.Progress);
            if (!_busy)
            {
                // 로그인이 확인되면 곧바로 접는다. 로그인 직후의 [관리도구]/[설정마법사] 선택은 접힌 채로 처리하고,
                // 자동으로 넘기지 못하면 SettleAfterLoginAsync가 다시 펼쳐 사용자에게 맡긴다.
                // [관리도구] 자동 선택을 끈 경우에는 사용자가 눌러야 하므로 관리 화면이 확인된 뒤에 접는다.
                if (_settings.AutoCollapseAfterLogin && _settings.AutoSelectAdminTool) SetBrowserExpanded(false);
                _ = SettleAfterLoginAsync();
            }
        }
        else if (newState == SessionState.LoggedOut)
        {
            _settleCts?.Cancel();
            if (oldState == SessionState.LoggedIn)
            {
                _log.Warn("공유기 세션 만료/로그아웃 감지 → [PC 켜기] 비활성화 (" + reason + ")");
                SetStatus("공유기 세션이 만료되었습니다. 공유기 화면 창에서 다시 로그인하세요.", BannerKind.Warning);
                SetBrowserExpanded(true);
                NotifyAttention();
            }
            else
            {
                SetStatus("공유기 화면 창에서 아이디·비밀번호·보안문자를 입력해 로그인하세요. 로그인이 확인되면 창은 저절로 닫힙니다.", BannerKind.Info);
            }
        }
    }

    /// <summary>
    /// 로그인 확인 후 관리 화면 준비:
    /// 선택 화면이면 설정에 따라 [관리도구]를 누르고, 관리 화면이 확인되면 공유기 화면을 접는다.
    /// 자동으로 넘기지 못하면 화면을 펼친 채 사용자에게 안내한다.
    /// </summary>
    public async Task<NavResult?> SettleAfterLoginAsync()
    {
        _settleCts?.Cancel();
        LastSettle = null;
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _settleCts = cts;
        // "관리 화면이 준비됐습니다"가 뜨기 전에는 동작 버튼을 막는다([관리도구] 자동 선택과 겹치지 않도록).
        SetAdminPreparing(true);
        try
        {
            var nav = await _wol.Navigator.EnsureAdminToolAsync(_settings.AutoSelectAdminTool, cts.Token);
            if (nav.Status == NavStatus.NeedUserSelect)
            {
                SetBrowserExpanded(true);
                _flow.OnAdminSelectNeeded(true);
                RefreshUi();
                SetStatus(nav.Message, BannerKind.Warning);
                NotifyAttention();
                _log.Info("관리 화면 대기: " + nav.Message);
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token);
                    var (kind, _) = await _wol.Navigator.ClassifyAsync(cts.Token);
                    if (kind is RouterPageKind.AdminMain or RouterPageKind.WolList)
                    {
                        nav = new NavResult(NavStatus.Ok, "관리 화면 표시됨", kind, "사용자");
                        break;
                    }
                }
            }
            _flow.OnAdminSelectNeeded(false);
            if (nav.Status == NavStatus.Ok)
            {
                SetAdminPreparing(false);
                SetStatus("공유기 관리 화면이 준비됐습니다. [PC 켜고 접속]을 누르면 끝까지 진행합니다.", BannerKind.Success);
                if (_settings.AutoCollapseAfterLogin && _browserExpanded && !_busy) SetBrowserExpanded(false);
            }
            else if (nav.Status == NavStatus.Failed)
            {
                if (!_busy) SetBrowserExpanded(true);
                SetStatus("로그인은 확인됐지만 관리 화면을 확인하지 못했습니다. 필요하면 공유기 화면 창에서 [관리도구]를 누르세요.", BannerKind.Warning);
                _log.Warn("관리 화면 확인 실패: " + nav.Message);
            }
            RefreshUi();
            LastSettle = nav;
            return nav;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Warn("로그인 후 화면 준비 오류: " + ex.Message);
            if (!_busy && !_exiting && _browser.Session.IsLoggedIn)
            {
                SetBrowserExpanded(true);
                SetStatus("로그인은 확인됐지만 관리 화면을 준비하지 못했습니다. 필요하면 공유기 화면 창에서 [관리도구]를 누르세요.", BannerKind.Warning);
            }
            return null;
        }
        finally
        {
            if (ReferenceEquals(_settleCts, cts))
            {
                _settleCts = null;
                // 실패·시간 초과·취소로 끝나도 버튼이 계속 막혀 있지 않게 푼다(새 준비 작업이 시작된 경우는 그쪽이 관리).
                SetAdminPreparing(false);
            }
            cts.Dispose();
        }
    }

    private void SetAdminPreparing(bool preparing)
    {
        if (_preparingAdmin == preparing) return;
        _preparingAdmin = preparing;
        _flow.OnAdminPreparing(preparing);
        if (!IsDisposed) RefreshUi();
    }

    private void OnApiCall(ApiCall c)
    {
        if (c.Method.Equals("session/info", StringComparison.OrdinalIgnoreCase))
            _log.Debug("공유기 API " + c); // 주기적으로 반복되므로 화면 기록에는 표시하지 않음
        else if (c.Method.StartsWith("session/", StringComparison.OrdinalIgnoreCase) || c.Method.StartsWith("wol/", StringComparison.OrdinalIgnoreCase))
            _log.Info("공유기 API " + c);
        if (c.Method is "session/login" or "session/logout")
        {
            var t = new System.Windows.Forms.Timer { Interval = 400 };
            t.Tick += async (_, _) => { t.Stop(); t.Dispose(); if (!_busy) await ProbeSessionNowAsync(); };
            t.Start();
        }
    }

    // ================================================================== 공유기 화면

    public async Task NavigateToRouterAsync(bool expand)
    {
        if (!_browser.IsReady) return;
        if (!InputRules.TryParseRouterUrl(_settings.RouterUrl, out var uri) || uri == null)
        {
            SetStatus("공유기 주소가 올바르지 않습니다. 설정(⚙)을 확인하세요.", BannerKind.Error);
            return;
        }
        if (expand) SetBrowserExpanded(true);
        SetStatus("공유기 관리자 페이지를 여는 중...", BannerKind.Progress);
        await _browser.NavigateAsync(uri.ToString(), TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(300);
        await ProbeSessionNowAsync();
        if (_browser.Session.State != SessionState.LoggedIn)
            SetStatus("공유기 화면 창에서 아이디·비밀번호·보안문자를 입력해 로그인하세요. 로그인이 확인되면 창은 저절로 닫힙니다.", BannerKind.Info);
        // 로그인 화면에서도 상태 판독을 위해 접근성 트리를 켜 둔다(입력은 사용자가 직접).
        _ = _browser.EnsureSemanticsAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
    }

    public void SetBrowserExpanded(bool expanded)
    {
        if (_browserExpanded == expanded && _initDone) return;
        _browserExpanded = expanded;
        if (expanded)
        {
            _routerWindow.ShowFor(this, _browser.Session.IsLoggedIn ? "공유기 관리 화면" : "공유기 로그인");
        }
        else
        {
            // 숨길 때 입력 초점이 공유기 화면에 남아 있으면 보이지 않는 페이지로 키 입력이 가므로 메인 창으로 옮긴다.
            var hadFocus = _routerWindow.ContainsFocus;
            _routerWindow.Hide();
            if (hadFocus && !_actions.SelectNextControl(null, true, true, false, true)) ActiveControl = null;
            if (hadFocus && !_exiting) Activate();
            // 공유기 화면에서 입력하다 닫으면 숨겨진 커서가 메인 창 위에 남을 수 있다(CursorGuard 설명 참고).
            _cursorGuard.RestoreIfHiddenAtPointer("공유기 창 닫음");
        }
        RefreshUi();
        _log.Debug(expanded ? "공유기 창 띄움" : "공유기 창 숨김(세션·브라우저 상태 유지)");
    }

    // ================================================================== 작업

    private void BeginOperation()
    {
        _busy = true;
        _opCts = new CancellationTokenSource();
        RefreshUi();
    }

    private void EndOperation()
    {
        _busy = false;
        _opCts?.Dispose();
        _opCts = null;
        RefreshUi();
    }

    public void CancelOperation()
    {
        if (_opCts is { IsCancellationRequested: false })
        {
            _log.Warn("사용자가 작업 취소를 요청했습니다.");
            _opCts.Cancel();
        }
    }

    private void OnWolStage(WolStep step, StageStatus status, string message)
    {
        // 로그인 직후 화면 준비(관리도구 선택)도 같은 이동 알림을 쓰므로,
        // PC 켜기 단계 표시는 실제로 PC 켜기를 실행 중일 때만 바꾼다.
        if (!_wakeRunning)
        {
            if (step == WolStep.NavigateToWol && status == StageStatus.Running) SetStatus(message, BannerKind.Progress);
            return;
        }
        _flow.OnWolStage(step, status, message);
        var kind = status switch
        {
            StageStatus.Failed => BannerKind.Error,
            StageStatus.Unknown => BannerKind.Warning,
            _ when message.Contains("누르세요", StringComparison.Ordinal) => BannerKind.Warning,
            _ => BannerKind.Progress,
        };
        SetStatus($"[{StepName(step)}] {message}", kind);
    }

    private static string StepName(WolStep s) => s switch
    {
        WolStep.SessionCheck => "세션 확인",
        WolStep.NavigateToWol => "WOL 화면",
        WolStep.Match => "대상 찾기",
        WolStep.Click => "버튼 클릭",
        WolStep.Confirm => "확인창",
        WolStep.RouterResponse => "공유기 응답",
        _ => s.ToString(),
    };

    public async Task<WolOutcome> RunWakeAsync(bool skipNavigation)
    {
        if (_busy) return WolOutcome.Fail(WolStep.SessionCheck, "다른 작업이 진행 중입니다.");
        var errors = _settings.ValidateRouter();
        if (errors.Count > 0)
        {
            var msg = string.Join(" ", errors) + " 설정(⚙)에서 입력하세요.";
            SetStatus(msg, BannerKind.Error);
            return WolOutcome.Fail(WolStep.SessionCheck, msg);
        }
        if (!_browser.Session.IsLoggedIn)
        {
            const string msg = "공유기 로그인이 확인되지 않았습니다. 공유기 화면 창에서 먼저 로그인하세요.";
            SetStatus(msg, BannerKind.Warning);
            SetBrowserExpanded(true);
            return WolOutcome.Fail(WolStep.SessionCheck, msg);
        }

        // 로그인 후 화면 준비 작업과 동시에 공유기 화면을 조작하지 않도록 먼저 멈춘다.
        _settleCts?.Cancel();
        _flow.OnWolStarted();
        BeginOperation();
        _wakeRunning = true;
        WolOutcome outcome;
        try
        {
            outcome = await _wol.WakeAsync(skipNavigation, _opCts!.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = WolOutcome.Fail(WolStep.Click, "PC 켜기 작업이 취소되었습니다.");
        }
        catch (Exception ex)
        {
            _log.Error("PC 켜기 오류: " + ex);
            outcome = WolOutcome.Fail(WolStep.Click, "오류: " + ex.Message);
        }
        finally
        {
            _wakeRunning = false;
            EndOperation();
        }

        _flow.OnWolOutcome(outcome, DateTimeOffset.Now);
        if (outcome.Success)
        {
            _log.Info("WOL 완료: " + outcome.Message);
            _power.CheckSoon(TimeSpan.FromSeconds(20)); // 부팅할 시간을 조금 준 뒤 전원 배지를 갱신
            SetStatus(outcome.RouterStatus == StageStatus.Done
                    ? "공유기가 PC 켜기 요청을 처리했습니다. 부팅 여부는 [PC 접속]에서 RDP 응답으로 확인합니다."
                    : outcome.Message,
                outcome.RouterStatus == StageStatus.Done ? BannerKind.Success : BannerKind.Warning);
            if (_settings.AutoCollapseAfterLogin && !SuppressDialogs) SetBrowserExpanded(false);
        }
        else
        {
            var cancelled = !outcome.RequestAborted && outcome.Message.Contains("취소되었습니다", StringComparison.Ordinal);
            _log.Warn("WOL 실패(" + StepName(outcome.LastStep) + "): " + outcome.Message);
            SetStatus(cancelled ? outcome.Message : outcome.Message + " 공유기 화면 창을 띄워 두었으니 필요하면 직접 [PC 켜기]를 누르세요.",
                cancelled || outcome.RequestAborted ? BannerKind.Warning : BannerKind.Error);
            if (!cancelled && outcome.LastStep is WolStep.Match or WolStep.NavigateToWol or WolStep.Confirm or WolStep.RouterResponse or WolStep.SessionCheck)
                SetBrowserExpanded(true);
            if (!cancelled) NotifyAttention();
        }
        RefreshUi();
        return outcome;
    }

    public async Task<ConnectOutcome> RunConnectAsync(ConnectMode mode)
    {
        if (_busy) return new ConnectOutcome(ConnectStage.Failed, false, "다른 작업이 진행 중입니다.", TimeSpan.Zero, false, mode);
        var errors = _settings.ValidateConnect(mode);
        if (errors.Count > 0)
        {
            var msg = string.Join(" ", errors) + " 설정(⚙)에서 입력하세요.";
            SetStatus(msg, BannerKind.Error);
            var failed = new ConnectOutcome(ConnectStage.Failed, false, msg, TimeSpan.Zero, false, mode);
            _flow.OnConnectOutcome(failed, DateTimeOffset.Now);
            return failed;
        }

        _flow.OnConnectStarted(mode, _settings.BootWaitSeconds);
        BeginOperation();
        var progress = new Progress<ConnectProgress>(p =>
        {
            _flow.OnConnectProgress(p, DateTimeOffset.Now);
            var kind = p.Stage == ConnectStage.PortOpen ? BannerKind.Success : BannerKind.Progress;
            SetStatus($"[PC 접속] {p.Message}", kind);
            if (p.Stage == ConnectStage.VpnConnecting) { _vpnConnecting = true; RefreshPills(); }
            if (p.Stage == ConnectStage.VpnConnected) { _vpnConnecting = false; _vpnConnectedCache = true; RefreshPills(); }
        });
        ConnectOutcome outcome;
        try
        {
            outcome = await _connect.RunAsync(_settings, mode, progress, _opCts!.Token);
        }
        finally
        {
            _vpnConnecting = false;
            EndOperation();
        }

        // Progress<T>는 비동기로 전달되므로 결과 반영 전에 남은 진행 알림을 먼저 처리한다.
        await Task.Yield();
        _flow.OnConnectOutcome(outcome, DateTimeOffset.Now);
        PollVpn();
        _power.CheckSoon();
        if (outcome.Success)
        {
            SetStatus("[PC 접속] " + outcome.Message, BannerKind.Success);
        }
        else
        {
            SetStatus("[PC 접속] " + outcome.Message, outcome.IsCancelled ? BannerKind.Warning : BannerKind.Error);
            if (!outcome.IsCancelled) NotifyAttention();
        }
        RefreshUi();
        return outcome;
    }

    /// <summary>[PC 켜고 접속]: 접속 설정을 먼저 확인 → PC 켜기 → 성공하면 이어서 접속. PC 켜기가 실패하면 접속하지 않는다.</summary>
    public async Task<(WolOutcome Wol, ConnectOutcome? Connect)> RunWakeAndConnectAsync(ConnectMode mode)
    {
        if (_busy) return (WolOutcome.Fail(WolStep.SessionCheck, "다른 작업이 진행 중입니다."), null);
        var connectErrors = _settings.ValidateConnect(mode);
        if (connectErrors.Count > 0)
        {
            var msg = string.Join(" ", connectErrors) + " 설정(⚙)에서 입력하세요.";
            SetStatus(msg, BannerKind.Error);
            return (WolOutcome.Fail(WolStep.SessionCheck, msg), null);
        }

        var wol = await RunWakeAsync(skipNavigation: false);
        if (!wol.Success) return (wol, null);

        SetStatus("PC 켜기 요청 완료. 이어서 부팅을 기다린 뒤 접속합니다.", BannerKind.Progress);
        var connect = await RunConnectAsync(mode);
        return (wol, connect);
    }

    // ================================================================== 종료

    /// <summary>
    /// [종료] 준비: 진행 중인 작업을 멈추고(VPN은 유지) 공유기 관리 세션을 로그아웃한 뒤 결과를 돌려준다.
    /// 창은 닫지 않는다(자체검사에서도 쓰기 위해). 실패하면 _exiting을 되돌려 계속 쓸 수 있게 한다.
    /// </summary>
    public async Task<RouterBrowser.LogoutResult> PrepareExitAsync()
    {
        _exiting = true;
        _modePopup?.Close();
        _settleCts?.Cancel();
        RefreshUi();
        if (_busy)
        {
            CancelOperation();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (_busy && DateTime.UtcNow < deadline) await Task.Delay(100);
        }

        SetStatus("공유기 관리 세션을 로그아웃하는 중...", BannerKind.Progress);
        RouterBrowser.LogoutResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            result = await _browser.LogoutAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new RouterBrowser.LogoutResult(false, false, "공유기가 제한 시간 안에 응답하지 않았습니다.");
        }
        catch (Exception ex)
        {
            result = new RouterBrowser.LogoutResult(false, false, "로그아웃 중 오류: " + ex.Message);
        }

        _log.Info($"종료 준비: {result.Message}");
        _flow.OnSession(_browser.Session.State, _browser.Session.LastConfirmedAt);
        RefreshUi();
        return result;
    }

    /// <summary>[종료] 버튼: 공유기 로그아웃 확인 후 종료. 확인이 안 되면 그래도 종료할지 묻는다.</summary>
    public async Task ExitWithLogoutAsync()
    {
        if (_exiting) return;
        var result = await PrepareExitAsync();
        if (!result.Confirmed)
        {
            if (SuppressDialogs)
            {
                _exiting = false;
                RefreshUi();
                return;
            }
            var r = MessageBox.Show(this,
                "공유기 로그아웃을 확인하지 못했습니다.\n" + result.Message + "\n\n그래도 종료할까요? (공유기 세션은 시간이 지나면 자동으로 만료됩니다)",
                "종료", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes)
            {
                _exiting = false;
                SetStatus("종료를 취소했습니다. " + result.Message, BannerKind.Warning);
                RefreshUi();
                return;
            }
        }
        SetStatus(result.Message + " 프로그램을 종료합니다.", BannerKind.Success);
        _exitApproved = true;
        Close();
    }

    // ================================================================== 설정 / 진단

    private void OpenSettings()
    {
        using var f = new SettingsForm(_settings, _vpn);
        var r = f.ShowDialog(this);
        if (f.ResetRequested)
        {
            ResetSettings(showSetup: true);
            return;
        }
        if (r != DialogResult.OK) return;
        ApplySettings(f.Result, save: true);
    }

    internal const string SetupSkippedMessage = "시작 설정을 건너뛰었습니다. ⚙ 설정에서 공유기 주소와 켤 PC 이름을 입력하면 공유기 화면이 열립니다.";

    /// <summary>시작 설정을 띄울지: 아직 마치지 않았고, 모의 공유기·자체검사 실행이 아닐 때.</summary>
    public static bool ShouldRunSetup(AppSettings s, LaunchOptions options) => !s.SetupCompleted && !options.Mock && !options.SelfTest;

    /// <summary>시작 설정 창. 완료하면 저장하고 공유기 화면을 연다. 건너뛰면 false.</summary>
    public bool RunSetupWizard()
    {
        using var w = new SetupWizardForm(_settings, _vpn, _portProbe);
        if (w.ShowDialog(this) != DialogResult.OK)
        {
            _log.Info("시작 설정을 건너뜀");
            return false;
        }
        _log.Info("시작 설정 완료");
        var routerChanged = !string.Equals(w.Result.RouterUrl, _settings.RouterUrl, StringComparison.OrdinalIgnoreCase);
        ApplySettings(w.Result, save: true);
        if (!routerChanged && _initDone) _ = NavigateToRouterAsync(expand: true);
        return true;
    }

    /// <summary>
    /// 설정 초기화: 모든 설정을 처음 상태로 되돌려 저장하고, 열려 있던 공유기 화면을 비운 뒤 시작 설정을 다시 연다.
    /// 기록 파일과 Windows VPN 연결은 건드리지 않는다.
    /// </summary>
    public void ResetSettings(bool showSetup)
    {
        _log.Warn("설정 초기화: 모든 설정을 처음 상태로 되돌립니다(기록·VPN 연결은 그대로).");
        _settleCts?.Cancel();
        var fresh = new AppSettings();
        fresh.ShowLog = _logVisible;
        _settings = fresh;
        TrySaveSettings();
        _browser.Session.Reset("설정 초기화");
        _flow.OnSession(SessionState.Unknown, null);
        _flow.ResetForNewSession();
        if (_browser.IsReady) _ = _browser.NavigateAsync("about:blank", TimeSpan.FromSeconds(5), CancellationToken.None);
        SetThemeMode(Theme.ParseMode(_settings.Theme), save: false);
        SetStatus("설정을 초기화했습니다. 시작 설정에서 공유기 주소와 켤 PC를 다시 입력하세요.", BannerKind.Info);
        RefreshUi();
        if (showSetup && !SuppressDialogs && !RunSetupWizard()) SetStatus(SetupSkippedMessage, BannerKind.Warning);
    }

    public void ApplySettings(AppSettings s, bool save)
    {
        // ⚙ 설정에서 공유기 설정을 제대로 저장하면 시작 설정을 마친 것으로 본다.
        if (save && s.ValidateRouter().Count == 0) s.SetupCompleted = true;
        var routerChanged = !string.Equals(s.RouterUrl, _settings.RouterUrl, StringComparison.OrdinalIgnoreCase);
        var themeChanged = !string.Equals(s.Theme, _settings.Theme, StringComparison.OrdinalIgnoreCase);
        // 창 위치·기록 표시 같은 화면 상태는 설정 창에서 편집하지 않으므로 현재 값을 유지한다.
        s.ShowLog = _logVisible;
        s.WindowLeft = _settings.WindowLeft;
        s.WindowTop = _settings.WindowTop;
        _settings = s;
        if (save) TrySaveSettings();
        _sessionTimer.Interval = Math.Max(5, _settings.SessionProbeIntervalSeconds) * 1000;
        if (themeChanged) SetThemeMode(Theme.ParseMode(_settings.Theme), save: false);
        _vpnConnectedCache = null;
        PollVpn();
        RefreshUi();
        if (routerChanged)
        {
            _browser.Session.Reset("공유기 URL 변경");
            _flow.OnSession(SessionState.Unknown, null);
            RefreshUi();
            if (_initDone)
            {
                if (s.RouterUri != null) _ = NavigateToRouterAsync(expand: true);
                else if (_browser.IsReady) _ = _browser.NavigateAsync("about:blank", TimeSpan.FromSeconds(5), CancellationToken.None);
            }
        }
    }

    private void TrySaveSettings()
    {
        try { _settings.Save(_settingsPath); }
        catch (Exception ex) { _log.Warn("설정 저장 실패: " + ex.Message); }
    }

    private void RestoreWindowPosition()
    {
        if (_options.SelfTest || _settings.WindowLeft is not { } x || _settings.WindowTop is not { } y) return;
        var probe = new Point(x + L(60), y + L(20));
        if (Screen.AllScreens.Any(s => s.WorkingArea.Contains(probe)))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
        }
    }

    private void SaveWindowPosition()
    {
        if (_options.SelfTest || WindowState != FormWindowState.Normal) return;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        TrySaveSettings();
    }

    public async Task<string?> ExportDiagnosticsAsync(string? path = null)
    {
        if (path == null)
        {
            if (SuppressDialogs)
            {
                path = Path.Combine(Path.GetTempPath(), DiagnosticsExporter.DefaultFileName());
            }
            else
            {
                using var dlg = new SaveFileDialog
                {
                    FileName = DiagnosticsExporter.DefaultFileName(),
                    Filter = "JSON 파일|*.json",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Title = "진단 파일 저장 (비밀번호·쿠키·원본 HTML 미포함)",
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                path = dlg.FileName;
            }
        }
        try
        {
            var json = await DiagnosticsExporter.BuildAsync(_browser, _settings, _log, CancellationToken.None);
            await File.WriteAllTextAsync(path, json);
            _log.Info("진단 파일 저장: " + path);
            SetStatus("진단 파일을 저장했습니다: " + path, BannerKind.Success);
            return path;
        }
        catch (Exception ex)
        {
            _log.Error("진단 파일 저장 실패: " + ex.Message);
            SetStatus("진단 파일을 저장하지 못했습니다: " + ex.Message, BannerKind.Error);
            return null;
        }
    }
}
