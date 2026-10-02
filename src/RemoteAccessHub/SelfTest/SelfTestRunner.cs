using System.Text;
using RemoteAccessHub.Core;
using RemoteAccessHub.Router;
using RemoteAccessHub.Services;
using RemoteAccessHub.UI;

namespace RemoteAccessHub.SelfTest;

public sealed record TestResult(string Name, bool Pass, string Detail);

/// <summary>
/// `RemoteAccessHub.exe --selftest [결과파일]`.
/// 모의 공유기 + 실제 WebView2 + 실제 MainForm/RouterBrowser/RouterNavigator/WolAutomation 코드 경로로 회귀 검사를 수행한다.
/// 실제 공유기·VPN·RDP에는 접속하지 않는다(VPN/포트/RDP는 가짜 구현).
/// </summary>
public static class SelfTestRunner
{
    public static int Run(LaunchOptions options, AppLog log)
    {
        var results = new List<TestResult>();
        var vpn = new FakeVpnService();
        var port = new FakePortProbe();
        var rdp = new FakeRdpLauncher();
        var crd = new FakeCrdLauncher();
        var form = new MainForm(options, log, vpn, port, rdp, crd);
        form.Shown += async (_, _) =>
        {
            await Task.Yield();
            try
            {
                await new SelfTestDriver(form, log, results, options, vpn, port, rdp, crd).RunAsync();
            }
            catch (Exception ex)
            {
                results.Add(new("driver", false, ex.ToString()));
            }
            finally
            {
                try { form.Close(); } catch { /* ignore */ }
            }
        };
        Application.Run(form);

        var outPath = options.OutputPath ?? Path.Combine(Environment.CurrentDirectory, "selftest-results.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"RemoteAccessHub {AppInfo.Version} 자체검사 결과 — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"환경: {Environment.OSVersion.VersionString}, WebView2 모의 공유기, 가짜 VPN/포트/RDP");
        sb.AppendLine();
        foreach (var r in results)
            sb.AppendLine($"{(r.Pass ? "PASS" : "FAIL")}  {r.Name}  —  {r.Detail}");
        sb.AppendLine();
        var pass = results.Count(r => r.Pass);
        sb.AppendLine($"합계: {pass}/{results.Count} 통과");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            log.Error("결과 파일 저장 실패: " + ex.Message);
        }
        return results.Count > 0 && results.All(r => r.Pass) ? 0 : 1;
    }
}

internal sealed class SelfTestDriver
{
    private readonly MainForm _form;
    private readonly AppLog _log;
    private readonly List<TestResult> _results;
    private readonly LaunchOptions _options;
    private readonly FakeVpnService _vpn;
    private readonly FakePortProbe _port;
    private readonly FakeRdpLauncher _rdp;
    private readonly FakeCrdLauncher _crd;
    private readonly CancellationToken _ct = CancellationToken.None;

    private const string TargetName = MockRouterServer.DefaultTargetName;
    private const string MacA = "00:11:22:33:44:01";
    private const string MacB = "00:11:22:33:44:02";
    private const string MacC = "00:11:22:33:44:03";

    public SelfTestDriver(MainForm form, AppLog log, List<TestResult> results, LaunchOptions options, FakeVpnService vpn, FakePortProbe port, FakeRdpLauncher rdp, FakeCrdLauncher crd)
    {
        _form = form;
        _log = log;
        _results = results;
        _options = options;
        _vpn = vpn;
        _port = port;
        _rdp = rdp;
        _crd = crd;
    }

    private void Check(string name, bool pass, string detail)
    {
        _results.Add(new(name, pass, detail));
        _log.Info($"[검사] {(pass ? "PASS" : "FAIL")} {name} — {detail}");
    }

    private async Task<string> Js(string script) => await _form.Browser.EvaluateAsync(script, _ct) ?? "";

    private async Task<string> MockState() => await Js("window.__mock ? window.__mock.getState() : ''");

    private async Task<bool> WaitUntilAsync(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            await Task.Delay(200);
        }
        return cond();
    }

    private async Task<bool> WaitSessionAsync(SessionState expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_form.Browser.Session.State == expected) return true;
            if (!_form.IsBusy) await _form.ProbeSessionNowAsync();
            await Task.Delay(400);
        }
        return _form.Browser.Session.State == expected;
    }

    private async Task<bool> WaitViewAsync(string view, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            string st;
            try { st = await MockState(); } catch { st = ""; }
            if (st.Contains("\"view\":\"" + view + "\"", StringComparison.Ordinal)) return true;
            await Task.Delay(250);
        }
        return false;
    }

    private async Task<RouterPageKind> KindAsync() => (await _form.Wol.Navigator.ClassifyAsync(_ct)).Kind;

    private async Task<bool> WaitKindAsync(RouterPageKind kind, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await KindAsync() == kind) return true;
            await Task.Delay(300);
        }
        return false;
    }

    /// <summary>모의 로그인(사용자가 직접 입력하는 것을 대신). 실제 공유기에서는 프로그램이 절대 입력하지 않는다.</summary>
    private async Task<bool> LoginAsync()
    {
        await WaitViewAsync("login", TimeSpan.FromSeconds(10));
        await Js("window.__mock.login('admin','x','1234')");
        return await WaitSessionAsync(SessionState.LoggedIn, TimeSpan.FromSeconds(15));
    }

    private async Task LogoutViaApiAsync()
    {
        await Js("window.__mock.logout()");
        await WaitSessionAsync(SessionState.LoggedOut, TimeSpan.FromSeconds(10));
    }

    private async Task SetMock(string json) => await Js("window.__mock.setOptions(" + json + ")");

    private async Task ReloadRouterAsync()
    {
        await _form.NavigateToRouterAsync(expand: true);
        await Task.Delay(800);
    }

    private async Task GoAdminAsync()
    {
        await Js("window.__mock.goAdmin()");
        await WaitKindAsync(RouterPageKind.AdminMain, TimeSpan.FromSeconds(8));
    }

    private async Task ScreenshotAsync(string name)
    {
        if (string.IsNullOrEmpty(_options.ScreenshotDirectory)) return;
        try
        {
            Directory.CreateDirectory(_options.ScreenshotDirectory);
            using var bmp = new Bitmap(_form.Width, _form.Height);
            _form.DrawToBitmap(bmp, new Rectangle(0, 0, _form.Width, _form.Height));
            bmp.Save(Path.Combine(_options.ScreenshotDirectory, name + "-form.png"), System.Drawing.Imaging.ImageFormat.Png);
            var web = await _form.Browser.CaptureScreenshotAsync();
            if (web != null) await File.WriteAllBytesAsync(Path.Combine(_options.ScreenshotDirectory, name + "-web.png"), web);
        }
        catch (Exception ex)
        {
            _log.Warn("스크린샷 저장 실패: " + ex.Message);
        }
    }

    public async Task RunAsync()
    {
        var init = await _form.Initialized;
        Check("브라우저 초기화", init, init ? "WebView2 + 모의 공유기 준비됨" : "초기화 실패: " + _form.Browser.LastInitError);
        if (!init) return;
        var mock = _form.Mock!;
        var s = _form.Settings;

        // ================= A. 로그인 전 =================
        var loggedOut = await WaitSessionAsync(SessionState.LoggedOut, TimeSpan.FromSeconds(20));
        Check("로그인 전 상태 확정(LoggedOut)", loggedOut, "세션 상태: " + _form.Browser.Session.Describe());
        Check("로그인 전 PC 켜기 비활성화", !_form.WakeEnabled, "WakeEnabled=" + _form.WakeEnabled);
        Check("UI: 로그인 전 [PC 켜고 접속] 비활성화·단계 표시 '로그인'", !_form.WakeConnectEnabled && _form.ConnectEnabled && _form.Flow.Login.State == UI.StepState.Active && _form.RouterPillText.Contains("로그인 필요"),
            $"wakeConnect={_form.WakeConnectEnabled} connect={_form.ConnectEnabled} login={_form.Flow.Login} pill='{_form.RouterPillText}'");
        Check("로그인 전 화면 펼침", _form.IsBrowserExpanded, "expanded=" + _form.IsBrowserExpanded);
        await _form.Browser.EnsureSemanticsAsync(TimeSpan.FromSeconds(5), _ct);
        var k0 = await KindAsync();
        Check("로그인 화면 판정", k0 == RouterPageKind.Login, "page=" + k0);
        await ScreenshotAsync("01-login");

        // ================= B. 로그인 직후 선택 화면 =================
        s.AutoSelectAdminTool = false;
        var loggedIn = await LoginAsync();
        Check("로그인 성공 감지", loggedIn && await WaitUntilAsync(() => _form.Browser.Session.IsLoggedIn, TimeSpan.FromSeconds(3)), _form.Browser.Session.Describe());
        var waitingUser = await WaitUntilAsync(() => _form.StatusText.Contains("관리도구", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
        await Task.Delay(1500);
        var kSel = await KindAsync();
        Check("선택 화면에서는 공유기 화면을 접지 않음", waitingUser && _form.IsBrowserExpanded && kSel == RouterPageKind.ModeSelect,
            $"page={kSel} expanded={_form.IsBrowserExpanded} status='{_form.StatusText}'");
        var popupWhileSelect = _form.ShowModePopup(wakeFirst: false);
        popupWhileSelect?.Close();
        Check("UI: 관리 화면 준비 전([관리도구] 선택 대기)에는 [PC 켜고 접속]·[PC 켜기]·[PC 접속] 비활성",
            !_form.WakeEnabled && !_form.WakeConnectEnabled && !_form.ConnectEnabled && popupWhileSelect == null && _form.RouterPillText.Contains("관리도구 선택 필요"),
            $"wake={_form.WakeEnabled} wakeConnect={_form.WakeConnectEnabled} connect={_form.ConnectEnabled} popup={popupWhileSelect != null} pill='{_form.RouterPillText}'");
        await ScreenshotAsync("02-mode-select");
        await Js("window.__mock.chooseAdmin()");
        var collapsedAfterUser = await WaitUntilAsync(() => !_form.IsBrowserExpanded, TimeSpan.FromSeconds(10));
        Check("사용자가 [관리도구]를 누르면 관리 화면 확인 후 접기", collapsedAfterUser && _form.LastSettle?.Status == NavStatus.Ok,
            $"expanded={_form.IsBrowserExpanded} settle={_form.LastSettle?.Status}/{_form.LastSettle?.Method}");
        Check("UI: 관리 화면이 준비되면 세 버튼 활성", _form.WakeEnabled && _form.WakeConnectEnabled && _form.ConnectEnabled && !_form.IsPreparingAdmin && _form.Browser.Session.IsLoggedIn,
            $"wake={_form.WakeEnabled} wakeConnect={_form.WakeConnectEnabled} connect={_form.ConnectEnabled} preparing={_form.IsPreparingAdmin}");

        // 자동 선택(접근성 버튼)
        s.AutoSelectAdminTool = true;
        await LogoutViaApiAsync();
        await ReloadRouterAsync();
        var mockLogoutsBase = mock.LogoutCount;
        await LoginAsync();
        var autoOk = await WaitUntilAsync(() => _form.LastSettle != null, TimeSpan.FromSeconds(45)) && await WaitUntilAsync(() => !_form.IsBrowserExpanded, TimeSpan.FromSeconds(3)) && _form.LastSettle?.Status == NavStatus.Ok;
        var stAuto = await MockState();
        Check("선택 화면에서 [관리도구] 자동 선택(접근성 버튼)", autoOk && _form.LastSettle?.Method == "semantics" && stAuto.Contains("\"view\":\"admin\"") && stAuto.Contains("sem:admin"),
            $"settle={_form.LastSettle?.Status}/{_form.LastSettle?.Method} expanded={_form.IsBrowserExpanded} state={Short(stAuto)}");
        Check("UI: 로그인 후 화면 준비는 'PC 켜기' 단계를 바꾸지 않음", _form.Flow.Login.State == UI.StepState.Done && _form.Flow.Wake.State == UI.StepState.Pending && _form.WakeConnectEnabled && _form.RouterPillText.Contains("로그인됨"),
            $"login={_form.Flow.Login} wake={_form.Flow.Wake} wakeConnect={_form.WakeConnectEnabled} pill='{_form.RouterPillText}'");
        await ScreenshotAsync("02b-ready-collapsed");

        // 접근성 click()에 반응하지 않는 카드 → 화면 텍스트 위치 클릭
        await LogoutViaApiAsync();
        await SetMock("{modeSelectNoop:true}");
        await ReloadRouterAsync();
        await LoginAsync();
        var fbOk = await WaitUntilAsync(() => _form.LastSettle != null, TimeSpan.FromSeconds(45)) && _form.LastSettle?.Status == NavStatus.Ok && _form.LastSettle?.Method == "paragraph";
        var stFb = await MockState();
        Check("[관리도구] 접근성 클릭 무반응 → 텍스트 위치 클릭으로 선택", fbOk && stFb.Contains("ptr:admin"),
            $"settle={_form.LastSettle?.Status}/{_form.LastSettle?.Method} state={Short(stFb)}");
        await SetMock("{modeSelectNoop:false}");

        // 카드가 접근성 버튼으로 노출되지 않는 경우 → 텍스트만으로 선택
        await LogoutViaApiAsync();
        await SetMock("{modeSelectButtons:false}");
        await ReloadRouterAsync();
        await LoginAsync();
        var txtOk = await WaitUntilAsync(() => _form.LastSettle != null, TimeSpan.FromSeconds(45)) && _form.LastSettle?.Status == NavStatus.Ok;
        Check("[관리도구]가 텍스트로만 보일 때 선택", txtOk && _form.LastSettle?.Method == "paragraph", $"settle={_form.LastSettle?.Status}/{_form.LastSettle?.Method}");
        await SetMock("{modeSelectButtons:true}");
        Check("선택 화면 처리 중 로그아웃 클릭 없음", mock.LogoutCount == mockLogoutsBase + 2, $"logout calls={mock.LogoutCount - mockLogoutsBase} (검사용 로그아웃 2회만)");

        // 공유기 앱이 준비되기 전이라 처음 두 번의 누름이 무시되는 경우 → 다음 회차에서 선택
        await LogoutViaApiAsync();
        await SetMock("{modeSelectIgnoreTaps:2}");
        await ReloadRouterAsync();
        await LoginAsync();
        // 로그인이 확인되면 [관리도구]로 넘어가기 전에 곧바로 접혀야 한다(공유기 화면 영역 0).
        var collapsedEarly = await WaitUntilAsync(() => !_form.IsBrowserExpanded, TimeSpan.FromSeconds(3));
        var areaEarly = _form.BrowserAreaHeight;
        var stEarly = await MockState();
        Check("로그인 확인 즉시 공유기 화면 접힘([관리도구] 선택 전, 화면 영역 0)", collapsedEarly && areaEarly == 0 && stEarly.Contains("\"view\":\"modeSelect\""),
            $"expanded={_form.IsBrowserExpanded} area={areaEarly} state={Short(stEarly)}");
        Check("UI: [관리도구] 자동 선택 중에는 세 버튼 비활성·배지 '준비 중'·1단계 '관리 화면 준비 중'",
            !_form.WakeEnabled && !_form.WakeConnectEnabled && !_form.ConnectEnabled && _form.IsPreparingAdmin
            && _form.RouterPillText.Contains("준비 중") && _form.Flow.Login.State == UI.StepState.Active && _form.Flow.Login.Detail == "관리 화면 준비 중",
            $"wake={_form.WakeEnabled} wakeConnect={_form.WakeConnectEnabled} connect={_form.ConnectEnabled} pill='{_form.RouterPillText}' login={_form.Flow.Login}");
        var retryOk = await WaitUntilAsync(() => _form.LastSettle != null, TimeSpan.FromSeconds(60)) && _form.LastSettle?.Status == NavStatus.Ok;
        Check("UI: '관리 화면이 준비됐습니다'와 함께 세 버튼 활성·1단계 완료",
            retryOk && _form.StatusText.Contains("준비됐습니다") && _form.WakeEnabled && _form.WakeConnectEnabled && _form.ConnectEnabled && _form.Flow.Login.State == UI.StepState.Done,
            $"status='{_form.StatusText}' wake={_form.WakeEnabled} wakeConnect={_form.WakeConnectEnabled} connect={_form.ConnectEnabled} login={_form.Flow.Login}");
        var stRetry = await MockState();
        var ignored = System.Text.RegularExpressions.Regex.Matches(stRetry, "ignored:admin").Count;
        Check("[관리도구] 처음 누름이 무시돼도 다시 시도해 선택", retryOk && ignored == 2 && stRetry.Contains("\"view\":\"admin\""),
            $"settle={_form.LastSettle?.Status}/{_form.LastSettle?.Method} ignored={ignored} state={Short(stRetry)}");
        await SetMock("{modeSelectIgnoreTaps:0}");

        // ================= C. 접힌 상태 유지 =================
        _form.SetBrowserExpanded(false);
        for (var i = 0; i < 3; i++) { await _form.ProbeSessionNowAsync(); await Task.Delay(300); }
        Check("접힌 상태에서 세션 확인 반복 후 유지", _form.Browser.Session.IsLoggedIn && _form.WakeEnabled && !_form.IsBrowserExpanded,
            $"state={_form.Browser.Session.State} wake={_form.WakeEnabled} expanded={_form.IsBrowserExpanded}");
        // Flutter는 화면 갱신(requestAnimationFrame)으로 버튼·접근성 트리를 그리므로, 영역이 0이어도 페이지가 멈추지 않아야 한다.
        var frame = await Js("new Promise(function(r){var done=false;var t=setTimeout(function(){if(!done){done=true;r(document.visibilityState+':timeout');}},2000);" +
                             "requestAnimationFrame(function(){requestAnimationFrame(function(){if(!done){done=true;clearTimeout(t);r(document.visibilityState+':raf');}});});})");
        Check("접힌 상태(화면 영역 0)에서도 페이지 표시·화면 갱신 계속", _form.BrowserAreaHeight == 0 && frame == "visible:raf",
            $"area={_form.BrowserAreaHeight} page={frame}");
        var hLocked = _form.Height;
        _form.Height = hLocked + 200;
        await Task.Delay(200);
        Check("접힌 상태에서는 창을 늘려도 공유기 화면이 드러나지 않음", _form.Height == hLocked && _form.BrowserAreaHeight == 0,
            $"height {hLocked}→{_form.Height} area={_form.BrowserAreaHeight}");
        await SetMock("{hideSemantics:true}");
        await Task.Delay(300);
        var snapHidden = await _form.Browser.ProbeAsync(_ct);
        await _form.ProbeSessionNowAsync();
        Check("화면 판독 불가 시 로그아웃 취급 안 함", snapHidden.Nodes.Count == 0 && snapHidden.Paragraphs.Count == 0 && _form.Browser.Session.IsLoggedIn && _form.WakeEnabled,
            $"nodes={snapHidden.Nodes.Count} paras={snapHidden.Paragraphs.Count} state={_form.Browser.Session.State} wake={_form.WakeEnabled}");
        await SetMock("{hideSemantics:false}");
        _form.Browser.Session.Apply(SessionProbeResult.Unavailable, "모의: 판독 불가");
        Check("Unavailable 결과가 상태를 바꾸지 않음", _form.Browser.Session.IsLoggedIn, _form.Browser.Session.Describe());

        // ================= D. 세션 만료 =================
        mock.ExpireSessions();
        mock.ClearSignals();
        var oExp = await _form.RunWakeAsync(skipNavigation: false);
        Check("세션 만료 시 PC 켜기 거부(클릭 없음)", !oExp.Success && oExp.LastStep == WolStep.SessionCheck && mock.Signals.Count == 0,
            $"step={oExp.LastStep} signals={mock.Signals.Count} msg={oExp.Message}");
        Check("세션 만료 후 버튼 비활성화·화면 펼침", !_form.WakeEnabled && _form.IsBrowserExpanded && _form.Browser.Session.State == SessionState.LoggedOut,
            $"wake={_form.WakeEnabled} expanded={_form.IsBrowserExpanded} state={_form.Browser.Session.State}");
        await ReloadRouterAsync();
        var re = await LoginAsync();
        await WaitUntilAsync(() => _form.LastSettle?.Status == NavStatus.Ok, TimeSpan.FromSeconds(20));
        Check("재로그인 후 관리 화면 준비", re && await KindAsync() == RouterPageKind.AdminMain, $"{_form.Browser.Session.Describe()} page={await KindAsync()}");

        // ================= E. WOL 화면 이동 =================
        s.WolPcName = TargetName;
        s.WolPcMac = "";
        s.AutoConfirmWakeDialog = true;
        var nav = _form.Wol.Navigator;

        var n1 = await nav.NavigateToWolAsync(_ct);
        Check("관리 화면 → 앱 내부 경로 이동으로 WOL 화면", n1.Status == NavStatus.Ok && n1.Method == "경로" && await KindAsync() == RouterPageKind.WolList,
            $"{n1.Status}/{n1.Method} {n1.Message}");

        await GoAdminAsync();
        await SetMock("{ignorePopState:true, menuExpanded:false}");
        var logoutsBeforeMenu = mock.LogoutCount;
        var n2 = await nav.NavigateToWolAsync(_ct);
        Check("경로 이동 무반응 → 메뉴 그룹 펼친 뒤 메뉴 클릭", n2.Status == NavStatus.Ok && n2.Method == "메뉴" && await KindAsync() == RouterPageKind.WolList,
            $"{n2.Status}/{n2.Method} {n2.Message}");
        await ScreenshotAsync("03-wol-via-menu");

        await GoAdminAsync();
        await SetMock("{ignorePopState:true, menuExpanded:false, wolUnderLogout:true}");
        var n3 = await nav.NavigateToWolAsync(_ct);
        var st3 = await MockState();
        Check("로그아웃 줄에 겹친 메뉴 텍스트는 누르지 않음", n3.Status == NavStatus.Ok && mock.LogoutCount == logoutsBeforeMenu && _form.Browser.Session.IsLoggedIn && !st3.Contains("ptr:menu-logout"),
            $"{n3.Status}/{n3.Method} logout={mock.LogoutCount - logoutsBeforeMenu} state={Short(st3)}");
        await SetMock("{wolUnderLogout:false}");

        await GoAdminAsync();
        var savedMenu = s.WolMenuLabel;
        var savedGroup = s.WolMenuGroupLabel;
        s.WolMenuLabel = "없는 메뉴";
        s.WolMenuGroupLabel = "없는 그룹";
        var n4 = await nav.NavigateToWolAsync(_ct);
        Check("경로·메뉴 실패 → 주소 직접 열기로 WOL 화면", n4.Status == NavStatus.Ok && n4.Method == "주소" && await KindAsync() == RouterPageKind.WolList,
            $"{n4.Status}/{n4.Method} {n4.Message}");

        await GoAdminAsync();
        await SetMock("{reloadShowsModeSelect:true}");
        var n5 = await nav.NavigateToWolAsync(_ct);
        var st5 = await MockState();
        Check("모든 이동 실패 시 안내만 하고 엉뚱한 클릭 없음", n5.Status == NavStatus.Failed && mock.LogoutCount == logoutsBeforeMenu && _form.Browser.Session.IsLoggedIn,
            $"{n5.Status}/{n5.Method} logout={mock.LogoutCount - logoutsBeforeMenu} msg={n5.Message} state={Short(st5)}");
        s.WolMenuLabel = savedMenu;
        s.WolMenuGroupLabel = savedGroup;
        await SetMock("{ignorePopState:false, reloadShowsModeSelect:false, menuExpanded:false}");
        await Js("window.__mock.chooseAdmin()");
        await WaitKindAsync(RouterPageKind.AdminMain, TimeSpan.FromSeconds(8));

        // ================= F. 대상 매칭 =================
        mock.PcList = new() { new("OTHER-PC-1", MacA), new("OTHER-PC-2", MacC) };
        mock.ClearSignals();
        var o5 = await _form.RunWakeAsync(false);
        Check("대상 PC 없음 → 클릭 안 함", !o5.Success && o5.LastStep == WolStep.Match && o5.Match?.Status == WolMatchStatus.TargetNotFound && mock.Signals.Count == 0,
            $"step={o5.LastStep} status={o5.Match?.Status} signals={mock.Signals.Count} msg={o5.Message}");

        mock.PcList = new() { new(TargetName, MacA), new("OTHER-PC-2", MacC), new(TargetName, MacB) };
        mock.ClearSignals();
        var o6 = await _form.RunWakeAsync(false);
        Check("같은 이름 2개(MAC 미설정) → 거부", !o6.Success && o6.Match?.Status == WolMatchStatus.Ambiguous && mock.Signals.Count == 0,
            $"status={o6.Match?.Status} signals={mock.Signals.Count} msg={o6.Message}");

        s.WolPcMac = MacB;
        mock.ClearSignals();
        var o7 = await _form.RunWakeAsync(false);
        Check("같은 이름 2개 + MAC 설정 → 올바른 행 클릭", o7.Success && mock.Signals.Count == 1 && mock.Signals[0] == MacB,
            $"success={o7.Success} strategy={o7.Match?.Target?.Strategy} signals=[{Masked(mock.Signals)}] msg={o7.Message}");
        s.WolPcMac = "";

        mock.PcList = new() { new("OTHER-PC-1", MacA), new(TargetName, MacB), new("OTHER-PC-2", MacC) };
        mock.ClearSignals();
        var o8 = await _form.RunWakeAsync(false);
        Check("정상 WOL(실기기 구조: 행 안의 버튼) → 구조 매칭으로 대상만 클릭", o8.Success && mock.Signals.Count == 1 && mock.Signals[0] == MacB && o8.Match?.Target?.Strategy == "구조",
            $"success={o8.Success} strategy={o8.Match?.Target?.Strategy} signals=[{Masked(mock.Signals)}] msg={o8.Message}");
        Check("단계 표시: 클릭 완료·공유기 처리 완료", _form.WolClickText.Contains("완료") && _form.WolRouterText.Contains("완료") && o8.RouterStatus == StageStatus.Done,
            $"click='{_form.WolClickText}' router='{_form.WolRouterText}' boot='{_form.WolBootText}'");

        await SetMock("{nestedRows:false}");
        mock.ClearSignals();
        var o8b = await _form.RunWakeAsync(false);
        Check("평탄한 구조(버튼이 행과 분리) → 좌표 매칭으로 대상만 클릭", o8b.Success && mock.Signals.Count == 1 && mock.Signals[0] == MacB && o8b.Match?.Target?.Strategy == "좌표",
            $"success={o8b.Success} strategy={o8b.Match?.Target?.Strategy} signals=[{Masked(mock.Signals)}]");
        await SetMock("{nestedRows:true}");

        s.WolPcMac = "AA:BB:CC:DD:EE:FF";
        mock.ClearSignals();
        var o9m = await _form.RunWakeAsync(false);
        s.WolPcMac = "";
        Check("MAC 잘못 설정 시 거부", !o9m.Success && o9m.Match?.Status == WolMatchStatus.MacMismatch && mock.Signals.Count == 0, $"status={o9m.Match?.Status} signals={mock.Signals.Count}");

        // ================= G. 확인창 =================
        s.AutoConfirmWakeDialog = false;
        mock.ClearSignals();
        _form.SetBrowserExpanded(false);
        var wakeTask = _form.RunWakeAsync(false);
        var expandedByDialog = await WaitUntilAsync(() => _form.IsBrowserExpanded && _form.StatusText.Contains("확인창"), TimeSpan.FromSeconds(25));
        Check("확인창 감지 시 화면 자동 펼침", expandedByDialog, $"expanded={_form.IsBrowserExpanded} status='{_form.StatusText}'");
        await ScreenshotAsync("04-dialog-expanded");
        await Js("window.__mock.confirmDialog()");
        var o9 = await wakeTask;
        Check("사용자 확인 후 WOL 요청 처리", o9.Success && mock.Signals.Count == 1 && mock.Signals[0] == MacB, $"success={o9.Success} signals={mock.Signals.Count} msg={o9.Message}");

        // 확인창 자동 확인(실기기 모양: [취소] [확인] 나란히). 화면을 접은 채로 끝나야 한다.
        s.AutoConfirmWakeDialog = true;
        await ConfirmScenarioAsync(mock, "{dialogButtonRole:true, dialogSemClickNoop:false}", "확인창 자동 확인(접근성 버튼) — 화면 접힌 채 유지", "sem:dok");
        await ConfirmScenarioAsync(mock, "{dialogButtonRole:false, dialogSemClickNoop:false}", "확인창 자동 확인(역할 없는 노드 → 위치 클릭)", "ptr:dok");
        await ConfirmScenarioAsync(mock, "{dialogButtonRole:true, dialogSemClickNoop:true}", "확인창 자동 확인(접근성 클릭 무반응 → 텍스트 위치)", "ptr:dok");
        await SetMock("{dialogButtonRole:true, dialogSemClickNoop:false, dialogOkLabel:'진행'}");
        mock.ClearSignals();
        _form.SetBrowserExpanded(false);
        var cBefore = await CountsAsync();
        var attentionBefore = _form.AttentionCount;
        var manualTask = _form.RunWakeAsync(false);
        var expandedFallback = await WaitUntilAsync(() => _form.IsBrowserExpanded && _form.StatusText.Contains("자동으로 누르지 못했습니다"), TimeSpan.FromSeconds(30));
        var cMid = await CountsAsync();
        Check("[확인]이 없으면 화면을 펼쳐 사용자에게 맡김([취소] 안 누름)", expandedFallback && cMid.Cancel == cBefore.Cancel && cMid.Confirm == cBefore.Confirm && mock.Signals.Count == 0,
            $"expanded={_form.IsBrowserExpanded} cancel+{cMid.Cancel - cBefore.Cancel} signals={mock.Signals.Count} status='{_form.StatusText}'");
        await Task.Delay(300);
        Check("UI: 사용자 조작 필요 → 주의 알림·경고 단계·경고 안내", _form.AttentionCount > attentionBefore && _form.Flow.Wake.State == UI.StepState.Warning && _form.StatusKind == UI.Controls.BannerKind.Warning,
            $"attention+{_form.AttentionCount - attentionBefore} wake={_form.Flow.Wake} banner={_form.StatusKind}");
        await ScreenshotAsync("04b-confirm-needed");
        await Js("window.__mock.confirmDialog()");
        var oManual = await manualTask;
        Check("펼친 뒤 사용자 확인으로 WOL 처리", oManual.Success && mock.Signals.Count == 1, $"success={oManual.Success} signals={mock.Signals.Count}");
        await SetMock("{dialogOkLabel:'확인'}");

        s.AutoConfirmWakeDialog = true;
        await Js("window.__mock.resetConfirmPath()");
        await SetMock("{dialog:'window'}");
        mock.ClearSignals();
        var before = _form.ScriptDialogCount;
        var o10 = await _form.RunWakeAsync(false);
        Check("window.confirm 대화상자를 프로그램이 처리", o10.Success && _form.ScriptDialogCount == before + 1 && mock.Signals.Count == 1,
            $"success={o10.Success} dialogs={_form.ScriptDialogCount - before} signals={mock.Signals.Count}");
        await SetMock("{dialog:'canvas'}");

        mock.SignalShouldFail = true;
        mock.ClearSignals();
        var o11 = await _form.RunWakeAsync(false);
        Check("공유기 오류 응답 → 실패로 보고", !o11.Success && o11.RouterStatus == StageStatus.Failed && o11.ClickDone,
            $"success={o11.Success} router={o11.RouterStatus} click={o11.ClickDone} msg={o11.Message}");
        mock.SignalShouldFail = false;

        // ================= G2. 요청이 공유기 화면에서 중간에 취소됨(net::ERR_ABORTED) =================
        // 2026-09-15 사용자 PC 기록: 목록 요청(wol/show)이 끊긴 채 [PC 켜기]→[확인]을 누르면 wol/signal이 0.1초 안에 끊겼다.
        async Task<(int Aborts, int Ok)> SignalCounts()
        {
            using var d = System.Text.Json.JsonDocument.Parse(await MockState());
            return (d.RootElement.GetProperty("signalAborts").GetInt32(), d.RootElement.GetProperty("signalOk").GetInt32());
        }

        // 1) 목록 요청이 끊긴 WOL 화면: 누르기 전에 목록을 다시 불러와 요청이 끊기지 않게 한다
        await Js("window.__mock.goAdmin()");
        await Task.Delay(500);
        await SetMock("{staleListAborts:1}");
        mock.ClearSignals();
        var c0 = await SignalCounts();
        var t0 = DateTimeOffset.Now;
        var oStale = await _form.RunWakeAsync(false);
        var c1 = await SignalCounts();
        var shows = _form.Browser.Network.Since(t0, "wol/show");
        Check("목록 요청이 끊긴 WOL 화면 → 목록을 다시 불러온 뒤 눌러 WOL 요청이 끊기지 않음",
            oStale.Success && c1.Aborts == c0.Aborts && c1.Ok == c0.Ok + 1 && mock.Signals.Count == 1
            && shows.Any(WolAutomation.IsAborted) && shows.Any(c => c.Completed && !c.Failed),
            $"success={oStale.Success} aborts+{c1.Aborts - c0.Aborts} ok+{c1.Ok - c0.Ok} signals={mock.Signals.Count} wol/show=[{string.Join(",", shows.Select(c => c.Failed ? "끊김" : c.Status?.ToString() ?? "진행"))}] msg={oStale.Message}");

        // 2) 목록은 정상인데 WOL 요청이 한 번 끊김 → 목록을 새로 불러와 한 번 더 보내 성공
        await SetMock("{abortSignals:1}");
        mock.ClearSignals();
        c0 = await SignalCounts();
        t0 = DateTimeOffset.Now;
        var oRetry = await _form.RunWakeAsync(false);
        c1 = await SignalCounts();
        var signals = _form.Browser.Network.Since(t0, "wol/signal");
        Check("WOL 요청이 한 번 끊김 → 한 번 더 보내 공유기 처리 확인",
            oRetry.Success && oRetry.RouterStatus == StageStatus.Done && c1.Aborts == c0.Aborts + 1 && c1.Ok == c0.Ok + 1
            && mock.Signals.Count == 1 && signals.Count == 2 && _form.Flow.Wake.State == UI.StepState.Done,
            $"success={oRetry.Success} aborts+{c1.Aborts - c0.Aborts} ok+{c1.Ok - c0.Ok} observed={signals.Count} wake={_form.Flow.Wake} msg={oRetry.Message}");

        // 3) 최대 횟수 모두 끊김 → 더는 보내지 않고 "처리 여부 확인 불가"로 알림(네트워크 오류로 단정하지 않음)
        await SetMock("{abortSignals:" + (WolAutomation.MaxWakeAttempts + 3) + "}");
        mock.ClearSignals();
        c0 = await SignalCounts();
        var oTwice = await _form.RunWakeAsync(false);
        c1 = await SignalCounts();
        await SetMock("{abortSignals:0}");
        Check($"WOL 요청이 {WolAutomation.MaxWakeAttempts}번 모두 끊김 → 더 보내지 않고 '처리 여부 확인 불가'(주의)로 알림",
            !oTwice.Success && oTwice.RequestAborted && oTwice.RouterStatus == StageStatus.Unknown && c1.Aborts == c0.Aborts + WolAutomation.MaxWakeAttempts && c1.Ok == c0.Ok
            && oTwice.Message.Contains($"{WolAutomation.MaxWakeAttempts}번 모두", StringComparison.Ordinal) && !_form.StatusText.Contains("네트워크 오류", StringComparison.Ordinal)
            && _form.StatusKind == UI.Controls.BannerKind.Warning && _form.Flow.Wake.Detail == "요청 취소됨",
            $"success={oTwice.Success} aborted={oTwice.RequestAborted} aborts+{c1.Aborts - c0.Aborts} ok+{c1.Ok - c0.Ok} banner={_form.StatusKind} wake={_form.Flow.Wake} status='{_form.StatusText}'");

        // 4) 공유기가 처리하고 응답(200)까지 보냈는데 공유기 화면이 완료 전에 연결을 닫음(실제 공유기 앱 코드의 finally → abort())
        //    → 브라우저 기록은 "끊김"이지만 응답을 받았으므로 다시 보내지 않고 공유기 처리로 판정
        await SetMock("{closeEarlySignals:1}");
        mock.ClearSignals();
        c0 = await SignalCounts();
        t0 = DateTimeOffset.Now;
        var oEarly = await _form.RunWakeAsync(false);
        c1 = await SignalCounts();
        signals = _form.Browser.Network.Since(t0, "wol/signal");
        await SetMock("{closeEarlySignals:0}");
        Check("응답(200)을 받은 뒤 끊김으로 기록된 WOL 요청 → 다시 보내지 않고 공유기 처리로 판정",
            oEarly.Success && oEarly.RouterStatus == StageStatus.Done && !oEarly.RequestAborted && mock.Signals.Count == 1 && c1.Ok == c0.Ok + 1
            && signals.Count == 1 && signals[0].Failed && signals[0].ResponseReceived && _form.Flow.Wake.State == UI.StepState.Done,
            $"success={oEarly.Success} router={oEarly.RouterStatus} signals={mock.Signals.Count} observed=[{string.Join(",", signals.Select(c => c.ToString()))}] wake={_form.Flow.Wake} msg={oEarly.Message}");

        // ================= H. 접근성 트리 없음 + 크기 0 단락 =================
        await SetMock("{semanticsBroken:true, zeroSizeParagraphs:true}");
        await ReloadRouterAsync();
        await WaitKindAsync(RouterPageKind.ModeSelect, TimeSpan.FromSeconds(10));
        mock.ClearSignals();
        var o12 = await _form.RunWakeAsync(false);
        Check("접근성 트리 없음 + 크기 0 단락: 선택 화면부터 대상만 켜기", o12.Success && o12.Match?.Target?.Kind == "paragraph" && mock.Signals.Count == 1 && mock.Signals[0] == MacB,
            $"success={o12.Success} kind={o12.Match?.Target?.Kind} signals=[{Masked(mock.Signals)}] msg={o12.Message}");
        var savedName = s.WolPcName;
        s.WolPcName = "NOT-REGISTERED-PC";
        mock.ClearSignals();
        var o12b = await _form.RunWakeAsync(false);
        Check("크기 0 단락 + 대상 없음 → 클릭 안 함", !o12b.Success && mock.Signals.Count == 0, $"status={o12b.Match?.Status} signals={mock.Signals.Count}");
        s.WolPcName = savedName;
        await SetMock("{semanticsBroken:false, zeroSizeParagraphs:false}");

        // ================= I. 전체 흐름: 로그인 직후 곧바로 [PC 켜기] =================
        await ReloadRouterAsync();
        await LogoutViaApiAsync();
        await ReloadRouterAsync();
        await LoginAsync();
        mock.ClearSignals();
        var oFull = await _form.RunWakeAsync(false); // 로그인 후 준비 작업을 멈추고 선택 화면부터 처리
        Check("로그인 직후(선택 화면) [PC 켜기] 한 번으로 WOL", oFull.Success && mock.Signals.Count == 1 && mock.Signals[0] == MacB,
            $"success={oFull.Success} step={oFull.LastStep} signals=[{Masked(mock.Signals)}] msg={oFull.Message}");
        await WaitUntilAsync(() => !_form.IsPreparingAdmin, TimeSpan.FromSeconds(3));
        Check("UI: 준비 작업이 중간에 멈춰도 버튼이 막힌 채 남지 않음", !_form.IsPreparingAdmin && _form.WakeEnabled && _form.ConnectEnabled,
            $"preparing={_form.IsPreparingAdmin} wake={_form.WakeEnabled} connect={_form.ConnectEnabled}");

        // ================= J. PC 접속 흐름(가짜 VPN/포트/RDP) =================
        await ConnectScenariosAsync(s);

        // ================= J-2. 크롬 원격 데스크톱 =================
        await CrdScenariosAsync(s);

        // ================= J-3. PC 전원 상태 배지 =================
        await PowerScenariosAsync(s);

        // ================= L. 화면(UI) =================
        await UiScenariosAsync(mock, s);

        // ================= K. 진단 파일 =================
        await nav.NavigateToWolAsync(_ct);
        await _form.Browser.WaitForAsync(p => p.ContainsText(MacB.Substring(0, 5)), TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(400), _ct);
        var diagPath = await _form.ExportDiagnosticsAsync(Path.Combine(Path.GetTempPath(), "RemoteAccessHub-selftest-diag.json"));
        var diagOk = diagPath != null && File.Exists(diagPath);
        var diagText = diagOk ? await File.ReadAllTextAsync(diagPath!) : "";
        var anyRawMac = diagText.Contains(MacA) || diagText.Contains(MacB) || diagText.Contains(MacC);
        Check("진단 파일: MAC 마스킹", diagOk && !anyRawMac && diagText.Contains("**:**:**"), diagOk ? $"{diagText.Length} bytes, rawMac={anyRawMac}" : "생성 실패");
        var rawHosts = new[] { "127.0.0.1", "192.168.0.10" }.Where(h => diagText.Contains(h)).ToList();
        Check("진단 파일: 네트워크 경로·로그의 주소 마스킹", diagOk && rawHosts.Count == 0 && diagText.Contains("\"pageKind\""), $"노출된 주소={string.Join(",", rawHosts)}");

        await ScreenshotAsync("05-final");

        // ================= M. [종료]: 공유기 로그아웃 후 종료 (마지막에 실행) =================
        await ExitScenariosAsync(mock);

        // ================= N. 설정 초기화 (모든 설정을 지우므로 맨 끝) =================
        _form.ResetSettings(showSetup: false);
        await Task.Delay(500);
        var fresh = _form.Settings;
        Check("설정 초기화: 공유기 주소·PC 이름 비움, 시작 설정 다시 필요, 로그인 해제, VPN 연결 유지",
            fresh.RouterUrl.Length == 0 && fresh.WolPcName.Length == 0 && !fresh.SetupCompleted && !_form.Browser.Session.IsLoggedIn
            && !_form.WakeEnabled && _form.StatusText.Contains("초기화", StringComparison.Ordinal) && !_vpn.DisconnectCalled,
            $"url='{fresh.RouterUrl}' pc='{fresh.WolPcName}' setup={fresh.SetupCompleted} session={_form.Browser.Session.State} status='{_form.StatusText}'");
    }

    private async Task ExitScenariosAsync(MockRouterServer mock)
    {
        if (!_form.Browser.Session.IsLoggedIn)
        {
            await ReloadRouterAsync();
            await LoginAsync();
            await WaitUntilAsync(() => _form.LastSettle != null, TimeSpan.FromSeconds(30));
        }
        _vpn.Connected.Add("HomeVPN");
        var logoutsBefore = mock.LogoutCount;
        var sessionsBefore = mock.SessionCount;
        var attentionBefore = _form.AttentionCount;
        _form.SetBrowserExpanded(false);
        var r1 = await _form.PrepareExitAsync();
        var serverSessionGone = mock.SessionCount == sessionsBefore - 1;
        Check("종료: 공유기 로그아웃 요청 후 세션 끊김 확인", r1.Confirmed && !r1.AlreadyLoggedOut && mock.LogoutCount == logoutsBefore + 1 && _form.Browser.Session.State == SessionState.LoggedOut && serverSessionGone,
            $"confirmed={r1.Confirmed} logout+{mock.LogoutCount - logoutsBefore} state={_form.Browser.Session.State} serverSessions={mock.SessionCount} msg={r1.Message}");
        Check("종료: 의도한 로그아웃은 만료 경고·화면 펼침·알림 없음, VPN 유지", !_form.IsBrowserExpanded && _form.AttentionCount == attentionBefore && !_vpn.DisconnectCalled && _vpn.IsConnected("HomeVPN"),
            $"expanded={_form.IsBrowserExpanded} attention+{_form.AttentionCount - attentionBefore} vpn={_vpn.IsConnected("HomeVPN")}");
        Check("종료: 진행 중에는 버튼 비활성", !_form.WakeConnectEnabled && !_form.ConnectEnabled, $"wakeConnect={_form.WakeConnectEnabled} connect={_form.ConnectEnabled}");

        var r2 = await _form.PrepareExitAsync();
        Check("종료: 이미 로그아웃 상태면 추가 요청 없이 확인", r2.Confirmed && r2.AlreadyLoggedOut && mock.LogoutCount == logoutsBefore + 1,
            $"confirmed={r2.Confirmed} already={r2.AlreadyLoggedOut} logout+{mock.LogoutCount - logoutsBefore}");
    }

    private async Task<(int Confirm, int Cancel)> CountsAsync()
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await MockState());
            return (doc.RootElement.GetProperty("confirmCount").GetInt32(), doc.RootElement.GetProperty("cancelCount").GetInt32());
        }
        catch { return (-1, -1); }
    }

    private async Task ConfirmScenarioAsync(MockRouterServer mock, string options, string name, string expectTap)
    {
        await SetMock(options);
        mock.ClearSignals();
        _form.SetBrowserExpanded(false);
        var before = await CountsAsync();
        var expandedDuring = false;
        var task = _form.RunWakeAsync(false);
        while (!task.IsCompleted)
        {
            if (_form.IsBrowserExpanded) expandedDuring = true;
            await Task.Delay(100);
        }
        var o = await task;
        var after = await CountsAsync();
        var st = await MockState();
        var ok = o.Success && mock.Signals.Count == 1 && mock.Signals[0] == MacB
                 && after.Confirm == before.Confirm + 1 && after.Cancel == before.Cancel
                 && !expandedDuring && st.Contains(expectTap, StringComparison.Ordinal);
        Check(name, ok, $"success={o.Success} signals=[{Masked(mock.Signals)}] confirm+{after.Confirm - before.Confirm} cancel+{after.Cancel - before.Cancel} expandedDuring={expandedDuring} state={Short(st)}");
    }

    private async Task UiScenariosAsync(MockRouterServer mock, AppSettings s)
    {
        s.WolPcName = TargetName;
        s.WolPcMac = "";
        s.AutoConfirmWakeDialog = true;
        s.PublicHost = "127.0.0.1";
        s.PublicRdpPort = 33890;
        s.BootWaitSeconds = 15;
        mock.PcList = new() { new("OTHER-PC-1", MacA), new(TargetName, MacB) };
        _port.NeverOpen = false;
        if (!_form.Browser.Session.IsLoggedIn)
        {
            await ReloadRouterAsync();
            await LoginAsync();
        }
        await WaitUntilAsync(() => _form.WakeConnectEnabled && !_form.IsBusy, TimeSpan.FromSeconds(20));
        _form.SetThemeMode(ThemeMode.Dark, save: false);

        // 1) 한 번에: PC 켜기 → 부팅 확인 → 원격 데스크톱
        mock.ClearSignals();
        var launches = _rdp.LaunchCount;
        _port.OpenAfterAttempts = _port.Attempts + 2;
        var (w1, c1) = await _form.RunWakeAndConnectAsync(ConnectMode.Direct);
        var f = _form.Flow;
        Check("UI: [PC 켜고 접속] 한 번으로 WOL → 부팅 확인 → 원격 데스크톱", w1.Success && c1 is { Success: true } && mock.Signals.Count == 1 && _rdp.LaunchCount == launches + 1,
            $"wol={w1.Success} connect={c1?.Stage} signals={mock.Signals.Count} launches+{_rdp.LaunchCount - launches}");
        Check("UI: 완료 후 네 단계 모두 '완료'·성공 안내", f.Login.State == StepState.Done && f.Wake.State == StepState.Done && f.Boot.State == StepState.Done && f.Remote.State == StepState.Done && _form.StatusKind == UI.Controls.BannerKind.Success,
            f.Summary() + $" banner={_form.StatusKind}");
        _form.SetBrowserExpanded(false);
        await Task.Delay(200);
        await ScreenshotAsync("06-ui-done-dark");

        // 2) PC 켜기가 실패하면 접속하지 않음
        var savedName = s.WolPcName;
        s.WolPcName = "NOT-REGISTERED-PC";
        mock.ClearSignals();
        launches = _rdp.LaunchCount;
        var attempts = _port.Attempts;
        var (w2, c2) = await _form.RunWakeAndConnectAsync(ConnectMode.Direct);
        Check("UI: [PC 켜고 접속] — PC 켜기 실패 시 접속 시도 안 함·단계 '대상 PC 없음'",
            !w2.Success && c2 == null && _port.Attempts == attempts && _rdp.LaunchCount == launches && f.Wake.State == StepState.Failed && f.Wake.Detail.Contains("대상 PC 없음") && _form.StatusKind == UI.Controls.BannerKind.Error,
            $"wol={w2.Success} connect={(c2 == null ? "없음" : c2.Stage.ToString())} ports+{_port.Attempts - attempts} wake={f.Wake} banner={_form.StatusKind}");
        s.WolPcName = savedName;
        await ScreenshotAsync("06b-ui-failed-dark");

        // 3) 접속 설정이 없으면 PC를 켜지 않음
        var savedVpn = s.VpnName;
        s.VpnName = "";
        mock.ClearSignals();
        var (w3, c3) = await _form.RunWakeAndConnectAsync(ConnectMode.Vpn);
        Check("UI: [PC 켜고 접속] — 접속 설정이 없으면 PC를 켜지 않음", !w3.Success && c3 == null && mock.Signals.Count == 0,
            $"wol={w3.Success} signals={mock.Signals.Count} status='{_form.StatusText}'");

        // 4) 접속 방식 팝업: 방식별 설정만 검사, 화살표 없음, 누를 때마다 선택
        var direct = _form.ModeOptionState(ConnectMode.Direct);
        var vpn = _form.ModeOptionState(ConnectMode.Vpn);
        Check("UI: 접속 방식 선택지 — 일반 접속 사용 가능, VPN은 '설정 필요'", direct.Enabled && !vpn.Enabled && vpn.Detail.StartsWith("설정 필요"),
            $"direct={direct} vpn={vpn}");
        Check("UI: [PC 켜고 접속]·[PC 접속] 버튼에 화살표 없음", !_form.ConnectButtonsHaveArrow && _form.WakeConnectButtonText == "PC 켜고 접속",
            $"arrow={_form.ConnectButtonsHaveArrow} text='{_form.WakeConnectButtonText}'");

        var popupDisabled = _form.ShowModePopup(wakeFirst: false);
        await Task.Delay(300);
        var refusedDisabled = popupDisabled != null && !popupDisabled.Choose(ConnectMode.Vpn) && _form.CurrentModePopup != null && !_form.IsBusy;
        Check("UI: 팝업 — 설정이 없는 VPN은 고를 수 없고 창이 유지됨", refusedDisabled,
            $"popup={(popupDisabled != null)} open={_form.CurrentModePopup != null} busy={_form.IsBusy}");
        popupDisabled?.Close();
        await Task.Delay(200);

        s.VpnName = savedVpn.Length > 0 ? savedVpn : "HomeVPN";
        s.VpnDesktopIp = "192.168.0.10";
        Check("UI: VPN 설정을 채우면 VPN 선택지 사용 가능", _form.ModeOptionState(ConnectMode.Vpn).Enabled, _form.ModeOptionState(ConnectMode.Vpn).ToString());

        // [PC 접속] 팝업에서 VPN 선택 → 내부 IP로 접속
        _vpn.Connected.Add("HomeVPN");
        _port.NeverOpen = false;
        _port.OpenAfterAttempts = _port.Attempts + 1;
        launches = _rdp.LaunchCount;
        var p1 = _form.ShowModePopup(wakeFirst: false);
        await Task.Delay(300);
        var heading1 = p1?.Heading ?? "";
        var chose1 = p1 != null && p1.Choose(ConnectMode.Vpn);
        await WaitUntilAsync(() => _rdp.LaunchCount > launches && !_form.IsBusy, TimeSpan.FromSeconds(15));
        Check("UI: [PC 접속] 팝업에서 VPN 선택 → VPN 내부 IP로 원격 데스크톱", chose1 && _rdp.LaunchCount == launches + 1 && _rdp.Launches[^1].Host == "192.168.0.10" && _form.CurrentModePopup == null,
            $"heading='{heading1}' chose={chose1} host={(_rdp.LaunchCount > 0 ? _rdp.Launches[^1].Host : "-")} popupClosed={_form.CurrentModePopup == null}");

        // [PC 켜고 접속] 팝업에서 일반 선택 → WOL 후 공인 주소로 접속
        mock.ClearSignals();
        _port.OpenAfterAttempts = _port.Attempts + 1;
        launches = _rdp.LaunchCount;
        var p2 = _form.ShowModePopup(wakeFirst: true);
        await Task.Delay(300);
        if (!string.IsNullOrEmpty(_options.ScreenshotDirectory) && p2 != null)
        {
            try
            {
                using var bmp = new Bitmap(p2.Width, p2.Height);
                p2.DrawToBitmap(bmp, new Rectangle(0, 0, p2.Width, p2.Height));
                bmp.Save(Path.Combine(_options.ScreenshotDirectory, "09-mode-popup.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            catch { /* ignore */ }
        }
        var chose2 = p2 != null && p2.Choose(ConnectMode.Direct);
        await WaitUntilAsync(() => _rdp.LaunchCount > launches && !_form.IsBusy, TimeSpan.FromSeconds(40));
        Check("UI: [PC 켜고 접속] 팝업에서 일반 선택 → WOL → 공인 주소로 원격 데스크톱", chose2 && mock.Signals.Count == 1 && _rdp.LaunchCount == launches + 1 && _rdp.Launches[^1].Host == "127.0.0.1" && s.LastMode == ConnectMode.Direct,
            $"heading='{p2?.Heading}' chose={chose2} signals={mock.Signals.Count} host={(_rdp.LaunchCount > 0 ? _rdp.Launches[^1].Host : "-")} last={s.LastMode}");

        // 팝업은 Esc로 닫히고 아무것도 실행하지 않음
        launches = _rdp.LaunchCount;
        var p3 = _form.ShowModePopup(wakeFirst: false);
        await Task.Delay(300);
        p3?.Close();
        await Task.Delay(500);
        Check("UI: 팝업을 닫으면 아무 작업도 시작하지 않음", p3 != null && _form.CurrentModePopup == null && !_form.IsBusy && _rdp.LaunchCount == launches,
            $"busy={_form.IsBusy} launches+{_rdp.LaunchCount - launches}");

        // 5) 기록 창, 공유기 화면 접기
        var h0 = _form.ClientSize.Height;
        _form.SetLogVisible(true);
        var h1 = _form.ClientSize.Height;
        _form.SetLogVisible(false);
        var h2 = _form.ClientSize.Height;
        Check("UI: 기록 창 보이기/숨기기(창 높이 조정, 버튼 상태 유지)", h1 > h0 && h2 == h0 && _form.WakeConnectEnabled, $"h0={h0} h1={h1} h2={h2}");
        _form.SetBrowserExpanded(true);
        var hExpanded = _form.ClientSize.Height;
        _form.SetBrowserExpanded(false);
        var hCollapsed = _form.ClientSize.Height;
        Check("UI: 공유기 화면 접기 → 창 높이 축소, 로그인 유지", hExpanded - hCollapsed > 500 && _form.BrowserAreaHeight == 0 && _form.Browser.Session.IsLoggedIn && _form.WakeEnabled,
            $"expanded={hExpanded} collapsed={hCollapsed} area={_form.BrowserAreaHeight}");
        _form.SetLogVisible(true);
        var areaWithLog = _form.BrowserAreaHeight;
        _form.SetLogVisible(false);
        Check("UI: 접힌 채 기록 창을 열고 닫아도 공유기 화면 영역 0", areaWithLog == 0 && _form.BrowserAreaHeight == 0 && _form.ClientSize.Height == hCollapsed,
            $"withLog={areaWithLog} after={_form.BrowserAreaHeight} h={_form.ClientSize.Height}/{hCollapsed}");

        // 6) 테마 전환
        _form.SetThemeMode(ThemeMode.Light, save: false);
        var lightOk = ReferenceEquals(Theme.Current, Theme.Light) && _form.BackColor == Theme.Light.Background;
        await Task.Delay(200);
        await ScreenshotAsync("07-ui-light");
        _form.SetThemeMode(ThemeMode.Dark, save: false);
        var darkOk = ReferenceEquals(Theme.Current, Theme.Dark) && _form.BackColor == Theme.Dark.Background;
        Check("UI: 테마 전환(밝게 → 어둡게)", lightOk && darkOk, $"light={lightOk} dark={darkOk}");

        // 7) 설정 창: 테마 적용된 상태로 열리고 값이 그대로 표시되는지
        string? settingsError = null;
        var settingsShown = false;
        var settingsIcon = false;
        try
        {
            using var sf = new SettingsForm(s, _vpn) { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false };
            sf.Show(_form);
            await Task.Delay(400);
            settingsShown = sf.Visible && sf.BackColor == Theme.Current.Background;
            settingsIcon = AppIcon.Current != null && ReferenceEquals(sf.Icon, AppIcon.Current);
            if (!string.IsNullOrEmpty(_options.ScreenshotDirectory))
            {
                using var bmp = new Bitmap(sf.Width, sf.Height);
                sf.DrawToBitmap(bmp, new Rectangle(0, 0, sf.Width, sf.Height));
                bmp.Save(Path.Combine(_options.ScreenshotDirectory, "08-settings-dark.png"), System.Drawing.Imaging.ImageFormat.Png);
                // 아래쪽(크롬 원격 데스크톱·설정 파일)도 한 장 남긴다.
                sf.ScrollTo(0.62);
                await Task.Delay(200);
                using var bmp2 = new Bitmap(sf.Width, sf.Height);
                sf.DrawToBitmap(bmp2, new Rectangle(0, 0, sf.Width, sf.Height));
                bmp2.Save(Path.Combine(_options.ScreenshotDirectory, "08b-settings-crd.png"), System.Drawing.Imaging.ImageFormat.Png);
                sf.ScrollTo(1);
                await Task.Delay(200);
                using var bmp3 = new Bitmap(sf.Width, sf.Height);
                sf.DrawToBitmap(bmp3, new Rectangle(0, 0, sf.Width, sf.Height));
                bmp3.Save(Path.Combine(_options.ScreenshotDirectory, "08c-settings-bottom.png"), System.Drawing.Imaging.ImageFormat.Png);
                sf.ScrollTo(0);
            }
            sf.Close();
        }
        catch (Exception ex)
        {
            settingsError = ex.Message;
        }
        Check("UI: 설정 창 열기(테마 적용, 오류 없음)", settingsShown && settingsError == null, settingsError ?? "ok");

        // 7-1) 설정 파일: 내보내기 → 가져오기(창에 채움, 저장 전) → 초기화 요청
        var exportPath = Path.Combine(Path.GetTempPath(), "rah-selftest-export-" + Guid.NewGuid().ToString("N") + ".json");
        string? fileDetail = null;
        var fileOk = false;
        try
        {
            using (var sf = new SettingsForm(s, _vpn) { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false })
            {
                sf.Show(_form);
                var exportError = sf.ExportTo(exportPath);
                var exported = File.Exists(exportPath) ? File.ReadAllText(exportPath) : "";
                sf.Close();
                var edited = s.Clone();
                edited.WolPcName = "CHANGED-PC";
                using var sf2 = new SettingsForm(edited, _vpn) { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false };
                sf2.Show(_form);
                var importError = sf2.ImportFrom(exportPath);
                var imported = sf2.Result;
                var badPath = exportPath + ".bad.json";
                File.WriteAllText(badPath, "{\"hello\":1}");
                var badError = sf2.ImportFrom(badPath);
                File.Delete(badPath);
                sf2.RequestReset();
                fileOk = exportError == null && importError == null && exported.Contains(s.WolPcName, StringComparison.Ordinal)
                    && !exported.Contains("WindowLeft\": 1", StringComparison.Ordinal) && imported.WolPcName == s.WolPcName
                    && badError != null && sf2.ResetRequested && sf2.DialogResult == DialogResult.Abort;
                fileDetail = $"export={exportError ?? "ok"} import={importError ?? "ok"} importedPc={imported.WolPcName} bad='{badError}' reset={sf2.ResetRequested}";
            }
        }
        catch (Exception ex)
        {
            fileDetail = "예외: " + ex.Message;
        }
        finally
        {
            try { File.Delete(exportPath); } catch { /* ignore */ }
        }
        Check("UI: 설정 파일 내보내기·가져오기(다른 파일 거부)·초기화 요청", fileOk, fileDetail ?? "");

        // 7-2) 시작 설정 창: 단계별 입력 검사, 연결 확인, 완료 결과
        var oldOpenAfter = _port.OpenAfterAttempts;
        var oldNever = _port.NeverOpen;
        _port.OpenAfterAttempts = 0;
        _port.NeverOpen = false;
        string wizardDetail;
        var wizardOk = false;
        try
        {
            using var w = new SetupWizardForm(new AppSettings(), _vpn, _port) { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false };
            w.Show(_form);
            await Task.Delay(300);
            var startPage = w.CurrentPage;
            void Shot(string name)
            {
                if (string.IsNullOrEmpty(_options.ScreenshotDirectory)) return;
                using var bmp = new Bitmap(w.Width, w.Height);
                w.DrawToBitmap(bmp, new Rectangle(0, 0, w.Width, w.Height));
                bmp.Save(Path.Combine(_options.ScreenshotDirectory, name), System.Drawing.Imaging.ImageFormat.Png);
            }
            Shot("10-setup-welcome.png");
            w.Next(); // 시작 → 공유기 주소
            w.RouterUrlBox.Text = "not a router url";
            var badUrlBlocked = !w.Next() && w.CurrentPage == SetupWizardForm.Page.Router && w.ErrorText.Length > 0;
            w.RouterUrlBox.Text = "http://127.0.0.1:" + _form.Settings.PublicRdpPort + "/";
            await w.CheckRouterForTestAsync();
            var checkText = w.RouterCheckText;
            w.Next(); // → 켤 PC
            w.PcNameBox.Text = "";
            var emptyPcBlocked = !w.Next() && w.CurrentPage == SetupWizardForm.Page.Pc;
            w.PcNameBox.Text = "MY-PC";
            w.PcMacBox.Text = "02-00-aa-bb-cc-01";
            w.Next(); // → 접속 방법
            var hostPrefilled = w.PublicHostBox.Text == "127.0.0.1";
            w.UseDirectBox.Checked = true;
            w.PublicPortBox.Value = 41000;
            w.UseVpnBox.Checked = true;
            w.VpnNameBox.Text = "";
            var vpnBlocked = !w.Next() && w.CurrentPage == SetupWizardForm.Page.Connect;
            w.VpnNameBox.Text = "HomeVPN";
            w.VpnIpBox.Text = "192.168.0.10";
            Shot("10-setup-connect.png");
            w.Next(); // → 확인
            var onDone = w.CurrentPage == SetupWizardForm.Page.Done;
            Shot("10-setup-summary.png");
            w.Next(); // 완료
            var r = w.Result;
            wizardOk = startPage == SetupWizardForm.Page.Welcome && badUrlBlocked && emptyPcBlocked && hostPrefilled && vpnBlocked && onDone
                && checkText.StartsWith("연결됨", StringComparison.Ordinal)
                && w.DialogResult == DialogResult.OK && r.SetupCompleted && r.RouterUrl.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
                && r.WolPcName == "MY-PC" && r.WolPcMac == "02:00:AA:BB:CC:01" && r.PublicHost == "127.0.0.1" && r.PublicRdpPort == 41000
                && r.VpnName == "HomeVPN" && r.VpnDesktopIp == "192.168.0.10" && r.ValidateRouter().Count == 0;
            wizardDetail = $"badUrl={badUrlBlocked} emptyPc={emptyPcBlocked} prefill={hostPrefilled} vpnBlocked={vpnBlocked} check='{checkText}' result={r.RouterUrl}|{r.WolPcName}|{r.WolPcMac}|{r.PublicHost}:{r.PublicRdpPort}|{r.VpnName}|{r.VpnDesktopIp} setup={r.SetupCompleted}";
        }
        catch (Exception ex)
        {
            wizardDetail = "예외: " + ex;
        }
        finally
        {
            _port.OpenAfterAttempts = oldOpenAfter;
            _port.NeverOpen = oldNever;
        }
        Check("UI: 시작 설정(주소·PC 이름·VPN 입력 검사, 연결 확인, 완료 시 설정 반영)", wizardOk, wizardDetail);
        Check("첫 실행 판단: 시작 설정 전이면 띄우고, 마쳤거나 모의·자체검사면 띄우지 않음",
            MainForm.ShouldRunSetup(new AppSettings(), new LaunchOptions()) && !MainForm.ShouldRunSetup(new AppSettings { SetupCompleted = true }, new LaunchOptions())
            && !MainForm.ShouldRunSetup(new AppSettings(), _options),
            "ok");
        // 실행 파일에 박힌 아이콘이 기본 아이콘이 아니라 app.ico인지: 32px 그림을 픽셀 단위로 비교
        var exeMatch = 0.0;
        try
        {
            using var exeIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
            if (exeIcon != null && AppIcon.Current != null)
            {
                using var a = new Icon(exeIcon, 32, 32).ToBitmap();
                using var b = new Icon(AppIcon.Current, 32, 32).ToBitmap();
                var same = 0;
                for (var y = 0; y < 32; y++)
                    for (var x = 0; x < 32; x++)
                        if (a.GetPixel(x, y).ToArgb() == b.GetPixel(x, y).ToArgb()) same++;
                exeMatch = same / 1024.0;
            }
        }
        catch { /* 아래 검사에서 실패로 보고 */ }
        Check("UI: 프로그램 아이콘(메인 창·설정 창·실행 파일)", AppIcon.Current != null && ReferenceEquals(_form.Icon, AppIcon.Current) && settingsIcon && exeMatch > 0.95,
            $"main={ReferenceEquals(_form.Icon, AppIcon.Current)} settings={settingsIcon} exe32px 일치={exeMatch:P0}");

        // 공유기 화면 입력 중 숨겨진 커서가 메인 창 위에 남는 경우.
        // 실제 커서는 건드리지 않고, 커서 상태 읽기·표시 호출만 바꿔 메시지 경로와 판단을 확인한다.
        var guard = _form.CursorGuard;
        var oldRead = guard.ReadCursorFlags;
        var oldShow = guard.ShowCursorCall;
        var hidden = true;
        var showCalls = 0;
        guard.ReadCursorFlags = () => hidden ? 0 : CursorGuard.CURSOR_SHOWING;
        guard.ShowCursorCall = v => { showCalls++; if (v) hidden = false; return 0; };
        try
        {
            var before = guard.RestoreCount;
            // 공유기 화면(브라우저) 위: 건드리지 않음. 실제 마우스 움직임과 섞이지 않도록 메시지 대신 직접 판단을 부른다.
            var browserRestored = guard.RestoreIfHidden(_form.BrowserControlHandle, "자체검사");
            var browserUntouched = !browserRestored && hidden && showCalls == 0 && guard.RestoreCount == before;
            CursorGuard.PostMouseMove(_form.ActionsHandle); // 버튼 줄 위: 다시 표시
            await WaitUntilAsync(() => !hidden, TimeSpan.FromSeconds(3));
            var restored = !hidden && showCalls == 1 && guard.RestoreCount == before + 1;
            showCalls = 0;
            CursorGuard.PostMouseMove(_form.ActionsHandle); // 이미 보이면 아무것도 안 함
            await Task.Delay(300);
            Check("UI: 공유기 화면 입력 후 숨겨진 커서를 메인 창 위에서 다시 표시(브라우저 위는 그대로)", browserUntouched && restored && showCalls == 0,
                $"browserUntouched={browserUntouched} restored={restored} extraCalls={showCalls}");
        }
        finally
        {
            guard.ReadCursorFlags = oldRead;
            guard.ShowCursorCall = oldShow;
        }
    }

    private static string Masked(IEnumerable<string> macs) => string.Join(",", macs.Select(InputRules.MaskMac));

    private static string Short(string s) => s.Length > 260 ? s[..260] + "…" : s;

    /// <summary>PC 전원 상태 배지: 포트 응답만 보고, 응답이 없다고 꺼졌다고 단정하지 않는지 확인한다.</summary>
    private async Task PowerScenariosAsync(AppSettings s)
    {
        var savedMode = s.PowerCheckMode;
        var savedNeverOpen = _port.NeverOpen;
        try
        {
            s.PowerCheckMode = "direct";
            s.PublicHost = "127.0.0.1";
            s.PublicRdpPort = 33890;

            // 1) 포트가 응답하면 "켜짐"
            _port.NeverOpen = false;
            _port.OpenAfterAttempts = 0;
            await _form.CheckPowerNowAsync();
            var on = _form.PcPowerStatus;
            Check("전원 배지: 포트가 응답하면 '켜짐'",
                on.State == PcPowerState.On && _form.PowerPillText.StartsWith("PC 켜짐") && on.CheckedAt != null,
                $"state={on.State} pill='{_form.PowerPillText}' detail='{on.Detail}'");

            // 2) 응답이 없으면 "응답 없음"(꺼짐이라고 쓰지 않음)
            _port.NeverOpen = true;
            await _form.CheckPowerNowAsync();
            var off = _form.PcPowerStatus;
            Check("전원 배지: 응답이 없을 때 '꺼짐'이라고 단정하지 않음",
                off.State == PcPowerState.NoAnswer && _form.PowerPillText == "PC 응답 없음" && !_form.PowerPillText.Contains("꺼짐") && off.Detail.Contains("포트가 막힘"),
                $"state={off.State} pill='{_form.PowerPillText}' detail='{off.Detail}'");

            // 3) VPN이 연결되어 있으면 내부 주소로 확인(연결은 스스로 하지 않음)
            s.PowerCheckMode = "vpn";
            s.VpnDesktopIp = "127.0.0.1";
            s.VpnRdpPort = 33890;
            _vpn.Connected.Clear();
            var callsBefore = _vpn.ConnectCalls;
            // VPN 연결 상태는 주기적으로 읽어 두므로 배지가 따라올 때까지 기다린다.
            await WaitUntilAsync(() => !_form.VpnPillText.Contains("연결됨"), TimeSpan.FromSeconds(15));
            await _form.CheckPowerNowAsync();
            var noVpn = _form.PcPowerStatus;
            _vpn.Connected.Add("HomeVPN");
            await WaitUntilAsync(() => _form.VpnPillText.Contains("연결됨"), TimeSpan.FromSeconds(10));
            _port.NeverOpen = false;
            var targetsBefore = _port.Targets.Count;
            await _form.CheckPowerNowAsync();
            var withVpn = _form.PcPowerStatus;
            Check("전원 배지: VPN 확인은 이미 연결되어 있을 때만 하고, 확인하려고 연결하지 않음",
                noVpn.State == PcPowerState.Disabled && _vpn.ConnectCalls == callsBefore
                    && withVpn.State == PcPowerState.On && _port.Targets.Count > targetsBefore,
                $"vpn없음={noVpn.State} connectCalls+{_vpn.ConnectCalls - callsBefore} vpn있음={withVpn.State}");

            // 4) 끄면 배지도 사라지고 네트워크를 건드리지 않음
            s.PowerCheckMode = "off";
            var attempts = _port.Attempts;
            await _form.CheckPowerNowAsync();
            Check("전원 배지: 표시하지 않음으로 두면 배지도 없고 포트도 보지 않음",
                _form.PcPowerStatus.State == PcPowerState.Disabled && _form.PowerPillText == "" && _port.Attempts == attempts,
                $"state={_form.PcPowerStatus.State} pill='{_form.PowerPillText}' ports+{_port.Attempts - attempts}");
        }
        finally
        {
            s.PowerCheckMode = savedMode;
            _port.NeverOpen = savedNeverOpen;
            // 뒤따르는 화면 검사·화면 저장에서 배지가 실제 사용 모습(켜짐)으로 보이도록 한 번 더 확인한다.
            _port.NeverOpen = false;
            _port.OpenAfterAttempts = 0;
            await _form.CheckPowerNowAsync();
        }
    }

    /// <summary>크롬 원격 데스크톱: 구글 중계이므로 열어 둔 포트가 없다. 확인하지 않은 것을 확인한 척하지 않는지도 함께 본다.</summary>
    private async Task CrdScenariosAsync(AppSettings s)
    {
        const string HostId = "7f3a1b9c2d4e5f60";
        var sessionUrl = CrdLauncher.AccessUrl + "/session/" + HostId;
        var savedUse = s.UseCrd;
        var savedId = s.CrdHostId;
        var savedCheck = s.CrdBootCheckMode;
        var savedMode = s.LastConnectMode;
        try
        {
            // 1) 설정에서 꺼져 있으면 선택지에 없고 실행도 안 됨
            s.UseCrd = false;
            var off = _form.ModeOptionState(ConnectMode.Crd);
            var offCount = _form.ModeOptionCount;
            var openedBefore = _crd.OpenCount;
            var refused = await _form.RunConnectAsync(ConnectMode.Crd);
            Check("크롬 원격 데스크톱: 꺼져 있으면 선택지에 없고 브라우저도 열지 않음",
                offCount == 2 && !off.Enabled && !refused.Success && _crd.OpenCount == openedBefore,
                $"options={offCount} enabled={off.Enabled} stage={refused.Stage} opened+{_crd.OpenCount - openedBefore}");

            // 2) 켜고 부팅 확인 없음: 포트를 보지 않고 저장된 기기 주소를 연다
            s.UseCrd = true;
            s.CrdHostId = HostId;
            s.CrdBootCheckMode = "none";
            _port.NeverOpen = true; // 포트를 본다면 실패할 상황 — 보지 않아야 성공한다
            var attempts = _port.Attempts;
            var vpnCalls = _vpn.ConnectCalls;
            var rdpLaunches = _rdp.LaunchCount;
            openedBefore = _crd.OpenCount;
            var a = await _form.RunConnectAsync(ConnectMode.Crd);
            var f = _form.Flow;
            Check("크롬 원격 데스크톱: 부팅 확인 없이 저장된 기기 주소를 연다(포트·VPN·mstsc 사용 없음)",
                a.Success && _crd.OpenCount == openedBefore + 1 && _crd.Opened[^1] == sessionUrl
                    && _port.Attempts == attempts && _vpn.ConnectCalls == vpnCalls && _rdp.LaunchCount == rdpLaunches,
                $"stage={a.Stage} url={_crd.Opened[^1]} ports+{_port.Attempts - attempts} vpn+{_vpn.ConnectCalls - vpnCalls} mstsc+{_rdp.LaunchCount - rdpLaunches}");
            Check("크롬 원격 데스크톱: 확인하지 않은 부팅 단계를 완료로 표시하지 않음",
                !a.PcRespondedOnPort && f.Boot.State == UI.StepState.Pending && f.Boot.Detail == "확인 안 함"
                    && _form.WolBootText == "PC 부팅: 확인 안 함" && f.Remote.State == UI.StepState.Done,
                $"boot={f.Boot} remote={f.Remote} wolBoot='{_form.WolBootText}'");

            // 3) 기기 ID가 없으면 기기 목록 화면
            s.CrdHostId = "";
            openedBefore = _crd.OpenCount;
            var b = await _form.RunConnectAsync(ConnectMode.Crd);
            Check("크롬 원격 데스크톱: 기기 ID가 없으면 기기 목록 화면을 연다",
                b.Success && _crd.OpenCount == openedBefore + 1 && _crd.Opened[^1] == CrdLauncher.AccessUrl,
                $"stage={b.Stage} url={_crd.Opened[^1]}");

            // 4) 부팅 확인을 켜면 포트가 응답할 때까지 기다리고, 응답이 없으면 브라우저를 열지 않는다
            s.CrdHostId = HostId;
            s.CrdBootCheckMode = "direct";
            s.BootWaitSeconds = 10;
            _port.NeverOpen = true;
            openedBefore = _crd.OpenCount;
            var c = await _form.RunConnectAsync(ConnectMode.Crd);
            Check("크롬 원격 데스크톱: 부팅 확인 실패 시 브라우저를 열지 않음",
                !c.Success && c.Stage == ConnectStage.TimedOut && _crd.OpenCount == openedBefore,
                $"stage={c.Stage} opened+{_crd.OpenCount - openedBefore}");

            _port.NeverOpen = false;
            _port.OpenAfterAttempts = _port.Attempts + 2;
            attempts = _port.Attempts;
            openedBefore = _crd.OpenCount;
            var d = await _form.RunConnectAsync(ConnectMode.Crd);
            Check("크롬 원격 데스크톱: 부팅 확인 후 브라우저를 연다",
                d.Success && d.PcRespondedOnPort && _port.Attempts > attempts && _crd.OpenCount == openedBefore + 1 && _form.Flow.Boot.State == UI.StepState.Done,
                $"stage={d.Stage} ports+{_port.Attempts - attempts} boot={_form.Flow.Boot}");

            // 5) 접속 방식 팝업에 세 번째 선택지가 생기고, 골라서 바로 열 수 있다
            s.CrdBootCheckMode = "none";
            var count = _form.ModeOptionCount;
            var opt = _form.ModeOptionState(ConnectMode.Crd);
            openedBefore = _crd.OpenCount;
            var popup = _form.ShowModePopup(wakeFirst: false);
            await Task.Delay(300);
            if (!string.IsNullOrEmpty(_options.ScreenshotDirectory) && popup != null)
            {
                try
                {
                    Directory.CreateDirectory(_options.ScreenshotDirectory);
                    using var bmp = new Bitmap(popup.Width, popup.Height);
                    popup.DrawToBitmap(bmp, new Rectangle(0, 0, popup.Width, popup.Height));
                    bmp.Save(Path.Combine(_options.ScreenshotDirectory, "09b-mode-popup-crd.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                catch { /* ignore */ }
            }
            var rows = popup?.OptionCount ?? 0;
            var chose = popup != null && popup.Choose(ConnectMode.Crd);
            await WaitUntilAsync(() => _crd.OpenCount > openedBefore && !_form.IsBusy, TimeSpan.FromSeconds(15));
            Check("UI: 접속 방식 팝업에 크롬 원격 데스크톱 선택지 추가(골라서 바로 열기)",
                count == 3 && rows == 3 && opt.Enabled && opt.Title == "크롬 원격 데스크톱" && chose
                    && _crd.OpenCount == openedBefore + 1 && s.LastMode == ConnectMode.Crd,
                $"options={count} rows={rows} detail='{opt.Detail}' chose={chose} last={s.LastMode}");
            _form.CurrentModePopup?.Close();
            await Task.Delay(200);

            // 6) 앱이 설치된 PC와 아닌 PC에서 안내가 다른지(가짜 실행기로 양쪽을 흉내 낸다)
            _crd.Target = CrdOpenTarget.App;
            var inApp = await _form.RunConnectAsync(ConnectMode.Crd);
            _crd.Target = CrdOpenTarget.AppHome;
            var appHome = await _form.RunConnectAsync(ConnectMode.Crd);
            _crd.Target = CrdOpenTarget.Browser;
            var inBrowser = await _form.RunConnectAsync(ConnectMode.Crd);
            Check("크롬 원격 데스크톱: 앱으로 열었는지 브라우저로 열었는지 그대로 알림",
                inApp.Message.Contains("앱을 열었습니다") && !inApp.Message.Contains("브라우저로")
                    && appHome.Message.Contains("기기 목록") && !appHome.Message.Contains("저장된 기기")
                    && inBrowser.Message.Contains("브라우저로"),
                $"app='{inApp.Message}' appHome='{appHome.Message}' browser='{inBrowser.Message}'");

            // 7) 이 PC의 실제 앱 찾기(설치 여부와 무관하게 예외 없이 끝나야 한다)
            CrdInstalledApp? found = null;
            string? findError = null;
            try { found = CrdAppFinder.Refresh(); }
            catch (Exception ex) { findError = ex.Message; }
            var icon = found == null ? null : CrdIcon.Reload();
            var appOk = findError == null && (found == null
                || (found.Aumid.EndsWith("!App", StringComparison.Ordinal) && found.PackageFamilyName.Length > 0
                    && (found.BrowserAppId == null || found.BrowserAppId.Length == 32)));
            Check("크롬 원격 데스크톱: 설치된 앱 찾기(설치돼 있지 않아도 오류 없이 넘어감)", appOk,
                findError != null ? "오류: " + findError
                    : found == null ? "이 PC에는 앱 없음 → 기본 브라우저로 엽니다"
                    : $"앱 찾음 · 주소 전달 {(found.CanOpenUrlInApp ? "가능" : "불가")} · 아이콘 {(icon != null ? "읽음" : "없음")}");
        }
        finally
        {
            // 뒤따르는 검사는 기존 두 가지 방식만 쓰므로 설정을 되돌린다.
            _crd.Target = CrdOpenTarget.Browser;
            s.UseCrd = savedUse;
            s.CrdHostId = savedId;
            s.CrdBootCheckMode = savedCheck;
            s.LastConnectMode = savedMode;
            _port.NeverOpen = false;
        }
    }

    private async Task ConnectScenariosAsync(AppSettings s)
    {
        s.PublicHost = "127.0.0.1";
        s.PublicRdpPort = 33890;
        s.VpnName = "HomeVPN";
        s.VpnDesktopIp = "192.168.0.10";
        s.VpnRdpPort = 3389;
        s.BootWaitSeconds = 10;
        s.VpnWaitSeconds = 10;

        _port.NeverOpen = false;
        _port.OpenAfterAttempts = _port.Attempts + 2;
        var a = await _form.RunConnectAsync(ConnectMode.Direct);
        Check("일반 접속: 포트 응답 후 mstsc 실행", a.Success && _rdp.LaunchCount == 1 && _rdp.Launches[0].Host == "127.0.0.1" && _rdp.Launches[0].Port == 33890,
            $"stage={a.Stage} launches={_rdp.LaunchCount} boot='{_form.WolBootText}'");

        _vpn.ConnectSucceeds = false;
        _vpn.Connected.Clear();
        var launchesBefore = _rdp.LaunchCount;
        var b = await _form.RunConnectAsync(ConnectMode.Vpn);
        Check("VPN 실패 시 일반 접속 전환 없음", !b.Success && b.Stage == ConnectStage.Failed && _rdp.LaunchCount == launchesBefore && !_vpn.DisconnectCalled,
            $"stage={b.Stage} launches={_rdp.LaunchCount - launchesBefore}");

        _vpn.ConnectSucceeds = true;
        _port.OpenAfterAttempts = _port.Attempts + 1;
        var c = await _form.RunConnectAsync(ConnectMode.Vpn);
        Check("VPN 접속: 연결 후 내부 IP로 mstsc", c.Success && _rdp.Launches[^1].Host == "192.168.0.10" && _vpn.ConnectCalls >= 1,
            $"stage={c.Stage} port={_rdp.Launches[^1].Port}");
        var callsBefore = _vpn.ConnectCalls;
        _port.OpenAfterAttempts = _port.Attempts + 1;
        var c2 = await _form.RunConnectAsync(ConnectMode.Vpn);
        Check("이미 연결된 VPN 재사용(재연결 시도 없음)", c2.Success && _vpn.ConnectCalls == callsBefore, $"connectCalls={_vpn.ConnectCalls - callsBefore}");

        _port.NeverOpen = true;
        s.BootWaitSeconds = 10;
        var d = await _form.RunConnectAsync(ConnectMode.Direct);
        Check("부팅 대기 시간 초과 처리", !d.Success && d.Stage == ConnectStage.TimedOut && _form.WolBootText.Contains("응답 없음"),
            $"stage={d.Stage} boot='{_form.WolBootText}'");

        _port.NeverOpen = true;
        s.BootWaitSeconds = 60;
        var connectTask = _form.RunConnectAsync(ConnectMode.Vpn);
        await WaitUntilAsync(() => _form.IsBusy && _form.StatusText.Contains("대기"), TimeSpan.FromSeconds(10));
        await Task.Delay(2600); // 1초 주기 화면 갱신이 경과 시간을 반영하도록
        var boot = _form.Flow.Boot;
        Check("UI: RDP 응답 대기 중 경과 시간·진행률 표시", boot.State == UI.StepState.Active && boot.Progress is > 0 and < 1 && boot.Detail.Contains("RDP 응답 대기"),
            $"boot={boot} progress={boot.Progress:0.00}");
        _form.CancelOperation();
        var e = await connectTask;
        Check("작업 취소 시 VPN 유지·취소 보고", e.IsCancelled && !_vpn.DisconnectCalled && _vpn.IsConnected("HomeVPN") && !_form.IsBusy,
            $"stage={e.Stage} vpnConnected={_vpn.IsConnected("HomeVPN")} busy={_form.IsBusy}");
        _port.NeverOpen = false;
    }
}
