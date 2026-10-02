using RemoteAccessHub.Core;
using RemoteAccessHub.SelfTest;
using RemoteAccessHub.Services;
using RemoteAccessHub.UI;
using Xunit;

namespace RemoteAccessHub.Tests;

/// <summary>PC 전원 상태 배지의 판정 규칙.</summary>
public class PowerRulesTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 3, 2, 31, 0, TimeSpan.FromHours(9));

    private static AppSettings Settings(string mode = "auto") => new()
    {
        PowerCheckMode = mode,
        PublicHost = "myhome.iptime.org", PublicRdpPort = 41000,
        VpnName = "HomeVPN", VpnDesktopIp = "192.168.0.10", VpnRdpPort = 3389,
    };

    [Fact]
    public void Auto_prefers_the_vpn_address_only_while_the_vpn_is_connected()
    {
        Assert.Equal(new PowerTarget("myhome.iptime.org", 41000, "일반 접속 주소"), PowerRules.Target(Settings(), vpnConnected: false));
        Assert.Equal(new PowerTarget("192.168.0.10", 3389, "VPN 내부 IP"), PowerRules.Target(Settings(), vpnConnected: true));
    }

    [Fact]
    public void Vpn_mode_never_asks_for_a_vpn_connection()
    {
        // VPN이 연결되어 있지 않으면 확인하지 않는다(확인하려고 VPN을 연결하지 않는다).
        Assert.Null(PowerRules.Target(Settings("vpn"), vpnConnected: false));
        Assert.Equal("VPN 내부 IP", PowerRules.Target(Settings("vpn"), vpnConnected: true)!.Via);
    }

    [Fact]
    public void Direct_mode_ignores_the_vpn()
    {
        Assert.Equal("일반 접속 주소", PowerRules.Target(Settings("direct"), vpnConnected: true)!.Via);
        var noDirect = Settings("direct");
        noDirect.PublicHost = "";
        Assert.Null(PowerRules.Target(noDirect, vpnConnected: true));
    }

    [Fact]
    public void Off_and_missing_settings_turn_the_badge_off()
    {
        Assert.Null(PowerRules.Target(Settings("off"), vpnConnected: true));
        var empty = new AppSettings();
        Assert.Null(PowerRules.Target(empty, vpnConnected: false));
        Assert.Equal(PcPowerState.Disabled, PowerRules.Decide(null, true, true, T).State);
    }

    [Fact]
    public void An_answering_port_means_the_pc_is_on()
    {
        var target = new PowerTarget("myhome.iptime.org", 41000, "일반 접속 주소");
        var s = PowerRules.Decide(target, portOpen: true, routerLoggedIn: false, T);
        Assert.Equal(PcPowerState.On, s.State);
        Assert.Equal(T, s.CheckedAt);
        Assert.Equal("PC 켜짐 · 02:31", s.PillText);
    }

    [Fact]
    public void No_answer_is_never_reported_as_powered_off()
    {
        var target = new PowerTarget("myhome.iptime.org", 41000, "일반 접속 주소");
        var offline = PowerRules.Decide(target, portOpen: false, routerLoggedIn: false, T);
        Assert.Equal(PcPowerState.NoAnswer, offline.State);
        Assert.Equal("PC 응답 없음", offline.PillText);
        Assert.DoesNotContain("꺼짐", offline.PillText);
        Assert.Contains("포트가 막힘", offline.Detail);

        // 공유기까지 닿아 있으면 그 사실을 함께 알려 준다.
        var viaRouter = PowerRules.Decide(target, portOpen: false, routerLoggedIn: true, T);
        Assert.Equal(PcPowerState.NoAnswer, viaRouter.State);
        Assert.Contains("공유기는 연결됨", viaRouter.Detail);
    }

    [Fact]
    public void Not_checked_yet_stays_unknown()
    {
        var target = new PowerTarget("myhome.iptime.org", 41000, "일반 접속 주소");
        var s = PowerRules.Decide(target, portOpen: null, routerLoggedIn: true, T);
        Assert.Equal(PcPowerState.Unknown, s.State);
        Assert.Null(s.CheckedAt);
        Assert.Equal("PC 확인 전", s.PillText);
    }

    [Theory]
    [InlineData("auto", PowerSource.Auto)]
    [InlineData("DIRECT", PowerSource.Direct)]
    [InlineData("vpn", PowerSource.Vpn)]
    [InlineData("off", PowerSource.Off)]
    [InlineData("", PowerSource.Auto)]
    [InlineData("무엇이든", PowerSource.Auto)]
    public void Check_mode_is_parsed(string stored, PowerSource expected)
        => Assert.Equal(expected, new AppSettings { PowerCheckMode = stored }.PowerCheck);

    [Theory]
    [InlineData(10)]
    [InlineData(5000)]
    public void Out_of_range_interval_is_reported(int seconds)
    {
        var s = Settings();
        s.RouterUrl = "http://10.0.0.1:8080/";
        s.WolPcName = "PC-1";
        s.PowerCheckSeconds = seconds;
        Assert.Contains("전원 확인 주기", string.Join("\n", s.ValidateRouter()));
    }
}

public class PowerWatcherTests
{
    private static (PowerWatcher W, FakePortProbe Port, AppSettings S) Make(bool vpnConnected = false, bool routerLoggedIn = false, bool paused = false)
    {
        var s = new AppSettings
        {
            PublicHost = "myhome.iptime.org", PublicRdpPort = 41000,
            VpnName = "HomeVPN", VpnDesktopIp = "192.168.0.10", VpnRdpPort = 3389,
            PowerCheckSeconds = 15,
        };
        var port = new FakePortProbe { AttemptDelay = TimeSpan.FromMilliseconds(1) };
        var w = new PowerWatcher(port, new AppLog(null), () => s, () => vpnConnected, () => routerLoggedIn, () => paused)
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(20),
        };
        return (w, port, s);
    }

    [Fact]
    public async Task Check_reports_on_and_raises_the_change()
    {
        var (w, port, _) = Make(routerLoggedIn: true);
        var seen = new List<PcPowerState>();
        w.Changed += st => seen.Add(st.State);
        await w.CheckNowAsync();
        Assert.Equal(PcPowerState.On, w.Status.State);
        Assert.Equal(1, port.Attempts);
        Assert.Equal("myhome.iptime.org:41000", port.Targets[0]);
        Assert.Contains(PcPowerState.On, seen);
    }

    [Fact]
    public async Task Closed_port_is_no_answer_not_off()
    {
        var (w, port, _) = Make(routerLoggedIn: true);
        port.NeverOpen = true;
        await w.CheckNowAsync();
        Assert.Equal(PcPowerState.NoAnswer, w.Status.State);
        Assert.Equal("PC 응답 없음", w.Status.PillText);
    }

    [Fact]
    public async Task Turned_off_in_settings_does_not_touch_the_network()
    {
        var (w, port, s) = Make();
        s.PowerCheckMode = "off";
        await w.CheckNowAsync();
        Assert.Equal(PcPowerState.Disabled, w.Status.State);
        Assert.Equal(0, port.Attempts);
        Assert.Null(w.CurrentTarget());
    }

    [Fact]
    public async Task While_another_job_runs_the_periodic_check_stays_out_of_the_way()
    {
        var (w, port, _) = Make(paused: true);
        w.CheckSoon();
        w.Tick();
        await Task.Delay(100);
        Assert.Equal(0, port.Attempts);

        // 사용자가 배지를 누르면 작업 중이어도 확인한다.
        await w.CheckNowAsync();
        Assert.Equal(1, port.Attempts);
    }

    [Fact]
    public async Task Connected_vpn_switches_the_target_to_the_internal_address()
    {
        var (w, port, _) = Make(vpnConnected: true);
        await w.CheckNowAsync();
        Assert.Equal("192.168.0.10:3389", port.Targets[0]);
        Assert.Equal("VPN 내부 IP", w.CurrentTarget()!.Via);
    }
}

public class ConnectBlinkTests
{
    [Fact]
    public void Blinks_only_when_the_pc_is_known_to_be_on()
    {
        Assert.True(ActionGate.ShouldBlinkConnect(true, PcPowerState.On, connectEnabled: true, busy: false, exiting: false));
        // 응답 없음은 꺼진 것인지 포트가 막힌 것인지 모르므로 깜빡이지 않는다.
        foreach (var state in new[] { PcPowerState.NoAnswer, PcPowerState.Unknown, PcPowerState.Checking, PcPowerState.Disabled })
            Assert.False(ActionGate.ShouldBlinkConnect(true, state, connectEnabled: true, busy: false, exiting: false));
    }

    [Fact]
    public void Blink_stops_while_the_button_cannot_be_pressed()
    {
        Assert.False(ActionGate.ShouldBlinkConnect(true, PcPowerState.On, connectEnabled: false, busy: false, exiting: false));
        Assert.False(ActionGate.ShouldBlinkConnect(true, PcPowerState.On, connectEnabled: true, busy: true, exiting: false));
        Assert.False(ActionGate.ShouldBlinkConnect(true, PcPowerState.On, connectEnabled: true, busy: false, exiting: true));
    }

    [Fact]
    public void Setting_turns_it_off()
    {
        Assert.False(ActionGate.ShouldBlinkConnect(false, PcPowerState.On, connectEnabled: true, busy: false, exiting: false));
        Assert.True(new AppSettings().BlinkConnectWhenPcOn);
    }

    [Fact]
    public void Setting_survives_export_and_import()
    {
        var s = new AppSettings { BlinkConnectWhenPcOn = false, PowerCheckMode = "direct" };
        var back = AppSettings.FromExportJson(s.ToExportJson(), out var error);
        Assert.Null(error);
        Assert.False(back!.BlinkConnectWhenPcOn);
        Assert.Equal(PowerSource.Direct, back.PowerCheck);
    }
}
