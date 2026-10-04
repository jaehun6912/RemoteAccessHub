using RemoteAccessHub.Core;

namespace RemoteAccessHub.Services;

/// <summary>크롬 원격 데스크톱을 어디로 열었는지.</summary>
public enum CrdOpenTarget
{
    /// <summary>설치된 앱으로, 원하는 주소까지 열었다.</summary>
    App,
    /// <summary>설치된 앱을 열었지만 주소를 넘기지 못해 앱 첫 화면(기기 목록)이 열렸다.</summary>
    AppHome,
    /// <summary>앱이 없어 기본 브라우저로 열었다.</summary>
    Browser,
}

public interface ICrdLauncher
{
    /// <summary>
    /// 크롬 원격 데스크톱을 연다. 이 PC에 앱이 설치되어 있으면 앱으로, 없으면 기본 브라우저로 연다.
    /// 기기 ID가 있으면 그 기기의 세션 주소를, 없으면 기기 목록 화면을 연다.
    /// 구글 로그인과 PIN 입력은 사용자가 직접 한다.
    /// </summary>
    CrdOpenTarget Open(string? hostId);
}

public sealed class CrdLauncher : ICrdLauncher
{
    /// <summary>기기 목록 화면.</summary>
    public const string AccessUrl = "https://remotedesktop.google.com/access";

    private readonly AppLog? _log;

    public CrdLauncher(AppLog? log = null) => _log = log;

    /// <summary>열 주소를 만든다. 기기 ID는 16진수와 '-'만 허용하므로 주소에 그대로 넣어도 안전하다.</summary>
    public static string BuildUrl(string? hostId)
    {
        var id = InputRules.NormalizeCrdHostId(hostId);
        return id == null ? AccessUrl : $"{AccessUrl}/session/{id}";
    }

    public CrdOpenTarget Open(string? hostId)
    {
        var url = BuildUrl(hostId);
        var where = url == AccessUrl ? "기기 목록" : "저장된 기기 세션";
        var app = CrdAppFinder.Find();

        if (app != null)
        {
            // 1) 설치된 앱이 있으면 앱 창으로 원하는 주소까지 연다.
            //    --app-id는 앱의 첫 화면(기기 목록)만 열고 주소를 무시하므로 --app=<주소>를 쓴다.
            var browser = FindBrowser();
            if (browser != null)
            {
                try
                {
                    _log?.Info($"크롬 원격 데스크톱 앱 열기: {where}");
                    // 앱 창은 저장해 둔 크기로 뜨고 명령줄 크기 지정을 따르지 않으므로, 뜬 뒤에 최대화한다.
                    var processName = BrowserWindows.ProcessNameFor(browser);
                    var before = BrowserWindows.Snapshot(processName);
                    using var _ = ProcessRunner.StartWindowed(browser, AppArgs(app, url));
                    MaximizeInBackground(processName, before);
                    return CrdOpenTarget.App;
                }
                catch (Exception ex)
                {
                    _log?.Warn("크롬 원격 데스크톱 앱 실행 실패, 다른 방법으로 엽니다: " + ex.Message);
                }
            }

            // 2) 앱은 있지만 주소를 넘길 수 없는 경우: 앱만 띄운다(앱 첫 화면 = 기기 목록).
            try
            {
                _log?.Info("크롬 원격 데스크톱 앱 열기: 앱 첫 화면(주소 전달 불가)");
                using var _ = ProcessRunner.StartWindowed(Explorer(), new[] { @"shell:AppsFolder\" + app.Aumid });
                return CrdOpenTarget.AppHome;
            }
            catch (Exception ex)
            {
                _log?.Warn("크롬 원격 데스크톱 앱 실행 실패, 브라우저로 엽니다: " + ex.Message);
            }
        }

        // 3) 앱이 없거나 실행하지 못한 경우: 기본 브라우저.
        // 셸 문자열로 조립하지 않고 explorer.exe에 인수로 넘겨 기본 브라우저가 열게 한다.
        _log?.Info($"크롬 원격 데스크톱 브라우저로 열기: {where}");
        using var __ = ProcessRunner.StartWindowed(Explorer(), new[] { url });
        return CrdOpenTarget.Browser;
    }

    /// <summary>새 앱 창이 뜨기를 기다렸다가 최대화한다(여는 흐름을 막지 않도록 따로 돌린다).</summary>
    private void MaximizeInBackground(string processName, HashSet<IntPtr> before) => Task.Run(() =>
    {
        try
        {
            if (!BrowserWindows.MaximizeNew(processName, before, TimeSpan.FromSeconds(10)))
                _log?.Debug("크롬 원격 데스크톱 앱 창을 최대화하지 못했습니다(이미 열려 있던 창을 다시 쓴 경우 포함).");
        }
        catch (Exception ex)
        {
            _log?.Debug("크롬 원격 데스크톱 앱 창 최대화 실패: " + ex.Message);
        }
    });

    /// <summary>
    /// 앱 창으로 주소를 열 때 브라우저에 넘길 인수.
    /// --app-id는 앱의 첫 화면만 열고 주소를 무시하므로 쓰지 않는다(2026-10-03 실기기 확인).
    /// </summary>
    internal static IReadOnlyList<string> AppArgs(CrdInstalledApp app, string url)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(app.ProfileDirectory)) args.Add("--profile-directory=" + app.ProfileDirectory);
        args.Add("--app=" + url);
        return args;
    }

    private static string Explorer() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>앱을 띄울 수 있는 브라우저 실행 파일(Edge 우선, 없으면 Chrome). 없으면 null.</summary>
    internal static string? FindBrowser()
    {
        foreach (var exe in new[] { "msedge.exe", "chrome.exe" })
        {
            var dir = AppPathDirectory(exe);
            // *_proxy.exe는 앱 창을 띄우고 바로 끝나는 실행 파일이라 작업 표시줄이 깔끔하다.
            foreach (var candidate in new[] { exe.Replace(".exe", "_proxy.exe"), exe })
            {
                if (dir != null)
                {
                    var p = Path.Combine(dir, candidate);
                    if (File.Exists(p)) return p;
                }
            }
        }
        return null;
    }

    /// <summary>App Paths 레지스트리에서 브라우저 설치 폴더를 읽는다(읽기 전용).</summary>
    private static string? AppPathDirectory(string exe)
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var k = root.OpenSubKey(key + exe);
                var path = k?.GetValue("") as string;
                if (string.IsNullOrWhiteSpace(path)) continue;
                var dir = Path.GetDirectoryName(path.Trim('"'));
                if (dir != null && Directory.Exists(dir)) return dir;
            }
            catch { /* 계속 */ }
        }
        return null;
    }
}
