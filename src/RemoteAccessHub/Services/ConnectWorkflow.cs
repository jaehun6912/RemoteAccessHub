using System.Diagnostics;
using RemoteAccessHub.Core;

namespace RemoteAccessHub.Services;

public enum ConnectStage
{
    Validating,
    VpnConnecting,
    VpnConnected,
    WaitingPort,
    PortOpen,
    /// <summary>크롬 원격 데스크톱: 부팅 확인 없이 바로 여는 경우.</summary>
    BootCheckSkipped,
    LaunchingRdp,
    /// <summary>크롬 원격 데스크톱을 브라우저로 여는 중.</summary>
    LaunchingCrd,
    Done,
    Failed,
    Cancelled,
    TimedOut,
}

public sealed record ConnectProgress(ConnectStage Stage, string Message);

public sealed record ConnectOutcome(ConnectStage Stage, bool Success, string Message, TimeSpan Elapsed, bool PcRespondedOnPort, ConnectMode Mode)
{
    public bool IsCancelled => Stage == ConnectStage.Cancelled;
    public bool IsTimedOut => Stage == ConnectStage.TimedOut;
}

/// <summary>
/// [PC 접속] 흐름: (VPN 모드면 VPN 확보) → RDP 포트 응답 대기 → mstsc 실행.
/// VPN 실패 시 일반 접속으로 전환하지 않으며, 취소·종료 시 VPN을 끊지 않는다.
/// </summary>
public sealed class ConnectWorkflow
{
    private readonly IVpnService _vpn;
    private readonly IPortProbe _probe;
    private readonly IRdpLauncher _rdp;
    private readonly ICrdLauncher _crd;
    private readonly AppLog _log;

    public TimeSpan PortAttemptTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan PortRetryInterval { get; init; } = TimeSpan.FromSeconds(3);

    public ConnectWorkflow(IVpnService vpn, IPortProbe probe, IRdpLauncher rdp, ICrdLauncher crd, AppLog log)
    {
        _vpn = vpn;
        _probe = probe;
        _rdp = rdp;
        _crd = crd;
        _log = log;
    }

    public async Task<ConnectOutcome> RunAsync(AppSettings settings, ConnectMode mode, IProgress<ConnectProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var responded = false;
        try
        {
            progress?.Report(new(ConnectStage.Validating, "설정 확인 중..."));
            var errors = settings.ValidateConnect(mode);
            if (errors.Count > 0)
                return new(ConnectStage.Failed, false, string.Join("\n", errors), sw.Elapsed, false, mode);

            // 크롬 원격 데스크톱: 열어 둘 포트가 없으므로 부팅 확인은 설정한 방법으로만 한다(안 할 수도 있음).
            if (mode == ConnectMode.Crd)
            {
                var check = settings.CrdCheck;
                if (check == CrdBootCheck.Vpn && await EnsureVpnAsync(settings, progress, sw, ct) is { } vpnFail) return vpnFail;
                if (check == CrdBootCheck.None)
                {
                    progress?.Report(new(ConnectStage.BootCheckSkipped, "부팅 확인 없이 크롬 원격 데스크톱을 엽니다(열어 둔 포트가 없어 확인할 수 없음)."));
                }
                else
                {
                    var crdHost = check == CrdBootCheck.Vpn ? settings.VpnDesktopIp.Trim() : settings.PublicHost.Trim();
                    var crdPort = check == CrdBootCheck.Vpn ? settings.VpnRdpPort : settings.PublicRdpPort;
                    var wait = await WaitForPortAsync(settings, crdHost, crdPort, progress, sw, mode, ct);
                    if (wait != null) return wait;
                    responded = true;
                }
                progress?.Report(new(ConnectStage.LaunchingCrd, "크롬 원격 데스크톱을 여는 중..."));
                _crd.Open(settings.CrdHostId);
                var where = InputRules.NormalizeCrdHostId(settings.CrdHostId) == null ? "기기 목록" : "저장된 기기";
                return new(ConnectStage.Done, true,
                    $"크롬 원격 데스크톱을 열었습니다({where}). 브라우저에서 구글 로그인과 PIN 입력은 직접 하세요.",
                    sw.Elapsed, responded, mode);
            }

            string host;
            int port;
            if (mode == ConnectMode.Vpn)
            {
                if (await EnsureVpnAsync(settings, progress, sw, ct) is { } fail) return fail;
                host = settings.VpnDesktopIp.Trim();
                port = settings.VpnRdpPort;
            }
            else
            {
                host = settings.PublicHost.Trim();
                port = settings.PublicRdpPort;
            }

            // 포트 응답 대기 (= 실제 PC 부팅 확인)
            var timeout = await WaitForPortAsync(settings, host, port, progress, sw, mode, ct);
            if (timeout != null) return timeout;
            responded = true;

            progress?.Report(new(ConnectStage.PortOpen, "RDP 포트 응답 확인. 원격 데스크톱을 실행합니다."));
            _log.Info($"RDP 포트 응답 확인: {InputRules.HostPort(host, port)}");
            progress?.Report(new(ConnectStage.LaunchingRdp, "mstsc 실행 중..."));
            _rdp.Launch(host, port, settings.RdpFullScreen);
            return new(ConnectStage.Done, true, "원격 데스크톱을 실행했습니다. 자격 증명은 Windows 창에서 입력하세요.", sw.Elapsed, true, mode);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("PC 접속 작업이 취소되었습니다. (VPN은 그대로 둡니다)");
            return new(ConnectStage.Cancelled, false, "작업이 취소되었습니다. 기존 VPN 연결은 끊지 않았습니다.", sw.Elapsed, responded, mode);
        }
        catch (Exception ex)
        {
            _log.Error("PC 접속 오류: " + ex.Message);
            return new(ConnectStage.Failed, false, "오류: " + ex.Message, sw.Elapsed, responded, mode);
        }
    }

    /// <summary>VPN이 연결되어 있는지 확인하고, 아니면 연결한다. 실패하면 그 결과를, 성공하면 null을 돌려준다.</summary>
    private async Task<ConnectOutcome?> EnsureVpnAsync(AppSettings settings, IProgress<ConnectProgress>? progress, Stopwatch sw, CancellationToken ct)
    {
        var mode = ConnectMode.Vpn;
        var name = settings.VpnName.Trim();
        if (_vpn.IsConnected(name))
        {
            _log.Info($"VPN '{name}' 이미 연결됨 → 재사용");
            progress?.Report(new(ConnectStage.VpnConnected, $"VPN '{name}' 연결 확인(기존 연결 재사용)"));
            return null;
        }
        progress?.Report(new(ConnectStage.VpnConnecting, $"VPN '{name}' 연결 중..."));
        var vpnProgress = new Progress<string>(m => progress?.Report(new(ConnectStage.VpnConnecting, m)));
        var r = await _vpn.ConnectAsync(name, TimeSpan.FromSeconds(settings.VpnWaitSeconds), vpnProgress, ct);
        if (!r.Success)
        {
            _log.Error("VPN 연결 실패: " + r.Message);
            return new(ConnectStage.Failed, false, "VPN 연결 실패: " + r.Message + "\n(일반 접속으로 자동 전환하지 않습니다.)", sw.Elapsed, false, mode);
        }
        if (!_vpn.IsConnected(name))
            return new(ConnectStage.Failed, false, $"VPN '{name}'이(가) 연결된 것으로 확인되지 않습니다.", sw.Elapsed, false, mode);
        _log.Info(r.Message);
        progress?.Report(new(ConnectStage.VpnConnected, r.Message));
        return null;
    }

    /// <summary>포트가 응답할 때까지 기다린다(= 실제 부팅 확인). 시간이 초과되면 그 결과를, 응답하면 null을 돌려준다.</summary>
    private async Task<ConnectOutcome?> WaitForPortAsync(AppSettings settings, string host, int port, IProgress<ConnectProgress>? progress, Stopwatch sw, ConnectMode mode, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(settings.BootWaitSeconds);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            var remaining = deadline - DateTime.UtcNow;
            progress?.Report(new(ConnectStage.WaitingPort, $"{InputRules.HostPort(host, port)} RDP 포트 응답 대기 중... (시도 {attempt}, 남은 시간 {Math.Max(0, remaining.TotalSeconds):0}초)"));
            if (await _probe.IsOpenAsync(host, port, PortAttemptTimeout, ct)) return null;
            if (DateTime.UtcNow >= deadline)
            {
                _log.Warn($"RDP 포트 응답 없음: {InputRules.HostPort(host, port)} ({settings.BootWaitSeconds}초)");
                return new(ConnectStage.TimedOut, false, $"{settings.BootWaitSeconds}초 동안 {InputRules.HostPort(host, port)}의 RDP 포트가 응답하지 않았습니다. PC가 아직 켜지지 않았거나 포트/방화벽 설정을 확인하세요.", sw.Elapsed, false, mode);
            }
            await Task.Delay(PortRetryInterval, ct);
        }
    }
}
