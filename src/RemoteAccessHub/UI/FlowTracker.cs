using RemoteAccessHub.Core;
using RemoteAccessHub.Router;
using RemoteAccessHub.Services;

namespace RemoteAccessHub.UI;

public enum StepState
{
    Pending,
    Active,
    Done,
    Warning,
    Failed,
}

public sealed class StepInfo
{
    public StepInfo(string title) => Title = title;

    public string Title { get; }
    public string Detail { get; internal set; } = "대기";
    public StepState State { get; internal set; } = StepState.Pending;
    /// <summary>진행률(0~1). 진행 막대가 필요 없으면 null.</summary>
    public double? Progress { get; internal set; }

    internal void Set(StepState state, string detail, double? progress = null)
    {
        State = state;
        Detail = detail;
        Progress = progress;
    }

    public override string ToString() => $"{Title}: {State} {Detail}";
}

/// <summary>
/// 화면 단계 표시용 상태 모델: ① 공유기 로그인 ② PC 켜기 ③ 부팅 확인 ④ 원격 접속.
/// 자동화 결과(세션 상태, WOL 단계, 접속 단계)를 받아 사람이 읽는 상태로 바꾼다. UI와 분리해 단위 검사한다.
/// "버튼 클릭 / 공유기 처리 / 실제 부팅"의 구분은 그대로 유지한다.
/// </summary>
public sealed class FlowTracker
{
    public StepInfo Login { get; } = new("공유기 로그인");
    public StepInfo Wake { get; } = new("PC 켜기");
    public StepInfo Boot { get; } = new("부팅 확인");
    public StepInfo Remote { get; } = new("원격 접속");

    public IReadOnlyList<StepInfo> Steps => new[] { Login, Wake, Boot, Remote };

    /// <summary>이전 화면과의 호환(자체검사·진단): "WOL 버튼 클릭: 완료" 형식.</summary>
    public string WolClickText { get; private set; } = "WOL 버튼 클릭: -";
    public string WolRouterText { get; private set; } = "공유기 처리: -";
    public string WolBootText { get; private set; } = "PC 부팅: -";

    public event Action? Changed;

    private DateTimeOffset? _waitStartedAt;
    private int _bootWaitSeconds;
    private bool _needAdminSelect;
    private bool _preparingAdmin;
    private SessionState _session = SessionState.Unknown;
    private DateTimeOffset? _confirmedAt;

    private static string Hm(DateTimeOffset t) => t.ToString("HH:mm");

    private void Raise() => Changed?.Invoke();

    // ------------------------------------------------------------------ 로그인

    public void OnSession(SessionState state, DateTimeOffset? confirmedAt)
    {
        _session = state;
        _confirmedAt = confirmedAt;
        if (state != SessionState.LoggedIn) { _needAdminSelect = false; _preparingAdmin = false; }
        UpdateLogin();
        Raise();
    }

    public void OnAdminSelectNeeded(bool needed)
    {
        _needAdminSelect = needed;
        UpdateLogin();
        Raise();
    }

    /// <summary>로그인 직후 관리 화면 준비([관리도구] 선택) 중인지.</summary>
    public void OnAdminPreparing(bool preparing)
    {
        _preparingAdmin = preparing;
        UpdateLogin();
        Raise();
    }

    private void UpdateLogin()
    {
        switch (_session)
        {
            case SessionState.LoggedIn when _needAdminSelect:
                Login.Set(StepState.Warning, "[관리도구] 선택 필요");
                break;
            case SessionState.LoggedIn when _preparingAdmin:
                Login.Set(StepState.Active, "관리 화면 준비 중");
                break;
            case SessionState.LoggedIn:
                Login.Set(StepState.Done, _confirmedAt is { } t ? $"로그인됨 · {Hm(t)}" : "로그인됨");
                break;
            case SessionState.LoggedOut:
                Login.Set(StepState.Active, "공유기 창에서 로그인");
                break;
            default:
                Login.Set(StepState.Pending, "확인 전");
                break;
        }
    }

    /// <summary>새 공유기 로그인: 이전 실행의 PC 켜기·부팅 확인·원격 접속 결과를 지운다(진행 중인 단계는 그대로).</summary>
    public void ResetForNewSession()
    {
        foreach (var s in new[] { Wake, Boot, Remote })
            if (s.State != StepState.Active) s.Set(StepState.Pending, "대기");
        _waitStartedAt = null;
        WolClickText = "WOL 버튼 클릭: -";
        WolRouterText = "공유기 처리: -";
        WolBootText = "PC 부팅: -";
        Raise();
    }

    // ------------------------------------------------------------------ PC 켜기

    public void OnWolStarted()
    {
        Wake.Set(StepState.Active, "시작");
        Boot.Set(StepState.Pending, "대기");
        Remote.Set(StepState.Pending, "대기");
        WolClickText = "WOL 버튼 클릭: 대기";
        WolRouterText = "공유기 처리: 대기";
        WolBootText = "PC 부팅: 미확인";
        Raise();
    }

    public void OnWolStage(WolStep step, StageStatus status, string message)
    {
        static string Mark(StageStatus st) => st switch
        {
            StageStatus.Running => "진행 중",
            StageStatus.Done => "완료",
            StageStatus.Failed => "실패",
            StageStatus.Unknown => "확인 불가",
            _ => "-",
        };
        if (step == WolStep.Click) WolClickText = "WOL 버튼 클릭: " + Mark(status);
        if (step == WolStep.RouterResponse) WolRouterText = "공유기 처리: " + Mark(status);

        if (status == StageStatus.Running || (status == StageStatus.Done && step != WolStep.RouterResponse))
        {
            var detail = step switch
            {
                WolStep.SessionCheck => "세션 확인",
                WolStep.NavigateToWol => "WOL 화면으로 이동",
                WolStep.Match => "대상 PC 찾기",
                WolStep.Click => "[PC 켜기] 누르는 중",
                WolStep.Confirm => "확인창 처리",
                WolStep.RouterResponse => "공유기 응답 대기",
                _ => "진행 중",
            };
            if (status == StageStatus.Done && step == WolStep.Click) detail = "버튼 누름 · 확인창 대기";
            if (message.Contains("누르세요", StringComparison.Ordinal))
                Wake.Set(StepState.Warning, "확인창 [확인] 필요");
            else
                Wake.Set(StepState.Active, detail);
        }
        Raise();
    }

    public void OnWolOutcome(WolOutcome o, DateTimeOffset now)
    {
        if (o.Success)
        {
            if (o.RouterStatus == StageStatus.Done) Wake.Set(StepState.Done, $"공유기 처리 완료 · {Hm(now)}");
            else Wake.Set(StepState.Warning, "요청함 · 처리 응답 미확인");
        }
        else if (!o.RequestAborted && o.Message.Contains("취소", StringComparison.Ordinal))
        {
            Wake.Set(StepState.Warning, "취소됨");
        }
        else
        {
            var detail = o.LastStep switch
            {
                WolStep.SessionCheck => "로그인 필요",
                WolStep.NavigateToWol => "WOL 화면 이동 실패",
                WolStep.Match => o.Match?.Status switch
                {
                    WolMatchStatus.TargetNotFound => "대상 PC 없음",
                    WolMatchStatus.Ambiguous => "대상 구분 불가",
                    WolMatchStatus.MacMismatch => "MAC 불일치",
                    WolMatchStatus.ListEmpty => "등록된 PC 없음",
                    _ => "대상 찾기 실패",
                },
                WolStep.Click => "누르지 않음",
                WolStep.Confirm => "확인창 미처리",
                WolStep.RouterResponse => o.RequestAborted ? "요청 취소됨" : o.RouterStatus == StageStatus.Failed ? "공유기 오류" : "응답 미확인",
                _ => "실패",
            };
            Wake.Set(o.ClickDone && o.RouterStatus == StageStatus.Unknown ? StepState.Warning : StepState.Failed, detail);
        }
        Raise();
    }

    // ------------------------------------------------------------------ 부팅 확인 · 원격 접속

    public void OnConnectStarted(ConnectMode mode, int bootWaitSeconds)
    {
        _bootWaitSeconds = Math.Max(1, bootWaitSeconds);
        _waitStartedAt = null;
        Boot.Set(StepState.Active, mode switch { ConnectMode.Vpn => "VPN 확인", ConnectMode.Crd => "크롬 원격 데스크톱 준비", _ => "준비" });
        Remote.Set(StepState.Pending, "대기");
        Raise();
    }

    public void OnConnectProgress(ConnectProgress p, DateTimeOffset now)
    {
        switch (p.Stage)
        {
            case ConnectStage.VpnConnecting:
                Boot.Set(StepState.Active, "VPN 연결 중");
                break;
            case ConnectStage.VpnConnected:
                Boot.Set(StepState.Active, "VPN 연결됨");
                break;
            case ConnectStage.WaitingPort:
                _waitStartedAt ??= now;
                UpdateWait(now);
                break;
            case ConnectStage.PortOpen:
                _waitStartedAt = null;
                Boot.Set(StepState.Done, $"응답 확인 · {Hm(now)}");
                WolBootText = $"PC 부팅: 응답 확인 {now:HH:mm:ss}";
                break;
            case ConnectStage.BootCheckSkipped:
                // 크롬 원격 데스크톱은 열어 둔 포트가 없어 부팅을 확인하지 않는다. 확인한 척하지 않는다.
                _waitStartedAt = null;
                Boot.Set(StepState.Pending, "확인 안 함");
                WolBootText = "PC 부팅: 확인 안 함";
                break;
            case ConnectStage.LaunchingRdp:
                Remote.Set(StepState.Active, "원격 데스크톱 실행 중");
                break;
            case ConnectStage.LaunchingCrd:
                Remote.Set(StepState.Active, "크롬 원격 데스크톱 여는 중");
                break;
        }
        Raise();
    }

    /// <summary>응답 대기 중 경과 시간 갱신(1초 주기). 변경이 있으면 true.</summary>
    public bool Tick(DateTimeOffset now)
    {
        if (_waitStartedAt == null || Boot.State != StepState.Active) return false;
        UpdateWait(now);
        Raise();
        return true;
    }

    private void UpdateWait(DateTimeOffset now)
    {
        if (_waitStartedAt is not { } start) return;
        var elapsed = Math.Max(0, (int)(now - start).TotalSeconds);
        Boot.Set(StepState.Active, $"RDP 응답 대기 · {elapsed}/{_bootWaitSeconds}초", Math.Min(1.0, elapsed / (double)_bootWaitSeconds));
    }

    public void OnConnectOutcome(ConnectOutcome o, DateTimeOffset now)
    {
        _waitStartedAt = null;
        if (o.Success)
        {
            // 부팅 확인을 하지 않은 경우(크롬 원격 데스크톱)는 확인한 것처럼 표시하지 않는다.
            if (Boot.State != StepState.Done && o.PcRespondedOnPort) Boot.Set(StepState.Done, $"응답 확인 · {Hm(now)}");
            Remote.Set(StepState.Done, o.Mode == ConnectMode.Crd ? $"크롬 원격 데스크톱 열림 · {Hm(now)}" : $"원격 데스크톱 실행 · {Hm(now)}");
        }
        else if (o.IsCancelled)
        {
            if (Boot.State == StepState.Active) Boot.Set(StepState.Warning, "취소됨");
            if (Remote.State == StepState.Active) Remote.Set(StepState.Warning, "취소됨");
        }
        else if (o.IsTimedOut)
        {
            Boot.Set(StepState.Failed, "응답 없음(시간 초과)");
            WolBootText = "PC 부팅: 응답 없음(시간 초과)";
        }
        else if (o.PcRespondedOnPort)
        {
            Remote.Set(StepState.Failed, "실행 실패");
        }
        else
        {
            var vpn = o.Mode == ConnectMode.Vpn && o.Message.Contains("VPN", StringComparison.Ordinal);
            Boot.Set(StepState.Failed, vpn ? "VPN 연결 실패" : o.Stage == ConnectStage.Failed && o.Elapsed == TimeSpan.Zero ? "설정 확인 필요" : "실패");
        }
        Raise();
    }

    public string Summary() => string.Join(" / ", Steps.Select(s => $"{s.Title} {s.Detail}"));
}
