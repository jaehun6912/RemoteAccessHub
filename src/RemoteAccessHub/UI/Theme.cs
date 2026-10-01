using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RemoteAccessHub.UI;

public enum ThemeMode
{
    System,
    Dark,
    Light,
}

/// <summary>화면 색 묶음. 글자와 배경의 명암비는 단위 검사(ThemeTests)로 확인한다.</summary>
public sealed record Palette(
    string Name,
    bool IsDark,
    Color Background,
    Color Surface,
    Color SurfaceAlt,
    Color Border,
    Color Text,
    Color SubText,
    Color Muted,
    Color Accent,
    Color AccentHover,
    Color AccentPressed,
    Color OnAccent,
    Color Success,
    Color Warning,
    Color Danger,
    Color Info,
    Color LogBackground,
    Color LogText);

public static class Theme
{
    /// <summary>어두운 테마. 공유기 관리 화면(어두운 바탕, 초록 강조)과 어울리게 맞췄다.</summary>
    public static readonly Palette Dark = new(
        "dark", true,
        Background: Hex("#16181A"),
        Surface: Hex("#202326"),
        SurfaceAlt: Hex("#2A2E32"),
        Border: Hex("#3A3F44"),
        Text: Hex("#E9ECEF"),
        SubText: Hex("#B3BAC1"),
        Muted: Hex("#7E868E"),
        Accent: Hex("#7CB342"),
        AccentHover: Hex("#8BC34A"),
        AccentPressed: Hex("#689F38"),
        OnAccent: Hex("#0E1A04"),
        Success: Hex("#81C784"),
        Warning: Hex("#FFB74D"),
        Danger: Hex("#EF7B7B"),
        Info: Hex("#64B5F6"),
        LogBackground: Hex("#121416"),
        LogText: Hex("#C9CFD5"));

    public static readonly Palette Light = new(
        "light", false,
        Background: Hex("#F3F4F6"),
        Surface: Hex("#FFFFFF"),
        SurfaceAlt: Hex("#EEF0F3"),
        Border: Hex("#D5D9DE"),
        Text: Hex("#1D2125"),
        SubText: Hex("#525A63"),
        Muted: Hex("#9AA1A9"),
        Accent: Hex("#3F7D20"),
        AccentHover: Hex("#4A8F27"),
        AccentPressed: Hex("#336A19"),
        OnAccent: Hex("#FFFFFF"),
        Success: Hex("#2E7D32"),
        Warning: Hex("#9A5B00"),
        Danger: Hex("#C62828"),
        Info: Hex("#1565C0"),
        LogBackground: Hex("#FAFBFC"),
        LogText: Hex("#2B3137"));

    public static Palette Current { get; private set; } = Dark;
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    public static event Action? Changed;

    public static ThemeMode ParseMode(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "dark" => ThemeMode.Dark,
        "light" => ThemeMode.Light,
        _ => ThemeMode.System,
    };

    public static string ToSetting(ThemeMode m) => m switch
    {
        ThemeMode.Dark => "dark",
        ThemeMode.Light => "light",
        _ => "system",
    };

    public static Palette Resolve(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => Dark,
        ThemeMode.Light => Light,
        _ => SystemPrefersLight() ? Light : Dark,
    };

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        var next = Resolve(mode);
        var changed = !ReferenceEquals(next, Current);
        Current = next;
        if (changed) Changed?.Invoke();
    }

    /// <summary>시스템 설정이 바뀌었을 때(시스템 모드일 때만) 다시 적용.</summary>
    public static void RefreshFromSystem()
    {
        if (Mode == ThemeMode.System) Apply(ThemeMode.System);
    }

    /// <summary>Windows "앱 모드" 설정. 읽지 못하면 어두운 테마를 쓴다.</summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 글꼴

    private static readonly Dictionary<(float, FontStyle), Font> FontCache = new();
    private static readonly Dictionary<float, Font> IconFontCache = new();
    private static string? _iconFamily;

    public static Font UiFont(float size = 9.5f, FontStyle style = FontStyle.Regular)
    {
        lock (FontCache)
        {
            if (!FontCache.TryGetValue((size, style), out var f))
            {
                f = new Font("Malgun Gothic", size, style);
                FontCache[(size, style)] = f;
            }
            return f;
        }
    }

    /// <summary>Windows 11 "Segoe Fluent Icons", 없으면 Windows 10 "Segoe MDL2 Assets".</summary>
    public static Font IconFont(float size = 11f)
    {
        lock (IconFontCache)
        {
            if (IconFontCache.TryGetValue(size, out var f)) return f;
            _iconFamily ??= FindIconFamily();
            f = new Font(_iconFamily, size, FontStyle.Regular);
            IconFontCache[size] = f;
            return f;
        }
    }

    private static string FindIconFamily()
    {
        try
        {
            using var fonts = new System.Drawing.Text.InstalledFontCollection();
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                if (fonts.Families.Any(ff => ff.Name == name)) return name;
        }
        catch { /* ignore */ }
        return "Segoe UI Symbol";
    }

    /// <summary>아이콘 글리프(Segoe Fluent Icons / MDL2 공통 코드).</summary>
    public static class Glyph
    {
        public const string Power = "";
        public const string Connect = "";
        public const string Play = "";
        public const string Settings = "";
        public const string More = "";
        public const string ChevronDown = "";
        public const string ChevronUp = "";
        public const string Cancel = "";
        public const string Check = "";
        public const string Warning = "";
        public const string Error = "";
        public const string Info = "";
        public const string Sync = "";
        public const string Globe = "";
        public const string Lock = "";
        /// <summary>원격 접속(모니터) 아이콘. 크롬 원격 데스크톱 앱이 없을 때 쓴다.</summary>
        public const string Remote = "\uE8AF";
        public const string Document = "";
        public const string Folder = "";
        public const string Refresh = "";
        public const string Router = "";
        public const string Log = "";
        public const string Exit = "";
    }

    // ------------------------------------------------------------------ 창

    /// <summary>제목 표시줄을 테마에 맞춘다(Windows 10 20H1 이상). 실패해도 무시.</summary>
    public static void ApplyTitleBar(Form form)
    {
        if (!form.IsHandleCreated) return;
        try
        {
            var value = Current.IsDark ? 1 : 0;
            if (NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
        }
        catch { /* ignore */ }
    }

    /// <summary>표준 컨트롤(텍스트 상자·체크 상자 등)에 색을 입힌다. 직접 그리는 컨트롤은 스스로 Theme.Current를 쓴다.</summary>
    public static void ApplyToStandardControls(Control root)
    {
        var p = Current;
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox tb:
                    tb.BackColor = tb.ReadOnly && tb.Multiline ? p.LogBackground : p.SurfaceAlt;
                    tb.ForeColor = tb.ReadOnly && tb.Multiline ? p.LogText : p.Text;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case NumericUpDown nud:
                    nud.BackColor = p.SurfaceAlt;
                    nud.ForeColor = p.Text;
                    nud.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox cb:
                    cb.BackColor = p.SurfaceAlt;
                    cb.ForeColor = p.Text;
                    cb.FlatStyle = FlatStyle.Flat;
                    break;
                case CheckBox chk:
                    chk.ForeColor = p.Text;
                    chk.BackColor = Color.Transparent;
                    chk.FlatStyle = FlatStyle.Standard;
                    break;
                case Label lbl when lbl.Tag as string != "keep":
                    lbl.ForeColor = lbl.Tag as string == "sub" ? p.SubText : lbl.Tag as string == "section" ? p.Accent : p.Text;
                    lbl.BackColor = Color.Transparent;
                    break;
                case Panel or TableLayoutPanel or FlowLayoutPanel when c is not Controls.CardPanel:
                    c.BackColor = c.Tag as string == "surface" ? p.Surface : Color.Transparent;
                    break;
            }
            if (c.HasChildren) ApplyToStandardControls(c);
        }
    }

    // ------------------------------------------------------------------ 색 계산

    public static Color Hex(string hex)
    {
        var h = hex.TrimStart('#');
        return Color.FromArgb(Convert.ToInt32(h[..2], 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
    }

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * t),
        (int)Math.Round(a.G + (b.G - a.G) * t),
        (int)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>WCAG 2.x 명암비.</summary>
    public static double Contrast(Color a, Color b)
    {
        static double Lum(Color c)
        {
            static double Ch(int v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var l1 = Lum(a);
        var l2 = Lum(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}

internal static class NativeMethods
{
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    public const uint FLASHW_TRAY = 0x2;
    public const uint FLASHW_TIMERNOFG = 0xC;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>창이 활성 상태가 아닐 때 작업 표시줄 단추를 깜박인다(창이 앞으로 오면 멈춤).</summary>
    public static void FlashUntilForeground(Form form)
    {
        if (!form.IsHandleCreated || Form.ActiveForm == form) return;
        try
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = form.Handle,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch { /* ignore */ }
    }
}
