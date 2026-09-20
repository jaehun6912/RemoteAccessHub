namespace RemoteAccessHub.Core;

public enum SessionState
{
    /// <summary>아직 한 번도 확인하지 못함.</summary>
    Unknown,
    /// <summary>공유기가 "인증되지 않음"이라고 응답함(확정).</summary>
    LoggedOut,
    /// <summary>공유기가 세션 정보를 정상 응답함(확정).</summary>
    LoggedIn,
}

public enum SessionProbeResult
{
    /// <summary>session/info 정상 응답 → 로그인 상태 확정.</summary>
    Ok,
    /// <summary>공유기가 Unauthenticated(-31998) 등 인증 실패를 명시적으로 응답 → 로그아웃 확정.</summary>
    Unauthenticated,
    /// <summary>판독 불가(페이지가 공유기가 아님, 네트워크 오류, 응답 형식 불명, 화면 숨김 등). 상태를 바꾸지 않는다.</summary>
    Unavailable,
}

/// <summary>
/// 로그인 상태 래치. "판독 불가"는 절대 로그아웃으로 취급하지 않는다.
/// 상태는 확정적 증거(Ok / Unauthenticated)로만 바뀐다.
/// </summary>
public sealed class SessionTracker
{
    private readonly object _gate = new();

    public SessionState State { get; private set; } = SessionState.Unknown;
    public DateTimeOffset? LastConfirmedAt { get; private set; }
    public DateTimeOffset? LastProbeAt { get; private set; }
    public string LastReason { get; private set; } = "";
    public int ConsecutiveUnavailable { get; private set; }

    public event Action<SessionState, SessionState, string>? StateChanged;

    public bool IsLoggedIn => State == SessionState.LoggedIn;

    public SessionState Apply(SessionProbeResult result, string reason, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.Now;
        SessionState old, cur;
        lock (_gate)
        {
            old = State;
            LastProbeAt = t;
            switch (result)
            {
                case SessionProbeResult.Ok:
                    State = SessionState.LoggedIn;
                    LastConfirmedAt = t;
                    ConsecutiveUnavailable = 0;
                    LastReason = reason;
                    break;
                case SessionProbeResult.Unauthenticated:
                    State = SessionState.LoggedOut;
                    ConsecutiveUnavailable = 0;
                    LastReason = reason;
                    break;
                case SessionProbeResult.Unavailable:
                    ConsecutiveUnavailable++;
                    LastReason = "확인 불가: " + reason;
                    break;
            }
            cur = State;
        }
        if (old != cur) StateChanged?.Invoke(old, cur, reason);
        return cur;
    }

    /// <summary>공유기 URL이 바뀌는 등 이전 증거가 무효화될 때만 호출.</summary>
    public void Reset(string reason)
    {
        SessionState old;
        lock (_gate)
        {
            old = State;
            State = SessionState.Unknown;
            LastConfirmedAt = null;
            ConsecutiveUnavailable = 0;
            LastReason = reason;
        }
        if (old != SessionState.Unknown) StateChanged?.Invoke(old, SessionState.Unknown, reason);
    }

    public string Describe()
    {
        lock (_gate)
        {
            var s = State switch
            {
                SessionState.LoggedIn => "로그인됨",
                SessionState.LoggedOut => "로그인 필요",
                _ => "확인 전",
            };
            if (LastConfirmedAt is { } c && State == SessionState.LoggedIn) s += $" (확인 {c:HH:mm:ss})";
            if (ConsecutiveUnavailable > 0) s += $" [판독 불가 {ConsecutiveUnavailable}회, 상태 유지]";
            return s;
        }
    }
}
