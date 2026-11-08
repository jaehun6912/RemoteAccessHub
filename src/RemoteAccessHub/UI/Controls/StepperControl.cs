using System.Drawing.Drawing2D;

namespace RemoteAccessHub.UI.Controls;

/// <summary>가로 단계 표시: 번호 원 → 연결선 → 제목·세부 문구, 진행 중 단계는 진행 막대.</summary>
public sealed class StepperControl : Control
{
    private IReadOnlyList<StepInfo> _steps = Array.Empty<StepInfo>();

    public StepperControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.ProgressBar;
        TabStop = false;
    }

    public void SetSteps(IReadOnlyList<StepInfo> steps)
    {
        _steps = steps;
        AccessibleName = string.Join(", ", steps.Select((s, i) => $"{i + 1}단계 {s.Title} {StateWord(s.State)} {s.Detail}"));
        Invalidate();
    }

    private static string StateWord(StepState s) => s switch
    {
        StepState.Done => "완료",
        StepState.Active => "진행 중",
        StepState.Warning => "주의",
        StepState.Failed => "실패",
        _ => "대기",
    };

    private static Color StateColor(Palette p, StepState s) => s switch
    {
        StepState.Done => p.Success,
        StepState.Active => p.Info,
        StepState.Warning => p.Warning,
        StepState.Failed => p.Danger,
        _ => p.Muted,
    };

    /// <summary>이 폭 아래로는 2칸씩 두 줄로 접는다.</summary>
    public const int FourColumnMinWidth = 860;

    /// <summary>지금 폭에서 한 줄에 몇 칸이 들어가는지(자체검사용).</summary>
    public int ColumnCount => Columns(Width);

    private int Columns(int width) => _steps.Count <= 2 || width < Drawing.Scale(this, FourColumnMinWidth) ? 2 : 4;

    private int CardHeight() => (int)Drawing.Scale(this, 64);

    /// <summary>주어진 폭에서 필요한 높이(칸이 몇 줄로 접히는지에 따라 달라진다).</summary>
    public int PreferredHeight(int width)
    {
        if (_steps.Count == 0) return 0;
        var gap = (int)Drawing.Scale(this, 10);
        var cols = Columns(width);
        var rows = (_steps.Count + cols - 1) / cols;
        return rows * CardHeight() + (rows - 1) * gap;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Drawing.ParentBack(this));
        Drawing.Smooth(g);
        if (_steps.Count == 0) return;

        float S(float v) => Drawing.Scale(this, v);
        var gap = (int)S(10);
        var cols = Columns(Width);
        var rows = (_steps.Count + cols - 1) / cols;
        var cardH = CardHeight();
        var colW = (Width - gap * (cols - 1)) / (float)cols;

        for (var i = 0; i < _steps.Count; i++)
        {
            var st = _steps[i];
            var col = i % cols;
            var row = i / cols;
            var x = col * (colW + gap);
            var y = row * (cardH + gap);
            var color = StateColor(p, st.State);
            var lit = st.State != StepState.Pending;

            var r = new RectangleF(x + 0.5f, y + 0.5f, colW - 1.5f, cardH - 1.5f);
            using (var path = Drawing.RoundedRect(r, S(10)))
            using (var fill = new SolidBrush(lit ? Theme.Blend(p.Surface, color, 0.13) : Theme.Blend(p.Background, p.Surface, 0.7)))
            using (var pen = new Pen(lit ? Theme.Blend(p.Border, color, 0.65) : p.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            var d = S(19);
            var cx = x + S(14);
            var cy = y + S(13);
            var circle = new RectangleF(cx, cy, d, d);
            if (st.State == StepState.Done)
            {
                using var fill = new SolidBrush(p.Accent);
                g.FillEllipse(fill, circle);
            }
            else
            {
                using var pen = new Pen(st.State == StepState.Pending ? p.Muted : color, S(1.6f));
                g.DrawEllipse(pen, circle);
            }

            var glyph = st.State switch
            {
                StepState.Done => Theme.Glyph.Check,
                StepState.Failed => Theme.Glyph.Cancel,
                StepState.Warning => "!",
                StepState.Active => "",
                _ => (i + 1).ToString(),
            };
            if (glyph.Length > 0)
            {
                var glyphFont = st.State is StepState.Done or StepState.Failed ? Theme.IconFont(8f) : Theme.UiFont(8.5f, FontStyle.Bold);
                var glyphColor = st.State switch
                {
                    StepState.Done => p.OnAccent,
                    StepState.Pending => p.Muted,
                    _ => color,
                };
                TextRenderer.DrawText(g, glyph, glyphFont, Rectangle.Round(circle), glyphColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                // 진행 중: 원 안을 점 하나로 채워 "돌고 있음"을 나타낸다(애니메이션은 쓰지 않는다).
                using var dot = new SolidBrush(color);
                g.FillEllipse(dot, new RectangleF(cx + d / 2 - S(3), cy + d / 2 - S(3), S(6), S(6)));
            }

            var tx = (int)(cx + d + S(9));
            var tw = Math.Max((int)S(40), (int)(x + colW - S(12)) - tx);
            TextRenderer.DrawText(g, st.Title, Theme.UiFont(9.5f, FontStyle.Bold), new Rectangle(tx, (int)cy, tw, (int)S(19)),
                st.State == StepState.Pending ? p.SubText : p.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, st.Detail, Theme.UiFont(8.5f), new Rectangle((int)(x + S(14)), (int)(cy + S(23)), (int)(colW - S(26)), (int)S(17)),
                st.State is StepState.Failed or StepState.Warning ? color : p.SubText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>상단 상태 배지: 색 점 + 문구.</summary>
public sealed class StatusPill : Control
{
    private Color _dot;
    private string _glyph = "";

    public StatusPill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.UiFont(9f, FontStyle.Bold);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    public void Set(string text, Color dot, string glyph = "")
    {
        Text = text;
        AccessibleName = text;
        _dot = dot;
        _glyph = glyph;
        Width = PreferredPillWidth();
        Invalidate();
    }

    public int PreferredPillWidth() =>
        Drawing.MeasureText(Text, Font).Width + (int)Drawing.Scale(this, _glyph.Length > 0 ? 50 : 34);

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Drawing.ParentBack(this));
        Drawing.Smooth(g);
        float S(float v) => Drawing.Scale(this, v);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Drawing.RoundedRect(r, r.Height / 2))
        using (var fill = new SolidBrush(p.SurfaceAlt))
        using (var pen = new Pen(p.Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        var x = S(12);
        if (_glyph.Length > 0)
        {
            TextRenderer.DrawText(g, _glyph, Theme.IconFont(9f), new Rectangle((int)x, 0, (int)S(16), Height), p.SubText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += S(18);
        }
        var dd = S(8);
        using (var dotBrush = new SolidBrush(_dot))
            g.FillEllipse(dotBrush, x, (Height - dd) / 2, dd, dd);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(x + dd + S(6)), 0, Width, Height), p.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

public enum BannerKind
{
    Info,
    Progress,
    Success,
    Warning,
    Error,
}

/// <summary>현재 상황 안내 줄: 왼쪽 색 막대 + 아이콘 + 문구(최대 2줄).</summary>
public sealed class StatusBanner : Control
{
    private BannerKind _kind = BannerKind.Info;

    public StatusBanner()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.UiFont(10f);
        TabStop = false;
        AccessibleRole = AccessibleRole.Alert;
    }

    public BannerKind Kind => _kind;

    private readonly List<FlatButton> _buttons = new();

    /// <summary>안내 줄 오른쪽에 둘 행동 버튼. 비워서 부르면 없앤다.</summary>
    public void SetActions(params (string Text, bool Primary, Action OnClick)[] actions)
    {
        foreach (var b in _buttons) { Controls.Remove(b); b.Dispose(); }
        _buttons.Clear();
        foreach (var a in actions)
        {
            var b = new FlatButton
            {
                Text = a.Text,
                Variant = a.Primary ? ButtonVariant.Secondary : ButtonVariant.Ghost,
                Font = Theme.UiFont(9f),
            };
            var onClick = a.OnClick;
            b.Click += (_, _) => onClick();
            _buttons.Add(b);
            Controls.Add(b);
        }
        LayoutButtons();
        Invalidate();
    }

    /// <summary>자체검사용: 지금 걸려 있는 행동 버튼 글자.</summary>
    public IReadOnlyList<string> ActionTexts => _buttons.Select(b => b.Text).ToList();

    /// <summary>주어진 폭에서 문구가 다 들어가는 높이.</summary>
    public int PreferredHeight(int width)
    {
        float S(float v) => Drawing.Scale(this, v);
        var textW = Math.Max((int)S(120), width - (int)S(62) - ButtonsWidth());
        var h = TextRenderer.MeasureText(Text, Font, new Size(textW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        return Math.Max((int)S(50), h + (int)S(22));
    }

    private int ButtonsWidth()
    {
        if (_buttons.Count == 0) return 0;
        var gap = (int)Drawing.Scale(this, 7);
        var w = 0;
        foreach (var b in _buttons) w += b.PreferredWidth() + gap;
        return w + gap;
    }

    private void LayoutButtons()
    {
        if (_buttons.Count == 0) return;
        float S(float v) => Drawing.Scale(this, v);
        var h = (int)S(32);
        var gap = (int)S(7);
        var x = Width - gap;
        for (var i = _buttons.Count - 1; i >= 0; i--)
        {
            var b = _buttons[i];
            b.Size = new Size(b.PreferredWidth(), h);
            x -= b.Width;
            b.Location = new Point(x, (Height - h) / 2);
            x -= gap;
        }
    }

    protected override void OnResize(EventArgs e) { LayoutButtons(); base.OnResize(e); }

    public void Set(string text, BannerKind kind)
    {
        Text = text;
        AccessibleName = text;
        _kind = kind;
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Drawing.ParentBack(this));
        Drawing.Smooth(g);
        float S(float v) => Drawing.Scale(this, v);
        var color = _kind switch
        {
            BannerKind.Success => p.Success,
            BannerKind.Warning => p.Warning,
            BannerKind.Error => p.Danger,
            BannerKind.Progress => p.Info,
            _ => p.SubText,
        };
        var glyph = _kind switch
        {
            BannerKind.Success => Theme.Glyph.Check,
            BannerKind.Warning => Theme.Glyph.Warning,
            BannerKind.Error => Theme.Glyph.Error,
            BannerKind.Progress => Theme.Glyph.Sync,
            _ => Theme.Glyph.Info,
        };
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Drawing.RoundedRect(r, S(8)))
        using (var fill = new SolidBrush(Theme.Blend(p.Surface, color, _kind == BannerKind.Info ? 0.0 : 0.10)))
        using (var pen = new Pen(Theme.Blend(p.Border, color, _kind == BannerKind.Info ? 0.0 : 0.45)))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        using (var bar = new SolidBrush(color))
        {
            var br = new RectangleF(S(1), S(8), S(4), Height - S(16));
            using var barPath = Drawing.RoundedRect(br, S(2));
            g.FillPath(bar, barPath);
        }
        TextRenderer.DrawText(g, glyph, Theme.IconFont(13f), new Rectangle((int)S(16), 0, (int)S(24), Height), color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var textRect = new Rectangle((int)S(50), (int)S(4), Math.Max((int)S(80), Width - (int)S(62) - ButtonsWidth()), Height - (int)S(8));
        TextRenderer.DrawText(g, Text, Font, textRect, p.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

/// <summary>메뉴(ContextMenuStrip) 색을 테마에 맞춘다.</summary>
public sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemedMenuRenderer() : base(new Colors()) => RoundedEdges = false;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var p = Theme.Current;
        e.TextColor = e.Item.Enabled ? p.Text : p.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Current.SubText;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var p = Theme.Current;
        var r = new Rectangle(e.ImageRectangle.X - 2, e.ImageRectangle.Y - 2, e.ImageRectangle.Width + 4, e.ImageRectangle.Height + 4);
        TextRenderer.DrawText(e.Graphics, Theme.Glyph.Check, Theme.IconFont(9f), r, p.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private sealed class Colors : ProfessionalColorTable
    {
        private static Palette P => Theme.Current;
        public override Color ToolStripDropDownBackground => P.Surface;
        public override Color ImageMarginGradientBegin => P.Surface;
        public override Color ImageMarginGradientMiddle => P.Surface;
        public override Color ImageMarginGradientEnd => P.Surface;
        public override Color MenuBorder => P.Border;
        public override Color MenuItemBorder => P.Border;
        public override Color MenuItemSelected => P.SurfaceAlt;
        public override Color MenuItemSelectedGradientBegin => P.SurfaceAlt;
        public override Color MenuItemSelectedGradientEnd => P.SurfaceAlt;
        public override Color MenuItemPressedGradientBegin => P.Border;
        public override Color MenuItemPressedGradientEnd => P.Border;
        public override Color SeparatorDark => P.Border;
        public override Color SeparatorLight => P.Border;
        public override Color CheckBackground => P.SurfaceAlt;
        public override Color CheckSelectedBackground => P.SurfaceAlt;
        public override Color CheckPressedBackground => P.SurfaceAlt;
    }
}
