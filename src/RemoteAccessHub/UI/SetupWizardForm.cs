using RemoteAccessHub.Core;
using RemoteAccessHub.Services;
using RemoteAccessHub.UI.Controls;

namespace RemoteAccessHub.UI;

/// <summary>
/// 첫 실행 시작 설정: 시작 → 공유기 주소 → 켤 PC → 접속 방법 → 확인.
/// 꼭 필요한 값만 받고 나머지는 기본값을 쓴다. 비밀번호 입력칸은 없다(공유기 로그인은 매번 공유기 화면에서 직접).
/// </summary>
public sealed class SetupWizardForm : Form
{
    public enum Page { Welcome, Router, Pc, Connect, Done }

    private static readonly string[] StepNames = { "시작", "공유기 주소", "켤 PC", "접속 방법", "확인" };

    private readonly AppSettings _work;
    private readonly IVpnService _vpn;
    private readonly IPortProbe _probe;

    private readonly Label _stepLabel = new() { AutoSize = true, Tag = "sub" };
    private readonly Label _title = new() { AutoSize = true, Tag = "keep" };
    private readonly Label _desc = new() { AutoSize = true, Tag = "sub" };
    private readonly StepStrip _strip = new() { Dock = DockStyle.Fill };
    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    private readonly Label _error = new() { Dock = DockStyle.Bottom, Tag = "keep", TextAlign = ContentAlignment.MiddleLeft, Visible = false };
    private readonly FlatButton _back = new() { Text = "이전", Variant = ButtonVariant.Secondary };
    private readonly FlatButton _next = new() { Text = "다음", Variant = ButtonVariant.Primary };
    private readonly FlatButton _later = new() { Text = "나중에 설정", Variant = ButtonVariant.Ghost };
    private readonly Panel[] _pages = new Panel[5];
    private readonly Panel _buttons = new() { Dock = DockStyle.Bottom };

    // --- 입력칸 (자체검사에서 값을 넣기 위해 internal)
    internal readonly TextBox RouterUrlBox = new() { PlaceholderText = "http://myhome.iptime.org:8080/" };
    private readonly FlatButton _checkRouter = new() { Text = "연결 확인", Variant = ButtonVariant.Secondary, Glyph = Theme.Glyph.Sync };
    private readonly Label _checkResult = new() { AutoSize = true, Tag = "keep" };
    internal readonly CheckBox AllowCertBox = new() { Text = "이 공유기 주소에 한해 https 인증서 오류 허용 (자체 서명 인증서일 때만)" };
    internal readonly TextBox PcNameBox = new() { PlaceholderText = "예: MY-PC" };
    internal readonly TextBox PcMacBox = new() { PlaceholderText = "선택 · 예: 00:11:22:33:44:55" };
    internal readonly CheckBox UseDirectBox = new() { Text = "일반 접속 — 공유기 포트포워딩으로 바로 연결" };
    internal readonly TextBox PublicHostBox = new() { PlaceholderText = "예: myhome.iptime.org" };
    internal readonly NumericUpDown PublicPortBox = new() { Minimum = 1, Maximum = 65535, Value = 3389 };
    internal readonly CheckBox UseVpnBox = new() { Text = "VPN 접속 — Windows VPN을 연결한 뒤 집 내부 IP로 연결" };
    internal readonly ComboBox VpnNameBox = new() { DropDownStyle = ComboBoxStyle.DropDown };
    internal readonly TextBox VpnIpBox = new() { PlaceholderText = "예: 192.168.0.10" };
    internal readonly NumericUpDown VpnPortBox = new() { Minimum = 1, Maximum = 65535, Value = 3389 };
    private readonly TableLayoutPanel _summary = new() { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
    private readonly List<Control> _directFields = new();
    private readonly List<Control> _vpnFields = new();

    public Page CurrentPage { get; private set; }
    public string ErrorText => _error.Visible ? _error.Text : "";
    public AppSettings Result => _work;

    public SetupWizardForm(AppSettings current, IVpnService vpn, IPortProbe probe)
    {
        _work = current.Clone();
        _vpn = vpn;
        _probe = probe;

        Text = "RemoteAccessHub 시작 설정";
        AppIcon.ApplyTo(this);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.UiFont(9.5f);
        ClientSize = new Size(L(720), L(600));

        // --- 머리: 단계 이름 · 제목 · 설명
        var header = new Panel { Dock = DockStyle.Top, Height = L(112) };
        _stepLabel.Location = new Point(L(28), L(18));
        _title.Font = Theme.UiFont(15f, FontStyle.Bold);
        _title.Location = new Point(L(26), L(38));
        _desc.Location = new Point(L(28), L(76));
        _desc.MaximumSize = new Size(L(660), 0);
        header.Controls.AddRange(new Control[] { _stepLabel, _title, _desc });
        var stripHost = new Panel { Dock = DockStyle.Top, Height = L(6), Padding = new Padding(L(28), 0, L(28), 0) };
        stripHost.Controls.Add(_strip);

        // --- 아래: 오류 · 버튼
        _error.Height = L(30);
        _error.Padding = new Padding(L(28), 0, L(28), 0);
        _buttons.Height = L(66);
        _buttons.Padding = new Padding(L(24), L(12), L(24), L(14));
        _buttons.Controls.AddRange(new Control[] { _later, _back, _next });
        _buttons.Resize += (_, _) => LayoutButtons();
        _next.Click += (_, _) => Next();
        _back.Click += (_, _) => Back();
        _later.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _checkRouter.Click += async (_, _) => await CheckRouterAsync();
        RouterUrlBox.TextChanged += (_, _) => { UpdateCertOption(); _checkResult.Text = ""; };
        UseDirectBox.CheckedChanged += (_, _) => UpdateConnectFields();
        UseVpnBox.CheckedChanged += (_, _) => UpdateConnectFields();
        // 입력을 고치면 앞서 띄운 오류 문구는 지운다(다음을 누를 때 다시 검사).
        foreach (var tb in new Control[] { RouterUrlBox, PcNameBox, PcMacBox, PublicHostBox, VpnNameBox, VpnIpBox }) tb.TextChanged += (_, _) => SetError(null);
        foreach (var nud in new[] { PublicPortBox, VpnPortBox }) nud.ValueChanged += (_, _) => SetError(null);
        foreach (var cb in new[] { UseDirectBox, UseVpnBox, AllowCertBox }) cb.CheckedChanged += (_, _) => SetError(null);

        BuildPages();

        Controls.Add(_body);
        Controls.Add(_error);
        Controls.Add(_buttons);
        Controls.Add(stripHost);
        Controls.Add(header);
        AcceptButton = _next;
        CancelButton = _later;

        Bind();
        ApplyTheme();
        HandleCreated += (_, _) => Theme.ApplyTitleBar(this);
        ShowPage(Page.Welcome);
    }

    private int L(int logical) => LogicalToDeviceUnits(logical);

    private void LayoutButtons()
    {
        var h = Math.Max(1, _buttons.ClientSize.Height - _buttons.Padding.Vertical);
        foreach (var b in new[] { _later, _back }) b.Size = new Size(Math.Max(L(96), b.PreferredWidth()), h);
        _next.Size = new Size(Math.Max(L(120), _next.PreferredWidth()), h);
        _next.Location = new Point(_buttons.ClientSize.Width - _buttons.Padding.Right - _next.Width, _buttons.Padding.Top);
        _back.Location = new Point(_next.Left - L(8) - _back.Width, _buttons.Padding.Top);
        _later.Location = new Point(_buttons.Padding.Left, _buttons.Padding.Top);
    }

    // ------------------------------------------------------------------ 화면 구성

    private void BuildPages()
    {
        _pages[(int)Page.Welcome] = PageOf(t =>
        {
            Heading(t, "이 프로그램이 하는 일");
            Para(t, "① 프로그램 안에 ipTIME 공유기 관리자 화면을 열고, 로그인은 직접 합니다.\n② 공유기의 WOL 기능으로 집 PC를 켭니다.\n③ PC가 켜질 때까지 기다렸다가 원격 데스크톱으로 연결합니다.");
            Heading(t, "준비할 것");
            Para(t, "• 집 밖에서 열 수 있는 공유기 관리자 주소 (DDNS 주소와 원격 관리 포트)\n• 공유기 [특수 기능 → WOL 기능]에 등록한 PC 이름\n• 원격 데스크톱 연결 방법: 공유기 포트포워딩(일반 접속) 또는 Windows VPN 연결");
            Heading(t, "저장하지 않는 것");
            Para(t, "공유기 아이디·비밀번호·보안문자는 입력받지도, 저장하지도 않습니다. 모든 값은 나중에 ⚙ 설정에서 바꿀 수 있습니다.");
        });

        _pages[(int)Page.Router] = PageOf(t =>
        {
            Row(t, "관리자 페이지 주소", RouterUrlBox, "http:// 또는 https://부터 원격 관리 포트까지 전체 주소. 집 밖에서 쓰려면 DDNS 주소를 쓰세요.");
            var checkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, L(4), 0, L(4)) };
            _checkRouter.Size = new Size(Math.Max(L(104), _checkRouter.PreferredWidth()), L(32));
            _checkRouter.Margin = new Padding(0, 0, L(10), 0);
            _checkResult.Margin = new Padding(0, L(7), 0, 0);
            _checkResult.MaximumSize = new Size(L(330), 0);
            checkRow.Controls.Add(_checkRouter);
            checkRow.Controls.Add(_checkResult);
            t.Controls.Add(checkRow, 1, t.RowCount++);
            Hint(t, "[연결 확인]은 이 주소·포트로 연결되는지만 봅니다. 공유기 로그인 화면은 설정을 마친 뒤 열립니다.");
            CheckRow(t, AllowCertBox);
        });

        _pages[(int)Page.Pc] = PageOf(t =>
        {
            Row(t, "PC 이름", PcNameBox, "공유기 WOL 목록에 보이는 이름과 똑같이 입력하세요.");
            Row(t, "MAC 주소 (선택)", PcMacBox, "같은 이름의 PC가 여러 대일 때만 필요합니다.");
            Para(t, "\n프로그램은 이 이름과 (입력했다면) MAC이 정확히 맞는 행의 [PC 켜기]만 누릅니다. 맞는 행이 없거나 여러 개면 아무것도 누르지 않습니다.", sub: true);
        });

        _pages[(int)Page.Connect] = PageOf(t =>
        {
            CheckRow(t, UseDirectBox, top: true);
            _directFields.AddRange(Row(t, "접속 주소", PublicHostBox, "DDNS 주소 또는 공인 IP (포트 제외)"));
            _directFields.AddRange(Row(t, "외부 포트", PublicPortBox, "공유기 포트포워딩에서 원격 데스크톱으로 연결한 외부 포트"));
            CheckRow(t, UseVpnBox, top: true);
            _vpnFields.AddRange(Row(t, "Windows VPN 연결", VpnNameBox, "Windows 설정 > 네트워크 > VPN에 만든 연결 (목록에서 고르기)"));
            _vpnFields.AddRange(Row(t, "집 PC 내부 IP", VpnIpBox, "VPN 연결 뒤 접속할 집 PC의 IP"));
            _vpnFields.AddRange(Row(t, "내부 포트", VpnPortBox));
            Para(t, "\n둘 다 끄면 [PC 켜기]만 쓸 수 있고, 접속 방법은 나중에 ⚙ 설정에서 넣을 수 있습니다.", sub: true);
        });

        _pages[(int)Page.Done] = PageOf(t =>
        {
            _summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, L(150)));
            _summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _summary.BackColor = Color.Transparent;
            t.Controls.Add(_summary, 0, t.RowCount);
            t.SetColumnSpan(_summary, 2);
            t.RowCount++;
            Para(t, "\n[완료]를 누르면 저장하고 공유기 로그인 화면을 엽니다. 아이디·비밀번호·보안문자를 입력해 로그인하면 [PC 켜고 접속]을 누를 수 있습니다.", sub: true);
        });

        foreach (var p in _pages) _body.Controls.Add(p);
    }

    private Panel PageOf(Action<TableLayoutPanel> build)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(L(28), L(18), L(28), L(8)), Visible = false };
        var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, L(160)));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        build(t);
        page.Controls.Add(t);
        return page;
    }

    private Control[] Row(TableLayoutPanel t, string label, Control c, string? hint = null)
    {
        var l = new Label { Text = label, AutoSize = true, Margin = new Padding(0, L(9), L(8), 0) };
        c.Width = L(440);
        c.Margin = new Padding(0, L(4), 0, L(2));
        t.Controls.Add(l, 0, t.RowCount);
        t.Controls.Add(c, 1, t.RowCount);
        t.RowCount++;
        if (hint == null) return new Control[] { l, c };
        var h = Hint(t, hint);
        return new Control[] { l, c, h };
    }

    private Label Hint(TableLayoutPanel t, string text)
    {
        var h = new Label { Text = text, AutoSize = true, MaximumSize = new Size(L(460), 0), Margin = new Padding(0, 0, 0, L(8)), Tag = "sub", Font = Theme.UiFont(8.5f) };
        t.Controls.Add(h, 1, t.RowCount);
        t.RowCount++;
        return h;
    }

    private void CheckRow(TableLayoutPanel t, CheckBox c, bool top = false)
    {
        c.AutoSize = true;
        c.Margin = new Padding(0, top ? L(12) : L(4), 0, L(4));
        if (top) c.Font = Theme.UiFont(9.5f, FontStyle.Bold);
        t.Controls.Add(c, top ? 0 : 1, t.RowCount);
        if (top) t.SetColumnSpan(c, 2);
        t.RowCount++;
    }

    private void Heading(TableLayoutPanel t, string text)
    {
        var l = new Label { Text = text, AutoSize = true, Font = Theme.UiFont(10.5f, FontStyle.Bold), Margin = new Padding(0, L(10), 0, L(4)), Tag = "section" };
        t.Controls.Add(l, 0, t.RowCount);
        t.SetColumnSpan(l, 2);
        t.RowCount++;
    }

    private void Para(TableLayoutPanel t, string text, bool sub = false)
    {
        var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(L(640), 0), Margin = new Padding(0, 0, 0, L(4)), Tag = sub ? "sub" : null };
        t.Controls.Add(l, 0, t.RowCount);
        t.SetColumnSpan(l, 2);
        t.RowCount++;
    }

    private void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Background;
        ForeColor = p.Text;
        Theme.ApplyToStandardControls(this);
        _title.ForeColor = p.Text;
        _error.ForeColor = p.Danger;
        _checkResult.ForeColor = p.SubText;
        _strip.Invalidate();
        Theme.ApplyTitleBar(this);
    }

    // ------------------------------------------------------------------ 값

    private void Bind()
    {
        RouterUrlBox.Text = _work.RouterUrl;
        AllowCertBox.Checked = _work.AllowRouterCertificateError;
        PcNameBox.Text = _work.WolPcName;
        PcMacBox.Text = _work.WolPcMac;
        PublicHostBox.Text = _work.PublicHost;
        PublicPortBox.Value = Math.Clamp(_work.PublicRdpPort, 1, 65535);
        foreach (var n in _vpn.ListEntries()) VpnNameBox.Items.Add(n);
        VpnNameBox.Text = _work.VpnName;
        VpnIpBox.Text = _work.VpnDesktopIp;
        VpnPortBox.Value = Math.Clamp(_work.VpnRdpPort, 1, 65535);
        UseDirectBox.Checked = _work.PublicHost.Length > 0;
        UseVpnBox.Checked = _work.VpnName.Length > 0;
        UpdateCertOption();
        UpdateConnectFields();
    }

    private void UpdateCertOption()
    {
        var https = RouterUrlBox.Text.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        AllowCertBox.Enabled = https;
        if (!https) AllowCertBox.Checked = false;
    }

    private void UpdateConnectFields()
    {
        foreach (var c in _directFields) c.Enabled = UseDirectBox.Checked;
        foreach (var c in _vpnFields) c.Enabled = UseVpnBox.Checked;
    }

    private string? Validate(Page page)
    {
        switch (page)
        {
            case Page.Router:
                if (!InputRules.TryParseRouterUrl(RouterUrlBox.Text.Trim(), out _))
                    return "주소 형식이 올바르지 않습니다. http:// 또는 https://로 시작하는 전체 주소를 입력하세요. 예: http://myhome.iptime.org:8080/";
                return null;
            case Page.Pc:
                if (string.IsNullOrWhiteSpace(PcNameBox.Text)) return "공유기 WOL 목록에 등록된 PC 이름을 입력하세요.";
                if (PcMacBox.Text.Trim().Length > 0 && InputRules.NormalizeMac(PcMacBox.Text.Trim()) == null)
                    return "MAC 주소 형식이 올바르지 않습니다. 예: 00:11:22:33:44:55 (모르면 비워 두세요)";
                return null;
            case Page.Connect:
                if (UseDirectBox.Checked && !InputRules.IsValidHost(PublicHostBox.Text.Trim()))
                    return "일반 접속 주소를 확인하세요. DDNS 주소 또는 공인 IP만 입력합니다(포트 제외).";
                if (UseVpnBox.Checked && !InputRules.IsValidVpnName(VpnNameBox.Text.Trim()))
                    return "Windows VPN 연결 이름을 목록에서 고르거나 입력하세요.";
                if (UseVpnBox.Checked && !InputRules.IsValidHost(VpnIpBox.Text.Trim()))
                    return "VPN 연결 뒤 접속할 집 PC의 내부 IP를 확인하세요. 예: 192.168.0.10";
                return null;
            default:
                return null;
        }
    }

    private void Collect(Page page)
    {
        switch (page)
        {
            case Page.Router:
                _work.RouterUrl = RouterUrlBox.Text.Trim();
                _work.AllowRouterCertificateError = AllowCertBox.Enabled && AllowCertBox.Checked;
                break;
            case Page.Pc:
                _work.WolPcName = PcNameBox.Text.Trim();
                _work.WolPcMac = InputRules.NormalizeMac(PcMacBox.Text.Trim()) ?? "";
                break;
            case Page.Connect:
                _work.PublicHost = UseDirectBox.Checked ? PublicHostBox.Text.Trim() : "";
                _work.PublicRdpPort = (int)PublicPortBox.Value;
                _work.VpnName = UseVpnBox.Checked ? VpnNameBox.Text.Trim() : "";
                _work.VpnDesktopIp = UseVpnBox.Checked ? VpnIpBox.Text.Trim() : "";
                _work.VpnRdpPort = (int)VpnPortBox.Value;
                _work.LastConnectMode = !UseDirectBox.Checked && UseVpnBox.Checked ? "vpn" : "direct";
                break;
        }
    }

    // ------------------------------------------------------------------ 이동

    /// <summary>다음 단계로(마지막 단계면 저장하고 닫음). 입력이 틀리면 그대로 머물고 false.</summary>
    internal bool Next()
    {
        var error = Validate(CurrentPage);
        SetError(error);
        if (error != null) return false;
        Collect(CurrentPage);
        if (CurrentPage == Page.Done)
        {
            _work.SetupCompleted = true;
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }
        ShowPage(CurrentPage + 1);
        return true;
    }

    internal void Back()
    {
        if (CurrentPage == Page.Welcome) return;
        SetError(null);
        ShowPage(CurrentPage - 1);
    }

    private void ShowPage(Page page)
    {
        CurrentPage = page;
        var i = (int)page;
        for (var k = 0; k < _pages.Length; k++) _pages[k].Visible = k == i;
        _stepLabel.Text = $"{i + 1} / {StepNames.Length} · {StepNames[i]}";
        (_title.Text, _desc.Text) = page switch
        {
            Page.Welcome => ("RemoteAccessHub 시작 설정", "처음 한 번만 필요한 정보를 입력합니다."),
            Page.Router => ("공유기 관리자 주소", "브라우저에서 ipTIME 관리 화면을 열 때 쓰는 주소를 입력하세요."),
            Page.Pc => ("켤 PC", "공유기 [특수 기능 → WOL 기능] 목록에 등록된 PC를 지정합니다."),
            Page.Connect => ("원격 데스크톱 접속 방법", "쓰는 방법을 켜세요. 둘 다 켜면 접속할 때마다 고를 수 있습니다."),
            _ => ("확인", "아래 내용으로 저장합니다."),
        };
        _strip.Current = i;
        _strip.Count = StepNames.Length;
        _strip.Invalidate();
        _back.Visible = page != Page.Welcome;
        _next.Text = page switch { Page.Welcome => "시작", Page.Done => "완료", _ => "다음" };
        _next.Glyph = page == Page.Done ? Theme.Glyph.Check : "";
        LayoutButtons();

        if (page == Page.Connect && PublicHostBox.Text.Trim().Length == 0 && _work.RouterUri is { } uri)
            PublicHostBox.Text = uri.Host; // 보통 공유기와 같은 DDNS 주소를 쓴다
        if (page == Page.Done) FillSummary();

        var focus = page switch
        {
            Page.Router => RouterUrlBox,
            Page.Pc => PcNameBox,
            Page.Connect => (Control)UseDirectBox,
            _ => _next,
        };
        if (IsHandleCreated) BeginInvoke(new Action(() => focus.Focus()));
    }

    private void FillSummary()
    {
        _summary.SuspendLayout();
        _summary.Controls.Clear();
        _summary.RowCount = 0;
        void Line(string k, string v)
        {
            var kl = new Label { Text = k, AutoSize = true, Margin = new Padding(0, L(6), L(8), L(6)), ForeColor = Theme.Current.SubText, BackColor = Color.Transparent };
            var vl = new Label { Text = v, AutoSize = true, MaximumSize = new Size(L(480), 0), Margin = new Padding(0, L(6), 0, L(6)), ForeColor = Theme.Current.Text, BackColor = Color.Transparent };
            _summary.Controls.Add(kl, 0, _summary.RowCount);
            _summary.Controls.Add(vl, 1, _summary.RowCount);
            _summary.RowCount++;
        }
        Line("공유기 주소", _work.RouterUrl + (_work.AllowRouterCertificateError ? "  (인증서 오류 허용)" : ""));
        Line("켤 PC", _work.WolPcName + (_work.WolPcMac.Length > 0 ? $"  ·  {_work.WolPcMac}" : ""));
        Line("일반 접속", _work.PublicHost.Length > 0 ? InputRules.HostPort(_work.PublicHost, _work.PublicRdpPort) : "사용 안 함");
        Line("VPN 접속", _work.VpnName.Length > 0 ? $"{_work.VpnName} 연결 후 {InputRules.HostPort(_work.VpnDesktopIp, _work.VpnRdpPort)}" : "사용 안 함");
        _summary.ResumeLayout(true);
    }

    private void SetError(string? text)
    {
        _error.Text = text ?? "";
        _error.Visible = !string.IsNullOrEmpty(text);
    }

    private async Task CheckRouterAsync()
    {
        if (!InputRules.TryParseRouterUrl(RouterUrlBox.Text.Trim(), out var uri) || uri == null)
        {
            SetCheck("주소 형식부터 확인하세요.", Theme.Current.Warning);
            return;
        }
        _checkRouter.Enabled = false;
        SetCheck("확인 중...", Theme.Current.SubText);
        try
        {
            var open = await _probe.IsOpenAsync(uri.Host, uri.Port, TimeSpan.FromSeconds(4), CancellationToken.None);
            SetCheck(open
                    ? $"연결됨 · {uri.Host}:{uri.Port}"
                    : "응답 없음 · 주소와 포트, 공유기의 원격 관리 허용 설정을 확인하세요.",
                open ? Theme.Current.Success : Theme.Current.Warning);
        }
        catch (Exception ex)
        {
            SetCheck("확인하지 못했습니다: " + ex.Message, Theme.Current.Warning);
        }
        finally
        {
            if (!IsDisposed) _checkRouter.Enabled = true;
        }
    }

    internal string RouterCheckText => _checkResult.Text;
    internal Task CheckRouterForTestAsync() => CheckRouterAsync();

    private void SetCheck(string text, Color color)
    {
        if (IsDisposed) return;
        _checkResult.Text = text;
        _checkResult.ForeColor = color;
    }

    /// <summary>단계 진행 막대.</summary>
    private sealed class StepStrip : Control
    {
        public int Count { get; set; } = 5;
        public int Current { get; set; }

        public StepStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            e.Graphics.Clear(p.Background);
            Drawing.Smooth(e.Graphics);
            var gap = Drawing.Scale(this, 6);
            var w = (Width - gap * (Count - 1)) / Count;
            for (var i = 0; i < Count; i++)
            {
                var r = new RectangleF(i * (w + gap), 0, w, Height);
                using var path = Drawing.RoundedRect(r, Height / 2f);
                using var b = new SolidBrush(i <= Current ? p.Accent : p.Border);
                e.Graphics.FillPath(b, path);
            }
        }
    }
}
