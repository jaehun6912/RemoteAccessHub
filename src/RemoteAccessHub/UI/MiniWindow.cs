using RemoteAccessHub.Services;
using RemoteAccessHub.UI.Controls;

namespace RemoteAccessHub.UI;

/// <summary>
/// 작게 접었을 때 화면 구석에 떠 있는 창. 상태 한 줄과 [접속]만 보여 준다.
/// 끌어서 옮길 수 있고, ⤢로 본 창으로 돌아간다.
/// </summary>
public sealed class MiniWindow : Form
{
    private readonly FlatButton _connect = new();
    private readonly FlatButton _restore = new();
    private readonly Label _title = new();
    private readonly Label _sub = new();
    private readonly Panel _dot = new();
    private bool _placed;
    private Point _dragFrom;
    private bool _dragging;

    public MiniWindow()
    {
        Text = "RemoteAccessHub";
        AppIcon.ApplyTo(this);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.UiFont(9.5f);
        ClientSize = new Size(L(380), L(60));

        _dot.Size = new Size(L(34), L(34));
        _dot.Location = new Point(L(12), L(13));

        _title.AutoSize = false;
        _title.Font = Theme.UiFont(10f, FontStyle.Bold);
        _title.Bounds = new Rectangle(L(56), L(12), L(170), L(19));
        _title.Tag = "keep";

        _sub.AutoSize = false;
        _sub.Font = Theme.UiFont(8.5f);
        _sub.Bounds = new Rectangle(L(56), L(31), L(170), L(17));
        _sub.Tag = "sub";

        _connect.Text = "접속";
        _connect.Glyph = Theme.Glyph.Play;
        _connect.Variant = ButtonVariant.Primary;
        _connect.Size = new Size(L(86), L(32));
        _connect.Click += (_, _) => ConnectRequested?.Invoke();

        _restore.IconOnly = true;
        _restore.Glyph = Theme.Glyph.ChevronUp;
        _restore.Variant = ButtonVariant.Ghost;
        _restore.AccessibleName = "큰 창으로";
        _restore.Text = "큰 창으로";
        _restore.Size = new Size(L(34), L(32));
        _restore.Click += (_, _) => RestoreRequested?.Invoke();

        Controls.AddRange(new Control[] { _dot, _title, _sub, _connect, _restore });
        foreach (Control c in Controls) c.MouseDown += OnDragStart;
        MouseDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseUp += (_, _) => _dragging = false;
        HandleCreated += (_, _) => Theme.ApplyTitleBar(this);
        Resize += (_, _) => LayoutButtons();
        LayoutButtons();
        ApplyTheme();
    }

    public event Action? ConnectRequested;
    public event Action? RestoreRequested;

    private int L(int logical) => LogicalToDeviceUnits(logical);

    private void LayoutButtons()
    {
        _restore.Location = new Point(ClientSize.Width - _restore.Width - L(10), (ClientSize.Height - _restore.Height) / 2);
        _connect.Location = new Point(_restore.Left - _connect.Width - L(6), (ClientSize.Height - _connect.Height) / 2);
        _title.Width = Math.Max(L(60), _connect.Left - _title.Left - L(10));
        _sub.Width = _title.Width;
    }

    private void OnDragStart(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || sender is FlatButton) return;
        _dragging = true;
        _dragFrom = Cursor.Position;
    }

    private void OnDragMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var now = Cursor.Position;
        Location = new Point(Location.X + now.X - _dragFrom.X, Location.Y + now.Y - _dragFrom.Y);
        _dragFrom = now;
    }

    public void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Surface;
        _title.ForeColor = p.Text;
        _title.BackColor = Color.Transparent;
        _sub.ForeColor = p.SubText;
        _sub.BackColor = Color.Transparent;
        _dot.BackColor = Color.Transparent;
        Theme.ApplyTitleBar(this);
        Invalidate(true);
    }

    /// <summary>본 창의 상태를 그대로 옮겨 적는다.</summary>
    public void Show(PcPowerStatus power, string pcName, bool connectEnabled, string connectText)
    {
        var p = Theme.Current;
        var on = power.State == PcPowerState.On;
        _title.Text = (pcName.Length > 0 ? pcName : "집 PC") + (on ? " · 켜짐" : power.State == PcPowerState.NoAnswer ? " · 응답 없음" : "");
        _sub.Text = power.CheckedAt is { } t ? "마지막 확인 " + t.ToString("HH:mm") : power.Detail;
        _dotColor = on ? p.Success : power.State == PcPowerState.Checking ? p.Info : p.Muted;
        _connect.Enabled = connectEnabled;
        _connect.Text = connectText;
        _connect.Size = new Size(Math.Max(L(86), _connect.PreferredWidth()), L(32));
        LayoutButtons();
        _dot.Invalidate();
        Invalidate(true);
    }

    private Color _dotColor = Theme.Current.Muted;

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        Drawing.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, ClientSize.Width - 1.5f, ClientSize.Height - 1.5f);
        using (var path = Drawing.RoundedRect(r, LogicalToDeviceUnits(16)))
        using (var fill = new SolidBrush(p.Surface))
        using (var pen = new Pen(p.Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        var d = LogicalToDeviceUnits(34);
        var circle = new RectangleF(_dot.Left, _dot.Top, d, d);
        using (var fill = new SolidBrush(Theme.Blend(p.Surface, _dotColor, 0.2)))
        using (var pen = new Pen(Theme.Blend(p.Surface, _dotColor, 0.5)))
        {
            g.FillEllipse(fill, circle);
            g.DrawEllipse(pen, circle);
        }
        TextRenderer.DrawText(g, Theme.Glyph.Router, Theme.IconFont(10f), Rectangle.Round(circle), _dotColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        base.OnPaint(e);
    }

    /// <summary>처음 띄울 때는 본 창의 오른쪽 아래 구석에 둔다.</summary>
    public void ShowNear(Form owner)
    {
        if (!_placed)
        {
            _placed = true;
            var area = Screen.FromControl(owner).WorkingArea;
            Location = new Point(
                Math.Max(area.Left, area.Right - Width - LogicalToDeviceUnits(24)),
                Math.Max(area.Top, area.Bottom - Height - LogicalToDeviceUnits(24)));
        }
        if (!Visible) Show();
        BringToFront();
    }
}
