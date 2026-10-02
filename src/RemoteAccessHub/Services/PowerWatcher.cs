using RemoteAccessHub.Core;

namespace RemoteAccessHub.Services;

/// <summary>PC 전원 상태를 확인할 대상.</summary>
/// <param name="Via">사용자에게 보여 줄 경로 이름. 실제 주소는 메시지에 쓰지 않는다.</param>
public sealed record PowerTarget(string Host, int Port, string Via);

public enum PcPowerState
{
    /// <summary>확인하지 않음(설정이 꺼져 있거나, 확인할 주소가 없음).</summary>
    Disabled,
    /// <summary>아직 확인 전.</summary>
    Unknown,
    /// <summary>확인 중.</summary>
    Checking,
    /// <summary>포트가 응답함 = 켜져 있음(확실).</summary>
    On,
    /// <summary>포트가 응답하지 않음. 꺼졌을 수도, 포트·방화벽 때문일 수도 있다.</summary>
    NoAnswer,
}

/// <param name="Detail">상태 옆에 보여 줄 짧은 설명.</param>
/// <param name="CheckedAt">마지막으로 확인한 시각(확인한 적이 없으면 null).</param>
public sealed record PcPowerStatus(PcPowerState State, string Detail, DateTimeOffset? CheckedAt)
{
    public static readonly PcPowerStatus Disabled = new(PcPowerState.Disabled, "확인 안 함", null);
    public static readonly PcPowerStatus Unknown = new(PcPowerState.Unknown, "확인 전", null);

    /// <summary>배지에 쓸 한 줄.</summary>
    public string PillText => State switch
    {
        PcPowerState.On => CheckedAt is { } t ? $"PC 켜짐 · {t:HH:mm}" : "PC 켜짐",
        PcPowerState.NoAnswer => "PC 응답 없음",
        PcPowerState.Checking => "PC 확인 중",
        PcPowerState.Disabled => "PC 확인 안 함",
        _ => "PC 확인 전",
    };
}

/// <summary>
/// PC 전원 상태 판정 규칙. 시간·네트워크에 기대지 않는 순수 함수라 그대로 검사할 수 있다.
/// </summary>
public static class PowerRules
{
    /// <summary>
    /// 지금 확인할 대상. VPN 확인은 <b>이미 연결되어 있을 때만</b> 하고, 확인하려고 VPN을 연결하지 않는다.
    /// 확인할 곳이 없으면 null.
    /// </summary>
    public static PowerTarget? Target(AppSettings s, bool vpnConnected)
    {
        var direct = InputRules.IsValidHost(s.PublicHost) && InputRules.IsValidPort(s.PublicRdpPort)
            ? new PowerTarget(s.PublicHost.Trim(), s.PublicRdpPort, "일반 접속 주소")
            : null;
        var vpn = vpnConnected && InputRules.IsValidHost(s.VpnDesktopIp) && InputRules.IsValidPort(s.VpnRdpPort)
            ? new PowerTarget(s.VpnDesktopIp.Trim(), s.VpnRdpPort, "VPN 내부 IP")
            : null;
        return s.PowerCheck switch
        {
            PowerSource.Off => null,
            PowerSource.Direct => direct,
            PowerSource.Vpn => vpn,
            // 자동: VPN이 이미 연결되어 있으면 내부 주소가 더 확실하다.
            _ => vpn ?? direct,
        };
    }

    /// <summary>
    /// 확인 결과를 상태로 바꾼다. 포트가 응답하면 켜진 것이 확실하지만,
    /// 응답이 없다고 꺼졌다고 단정하지 않는다(포트·방화벽일 수 있다).
    /// </summary>
    public static PcPowerStatus Decide(PowerTarget? target, bool? portOpen, bool routerLoggedIn, DateTimeOffset now)
    {
        if (target == null) return PcPowerStatus.Disabled;
        if (portOpen == null) return PcPowerStatus.Unknown;
        if (portOpen.Value) return new(PcPowerState.On, $"{target.Via} 응답", now);
        var why = routerLoggedIn
            ? "공유기는 연결됨 · 꺼져 있거나 포트가 막힘"
            : "꺼져 있거나 포트가 막힘";
        return new(PcPowerState.NoAnswer, why, now);
    }
}

/// <summary>
/// 설정한 주기마다 PC 포트 응답을 확인해 전원 상태를 알려 준다.
/// 다른 작업(PC 켜기·접속)이 진행 중이면 쉬고, VPN을 스스로 연결하지 않는다.
/// </summary>
public sealed class PowerWatcher : IDisposable
{
    private readonly IPortProbe _probe;
    private readonly AppLog _log;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _vpnConnected;
    private readonly Func<bool> _routerLoggedIn;
    private readonly Func<bool> _paused;

    private CancellationTokenSource? _cts;
    private int _running;
    private DateTime _nextDue = DateTime.MinValue;

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public PcPowerStatus Status { get; private set; } = PcPowerStatus.Unknown;

    /// <summary>상태가 바뀌면 호출된다(확인을 시작한 스레드에서 호출하므로 화면 갱신은 받는 쪽에서 맞춘다).</summary>
    public event Action<PcPowerStatus>? Changed;

    public PowerWatcher(IPortProbe probe, AppLog log, Func<AppSettings> settings,
        Func<bool> vpnConnected, Func<bool> routerLoggedIn, Func<bool> paused)
    {
        _probe = probe;
        _log = log;
        _settings = settings;
        _vpnConnected = vpnConnected;
        _routerLoggedIn = routerLoggedIn;
        _paused = paused;
    }

    /// <summary>지금 확인할 대상(없으면 null).</summary>
    public PowerTarget? CurrentTarget() => PowerRules.Target(_settings(), Safe(_vpnConnected));

    /// <summary>주기가 됐으면 한 번 확인한다. 화면 타이머에서 부른다.</summary>
    public void Tick()
    {
        if (DateTime.UtcNow < _nextDue) return;
        _ = CheckAsync(force: false);
    }

    /// <summary>다음 확인을 앞당긴다(설정이 바뀌었거나 PC를 켠 직후).</summary>
    public void CheckSoon(TimeSpan? after = null) => _nextDue = DateTime.UtcNow + (after ?? TimeSpan.Zero);

    /// <summary>지금 확인한다(배지를 눌렀을 때).</summary>
    public Task CheckNowAsync() => CheckAsync(force: true);

    private async Task CheckAsync(bool force)
    {
        var s = _settings();
        var target = PowerRules.Target(s, Safe(_vpnConnected));
        if (target == null)
        {
            _nextDue = DateTime.UtcNow + Interval(s);
            Apply(PcPowerStatus.Disabled);
            return;
        }
        // 접속·PC 켜기 작업 중에는 그 작업이 이미 포트를 확인하므로 끼어들지 않는다.
        if (!force && Safe(_paused))
        {
            _nextDue = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            return;
        }
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            if (Status.State is PcPowerState.Unknown or PcPowerState.Disabled)
                Apply(new PcPowerStatus(PcPowerState.Checking, target.Via + " 확인 중", Status.CheckedAt));
            var open = await _probe.IsOpenAsync(target.Host, target.Port, ProbeTimeout, cts.Token).ConfigureAwait(false);
            var next = PowerRules.Decide(target, open, Safe(_routerLoggedIn), DateTimeOffset.Now);
            if (next.State != Status.State)
                _log.Debug($"PC 전원 확인: {next.PillText} ({next.Detail})");
            Apply(next);
        }
        catch (OperationCanceledException)
        {
            // 종료 중 — 상태를 바꾸지 않는다.
        }
        catch (Exception ex)
        {
            _log.Debug("PC 전원 확인 실패: " + ex.Message);
            Apply(new PcPowerStatus(PcPowerState.Unknown, "확인하지 못함", Status.CheckedAt));
        }
        finally
        {
            _nextDue = DateTime.UtcNow + Interval(_settings());
            _cts = null;
            cts.Dispose();
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static TimeSpan Interval(AppSettings s) => TimeSpan.FromSeconds(Math.Clamp(s.PowerCheckSeconds, 15, 3600));

    private static bool Safe(Func<bool> f)
    {
        try { return f(); }
        catch { return false; }
    }

    private void Apply(PcPowerStatus status)
    {
        if (Status == status) return;
        Status = status;
        Changed?.Invoke(status);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); }
        catch { /* 무시 */ }
    }
}
