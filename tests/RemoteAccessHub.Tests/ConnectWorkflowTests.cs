using RemoteAccessHub.Core;
using RemoteAccessHub.SelfTest;
using RemoteAccessHub.Services;
using Xunit;

namespace RemoteAccessHub.Tests;

public class ConnectWorkflowTests
{
    private static (ConnectWorkflow Wf, FakeVpnService Vpn, FakePortProbe Port, FakeRdpLauncher Rdp) Make()
    {
        var log = new AppLog(null);
        var vpn = new FakeVpnService();
        var port = new FakePortProbe { AttemptDelay = TimeSpan.FromMilliseconds(10) };
        var rdp = new FakeRdpLauncher();
        var wf = new ConnectWorkflow(vpn, port, rdp, new FakeCrdLauncher(), log) { PortRetryInterval = TimeSpan.FromMilliseconds(20), PortAttemptTimeout = TimeSpan.FromMilliseconds(50) };
        return (wf, vpn, port, rdp);
    }

    private static AppSettings Settings() => new()
    {
        PublicHost = "myhome.iptime.org", PublicRdpPort = 41000,
        VpnName = "HomeVPN", VpnDesktopIp = "192.168.0.10", VpnRdpPort = 3389,
        BootWaitSeconds = 10, VpnWaitSeconds = 10, RdpFullScreen = true,
    };

    [Fact]
    public async Task Direct_mode_waits_port_then_launches()
    {
        var (wf, _, port, rdp) = Make();
        port.OpenAfterAttempts = 3;
        var r = await wf.RunAsync(Settings(), ConnectMode.Direct, null, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Equal(ConnectStage.Done, r.Stage);
        Assert.Equal(3, port.Attempts);
        Assert.Single(rdp.Launches);
        Assert.Equal(("myhome.iptime.org", 41000, true), rdp.Launches[0]);
    }

    [Fact]
    public async Task Direct_mode_ignores_missing_vpn_settings()
    {
        var (wf, _, _, rdp) = Make();
        var s = Settings();
        s.VpnName = ""; s.VpnDesktopIp = "";
        var r = await wf.RunAsync(s, ConnectMode.Direct, null, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Single(rdp.Launches);
    }

    [Fact]
    public async Task Vpn_failure_does_not_fall_back_to_direct()
    {
        var (wf, vpn, port, rdp) = Make();
        vpn.ConnectSucceeds = false;
        var r = await wf.RunAsync(Settings(), ConnectMode.Vpn, null, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Equal(ConnectStage.Failed, r.Stage);
        Assert.Empty(rdp.Launches);
        Assert.Equal(0, port.Attempts);
        Assert.Contains("자동 전환하지 않습니다", r.Message);
        Assert.False(vpn.DisconnectCalled);
    }

    [Fact]
    public async Task Vpn_connected_is_reused_and_internal_ip_used()
    {
        var (wf, vpn, _, rdp) = Make();
        vpn.Connected.Add("HomeVPN");
        var r = await wf.RunAsync(Settings(), ConnectMode.Vpn, null, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Equal(0, vpn.ConnectCalls);
        Assert.Equal("192.168.0.10", rdp.Launches[0].Host);
        Assert.Equal(3389, rdp.Launches[0].Port);
    }

    [Fact]
    public async Task Boot_timeout_is_reported()
    {
        var (wf, _, port, rdp) = Make();
        port.NeverOpen = true;
        var s = Settings();
        s.BootWaitSeconds = 10; // 최소값; 재시도 간격이 짧아 곧 초과
        var wf2 = new ConnectWorkflow(new FakeVpnService(), port, rdp, new FakeCrdLauncher(), new AppLog(null)) { PortRetryInterval = TimeSpan.FromMilliseconds(5), PortAttemptTimeout = TimeSpan.FromMilliseconds(5) };
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var r = await wf2.RunAsync(s, ConnectMode.Direct, null, cts.Token);
        Assert.Equal(ConnectStage.TimedOut, r.Stage);
        Assert.False(r.Success);
        Assert.Empty(rdp.Launches);
    }

    [Fact]
    public async Task Cancel_leaves_vpn_connected()
    {
        var (wf, vpn, port, rdp) = Make();
        vpn.Connected.Add("HomeVPN");
        port.NeverOpen = true;
        var s = Settings();
        s.BootWaitSeconds = 3600;
        using var cts = new CancellationTokenSource();
        var task = wf.RunAsync(s, ConnectMode.Vpn, null, cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        var r = await task;
        Assert.Equal(ConnectStage.Cancelled, r.Stage);
        Assert.True(vpn.IsConnected("HomeVPN"));
        Assert.False(vpn.DisconnectCalled);
        Assert.Empty(rdp.Launches);
    }

    [Fact]
    public async Task Invalid_settings_fail_before_any_action()
    {
        var (wf, vpn, port, rdp) = Make();
        var s = Settings();
        s.PublicHost = "bad host;calc";
        var r = await wf.RunAsync(s, ConnectMode.Direct, null, CancellationToken.None);
        Assert.Equal(ConnectStage.Failed, r.Stage);
        Assert.Equal(0, port.Attempts);
        Assert.Equal(0, vpn.ConnectCalls);
        Assert.Empty(rdp.Launches);
    }

    [Fact]
    public void Rdp_launcher_rejects_invalid_input_without_starting_process()
    {
        var l = new RdpLauncher(new AppLog(null));
        Assert.Throws<ArgumentException>(() => l.Launch("host name with space", 3389, false));
        Assert.Throws<ArgumentException>(() => l.Launch("host", 0, false));
    }
}

public class CrdConnectWorkflowTests
{
    private const string HostId = "7f3a1b9c2d4e5f60";

    private static (ConnectWorkflow Wf, FakeVpnService Vpn, FakePortProbe Port, FakeRdpLauncher Rdp, FakeCrdLauncher Crd) Make()
    {
        var vpn = new FakeVpnService();
        var port = new FakePortProbe { AttemptDelay = TimeSpan.FromMilliseconds(10) };
        var rdp = new FakeRdpLauncher();
        var crd = new FakeCrdLauncher();
        var wf = new ConnectWorkflow(vpn, port, rdp, crd, new AppLog(null))
        {
            PortRetryInterval = TimeSpan.FromMilliseconds(5),
            PortAttemptTimeout = TimeSpan.FromMilliseconds(5),
        };
        return (wf, vpn, port, rdp, crd);
    }

    private static AppSettings Settings(string check = "none") => new()
    {
        UseCrd = true, CrdHostId = HostId, CrdBootCheckMode = check,
        PublicHost = "myhome.iptime.org", PublicRdpPort = 41000,
        VpnName = "HomeVPN", VpnDesktopIp = "192.168.0.10", VpnRdpPort = 3389,
        BootWaitSeconds = 10, VpnWaitSeconds = 10,
    };

    [Fact]
    public async Task Crd_without_boot_check_opens_browser_without_probing_or_vpn()
    {
        var (wf, vpn, port, rdp, crd) = Make();
        var stages = new List<ConnectStage>();
        var r = await wf.RunAsync(Settings(), ConnectMode.Crd, new Progress<ConnectProgress>(p => stages.Add(p.Stage)), CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Equal(ConnectStage.Done, r.Stage);
        Assert.Equal(ConnectMode.Crd, r.Mode);
        // 포트를 확인하지 않았으므로 "부팅 확인됨"이라고 주장하지 않는다.
        Assert.False(r.PcRespondedOnPort);
        Assert.Equal(0, port.Attempts);
        Assert.Equal(0, vpn.ConnectCalls);
        Assert.Empty(rdp.Launches);
        Assert.Single(crd.Opened);
        Assert.Equal($"{CrdLauncher.AccessUrl}/session/{HostId}", crd.Opened[0]);
        Assert.Contains(ConnectStage.BootCheckSkipped, stages);
    }

    [Fact]
    public async Task Crd_with_direct_boot_check_waits_for_public_port()
    {
        var (wf, vpn, port, _, crd) = Make();
        port.OpenAfterAttempts = 2;
        var r = await wf.RunAsync(Settings("direct"), ConnectMode.Crd, null, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.True(r.PcRespondedOnPort);
        Assert.Equal(2, port.Attempts);
        Assert.Equal("myhome.iptime.org:41000", port.Targets[0]);
        Assert.Equal(0, vpn.ConnectCalls);
        Assert.Single(crd.Opened);
    }

    [Fact]
    public async Task Crd_with_vpn_boot_check_connects_vpn_and_probes_internal_ip()
    {
        var (wf, vpn, port, _, crd) = Make();
        var r = await wf.RunAsync(Settings("vpn"), ConnectMode.Crd, null, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Equal(1, vpn.ConnectCalls);
        Assert.Equal("192.168.0.10:3389", port.Targets[0]);
        Assert.Single(crd.Opened);
    }

    [Fact]
    public async Task Crd_vpn_failure_does_not_open_browser_or_fall_back()
    {
        var (wf, vpn, port, _, crd) = Make();
        vpn.ConnectSucceeds = false;
        var r = await wf.RunAsync(Settings("vpn"), ConnectMode.Crd, null, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Equal(ConnectStage.Failed, r.Stage);
        Assert.Contains("자동 전환하지 않습니다", r.Message);
        Assert.Equal(0, port.Attempts);
        Assert.Empty(crd.Opened);
        Assert.False(vpn.DisconnectCalled);
    }

    [Fact]
    public async Task Crd_boot_check_timeout_does_not_open_browser()
    {
        var (wf, _, port, _, crd) = Make();
        port.NeverOpen = true;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var r = await wf.RunAsync(Settings("direct"), ConnectMode.Crd, null, cts.Token);
        Assert.Equal(ConnectStage.TimedOut, r.Stage);
        Assert.False(r.Success);
        Assert.Empty(crd.Opened);
    }

    [Fact]
    public async Task Crd_without_host_id_opens_device_list()
    {
        var (wf, _, _, _, crd) = Make();
        var s = Settings();
        s.CrdHostId = "";
        var r = await wf.RunAsync(s, ConnectMode.Crd, null, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Equal(CrdLauncher.AccessUrl, crd.Opened[0]);
        Assert.Contains("기기 목록", r.Message);
    }

    [Fact]
    public async Task Crd_turned_off_in_settings_fails_before_opening_browser()
    {
        var (wf, _, _, _, crd) = Make();
        var s = Settings();
        s.UseCrd = false;
        var r = await wf.RunAsync(s, ConnectMode.Crd, null, CancellationToken.None);
        Assert.Equal(ConnectStage.Failed, r.Stage);
        Assert.Empty(crd.Opened);
    }

    [Fact]
    public void Crd_url_is_built_only_from_a_valid_host_id()
    {
        Assert.Equal($"{CrdLauncher.AccessUrl}/session/{HostId}", CrdLauncher.BuildUrl(HostId));
        // 주소 전체를 붙여넣어도 기기 ID만 뽑아 쓴다.
        Assert.Equal($"{CrdLauncher.AccessUrl}/session/{HostId}", CrdLauncher.BuildUrl($"https://remotedesktop.google.com/access/session/{HostId}?hl=ko"));
        // 형식이 어긋나면 주소에 넣지 않고 기기 목록을 연다.
        Assert.Equal(CrdLauncher.AccessUrl, CrdLauncher.BuildUrl(""));
        Assert.Equal(CrdLauncher.AccessUrl, CrdLauncher.BuildUrl(null));
        Assert.Equal(CrdLauncher.AccessUrl, CrdLauncher.BuildUrl("7f3a1b9c2d4e5f60/../evil?x=1"));
        Assert.Equal(CrdLauncher.AccessUrl, CrdLauncher.BuildUrl("javascript:alert(1)"));
    }
}
