using System.Runtime.InteropServices;
using System.Text;

namespace RemoteAccessHub.Services;

/// <summary>
/// 브라우저가 띄운 앱 창을 찾아 최대화한다.
/// 크롬 원격 데스크톱 앱 창은 저장해 둔 크기를 쓰기 때문에 `--start-maximized`·`--window-size` 같은
/// 명령줄 지정을 따르지 않는다(2026-10-05 확인). 그래서 창이 뜬 뒤에 직접 최대화한다.
/// </summary>
internal static class BrowserWindows
{
    /// <summary>Chromium 계열 브라우저의 최상위 창 클래스.</summary>
    private const string WindowClass = "Chrome_WidgetWin_1";
    private const int SW_MAXIMIZE = 3;

    /// <summary>창을 가진 프로세스 이름. msedge_proxy.exe로 띄워도 창은 msedge.exe가 갖는다.</summary>
    internal static string ProcessNameFor(string browserExe)
    {
        var name = Path.GetFileNameWithoutExtension(browserExe);
        return name.EndsWith("_proxy", StringComparison.OrdinalIgnoreCase) ? name[..^"_proxy".Length] : name;
    }

    /// <summary>지금 떠 있는 그 브라우저의 창 목록.</summary>
    public static HashSet<IntPtr> Snapshot(string processName)
    {
        var windows = new HashSet<IntPtr>();
        try
        {
            var pids = ProcessIds(processName);
            if (pids.Count == 0) return windows;
            EnumWindows((h, _) =>
            {
                if (IsAppWindow(h, pids)) windows.Add(h);
                return true;
            }, IntPtr.Zero);
        }
        catch { /* 창을 못 찾아도 여는 데는 문제없다 */ }
        return windows;
    }

    /// <summary>
    /// 새로 생긴 창을 기다렸다가 최대화한다. 이미 떠 있던 창을 다시 쓴 경우에는 새 창이 없어 false.
    /// 다른 창을 건드리지 않으려고 <paramref name="before"/>에 없던 창만 대상으로 한다.
    /// </summary>
    public static bool MaximizeNew(string processName, HashSet<IntPtr> before, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
                var pids = ProcessIds(processName);
                if (pids.Count == 0) continue;
                var found = IntPtr.Zero;
                EnumWindows((h, _) =>
                {
                    if (before.Contains(h) || !IsAppWindow(h, pids)) return true;
                    found = h;
                    return false;
                }, IntPtr.Zero);
                if (found == IntPtr.Zero) continue;
                ShowWindow(found, SW_MAXIMIZE);
                return IsZoomed(found);
            }
        }
        catch { /* 최대화에 실패해도 창은 열려 있다 */ }
        return false;
    }

    private static bool IsAppWindow(IntPtr hwnd, HashSet<int> pids)
    {
        if (!IsWindowVisible(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (!pids.Contains(pid)) return false;
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString() == WindowClass;
    }

    private static HashSet<int> ProcessIds(string processName)
    {
        var pids = new HashSet<int>();
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
            {
                try { pids.Add(p.Id); }
                finally { p.Dispose(); }
            }
        }
        catch { /* 무시 */ }
        return pids;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);
}
