using System.Drawing.Drawing2D;

namespace RemoteAccessHub.UI.Controls;

internal static class Drawing
{
    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    public static float Scale(Control c, float v) => v * c.DeviceDpi / 96f;

    public static Size MeasureText(string text, Font font) =>
        TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

    /// <summary>부모 배경색(투명 모서리 채우기용).</summary>
    public static Color ParentBack(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return Theme.Current.Background;
    }
}

/// <summary>둥근 모서리 카드(표면색 + 테두리). 안쪽 컨트롤은 Padding 안에 배치.</summary>
public sealed class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Current.Surface;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var p = Theme.Current;
        e.Graphics.Clear(Drawing.ParentBack(this));
        Drawing.Smooth(e.Graphics);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Drawing.RoundedRect(r, Drawing.Scale(this, 8));
        using var fill = new SolidBrush(p.Surface);
        using var pen = new Pen(p.Border);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Current.Surface;
        Invalidate(true);
    }
}
