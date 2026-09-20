<#
.SYNOPSIS
  프로그램 아이콘(src\RemoteAccessHub\Assets\app.ico)을 코드로 그려 만든다.
.DESCRIPTION
  초록 타일 위에 모니터와 전원 표시(WOL로 PC를 켜고 원격 접속)를 그린다.
  16~24px은 작은 크기용 단순 도안(흰 화면 + 짙은 전원 표시), 32px 이상은 어두운 화면 + 밝은 전원 표시.
  48px 이하는 32비트 BMP, 256px은 PNG로 담는다. 외부 도구나 패키지는 쓰지 않는다.
.EXAMPLE
  .\tools\make-icon.ps1
  .\tools\make-icon.ps1 -Preview C:\temp\icon-preview.png
#>
[CmdletBinding()]
param(
    [string]$Output,
    [string]$Preview
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Output) { $Output = Join-Path $root 'src\RemoteAccessHub\Assets\app.ico' }

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class RahIconMaker
{
    public static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    static Color Hex(string h) { return ColorTranslator.FromHtml(h); }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // 256 기준 도안 좌표를 실제 크기로 옮긴다. 작은 크기에서는 가장자리를 픽셀 경계에 맞춰 흐리지 않게 한다.
    sealed class Grid
    {
        readonly float _s; readonly bool _snap;
        public Grid(int size) { _s = size / 256f; _snap = size <= 48; }
        public float V(float v) { return _snap ? (float)Math.Round(v * _s) : v * _s; }
        public float F(float v) { return v * _s; }
        public RectangleF R(float x, float y, float w, float h)
        {
            float l = V(x), t = V(y), r = V(x + w), b = V(y + h);
            return new RectangleF(l, t, Math.Max(1, r - l), Math.Max(1, b - t));
        }
    }

    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            var q = new Grid(size);
            bool small = size <= 24;

            // 1) 타일: 앱 강조색(초록) 세로 그라데이션
            float margin = size <= 24 ? 0 : size <= 48 ? 1 : 16 * size / 256f;
            var tile = new RectangleF(margin, margin, size - margin * 2, size - margin * 2);
            float tileRadius = tile.Width * 0.23f;
            using (var path = RoundRect(tile, tileRadius))
            using (var brush = new LinearGradientBrush(new PointF(0, tile.Top), new PointF(0, tile.Bottom + 1), Hex("#8CC751"), Hex("#4C8A24")))
            {
                g.FillPath(brush, path);
                if (size >= 32)
                {
                    using (var edge = new Pen(Color.FromArgb(90, 30, 60, 12), Math.Max(1f, size / 128f)))
                    {
                        g.DrawPath(edge, path);
                    }
                }
            }

            var white = Hex("#FFFFFF");
            if (small)
            {
                // 2a) 작은 크기: 흰 화면 + 짙은 초록 전원 표시, 받침은 한 줄
                var screen = q.R(40, 50, 176, 124);
                using (var p = RoundRect(screen, Math.Max(1f, q.F(16))))
                using (var b = new SolidBrush(white)) g.FillPath(b, p);
                using (var b = new SolidBrush(white))
                {
                    g.FillRectangle(b, q.R(112, 174, 32, 18));
                    g.FillRectangle(b, q.R(76, 192, 104, 22));
                }
                var ink = Hex("#2B5512");
                if (size == 16)
                {
                    // 16px은 원호가 번져 보이지 않으므로 픽셀로 찍는다(6×6).
                    string[] dots = { "..##..", "#.##.#", "#.##.#", "#....#", "#....#", ".####." };
                    int ox = (int)(screen.X + (screen.Width - 6) / 2), oy = (int)(screen.Y + (screen.Height - 6) / 2);
                    var old = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.None;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    using (var b = new SolidBrush(ink))
                        for (int y = 0; y < dots.Length; y++)
                            for (int x = 0; x < dots[y].Length; x++)
                                if (dots[y][x] == '#') g.FillRectangle(b, ox + x, oy + y, 1, 1);
                    g.SmoothingMode = old;
                }
                else
                {
                    // 굵기 2px, 중심을 픽셀 경계에 두어 선이 번지지 않게 한다.
                    float cx = (float)Math.Round(screen.X + screen.Width / 2f);
                    float cy = (float)Math.Round(screen.Y + screen.Height / 2f + 0.5f);
                    DrawPower(g, cx, cy, size <= 20 ? 3f : 3.5f, 2f, ink, cy - (size <= 20 ? 3f : 3.5f) - 1f);
                }
            }
            else
            {
                // 2b) 32px 이상: 흰 테두리 모니터, 어두운 화면, 밝은 초록 전원 표시, 받침
                var bezel = q.R(48, 58, 160, 112);
                using (var p = RoundRect(bezel, Math.Max(1.5f, q.F(14))))
                using (var b = new SolidBrush(white)) g.FillPath(b, p);
                float inset = Math.Max(2f, q.V(11));
                var screen = new RectangleF(bezel.X + inset, bezel.Y + inset, bezel.Width - inset * 2, bezel.Height - inset * 2);
                using (var p = RoundRect(screen, Math.Max(1f, q.F(6))))
                using (var b = new LinearGradientBrush(new PointF(0, screen.Top), new PointF(0, screen.Bottom + 1), Hex("#243620"), Hex("#16221A")))
                    g.FillPath(b, p);
                using (var b = new SolidBrush(white))
                {
                    g.FillRectangle(b, q.R(114, 170, 28, 18));
                    using (var p = RoundRect(q.R(80, 186, 96, 16), Math.Max(1f, q.F(8)))) g.FillPath(b, p);
                }
                if (size <= 48)
                {
                    float cx = (float)Math.Round(screen.X + screen.Width / 2f);
                    float cy = (float)Math.Round(screen.Y + screen.Height / 2f);
                    DrawPower(g, cx, cy, Math.Max(4f, q.F(26)), 2f, Hex("#B6E27C"));
                }
                else
                {
                    float cx = screen.X + screen.Width / 2f, cy = screen.Y + screen.Height / 2f + q.F(4);
                    DrawPower(g, cx, cy, q.F(26), q.F(11), Hex("#A5D66A"));
                }
            }
        }
        return bmp;
    }

    // 전원 표시: 위쪽이 트인 원호 + 세로 막대
    static void DrawPower(Graphics g, float cx, float cy, float radius, float stroke, Color color, float lineTop = float.NaN)
    {
        using (var pen = new Pen(color, stroke))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawArc(pen, cx - radius, cy - radius, radius * 2, radius * 2, 300, 300);
            // 작은 크기에서는 둥근 끝이 화면 밖으로 삐져나오지 않도록 막대 위끝을 원호 높이에 맞춘다.
            float top = !float.IsNaN(lineTop) ? lineTop : stroke > 3 ? cy - radius - stroke * 0.55f : cy - radius + stroke * 0.5f;
            g.DrawLine(pen, cx, top, cx, cy - radius * 0.2f);
        }
    }

    static byte[] ToPng(Bitmap bmp)
    {
        using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    // 32비트 BMP(DIB) 항목: BITMAPINFOHEADER + 아래에서 위로 BGRA + AND 마스크
    static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int maskRow = ((w + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);
                bw.Write(0); bw.Write(w * h * 4 + maskRow * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                var row = new byte[w * 4];
                var alpha = new bool[w, h];
                for (int y = h - 1; y >= 0; y--)
                {
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, w * 4);
                    bw.Write(row);
                    for (int x = 0; x < w; x++) alpha[x, y] = row[x * 4 + 3] != 0;
                }
                var mask = new byte[maskRow];
                for (int y = h - 1; y >= 0; y--)
                {
                    Array.Clear(mask, 0, mask.Length);
                    for (int x = 0; x < w; x++) if (!alpha[x, y]) mask[x / 8] |= (byte)(0x80 >> (x % 8));
                    bw.Write(mask);
                }
                return ms.ToArray();
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    public static void WriteIco(string path)
    {
        var images = new List<byte[]>();
        foreach (var size in Sizes)
            using (var bmp = Render(size)) images.Add(size >= 256 ? ToPng(bmp) : ToDib(bmp));

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var fs = File.Create(path))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int s = Sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s)); bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
                bw.Write(images[i].Length); bw.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) bw.Write(img);
        }
    }

    // 확인용 견본: 어두운/밝은 바탕에 실제 크기, 작은 크기는 8배 확대(픽셀 그대로)
    public static void WritePreview(string path)
    {
        int[] shown = { 16, 20, 24, 32, 48, 64, 256 };
        int[] zoom = { 16, 20, 24, 32 };
        const int pad = 24;
        int stripH = 256 + pad * 2;
        int zoomW = 0; foreach (var z in zoom) zoomW += z * 8 + pad;
        int realW = pad; foreach (var s in shown) realW += s + pad;
        int width = Math.Max(realW, zoomW + pad);
        int height = stripH * 2 + 32 * 8 + pad * 2;
        using (var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Hex("#F3F4F6"));
            using (var dark = new SolidBrush(Hex("#202020"))) g.FillRectangle(dark, 0, 0, width, stripH);
            for (int band = 0; band < 2; band++)
            {
                int x = pad;
                foreach (var s in shown)
                {
                    using (var icon = Render(s)) g.DrawImageUnscaled(icon, x, band * stripH + pad + (256 - s));
                    x += s + pad;
                }
            }
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            int zx = pad;
            foreach (var z in zoom)
            {
                using (var icon = Render(z)) g.DrawImage(icon, new Rectangle(zx, stripH * 2 + pad, z * 8, z * 8));
                zx += z * 8 + pad;
            }
            sheet.Save(path, ImageFormat.Png);
        }
    }
}
'@

[RahIconMaker]::WriteIco($Output)
Write-Host "아이콘 생성: $Output ($((Get-Item $Output).Length) bytes)"
if ($Preview) {
    [RahIconMaker]::WritePreview($Preview)
    Write-Host "견본: $Preview"
}
