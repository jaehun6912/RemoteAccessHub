using RemoteAccessHub.Core;
using RemoteAccessHub.Services;
using RemoteAccessHub.UI.Controls;

namespace RemoteAccessHub.UI;

/// <summary>설정 편집 화면. 비밀번호 종류의 입력칸은 존재하지 않는다.</summary>
public sealed class SettingsForm : Form
{
    private AppSettings _work;
    private readonly IVpnService _vpn;

    private readonly TextBox _routerUrl = new();
    private readonly TextBox _pcName = new();
    private readonly TextBox _pcMac = new();
    private readonly CheckBox _allowCert = new() { Text = "공유기 주소에 한해 인증서 오류 허용 (자체 서명 HTTPS일 때만 켜세요)" };
    private readonly TextBox _publicHost = new();
    private readonly NumericUpDown _publicPort = new() { Minimum = 1, Maximum = 65535 };
    private readonly ComboBox _vpnName = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _vpnIp = new();
    private readonly NumericUpDown _vpnPort = new() { Minimum = 1, Maximum = 65535 };
    private readonly NumericUpDown _vpnWait = new() { Minimum = 10, Maximum = 900 };
    private readonly NumericUpDown _bootWait = new() { Minimum = 10, Maximum = 3600 };
    private readonly CheckBox _fullScreen = new() { Text = "원격 데스크톱 전체 화면(/f)" };
    private readonly CheckBox _autoCollapse = new() { Text = "로그인이 확인되면 공유기 화면 자동 접기" };
    private readonly CheckBox _autoConfirm = new() { Text = "WOL 확인창(PC를 켜시겠습니까?)의 [확인] 자동 클릭" };
    private readonly CheckBox _autoAdminTool = new() { Text = "로그인 직후 선택 화면에서 [관리도구] 자동 선택" };
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _adminLabel = new();
    private readonly TextBox _wolGroup = new();
    private readonly TextBox _wolRoute = new();
    private readonly TextBox _wolMenu = new();
    private readonly TextBox _wakePattern = new();
    private readonly NumericUpDown _probeInterval = new() { Minimum = 5, Maximum = 600 };

    private static readonly (string Label, ThemeMode Mode)[] ThemeChoices =
    {
        ("Windows 설정 따르기", ThemeMode.System),
        ("어둡게", ThemeMode.Dark),
        ("밝게", ThemeMode.Light),
    };

    public AppSettings Result => _work;

    /// <summary>[초기화]를 확인했으면 true. 창을 닫은 뒤 메인 창이 초기화와 시작 설정을 진행한다.</summary>
    public bool ResetRequested { get; private set; }

    public SettingsForm(AppSettings current, IVpnService vpn)
    {
        _work = current.Clone();
        _vpn = vpn;

        Text = "설정";
        AppIcon.ApplyTo(this);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.UiFont(9.5f);
        ClientSize = new Size(L(760), L(780));

        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(L(16), L(8), L(16), L(8)) };

        // Dock=Top은 나중에 추가한 것이 위로 오므로 아래 섹션부터 추가한다.
        var sections = new List<Control>
        {
            Section("공유기 (ipTIME 관리자 페이지)", t =>
            {
                Row(t, "관리자 페이지 전체 URL", _routerUrl, "프로토콜·주소·관리 포트 포함. 예: http://myhome.iptime.org:8080/");
                Row(t, "WOL 대상 PC 이름", _pcName, "공유기 WOL 목록에 등록된 이름과 정확히 같아야 합니다.");
                Row(t, "WOL 대상 MAC 주소 (선택)", _pcMac, "같은 이름이 여러 개일 때 구분용. 예: 00:11:22:33:44:55");
                Check(t, _allowCert);
            }),
            Section("일반 접속 (DDNS 또는 공인 IP로 바로 RDP)", t =>
            {
                Row(t, "RDP 접속 주소", _publicHost, "DDNS 호스트 이름 또는 공인 IP (포트 제외)");
                Row(t, "RDP 외부 포트", _publicPort, "공유기 포트포워딩의 외부 포트");
            }),
            Section("VPN 접속 (Windows 기본 VPN)", t =>
            {
                Row(t, "Windows VPN 연결 이름", _vpnName, "Windows 설정 > VPN에 등록한 연결 이름 (목록에서 선택 가능)");
                Row(t, "데스크톱 내부 IP", _vpnIp, "VPN 연결 후 사용할 집 내부 IP. 예: 192.168.0.10");
                Row(t, "내부 RDP 포트", _vpnPort);
                Row(t, "VPN 연결 대기 시간(초)", _vpnWait);
            }),
            Section("동작과 화면", t =>
            {
                Row(t, "부팅 대기 시간(초)", _bootWait, "RDP 포트 응답을 기다리는 최대 시간");
                Row(t, "테마", _theme);
                Check(t, _fullScreen);
                Check(t, _autoCollapse);
                Check(t, _autoAdminTool);
                Check(t, _autoConfirm);
            }),
            Section("고급 (공유기 화면 구조가 바뀐 경우에만 수정)", t =>
            {
                Row(t, "WOL 페이지 경로", _wolRoute, "기본 /ui/wol  (공유기 주소 뒤에 붙는 경로)");
                Row(t, "관리도구 항목 이름", _adminLabel, "로그인 직후 선택 화면에서 누를 항목");
                Row(t, "WOL 메뉴 그룹 이름", _wolGroup, "WOL 메뉴가 들어 있는 메뉴 그룹");
                Row(t, "WOL 메뉴 이름", _wolMenu, "직접 이동이 안 될 때 누를 메뉴 이름");
                Row(t, "PC 켜기 버튼 패턴(정규식)", _wakePattern);
                Row(t, "세션 확인 주기(초)", _probeInterval);
            }),
            Section("설정 파일", t =>
            {
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, L(4)) };
                var export = new FlatButton { Text = "내보내기...", Variant = ButtonVariant.Secondary, Glyph = Theme.Glyph.Document };
                var import = new FlatButton { Text = "가져오기...", Variant = ButtonVariant.Secondary, Glyph = Theme.Glyph.Folder };
                var reset = new FlatButton { Text = "초기화...", Variant = ButtonVariant.Danger, Glyph = Theme.Glyph.Refresh };
                foreach (var b in new[] { export, import, reset })
                {
                    b.Size = new Size(Math.Max(L(110), b.PreferredWidth()), L(34));
                    b.Margin = new Padding(0, 0, L(8), 0);
                    row.Controls.Add(b);
                }
                export.Click += (_, _) => OnExport();
                import.Click += (_, _) => OnImport();
                reset.Click += (_, _) => OnReset();
                t.Controls.Add(row, 0, t.RowCount);
                t.SetColumnSpan(row, 2);
                t.RowCount++;
                var note = new Label { Text = "내보내기: 이 창의 설정을 JSON 파일로 저장합니다(비밀번호·보안문자는 원래 저장하지 않으므로 들어가지 않음). 공유기 주소·PC 이름이 들어 있으니 공유에 주의하세요.\n"
                    + "가져오기: 내보낸 파일의 값을 이 창에 채웁니다. [저장]을 눌러야 적용됩니다.\n"
                    + "초기화: 모든 설정을 처음 상태로 되돌리고 시작 설정을 다시 엽니다. 기록 파일과 VPN 연결은 그대로입니다.", AutoSize = true, MaximumSize = new Size(L(660), 0), Margin = new Padding(0, L(6), 0, 0), Tag = "sub", Font = Theme.UiFont(8.5f) };
                t.Controls.Add(note, 0, t.RowCount);
                t.SetColumnSpan(note, 2);
                t.RowCount++;
            }),
        };
        for (var i = 0; i < sections.Count; i++) sections[i].TabIndex = i; // 화면 위쪽부터 Tab 이동
        for (var i = sections.Count - 1; i >= 0; i--)
        {
            scroller.Controls.Add(sections[i]);
            if (i > 0) scroller.Controls.Add(new Panel { Dock = DockStyle.Top, Height = L(10), Tag = "gap" });
        }

        var header = new Panel { Dock = DockStyle.Top, Height = L(64), Padding = new Padding(L(20), L(12), L(20), 0) };
        var title = new Label { Text = "설정", Font = Theme.UiFont(14f, FontStyle.Bold), AutoSize = true, Location = new Point(L(20), L(10)) };
        var sub = new Label { Text = "비밀번호·보안문자는 저장하지 않습니다. 선택한 접속 방식에 필요한 항목만 검사합니다.", AutoSize = true, Location = new Point(L(22), L(40)), Tag = "sub" };
        header.Controls.Add(title);
        header.Controls.Add(sub);

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = L(64), Padding = new Padding(L(20), L(12), L(20), L(14)) };
        var save = new FlatButton { Text = "저장", Variant = ButtonVariant.Primary, Glyph = Theme.Glyph.Check };
        var cancel = new FlatButton { Text = "취소", Variant = ButtonVariant.Secondary, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { save, cancel });
        buttons.Resize += (_, _) =>
        {
            var h = buttons.ClientSize.Height - buttons.Padding.Vertical;
            save.Size = new Size(Math.Max(L(110), save.PreferredWidth()), h);
            cancel.Size = new Size(Math.Max(L(90), cancel.PreferredWidth()), h);
            save.Location = new Point(buttons.ClientSize.Width - buttons.Padding.Right - save.Width, buttons.Padding.Top);
            cancel.Location = new Point(save.Left - L(8) - cancel.Width, buttons.Padding.Top);
        };
        save.Click += (_, _) => OnSave();

        Controls.Add(scroller);
        Controls.Add(header);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;

        foreach (var (label, _) in ThemeChoices) _theme.Items.Add(label);

        _theme.DrawMode = DrawMode.OwnerDrawFixed;
        _theme.DrawItem += DrawThemedComboItem;

        Bind();
        ApplyTheme();
        HandleCreated += (_, _) => Theme.ApplyTitleBar(this);
        Shown += (_, _) =>
        {
            scroller.AutoScrollPosition = Point.Empty;
            ActiveControl = _routerUrl;
            _routerUrl.SelectionStart = _routerUrl.TextLength;
        };
    }

    private int L(int logical) => LogicalToDeviceUnits(logical);

    private Control Section(string title, Action<TableLayoutPanel> build)
    {
        var card = new CardPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(L(16), L(12), L(16), L(12)) };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, L(210)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var head = new Label { Text = title, Font = Theme.UiFont(10.5f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, L(8)), Tag = "section" };
        table.Controls.Add(head, 0, 0);
        table.SetColumnSpan(head, 2);
        table.RowCount = 1;
        build(table);
        card.Controls.Add(table);
        return card;
    }

    private void Row(TableLayoutPanel t, string label, Control c, string? hint = null)
    {
        var l = new Label { Text = label, AutoSize = true, Margin = new Padding(0, L(8), L(8), 0) };
        c.Width = L(440);
        c.Margin = new Padding(0, L(4), 0, L(4));
        t.Controls.Add(l, 0, t.RowCount);
        t.Controls.Add(c, 1, t.RowCount);
        t.RowCount++;
        if (hint != null)
        {
            var h = new Label { Text = hint, AutoSize = true, Margin = new Padding(0, 0, 0, L(6)), Tag = "sub", Font = Theme.UiFont(8.5f) };
            t.Controls.Add(h, 1, t.RowCount);
            t.RowCount++;
        }
    }

    private void Check(TableLayoutPanel t, CheckBox c)
    {
        c.AutoSize = true;
        c.Margin = new Padding(0, L(4), 0, L(4));
        t.Controls.Add(c, 1, t.RowCount);
        t.RowCount++;
    }

    /// <summary>드롭다운 목록형 콤보 상자는 BackColor를 무시하므로 직접 그린다.</summary>
    private static void DrawThemedComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox cb) return;
        var p = Theme.Current;
        var selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var bg = new SolidBrush(selected ? p.Border : p.SurfaceAlt)) e.Graphics.FillRectangle(bg, e.Bounds);
        if (e.Index >= 0)
            TextRenderer.DrawText(e.Graphics, cb.Items[e.Index]?.ToString(), cb.Font, e.Bounds, p.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        if ((e.State & DrawItemState.Focus) != 0 && (e.State & DrawItemState.NoFocusRect) == 0) e.DrawFocusRectangle();
    }

    private void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Background;
        ForeColor = p.Text;
        Theme.ApplyToStandardControls(this);
        foreach (var gap in Controls.OfType<Panel>().SelectMany(x => x.Controls.OfType<Panel>()).Where(x => x.Tag as string == "gap"))
            gap.BackColor = p.Background;
        Theme.ApplyTitleBar(this);
    }

    private void Bind()
    {
        _routerUrl.Text = _work.RouterUrl;
        _pcName.Text = _work.WolPcName;
        _pcMac.Text = _work.WolPcMac;
        _allowCert.Checked = _work.AllowRouterCertificateError;
        _publicHost.Text = _work.PublicHost;
        _publicPort.Value = Clamp(_work.PublicRdpPort, 1, 65535);
        _vpnName.Items.Clear();
        foreach (var n in _vpn.ListEntries()) _vpnName.Items.Add(n);
        _vpnName.Text = _work.VpnName;
        _vpnIp.Text = _work.VpnDesktopIp;
        _vpnPort.Value = Clamp(_work.VpnRdpPort, 1, 65535);
        _vpnWait.Value = Clamp(_work.VpnWaitSeconds, 10, 900);
        _bootWait.Value = Clamp(_work.BootWaitSeconds, 10, 3600);
        _fullScreen.Checked = _work.RdpFullScreen;
        _autoCollapse.Checked = _work.AutoCollapseAfterLogin;
        _autoConfirm.Checked = _work.AutoConfirmWakeDialog;
        _autoAdminTool.Checked = _work.AutoSelectAdminTool;
        var mode = Theme.ParseMode(_work.Theme);
        _theme.SelectedIndex = Array.FindIndex(ThemeChoices, c => c.Mode == mode);
        _adminLabel.Text = _work.AdminToolLabel;
        _wolGroup.Text = _work.WolMenuGroupLabel;
        _wolRoute.Text = _work.WolPageRoute;
        _wolMenu.Text = _work.WolMenuLabel;
        _wakePattern.Text = _work.WakeButtonPattern;
        _probeInterval.Value = Clamp(_work.SessionProbeIntervalSeconds, 5, 600);
    }

    private static decimal Clamp(int v, int min, int max) => Math.Min(max, Math.Max(min, v));

    private void Collect()
    {
        _work.RouterUrl = _routerUrl.Text.Trim();
        _work.WolPcName = _pcName.Text.Trim();
        _work.WolPcMac = _pcMac.Text.Trim();
        _work.AllowRouterCertificateError = _allowCert.Checked;
        _work.PublicHost = _publicHost.Text.Trim();
        _work.PublicRdpPort = (int)_publicPort.Value;
        _work.VpnName = _vpnName.Text.Trim();
        _work.VpnDesktopIp = _vpnIp.Text.Trim();
        _work.VpnRdpPort = (int)_vpnPort.Value;
        _work.VpnWaitSeconds = (int)_vpnWait.Value;
        _work.BootWaitSeconds = (int)_bootWait.Value;
        _work.RdpFullScreen = _fullScreen.Checked;
        _work.AutoCollapseAfterLogin = _autoCollapse.Checked;
        _work.AutoConfirmWakeDialog = _autoConfirm.Checked;
        _work.AutoSelectAdminTool = _autoAdminTool.Checked;
        _work.Theme = Theme.ToSetting(_theme.SelectedIndex >= 0 ? ThemeChoices[_theme.SelectedIndex].Mode : ThemeMode.System);
        _work.AdminToolLabel = string.IsNullOrWhiteSpace(_adminLabel.Text) ? "관리도구" : _adminLabel.Text.Trim();
        _work.WolMenuGroupLabel = string.IsNullOrWhiteSpace(_wolGroup.Text) ? "특수 기능" : _wolGroup.Text.Trim();
        _work.WolPageRoute = string.IsNullOrWhiteSpace(_wolRoute.Text) ? "/ui/wol" : _wolRoute.Text.Trim();
        _work.WolMenuLabel = string.IsNullOrWhiteSpace(_wolMenu.Text) ? "WOL 기능" : _wolMenu.Text.Trim();
        _work.WakeButtonPattern = string.IsNullOrWhiteSpace(_wakePattern.Text) ? @"^PC\s*켜기$" : _wakePattern.Text.Trim();
        _work.SessionProbeIntervalSeconds = (int)_probeInterval.Value;
    }

    private void OnSave()
    {
        Collect();
        var errors = new List<string>(_work.ValidateRouter());
        // 비어 있지 않은 접속 설정은 형식만 검사 (모드별 필수 여부는 접속 시점에 검사)
        if (_work.PublicHost.Length > 0 && !InputRules.IsValidHost(_work.PublicHost)) errors.Add("일반 접속 주소 형식이 올바르지 않습니다.");
        if (_work.VpnName.Length > 0 && !InputRules.IsValidVpnName(_work.VpnName)) errors.Add("VPN 연결 이름에 사용할 수 없는 문자가 있습니다.");
        if (_work.VpnDesktopIp.Length > 0 && !InputRules.IsValidHost(_work.VpnDesktopIp)) errors.Add("데스크톱 내부 IP 형식이 올바르지 않습니다.");
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "설정 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (InputRules.NormalizeMac(_work.WolPcMac) is { } mac) _work.WolPcMac = mac;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnExport()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "설정 내보내기",
            Filter = "JSON 파일|*.json",
            FileName = $"RemoteAccessHub-설정-{DateTime.Now:yyyyMMdd}.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var error = ExportTo(dlg.FileName);
        if (error == null)
            MessageBox.Show(this, "설정을 내보냈습니다.\n" + dlg.FileName + "\n\n공유기 주소·PC 이름·VPN 이름이 들어 있으니 다른 사람과 공유하지 마세요.", "설정 내보내기", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            MessageBox.Show(this, "설정을 내보내지 못했습니다: " + error, "설정 내보내기", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>현재 창의 값(아직 저장 전 포함)을 파일로 내보낸다. 실패하면 이유.</summary>
    internal string? ExportTo(string path)
    {
        try
        {
            Collect();
            File.WriteAllText(path, _work.ToExportJson());
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void OnImport()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "설정 가져오기",
            Filter = "JSON 파일|*.json|모든 파일|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var error = ImportFrom(dlg.FileName);
        if (error == null)
            MessageBox.Show(this, "설정을 가져왔습니다. 내용을 확인한 뒤 [저장]을 누르면 적용됩니다.", "설정 가져오기", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            MessageBox.Show(this, "설정을 가져오지 못했습니다: " + error, "설정 가져오기", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>내보낸 설정 파일의 값을 이 창에 채운다(저장 전까지 적용 안 됨). 실패하면 이유.</summary>
    internal string? ImportFrom(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) { return ex.Message; }
        var imported = AppSettings.FromExportJson(json, out var error);
        if (imported == null) return error;
        // 이 PC의 창 위치·기록 표시 상태는 가져오지 않는다.
        imported.WindowLeft = _work.WindowLeft;
        imported.WindowTop = _work.WindowTop;
        imported.ShowLog = _work.ShowLog;
        _work = imported;
        Bind();
        return null;
    }

    private void OnReset()
    {
        var r = MessageBox.Show(this,
            "모든 설정을 처음 상태로 되돌리고 시작 설정을 다시 엽니다.\n지금 공유기 화면의 로그인도 끊깁니다. 기록 파일과 Windows VPN 연결은 그대로입니다.\n\n필요하면 먼저 [내보내기]로 백업하세요. 초기화할까요?",
            "설정 초기화", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;
        RequestReset();
    }

    internal void RequestReset()
    {
        ResetRequested = true;
        DialogResult = DialogResult.Abort;
        Close();
    }
}
