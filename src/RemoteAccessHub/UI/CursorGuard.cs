using System.Runtime.InteropServices;
using RemoteAccessHub.Core;

namespace RemoteAccessHub.UI;

/// <summary>
/// 메인 창 위에서 마우스 커서가 숨겨진 채 남는 문제를 막는다.
///
/// 원인: 공유기 화면(WebView2)에 아이디·보안문자를 입력하면 Windows의 "입력하는 동안 포인터 숨기기" 설정에 따라
/// 브라우저가 커서를 숨긴다. 브라우저 창은 다른 프로세스지만 이 창에 붙어 입력 상태를 함께 쓰므로
/// 숨김이 메인 창 전체에 적용된다. 브라우저는 자기 화면 위에서 마우스가 움직여야 커서를 다시 보이는데,
/// 로그인 직후 공유기 화면을 접으면 그럴 기회가 없어 메인 창 위에서만 커서가 계속 안 보인다(창 밖에서는 보임).
///
/// 대응: 메인 창의 컨트롤(버튼·안내 줄 등) 위에서 마우스가 움직일 때, 그리고 공유기 화면을 접을 때
/// 커서가 숨겨져 있으면 다시 보이게 한다. 펼쳐진 공유기 화면 위(다른 프로세스 창)나 설정 창은 건드리지 않는다.
/// </summary>
public sealed class CursorGuard : IMessageFilter
{
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_NCMOUSEMOVE = 0x00A0;
    internal const int CURSOR_SHOWING = 0x1;
    internal const int CURSOR_SUPPRESSED = 0x2;

    private readonly Form _owner;
    private readonly Control _browserArea;
    private readonly AppLog _log;

    /// <summary>검사용으로 바꿀 수 있는 커서 상태 읽기·표시 호출.</summary>
    internal Func<int> ReadCursorFlags { get; set; } = NativeReadCursorFlags;
    internal Func<bool, int> ShowCursorCall { get; set; } = NativeShowCursor;

    public int RestoreCount { get; private set; }

    public CursorGuard(Form owner, Control browserArea, AppLog log)
    {
        _owner = owner;
        _browserArea = browserArea;
        _log = log;
    }

    /// <summary>숨겨진 커서를 다시 보여야 하는가. 터치·펜 입력으로 시스템이 감춘 경우(SUPPRESSED)는 건드리지 않는다.</summary>
    public static bool ShouldRestore(int cursorFlags, bool pointerOverOwnWindow, bool pointerOverBrowser) =>
        pointerOverOwnWindow
        && !pointerOverBrowser
        && (cursorFlags & CURSOR_SHOWING) == 0
        && (cursorFlags & CURSOR_SUPPRESSED) == 0;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == WM_MOUSEMOVE || m.Msg == WM_NCMOUSEMOVE)
            RestoreIfHidden(m.HWnd, "마우스 이동");
        return false; // 메시지는 그대로 전달
    }

    /// <summary>마우스 포인터가 지금 있는 곳을 기준으로 검사한다(공유기 화면을 접은 직후 등).</summary>
    public bool RestoreIfHiddenAtPointer(string reason)
    {
        try
        {
            if (!GetCursorPos(out var pt)) return false;
            return RestoreIfHidden(WindowFromPoint(pt), reason);
        }
        catch
        {
            return false;
        }
    }

    public bool RestoreIfHidden(IntPtr hwndUnderPointer, string reason)
    {
        if (hwndUnderPointer == IntPtr.Zero || _owner.IsDisposed) return false;
        var control = Control.FromChildHandle(hwndUnderPointer);
        var overOwn = control != null && (ReferenceEquals(control, _owner) || ReferenceEquals(control.FindForm(), _owner));
        var overBrowser = control != null && (ReferenceEquals(control, _browserArea) || _browserArea.Contains(control));
        var flags = ReadCursorFlags();
        if (!ShouldRestore(flags, overOwn, overBrowser)) return false;

        // 표시 카운터를 올려 가며 보일 때까지(과도한 호출을 막기 위해 최대 3번)
        for (var i = 0; i < 3; i++)
        {
            ShowCursorCall(true);
            flags = ReadCursorFlags();
            if ((flags & CURSOR_SHOWING) != 0) break;
        }
        var shown = (flags & CURSOR_SHOWING) != 0;
        RestoreCount++;
        var msg = shown
            ? $"숨겨진 마우스 커서를 다시 표시했습니다({reason}). 공유기 화면 입력 중 Windows '입력하는 동안 포인터 숨기기'로 숨겨진 상태였습니다."
            : $"숨겨진 마우스 커서를 다시 표시하지 못했습니다({reason}, 상태 {flags}).";
        if (RestoreCount <= 3 || !shown) _log.Info(msg);
        else _log.Debug(msg);
        return shown;
    }

    private static int NativeReadCursorFlags()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref ci) ? ci.flags : CURSOR_SHOWING;
    }

    private static int NativeShowCursor(bool show) => ShowCursor(show);

    /// <summary>자체검사용: 창에 마우스 이동 메시지를 보낸다.</summary>
    internal static void PostMouseMove(IntPtr hwnd) => PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((5 << 16) | 5));

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern int ShowCursor(bool bShow);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
