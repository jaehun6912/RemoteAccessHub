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
        var wf = new ConnectWorkflow(vpn, port, rdp, log) { PortRetryInterval = TimeSpan.FromMilliseconds(20), PortAttemptTimeout = TimeSpan.FromMilliseconds(50) };
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
        var wf2 = new ConnectWorkflow(new FakeVpnService(), port, rdp, new AppLog(null)) { PortRetryInterval = TimeSpan.FromMilliseconds(5), PortAttemptTimeout = TimeSpan.FromMilliseconds(5) };
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
