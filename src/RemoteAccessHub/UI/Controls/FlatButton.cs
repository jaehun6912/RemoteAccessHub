using System.ComponentModel;

namespace RemoteAccessHub.UI.Controls;

public enum ButtonVariant
{
    Primary,
    Secondary,
    Ghost,
    Danger,
}

/// <summary>
/// 테마를 따르는 평면 버튼. Button을 상속해 키보드(Enter/Space)·포커스·접근성 이름은 그대로 유지한다.
/// SplitWidth가 0보다 크면 오른쪽에 드롭다운 영역을 그리고, 그 영역을 누르면 DropDownClick을 발생시킨다.
/// </summary>
public sealed class FlatButton : Button
{
    private bool _hover;
    private bool _pressed;
    private ButtonVariant _variant = ButtonVariant.Secondary;
    private string _glyph = "";
    private Image? _glyphImage;
    private double _attention;
    private Color? _attentionColor;
    private int _splitWidth;

    public event EventHandler? DropDownClick;

    [DefaultValue(ButtonVariant.Secondary)]
    public ButtonVariant Variant { get => _variant; set { _variant = value; Invalidate(); } }

    /// <summary>왼쪽 아이콘 글리프(Theme.Glyph).</summary>
    [DefaultValue("")]
    public string Glyph { get => _glyph; set { _glyph = value ?? ""; Invalidate(); } }

    /// <summary>
    /// 강조 정도(0 = 평소, 1 = 주 버튼 색). 서서히 밝아졌다 어두워지도록 사이값을 받는다.
    /// </summary>
    /// <summary>강조할 때 쓸 색(지정하지 않으면 주 버튼 색).</summary>
    public Color AttentionColor
    {
        get => _attentionColor ?? Theme.Current.Accent;
        set { if (_attentionColor == value) return; _attentionColor = value; Invalidate(); }
    }

    [DefaultValue(0.0)]
    public double AttentionLevel
    {
        get => _attention;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(_attention - v) < 0.01) return;
            _attention = v;
            Invalidate();
        }
    }

    /// <summary>글리프 대신 쓸 그림 아이콘(없으면 글리프). 그림은 버튼이 소유하지 않는다.</summary>
    [DefaultValue(null)]
    public Image? GlyphImage { get => _glyphImage; set { _glyphImage = value; Invalidate(); } }

    /// <summary>오른쪽 드롭다운 영역 너비(논리 px). 0이면 일반 버튼.</summary>
    [DefaultValue(0)]
    public int SplitWidth { get => _splitWidth; set { _splitWidth = Math.Max(0, value); Invalidate(); } }

    /// <summary>드롭다운 화살표만 표시(버튼 전체가 메뉴를 여는 경우).</summary>
    [DefaultValue(false)]
    public bool ShowArrow { get; set; }

    /// <summary>아이콘만 있는 정사각 버튼.</summary>
    [DefaultValue(false)]
    public bool IconOnly { get; set; }

    public FlatButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Font = Theme.UiFont(9.5f, FontStyle.Bold);
        Height = 36;
    }

    private int SplitPx => (int)Drawing.Scale(this, _splitWidth);

    public bool IsInSplitArea(Point p) => _splitWidth > 0 && p.X >= Width - SplitPx;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    // 마우스로 눌렀을 때만 드롭다운 영역을 구분한다(키보드 Enter/Space는 항상 주 동작).
    private bool _clickFromMouse;
    private Point _mouseUpPoint;

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        _clickFromMouse = true;
        _mouseUpPoint = e.Location;
        Invalidate();
        base.OnMouseUp(e); // 여기서 OnClick이 동기적으로 호출된다
        _clickFromMouse = false;
    }

    protected override void OnClick(EventArgs e)
    {
        if (_splitWidth > 0 && _clickFromMouse && IsInSplitArea(_mouseUpPoint))
        {
            DropDownClick?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_splitWidth > 0 && (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down)))
        {
            e.Handled = true;
            DropDownClick?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Drawing.ParentBack(this));
        Drawing.Smooth(g);

        Color fill, border, text;
        switch (_variant)
        {
            case ButtonVariant.Primary:
                fill = !Enabled ? p.SurfaceAlt : _pressed ? p.AccentPressed : _hover ? p.AccentHover : p.Accent;
                border = fill;
                text = Enabled ? p.OnAccent : p.Muted;
                break;
            case ButtonVariant.Danger:
                fill = !Enabled ? p.SurfaceAlt : _pressed ? Theme.Blend(p.Surface, p.Danger, 0.35) : _hover ? Theme.Blend(p.Surface, p.Danger, 0.22) : p.Surface;
                border = Enabled ? p.Danger : p.Border;
                text = Enabled ? p.Danger : p.Muted;
                break;
            case ButtonVariant.Ghost:
                fill = !Enabled ? Drawing.ParentBack(this) : _pressed ? p.Border : _hover ? p.SurfaceAlt : Drawing.ParentBack(this);
                border = fill;
                text = Enabled ? p.Text : p.Muted;
                break;
            default:
                fill = !Enabled ? p.SurfaceAlt : _pressed ? p.Border : _hover ? Theme.Blend(p.SurfaceAlt, p.Border, 0.5) : p.SurfaceAlt;
                border = p.Border;
                text = Enabled ? p.Text : p.Muted;
                break;
        }

        // 깜빡임: 모양은 그대로 두고 색만 주 버튼 쪽으로 서서히 옮긴다.
        if (_attention > 0 && Enabled)
        {
            var accent = AttentionColor;
            fill = Theme.Blend(fill, accent, _attention);
            border = Theme.Blend(border, accent, _attention);
            // 중간 색 위에서도 글자가 읽히도록 두 글자색 중 대비가 나은 쪽을 쓴다.
            text = Theme.Contrast(p.OnAccent, fill) > Theme.Contrast(p.Text, fill) ? p.OnAccent : p.Text;
        }

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Drawing.RoundedRect(r, Drawing.Scale(this, 6)))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        var content = ClientRectangle;
        if (_splitWidth > 0)
        {
            var sx = Width - SplitPx;
            using var sep = new Pen(Theme.Blend(fill, text, 0.35));
            g.DrawLine(sep, sx, Drawing.Scale(this, 8), sx, Height - Drawing.Scale(this, 8));
            var arrowRect = new Rectangle(sx, 0, SplitPx, Height);
            TextRenderer.DrawText(g, Theme.Glyph.ChevronDown, Theme.IconFont(8f), arrowRect, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            content = new Rectangle(0, 0, sx, Height);
        }

        if (IconOnly)
        {
            TextRenderer.DrawText(g, _glyph, Theme.IconFont(11f), content, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        else
        {
            var label = Text + (ShowArrow ? "" : "");
            var iconFont = Theme.IconFont(10.5f);
            var textSize = Drawing.MeasureText(label, Font);
            var iconW = _glyph.Length > 0 || _glyphImage != null ? (int)Drawing.Scale(this, 22) : 0;
            var arrowW = ShowArrow ? (int)Drawing.Scale(this, 18) : 0;
            var total = iconW + textSize.Width + arrowW;
            var x = content.X + Math.Max((int)Drawing.Scale(this, 10), (content.Width - total) / 2);
            if (iconW > 0 && _glyphImage != null)
            {
                var side = (int)Drawing.Scale(this, 16);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(_glyphImage, new Rectangle(x, (Height - side) / 2, side, side));
            }
            else if (iconW > 0)
                TextRenderer.DrawText(g, _glyph, iconFont, new Rectangle(x, 0, iconW, Height), text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, label, Font, new Rectangle(x + iconW, 0, Math.Max(0, content.Right - x - iconW - arrowW), Height), text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (arrowW > 0)
                TextRenderer.DrawText(g, Theme.Glyph.ChevronDown, Theme.IconFont(8f), new Rectangle(x + iconW + textSize.Width + (int)Drawing.Scale(this, 6), 0, arrowW, Height), text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        if (Focused && ShowFocusCues)
        {
            var fr = new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f);
            using var path = Drawing.RoundedRect(fr, Drawing.Scale(this, 4));
            using var pen = new Pen(_variant == ButtonVariant.Primary ? p.OnAccent : p.Accent, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawPath(pen, path);
        }
    }

    /// <summary>텍스트·아이콘에 맞는 너비(논리 px 여백 포함).</summary>
    public int PreferredWidth()
    {
        if (IconOnly) return (int)Drawing.Scale(this, 38);
        var w = Drawing.MeasureText(Text, Font).Width + (int)Drawing.Scale(this, 28);
        if (_glyph.Length > 0 || _glyphImage != null) w += (int)Drawing.Scale(this, 22);
        if (ShowArrow) w += (int)Drawing.Scale(this, 20);
        w += SplitPx;
        return w;
    }
}
