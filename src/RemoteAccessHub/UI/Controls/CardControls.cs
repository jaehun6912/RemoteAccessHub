using RemoteAccessHub.Core;

namespace RemoteAccessHub.UI.Controls;

/// <summary>
/// 창 맨 위의 큰 상태 카드: 둥근 아이콘 + 큰 제목 + 설명 + (오른쪽) 곁들임 글자, 진행 중이면 가는 막대.
/// 카드 배경은 부모(CardPanel)가 그리므로 여기서는 내용만 그린다.
/// </summary>
public sealed class HeroCard : Control
{
    private string _glyph = Theme.Glyph.Router;
    private Color _accent = Theme.Current.Accent;
    private string _title = "";
    private string _subtitle = "";
    private string _meta = "";
    private double? _progress;

    public HeroCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    /// <summary>오른쪽에 놓을 버튼(없으면 null). 위치는 이 카드가 잡는다.</summary>
    public Control? RightControl { get; set; }

    /// <summary>아이콘 대신 쓸 그림(없으면 글리프).</summary>
    public Image? GlyphImage { get; set; }

    public void Set(string glyph, Color accent, string title, string subtitle, string meta, double? progress)
    {
        _glyph = glyph;
        _accent = accent;
        _title = title;
        _subtitle = subtitle;
        _meta = meta;
        _progress = progress;
        AccessibleName = title + ". " + subtitle;
        LayoutRight();
        Invalidate();
    }

    public int PreferredHeight() =>
        (int)Drawing.Scale(this, 54) + (_progress is null ? 0 : (int)Drawing.Scale(this, 18));

    protected override void OnResize(EventArgs e)
    {
        LayoutRight();
        base.OnResize(e);
    }

    private void LayoutRight()
    {
        if (RightControl is not { Visible: true } rc) return;
        float S(float v) => Drawing.Scale(this, v);
        rc.Location = new Point(Width - rc.Width, (int)((S(54) - rc.Height) / 2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Drawing.ParentBack(this));
        Drawing.Smooth(g);
        float S(float v) => Drawing.Scale(this, v);

        var d = S(54);
        var circle = new RectangleF(0, 0, d, d);
        using (var fill = new SolidBrush(Theme.Blend(p.Surface, _accent, 0.18)))
        using (var pen = new Pen(Theme.Blend(p.Surface, _accent, 0.45)))
        {
            g.FillEllipse(fill, circle);
            g.DrawEllipse(pen, circle);
        }
        var heroSide = (int)S(26);
        var heroBox = new Rectangle((int)((d - heroSide) / 2), (int)((d - heroSide) / 2), heroSide, heroSide);
        if (!Drawing.TryDrawImage(g, GlyphImage, heroBox, 1f))
        {
            TextRenderer.DrawText(g, _glyph, Theme.IconFont(16f), Rectangle.Round(circle), _accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        var metaFont = Theme.UiFont(9f);
        var metaW = _meta.Length == 0 ? 0 : Drawing.MeasureText(_meta, metaFont).Width + (int)S(12);
        var rightW = (RightControl is { Visible: true } rc ? rc.Width + (int)S(10) : 0) + metaW;
        var textX = (int)(d + S(16));
        var textW = Math.Max((int)S(80), Width - textX - rightW);

        TextRenderer.DrawText(g, _title, Theme.UiFont(17f, FontStyle.Bold), new Rectangle(textX, (int)S(4), textW, (int)S(27)), p.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _subtitle, Theme.UiFont(9.5f), new Rectangle(textX, (int)S(31), textW, (int)S(20)), p.SubText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        if (_meta.Length > 0)
        {
            var mx = Width - rightW + (int)S(2);
            TextRenderer.DrawText(g, _meta, metaFont, new Rectangle(mx, 0, metaW, (int)d), p.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        if (_progress is { } prog)
        {
            var bar = new RectangleF(0, d + S(8), Width, S(5));
            using var track = Drawing.RoundedRect(bar, S(2.5f));
            using var trackBrush = new SolidBrush(p.SurfaceAlt);
            g.FillPath(trackBrush, track);
            var w = (float)(bar.Width * Math.Clamp(prog, 0, 1));
            if (w > 1)
            {
                using var fillPath = Drawing.RoundedRect(new RectangleF(bar.X, bar.Y, Math.Max(S(5), w), bar.Height), S(2.5f));
                using var fillBrush = new SolidBrush(_accent);
                g.FillPath(fillBrush, fillPath);
            }
        }
    }
}

/// <summary>접속 방식 카드 하나. 누르면 그 방식으로 바로 접속한다.</summary>
public sealed class MethodCard : Control
{
    private bool _hover;
    private bool _pressed;

    public MethodCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
    }

    public ConnectMode Mode { get; set; }
    /// <summary>지난번에 쓴 방식인지. 크기는 같고 색만 강조해서 그린다.</summary>
    public bool Primary { get; set; }
    public string Glyph { get; set; } = Theme.Glyph.Globe;
    public Image? GlyphImage { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>오른쪽 위 꼬리표("지난번" 등). 비우면 안 그린다.</summary>
    public string Tagline { get; set; } = "";
    /// <summary>키보드 숫자 배지. 0이면 안 그린다.</summary>
    public int Badge { get; set; }

    /// <summary>카드 높이. 세 방식 모두 같은 크기로 그린다(위 단계 칸과 높이를 맞춘다).</summary>
    public int PreferredHeight() => (int)Drawing.Scale(this, 64);

    private double _attention;

    /// <summary>알림으로 밝아지는 정도(0~1). [PC 접속] 깜빡임이 이 카드로 옮겨 왔다.</summary>
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

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Focus(); Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
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
        float S(float v) => Drawing.Scale(this, v);

        var active = Enabled && (_hover || Focused);
        var fill = !Enabled ? Theme.Blend(p.Background, p.Surface, 0.4)
            : Primary ? Theme.Blend(p.Surface, p.Accent, _pressed ? 0.26 : active ? 0.20 : 0.13)
            : _pressed ? p.Border : active ? Theme.Blend(p.SurfaceAlt, p.Border, 0.5) : Theme.Blend(p.Background, p.Surface, 0.7);
        var border = !Enabled ? Theme.Blend(p.Background, p.Border, 0.6) : Primary ? p.Accent : p.Border;
        var text = Enabled ? p.Text : p.Muted;
        var sub = Enabled ? (Primary ? Theme.Blend(p.SubText, p.Accent, 0.35) : p.SubText) : p.Muted;

        if (_attention > 0 && Enabled)
        {
            fill = Theme.Blend(fill, p.Accent, _attention * 0.55);
            border = Theme.Blend(border, p.Accent, _attention);
        }

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Drawing.RoundedRect(r, S(10)))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border, Focused && Enabled ? S(1.8f) : 1f))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        var box = S(30);
        var bx = S(13);
        var boxRect = new RectangleF(bx, (Height - box) / 2, box, box);
        using (var iconPath = Drawing.RoundedRect(boxRect, S(8)))
        using (var iconFill = new SolidBrush(Primary ? Theme.Blend(p.Surface, p.Accent, 0.28) : p.SurfaceAlt))
            g.FillPath(iconFill, iconPath);
        var side = (int)S(18);
        var iconBox = new Rectangle((int)(boxRect.X + (box - side) / 2), (int)(boxRect.Y + (box - side) / 2), side, side);
        if (!Drawing.TryDrawImage(g, GlyphImage, iconBox, Enabled ? 1f : 0.4f))
        {
            TextRenderer.DrawText(g, Glyph, Theme.IconFont(Primary ? 12f : 10f), Rectangle.Round(boxRect),
                Enabled ? (Primary ? p.Accent : p.SubText) : p.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // 오른쪽에는 숫자 배지만 두고, 그 위에 "지난번" 꼬리표를 얹는다(세 카드의 글 폭이 같아진다).
        var badgeW = Badge > 0 ? (int)S(26) : (int)S(8);
        var tx = (int)(boxRect.Right + S(11));
        var tw = Math.Max((int)S(40), Width - tx - badgeW - (int)S(8));

        TextRenderer.DrawText(g, Title, Theme.UiFont(10f, FontStyle.Bold), new Rectangle(tx, (int)S(12), tw, (int)S(20)), text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Detail, Theme.UiFont(8.5f), new Rectangle(tx, (int)S(32), tw, (int)S(18)), sub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        if (Badge > 0)
        {
            var br = new RectangleF(Width - S(26), (Height - S(18)) / 2, S(20), S(18));
            using (var bp = Drawing.RoundedRect(br, S(5)))
            using (var pen = new Pen(Enabled ? p.Border : Theme.Blend(p.Background, p.Border, 0.6)))
                g.DrawPath(pen, bp);
            TextRenderer.DrawText(g, Badge.ToString(), Theme.UiFont(8f), Rectangle.Round(br), p.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        if (Tagline.Length > 0)
        {
            var tf = Theme.UiFont(8f);
            var tagW = Drawing.MeasureText(Tagline, tf).Width;
            TextRenderer.DrawText(g, Tagline, tf,
                new Rectangle(Width - tagW - (int)S(8), (int)S(3), tagW + (int)S(4), (int)S(14)),
                Enabled ? p.Accent : p.Muted, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>
/// 접속 방식 카드 묶음. 세 방식을 모두 같은 크기로 한 줄에 늘어놓고,
/// 창이 좁아지면 한 열로 쌓는다. 지난번에 쓴 카드만 색으로 강조한다.
/// </summary>
public sealed class MethodList : Panel
{
    /// <summary>이 폭 아래로는 카드를 한 열로 쌓는다.</summary>
    public const int TwoColumnMinWidth = 860;

    /// <summary>한 줄에 놓는 최대 칸 수.</summary>
    public const int MaxColumns = 3;

    private readonly List<MethodCard> _cards = new();

    public MethodList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = false;
    }

    public IReadOnlyList<MethodCard> Cards => _cards;

    /// <summary>지금 폭에서 한 줄에 놓는 칸 수(자체검사용).</summary>
    public int ColumnCount => ColumnsFor(Width);

    private int ColumnsFor(int width) =>
        _cards.Count == 0 ? 1
        : width >= Drawing.Scale(this, TwoColumnMinWidth) ? Math.Min(MaxColumns, _cards.Count)
        : 1;

    public MethodCard Add(MethodCard card)
    {
        _cards.Add(card);
        Controls.Add(card);
        return card;
    }

    public int PreferredHeight(int width)
    {
        if (_cards.Count == 0) return 0;
        var gap = (int)Drawing.Scale(this, 8);
        var cols = ColumnsFor(width);
        var rows = (_cards.Count + cols - 1) / cols;
        return rows * _cards[0].PreferredHeight() + (rows - 1) * gap;
    }

    protected override void OnResize(EventArgs e)
    {
        Relayout();
        base.OnResize(e);
    }

    /// <summary>카드를 모두 지운다(설정이 바뀌어 다시 만들 때).</summary>
    public void Clear()
    {
        foreach (var c in _cards) c.Dispose();
        _cards.Clear();
        Controls.Clear();
    }

    public void Relayout()
    {
        if (_cards.Count == 0) return;
        var gap = (int)Drawing.Scale(this, 8);
        var cols = ColumnCount;
        var h = _cards[0].PreferredHeight();
        var colW = (Width - gap * (cols - 1)) / cols;
        for (var i = 0; i < _cards.Count; i++)
        {
            var col = i % cols;
            var row = i / cols;
            // 마지막 열은 나눗셈에서 남은 1~2픽셀까지 가져가 오른쪽 끝을 맞춘다.
            var w = col == cols - 1 ? Width - col * (colW + gap) : colW;
            _cards[i].Bounds = new Rectangle(col * (colW + gap), row * (h + gap), w, h);
        }
    }
}
