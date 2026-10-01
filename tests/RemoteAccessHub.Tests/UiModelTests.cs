using RemoteAccessHub.Core;
using RemoteAccessHub.Router;
using RemoteAccessHub.Services;
using RemoteAccessHub.UI;
using Xunit;

namespace RemoteAccessHub.Tests;

public class ThemeTests
{
    public static IEnumerable<object[]> Palettes() => new[] { new object[] { Theme.Dark }, new object[] { Theme.Light } };

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Text_contrast_meets_wcag(Palette p)
    {
        Assert.True(Theme.Contrast(p.Text, p.Surface) >= 7, $"{p.Name} 본문/표면 {Theme.Contrast(p.Text, p.Surface):0.0}");
        Assert.True(Theme.Contrast(p.Text, p.Background) >= 7, $"{p.Name} 본문/바탕");
        Assert.True(Theme.Contrast(p.SubText, p.Surface) >= 4.5, $"{p.Name} 보조글/표면 {Theme.Contrast(p.SubText, p.Surface):0.0}");
        Assert.True(Theme.Contrast(p.SubText, p.Background) >= 4.5, $"{p.Name} 보조글/바탕");
        Assert.True(Theme.Contrast(p.OnAccent, p.Accent) >= 4.5, $"{p.Name} 주 버튼 글자 {Theme.Contrast(p.OnAccent, p.Accent):0.0}");
        Assert.True(Theme.Contrast(p.LogText, p.LogBackground) >= 7, $"{p.Name} 기록");
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Status_colors_are_readable_on_surface(Palette p)
    {
        foreach (var (name, c) in new[] { ("성공", p.Success), ("경고", p.Warning), ("오류", p.Danger), ("정보", p.Info), ("강조", p.Accent) })
            Assert.True(Theme.Contrast(c, p.Surface) >= 4.5, $"{p.Name} {name}/표면 {Theme.Contrast(c, p.Surface):0.0}");
    }

    [Fact]
    public void Theme_mode_round_trip()
    {
        foreach (var m in new[] { ThemeMode.System, ThemeMode.Dark, ThemeMode.Light })
            Assert.Equal(m, Theme.ParseMode(Theme.ToSetting(m)));
        Assert.Equal(ThemeMode.System, Theme.ParseMode("unknown"));
        Assert.Equal(ThemeMode.System, Theme.ParseMode(null));
    }

    [Fact]
    public void Contrast_matches_known_values()
    {
        Assert.Equal(21.0, Theme.Contrast(Color.Black, Color.White), 1);
        Assert.Equal(1.0, Theme.Contrast(Color.Gray, Color.Gray), 3);
    }
}

public class FlowTrackerTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 14, 15, 37, 0, TimeSpan.FromHours(9));

    [Fact]
    public void Initial_state_is_pending_and_login_prompt_when_logged_out()
    {
        var f = new FlowTracker();
        Assert.All(f.Steps, s => Assert.Equal(StepState.Pending, s.State));
        f.OnSession(SessionState.LoggedOut, null);
        Assert.Equal(StepState.Active, f.Login.State);
        Assert.Contains("로그인", f.Login.Detail);
    }

    [Fact]
    public void Admin_select_needed_is_warning_then_done()
    {
        var f = new FlowTracker();
        f.OnSession(SessionState.LoggedIn, T);
        Assert.Equal(StepState.Done, f.Login.State);
        Assert.Contains("15:37", f.Login.Detail);
        f.OnAdminSelectNeeded(true);
        Assert.Equal(StepState.Warning, f.Login.State);
        f.OnAdminSelectNeeded(false);
        Assert.Equal(StepState.Done, f.Login.State);
    }

    [Fact]
    public void Full_success_path_marks_all_done_and_keeps_legacy_texts()
    {
        var f = new FlowTracker();
        f.OnSession(SessionState.LoggedIn, T);
        f.OnWolStarted();
        Assert.Equal(StepState.Active, f.Wake.State);
        f.OnWolStage(WolStep.NavigateToWol, StageStatus.Running, "이동");
        Assert.Contains("WOL 화면", f.Wake.Detail);
        f.OnWolStage(WolStep.Click, StageStatus.Done, "clicked");
        f.OnWolStage(WolStep.RouterResponse, StageStatus.Done, "ok");
        f.OnWolOutcome(new WolOutcome(true, WolStep.RouterResponse, "ok", true, StageStatus.Done, null), T);
        Assert.Equal(StepState.Done, f.Wake.State);
        Assert.Equal("WOL 버튼 클릭: 완료", f.WolClickText);
        Assert.Equal("공유기 처리: 완료", f.WolRouterText);

        f.OnConnectStarted(ConnectMode.Vpn, 180);
        f.OnConnectProgress(new ConnectProgress(ConnectStage.VpnConnecting, "연결 중"), T);
        Assert.Contains("VPN", f.Boot.Detail);
        f.OnConnectProgress(new ConnectProgress(ConnectStage.WaitingPort, "대기"), T);
        Assert.True(f.Tick(T.AddSeconds(45)));
        Assert.Equal(StepState.Active, f.Boot.State);
        Assert.Equal(0.25, f.Boot.Progress!.Value, 2);
        Assert.Contains("45/180", f.Boot.Detail);
        f.OnConnectProgress(new ConnectProgress(ConnectStage.PortOpen, "열림"), T.AddSeconds(60));
        Assert.Equal(StepState.Done, f.Boot.State);
        Assert.StartsWith("PC 부팅: 응답 확인", f.WolBootText);
        Assert.False(f.Tick(T.AddSeconds(61)));
        f.OnConnectProgress(new ConnectProgress(ConnectStage.LaunchingRdp, "실행"), T.AddSeconds(61));
        f.OnConnectOutcome(new ConnectOutcome(ConnectStage.Done, true, "ok", TimeSpan.FromSeconds(61), true, ConnectMode.Vpn), T.AddSeconds(61));
        Assert.All(f.Steps, s => Assert.Equal(StepState.Done, s.State));
    }

    [Fact]
    public void Manual_confirm_needed_is_warning()
    {
        var f = new FlowTracker();
        f.OnWolStarted();
        f.OnWolStage(WolStep.Confirm, StageStatus.Running, "공유기가 확인창을 표시했습니다. 펼쳐진 화면에서 [확인]을 누르세요.");
        Assert.Equal(StepState.Warning, f.Wake.State);
    }

    [Theory]
    [InlineData(WolMatchStatus.TargetNotFound, "대상 PC 없음")]
    [InlineData(WolMatchStatus.Ambiguous, "대상 구분 불가")]
    [InlineData(WolMatchStatus.MacMismatch, "MAC 불일치")]
    public void Wake_failures_are_explained(WolMatchStatus status, string expected)
    {
        var f = new FlowTracker();
        f.OnWolStarted();
        var match = new WolMatchResult(status, "msg", null, Array.Empty<string>());
        f.OnWolOutcome(WolOutcome.Fail(WolStep.Match, "msg", match), T);
        Assert.Equal(StepState.Failed, f.Wake.State);
        Assert.Equal(expected, f.Wake.Detail);
    }

    [Fact]
    public void Cancel_and_timeout_are_distinguished()
    {
        var f = new FlowTracker();
        f.OnWolStarted();
        f.OnWolOutcome(WolOutcome.Fail(WolStep.Click, "PC 켜기 작업이 취소되었습니다."), T);
        Assert.Equal(StepState.Warning, f.Wake.State);

        f.OnConnectStarted(ConnectMode.Direct, 10);
        f.OnConnectProgress(new ConnectProgress(ConnectStage.WaitingPort, "대기"), T);
        f.OnConnectOutcome(new ConnectOutcome(ConnectStage.Cancelled, false, "취소", TimeSpan.FromSeconds(3), false, ConnectMode.Direct), T);
        Assert.Equal(StepState.Warning, f.Boot.State);

        f.OnConnectStarted(ConnectMode.Direct, 10);
        f.OnConnectOutcome(new ConnectOutcome(ConnectStage.TimedOut, false, "시간 초과", TimeSpan.FromSeconds(10), false, ConnectMode.Direct), T);
        Assert.Equal(StepState.Failed, f.Boot.State);
        Assert.Contains("응답 없음", f.WolBootText);
    }

    [Fact]
    public void Vpn_failure_and_launch_failure_are_separate()
    {
        var f = new FlowTracker();
        f.OnConnectStarted(ConnectMode.Vpn, 60);
        f.OnConnectOutcome(new ConnectOutcome(ConnectStage.Failed, false, "VPN 연결 실패: 691", TimeSpan.FromSeconds(5), false, ConnectMode.Vpn), T);
        Assert.Equal(StepState.Failed, f.Boot.State);
        Assert.Equal("VPN 연결 실패", f.Boot.Detail);

        f.OnConnectStarted(ConnectMode.Direct, 60);
        f.OnConnectProgress(new ConnectProgress(ConnectStage.PortOpen, "열림"), T);
        f.OnConnectOutcome(new ConnectOutcome(ConnectStage.Failed, false, "오류: mstsc", TimeSpan.FromSeconds(5), true, ConnectMode.Direct), T);
        Assert.Equal(StepState.Done, f.Boot.State);
        Assert.Equal(StepState.Failed, f.Remote.State);
    }

    [Fact]
    public void New_wake_resets_later_steps()
    {
        var f = new FlowTracker();
        f.OnConnectStarted(ConnectMode.Direct, 10);
        f.OnConnectProgress(new ConnectProgress(ConnectStage.PortOpen, "열림"), T);
        f.OnConnectOutcome(new ConnectOutcome(ConnectStage.Done, true, "ok", TimeSpan.FromSeconds(1), true, ConnectMode.Direct), T);
        f.OnWolStarted();
        Assert.Equal(StepState.Pending, f.Boot.State);
        Assert.Equal(StepState.Pending, f.Remote.State);
    }
}

public class ModeOptionsTests
{
    [Fact]
    public void Options_check_only_their_own_settings()
    {
        var s = new AppSettings { PublicHost = "myhome.iptime.org", PublicRdpPort = 41000, VpnName = "", VpnDesktopIp = "" };
        var o = ModeOptions.For(s);
        Assert.Equal(2, o.Count);
        Assert.Equal(ConnectMode.Direct, o[0].Mode);
        Assert.True(o[0].Enabled);
        Assert.Equal("myhome.iptime.org:41000", o[0].Detail);
        Assert.Equal(ConnectMode.Vpn, o[1].Mode);
        Assert.False(o[1].Enabled);
        Assert.StartsWith("설정 필요", o[1].Detail);
    }

    [Fact]
    public void Vpn_option_shows_connection_and_internal_address()
    {
        var s = new AppSettings { PublicHost = "", VpnName = "HomeVPN", VpnDesktopIp = "192.168.0.10", VpnRdpPort = 3389 };
        var vpn = ModeOptions.Build(s, ConnectMode.Vpn);
        Assert.True(vpn.Enabled);
        Assert.Equal("HomeVPN 연결 후 192.168.0.10:3389", vpn.Detail);
        Assert.False(ModeOptions.Build(s, ConnectMode.Direct).Enabled);
    }
}

public class FlowResetTests
{
    [Fact]
    public void New_session_clears_previous_results_but_keeps_running_step()
    {
        var t = DateTimeOffset.Now;
        var f = new FlowTracker();
        f.OnConnectStarted(ConnectMode.Vpn, 60);
        f.OnConnectProgress(new ConnectProgress(ConnectStage.PortOpen, "열림"), t);
        f.OnConnectOutcome(new ConnectOutcome(ConnectStage.Done, true, "ok", TimeSpan.FromSeconds(3), true, ConnectMode.Vpn), t);
        Assert.Equal(StepState.Done, f.Remote.State);
        f.ResetForNewSession();
        Assert.Equal(StepState.Pending, f.Boot.State);
        Assert.Equal(StepState.Pending, f.Remote.State);
        Assert.Equal("PC 부팅: -", f.WolBootText);

        f.OnWolStarted();
        f.ResetForNewSession();
        Assert.Equal(StepState.Active, f.Wake.State);
    }
}

public class ActionGateTests
{
    [Theory]
    //          loggedIn busy   exiting preparing  wake   connect
    [InlineData(false,   false, false,  false,     false, true)]  // 로그인 전: [PC 접속]만(이미 켜진 PC에 바로 접속)
    [InlineData(true,    false, false,  true,      false, false)] // 로그인 뒤 관리 화면 준비 중: 셋 다 막음
    [InlineData(true,    false, false,  false,     true,  true)]  // 준비 완료
    [InlineData(true,    true,  false,  false,     false, false)] // 작업 중
    [InlineData(true,    false, true,   false,     false, false)] // 종료 중
    [InlineData(false,   false, false,  true,      false, true)]  // 준비 표시가 남아도 로그아웃이면 [PC 접속]은 막지 않음
    public void Buttons_follow_login_preparation_and_work_state(bool loggedIn, bool busy, bool exiting, bool preparing, bool wake, bool connect)
    {
        var g = ActionGate.Compute(loggedIn, busy, exiting, preparing);
        Assert.Equal(wake, g.Wake);
        Assert.Equal(wake, g.WakeConnect);
        Assert.Equal(connect, g.Connect);
    }

    [Fact]
    public void Login_step_shows_preparing_then_done()
    {
        var f = new FlowTracker();
        f.OnSession(SessionState.LoggedIn, DateTimeOffset.Now);
        f.OnAdminPreparing(true);
        Assert.Equal(StepState.Active, f.Login.State);
        Assert.Equal("관리 화면 준비 중", f.Login.Detail);
        f.OnAdminSelectNeeded(true);
        Assert.Equal(StepState.Warning, f.Login.State);
        f.OnAdminSelectNeeded(false);
        f.OnAdminPreparing(false);
        Assert.Equal(StepState.Done, f.Login.State);
        f.OnAdminPreparing(true);
        f.OnSession(SessionState.LoggedOut, null);
        f.OnSession(SessionState.LoggedIn, DateTimeOffset.Now);
        Assert.Equal(StepState.Done, f.Login.State); // 로그아웃하면 준비 표시가 지워짐
    }
}

public class CrdUiModelTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 2, 21, 5, 0, TimeSpan.FromHours(9));

    private static AppSettings Settings() => new()
    {
        PublicHost = "myhome.iptime.org", PublicRdpPort = 41000,
        VpnName = "HomeVPN", VpnDesktopIp = "192.168.0.10", VpnRdpPort = 3389,
        UseCrd = true, CrdHostId = "7f3a1b9c2d4e5f60", CrdBootCheckMode = "none",
    };

    [Fact]
    public void Crd_option_appears_only_when_turned_on()
    {
        var off = Settings();
        off.UseCrd = false;
        Assert.Equal(2, ModeOptions.For(off).Count);

        var on = ModeOptions.For(Settings());
        Assert.Equal(3, on.Count);
        Assert.Equal(ConnectMode.Crd, on[2].Mode);
        Assert.True(on[2].Enabled);
        Assert.Equal("크롬 원격 데스크톱", on[2].Title);
        // 기존 두 방식의 순서와 내용은 그대로다.
        Assert.Equal(ConnectMode.Direct, on[0].Mode);
        Assert.Equal(ConnectMode.Vpn, on[1].Mode);
    }

    [Fact]
    public void Crd_option_detail_says_what_will_happen()
    {
        var s = Settings();
        Assert.Equal("저장된 기기로 바로 연결 · 부팅 확인 없음", ModeOptions.Build(s, ConnectMode.Crd).Detail);
        s.CrdHostId = "";
        Assert.Equal("브라우저에서 기기 고르기 · 부팅 확인 없음", ModeOptions.Build(s, ConnectMode.Crd).Detail);
        s.CrdBootCheckMode = "direct";
        Assert.Contains("부팅 확인: 일반 접속 주소", ModeOptions.Build(s, ConnectMode.Crd).Detail);
        s.CrdBootCheckMode = "vpn";
        Assert.Contains("부팅 확인: VPN", ModeOptions.Build(s, ConnectMode.Crd).Detail);
    }

    [Fact]
    public void Crd_without_boot_check_does_not_claim_the_pc_was_checked()
    {
        var f = new FlowTracker();
        f.OnConnectStarted(ConnectMode.Crd, 180);
        Assert.Equal(StepState.Active, f.Boot.State);
        f.OnConnectProgress(new ConnectProgress(ConnectStage.BootCheckSkipped, "확인 없이 엽니다"), T);
        Assert.Equal(StepState.Pending, f.Boot.State);
        Assert.Equal("확인 안 함", f.Boot.Detail);
        Assert.Equal("PC 부팅: 확인 안 함", f.WolBootText);
        Assert.False(f.Tick(T.AddSeconds(30)));

        f.OnConnectProgress(new ConnectProgress(ConnectStage.LaunchingCrd, "여는 중"), T);
        Assert.Equal(StepState.Active, f.Remote.State);
        f.OnConnectOutcome(new ConnectOutcome(ConnectStage.Done, true, "열었습니다", TimeSpan.FromSeconds(1), false, ConnectMode.Crd), T);
        Assert.Equal(StepState.Done, f.Remote.State);
        Assert.Contains("크롬 원격 데스크톱 열림", f.Remote.Detail);
        // 확인하지 않은 단계는 완료로 바꾸지 않는다.
        Assert.Equal(StepState.Pending, f.Boot.State);
    }

    [Fact]
    public void Crd_with_boot_check_marks_the_boot_step_done()
    {
        var f = new FlowTracker();
        f.OnConnectStarted(ConnectMode.Crd, 180);
        f.OnConnectProgress(new ConnectProgress(ConnectStage.WaitingPort, "대기"), T);
        f.OnConnectProgress(new ConnectProgress(ConnectStage.PortOpen, "열림"), T.AddSeconds(20));
        Assert.Equal(StepState.Done, f.Boot.State);
        f.OnConnectOutcome(new ConnectOutcome(ConnectStage.Done, true, "열었습니다", TimeSpan.FromSeconds(21), true, ConnectMode.Crd), T.AddSeconds(21));
        Assert.Equal(StepState.Done, f.Boot.State);
        Assert.Equal(StepState.Done, f.Remote.State);
    }
}
