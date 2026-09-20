using RemoteAccessHub.Core;
using RemoteAccessHub.UI.Controls;

namespace RemoteAccessHub.UI;

public sealed record ModeOption(ConnectMode Mode, string Title, string Detail, bool Enabled);

/// <summary>접속 방식 선택지. 방식마다 그 방식에 필요한 설정만 검사한다.</summary>
public static class ModeOptions
{
    public static IReadOnlyList<ModeOption> For(AppSettings s) => new[] { Build(s, ConnectMode.Direct), Build(s, ConnectMode.Vpn) };

    public static ModeOption Build(AppSettings s, ConnectMode mode)
    {
        var title = mode == ConnectMode.Vpn ? "VPN 접속" : "일반 접속";
        if (s.ValidateConnect(mode).Count > 0)
            return new(mode, title, "설정 필요 · ⚙ 설정에서 입력", false);
        var detail = mode == ConnectMode.Vpn
            ? $"{s.VpnName.Trim()} 연결 후 {InputRules.HostPort(s.VpnDesktopIp, s.VpnRdpPort)}"
            : InputRules.HostPort(s.PublicHost, s.PublicRdpPort);
        return new(mode, title, detail, true);
    }
}

/// <summary>
/// 버튼 아래에 뜨는 작은 접속 방식 선택 창.
/// 마우스로 고르거나 1/2 키, ↑/↓ + Enter로 고른다. Esc 또는 창 밖을 누르면 닫힌다.
/// </summary>
public sealed class ModePopup : Form
{
    private readonly List<OptionRow> _rows = new();
    private readonly Label _heading = new();
    private bool _closing;

    public ConnectMode? Result { get; private set; }
    public string Heading => _heading.Text;
    public IReadOnlyList<ModeOption> Options { get; }

    /// <summary>선택이 확정되면 창이 닫힌 뒤 호출된다.</summary>
    public event Action<ConnectMode>? Chosen;

    public ModePopup(string heading, IReadOnlyList<ModeOption> options, ConnectMode preferred)
    {
        Options = options;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.UiFont(9.5f);
        Padding = new Padding(1);
        Text = heading;
        AccessibleName = heading;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(L(10), L(8), L(10), L(10)) };
        _heading.Text = heading;
        _heading.Dock = DockStyle.Top;
        _heading.Height = L(26);
        _heading.Font = Theme.UiFont(9f, FontStyle.Bold);
        _heading.TextAlign = ContentAlignment.MiddleLeft;
        _heading.Padding = new Padding(L(4), 0, 0, 0);

        var index = 0;
        foreach (var o in options)
        {
            index++;
            var row = new OptionRow(o, index) { Dock = DockStyle.Top, Height = L(54), TabIndex = index };
            row.Click += (_, _) => Choose(o.Mode);
            _rows.Add(row);
        }
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            body.Controls.Add(_rows[i]);
            if (i > 0) body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = L(4), BackColor = Color.Transparent });
        }
        body.Controls.Add(_heading);
        Controls.Add(body);

        ClientSize = new Size(L(340), L(8) + L(26) + _rows.Count * L(54) + Math.Max(0, _rows.Count - 1) * L(4) + L(12));

        var p = Theme.Current;
        BackColor = p.Border;          // 1px 테두리
        body.BackColor = p.Surface;
        _heading.ForeColor = p.SubText;
        _heading.BackColor = p.Surface;

        var focus = _rows.FirstOrDefault(r => r.Option.Enabled && r.Option.Mode == preferred) ?? _rows.FirstOrDefault(r => r.Option.Enabled);
        if (focus != null) ActiveControl = focus;
    }

    private int L(int logical) => LogicalToDeviceUnits(logical);

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    /// <summary>기준 컨트롤 바로 아래(화면 밖으로 나가면 위)에 띄운다.</summary>
    public void ShowBelow(Form owner, Control anchor)
    {
        var screenPoint = anchor.PointToScreen(new Point(0, anchor.Height + L(4)));
        var area = Screen.FromControl(anchor).WorkingArea;
        var x = Math.Max(area.Left, Math.Min(screenPoint.X, area.Right - Width));
        var y = screenPoint.Y + Height > area.Bottom ? anchor.PointToScreen(Point.Empty).Y - Height - L(4) : screenPoint.Y;
        Location = new Point(x, y);
        Show(owner);
        Activate();
    }

    /// <summary>선택. 사용할 수 없는 방식이면 false를 돌려주고 창을 닫지 않는다.</summary>
    public bool Choose(ConnectMode mode)
    {
        var o = Options.FirstOrDefault(x => x.Mode == mode);
        if (o == null || !o.Enabled || _closing) return false;
        Result = mode;
        CloseQuietly();
        Chosen?.Invoke(mode);
        return true;
    }

    private void CloseQuietly()
    {
        if (_closing || IsDisposed) return;
        _closing = true;
        Close();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_closing) BeginInvoke(new Action(CloseQuietly));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                CloseQuietly();
                return true;
            case Keys.D1 or Keys.NumPad1 when Options.Count > 0:
                Choose(Options[0].Mode);
                return true;
            case Keys.D2 or Keys.NumPad2 when Options.Count > 1:
                Choose(Options[1].Mode);
                return true;
            case Keys.Up:
            case Keys.Down:
                MoveFocus(keyData == Keys.Down ? 1 : -1);
                return true;
            case Keys.Enter when ActiveControl is OptionRow r:
                Choose(r.Option.Mode);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void MoveFocus(int dir)
    {
        var enabled = _rows.Where(r => r.Option.Enabled).ToList();
        if (enabled.Count == 0) return;
        var i = ActiveControl is OptionRow cur ? enabled.IndexOf(cur) : -1;
        i = (i + dir + enabled.Count) % enabled.Count;
        enabled[i].Focus();
    }

    /// <summary>선택지 한 줄: 번호 · 아이콘 · 제목 · 주소.</summary>
    private sealed class OptionRow : Control
    {
        private bool _hover;
        public ModeOption Option { get; }
        private readonly int _number;

        public OptionRow(ModeOption option, int number)
        {
            Option = option;
            _number = number;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = option.Enabled;
            Enabled = true; // 비활성 선택지도 설명은 읽을 수 있게 그린다(선택만 막음)
            Cursor = option.Enabled ? Cursors.Hand : Cursors.Default;
            AccessibleRole = AccessibleRole.MenuItem;
            AccessibleName = $"{number}. {option.Title}, {option.Detail}{(option.Enabled ? "" : ", 사용할 수 없음")}";
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.Clear(p.Surface);
            Drawing.Smooth(g);
            float S(float v) => Drawing.Scale(this, v);
            var active = Option.Enabled && (_hover || Focused);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Drawing.RoundedRect(r, S(6)))
            {
                using var fill = new SolidBrush(active ? p.SurfaceAlt : p.Surface);
                g.FillPath(fill, path);
                if (Focused && Option.Enabled)
                {
                    using var pen = new Pen(p.Accent, S(1.5f));
                    g.DrawPath(pen, path);
                }
            }

            var textColor = Option.Enabled ? p.Text : p.Muted;
            var subColor = Option.Enabled ? p.SubText : p.Muted;

            // 번호
            var badge = new RectangleF(S(10), (Height - S(20)) / 2, S(20), S(20));
            using (var pen = new Pen(Option.Enabled ? p.Border : Theme.Blend(p.Surface, p.Border, 0.5)))
                g.DrawEllipse(pen, badge);
            TextRenderer.DrawText(g, _number.ToString(), Theme.UiFont(8.5f, FontStyle.Bold), Rectangle.Round(badge), subColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // 아이콘
            var glyph = Option.Mode == ConnectMode.Vpn ? Theme.Glyph.Lock : Theme.Glyph.Globe;
            TextRenderer.DrawText(g, glyph, Theme.IconFont(13f), new Rectangle((int)S(38), 0, (int)S(26), Height),
                Option.Enabled ? p.Accent : p.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var tx = (int)S(72);
            var tw = Width - tx - (int)S(10);
            TextRenderer.DrawText(g, Option.Title, Theme.UiFont(10f, FontStyle.Bold), new Rectangle(tx, (int)S(7), tw, (int)S(20)), textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, Option.Detail, Theme.UiFont(8.5f), new Rectangle(tx, (int)S(27), tw, (int)S(18)),
                Option.Enabled ? subColor : p.Warning,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }
}
