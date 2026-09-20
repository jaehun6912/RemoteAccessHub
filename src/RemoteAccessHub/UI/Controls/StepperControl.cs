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

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Drawing.ParentBack(this));
        Drawing.Smooth(g);
        if (_steps.Count == 0) return;

        float S(float v) => Drawing.Scale(this, v);
        var pad = S(16);
        var colW = (Width - pad * 2) / _steps.Count;
        var d = S(26);
        var cy = S(14) + d / 2;

        // 연결선: 글자와 겹치지 않도록 제목·세부 문구가 끝난 뒤부터 다음 원 앞까지만 그린다.
        var titleFont = Theme.UiFont(10f, FontStyle.Bold);
        var detailFont = Theme.UiFont(9f);
        for (var i = 0; i < _steps.Count - 1; i++)
        {
            var textX = pad + i * colW + d + S(10);
            var maxText = colW - d - S(18);
            var textW = Math.Min(maxText, Math.Max(Drawing.MeasureText(_steps[i].Title, titleFont).Width, Drawing.MeasureText(_steps[i].Detail, detailFont).Width));
            var x1 = textX + textW + S(12);
            var x2 = pad + (i + 1) * colW - S(10);
            if (x2 - x1 < S(16)) continue;
            var done = _steps[i].State == StepState.Done;
            using var pen = new Pen(done ? p.Success : p.Border, S(2));
            g.DrawLine(pen, x1, cy, x2, cy);
        }

        for (var i = 0; i < _steps.Count; i++)
        {
            var st = _steps[i];
            var x = pad + i * colW;
            var color = StateColor(p, st.State);
            var circle = new RectangleF(x, cy - d / 2, d, d);

            if (st.State is StepState.Pending)
            {
                using var pen = new Pen(p.Border, S(1.5f));
                using var fill = new SolidBrush(p.SurfaceAlt);
                g.FillEllipse(fill, circle);
                g.DrawEllipse(pen, circle);
            }
            else if (st.State == StepState.Active)
            {
                using var fill = new SolidBrush(Theme.Blend(p.Surface, color, 0.22));
                using var pen = new Pen(color, S(2));
                g.FillEllipse(fill, circle);
                g.DrawEllipse(pen, circle);
            }
            else
            {
                using var fill = new SolidBrush(color);
                g.FillEllipse(fill, circle);
            }

            var glyph = st.State switch
            {
                StepState.Done => Theme.Glyph.Check,
                StepState.Failed => Theme.Glyph.Cancel,
                StepState.Warning => "!",
                _ => (i + 1).ToString(),
            };
            var glyphFont = st.State is StepState.Done or StepState.Failed ? Theme.IconFont(9.5f) : Theme.UiFont(9.5f, FontStyle.Bold);
            var glyphColor = st.State switch
            {
                StepState.Pending => p.SubText,
                StepState.Active => color,
                _ => p.IsDark ? p.Background : Color.White,
            };
            TextRenderer.DrawText(g, glyph, glyphFont, Rectangle.Round(circle), glyphColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var textX = (int)(x + d + S(10));
            var textW = (int)Math.Max(10, colW - d - S(18));
            var titleRect = new Rectangle(textX, (int)(cy - S(19)), textW, (int)S(20));
            TextRenderer.DrawText(g, st.Title, Theme.UiFont(10f, FontStyle.Bold), titleRect,
                st.State == StepState.Pending ? p.SubText : p.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            var detailRect = new Rectangle(textX, (int)(cy + S(1)), textW, (int)S(18));
            TextRenderer.DrawText(g, st.Detail, Theme.UiFont(9f), detailRect,
                st.State is StepState.Failed or StepState.Warning ? color : p.SubText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            if (st.Progress is { } prog && st.State == StepState.Active)
            {
                var barY = cy + S(24);
                var bar = new RectangleF(textX, barY, textW - S(12), S(4));
                using var track = Drawing.RoundedRect(bar, S(2));
                using var trackBrush = new SolidBrush(p.SurfaceAlt);
                g.FillPath(trackBrush, track);
                if (prog > 0)
                {
                    var fillRect = new RectangleF(bar.X, bar.Y, Math.Max(S(4), (float)(bar.Width * prog)), bar.Height);
                    using var fillPath = Drawing.RoundedRect(fillRect, S(2));
                    using var fillBrush = new SolidBrush(color);
                    g.FillPath(fillBrush, fillPath);
                }
            }
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
        var textRect = new Rectangle((int)S(50), (int)S(4), Width - (int)S(62), Height - (int)S(8));
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
