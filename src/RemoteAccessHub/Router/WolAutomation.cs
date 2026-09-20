using System.Text.RegularExpressions;
using RemoteAccessHub.Core;

namespace RemoteAccessHub.Router;

public enum WolStep
{
    SessionCheck,
    NavigateToWol,
    Match,
    Click,
    Confirm,
    RouterResponse,
}

public enum StageStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Unknown,
}

/// <param name="RequestAborted">wol/signal 요청이 공유기 화면에서 중간에 취소됨(net::ERR_ABORTED) — 공유기 처리 여부를 알 수 없음</param>
public sealed record WolOutcome(bool Success, WolStep LastStep, string Message, bool ClickDone, StageStatus RouterStatus, WolMatchResult? Match, bool RequestAborted = false)
{
    public static WolOutcome Fail(WolStep step, string message, WolMatchResult? match = null, bool clickDone = false, StageStatus router = StageStatus.Pending)
        => new(false, step, message, clickDone, router, match);
}

/// <summary>
/// 로그인된 동일 브라우저 세션에서: 세션 재확인 → (필요하면 [관리도구] 선택) → WOL 화면 이동 →
/// 대상 PC의 [PC 켜기] 버튼 확인 후 클릭 → 확인창 처리 → 공유기 응답(wol/signal) 확인.
/// 클릭 성공 / 공유기 처리 / 실제 부팅은 서로 구분해 보고한다.
/// </summary>
public sealed class WolAutomation
{
    private readonly RouterBrowser _browser;
    private readonly AppLog _log;
    private readonly Func<AppSettings> _settings;

    public RouterNavigator Navigator { get; }

    public event Action<WolStep, StageStatus, string>? StageChanged;

    /// <summary>확인창이 화면에 나타났을 때 호출(접힌 화면을 펼치기 위함).</summary>
    public Func<Task>? DialogAppeared { get; set; }

    /// <summary>사용자 조작이 필요할 때 호출(접힌 화면을 펼치기 위함).</summary>
    public Func<Task>? UserActionNeeded { get; set; }

    public WolAutomation(RouterBrowser browser, AppLog log, Func<AppSettings> settings)
    {
        _browser = browser;
        _log = log;
        _settings = settings;
        Navigator = new RouterNavigator(browser, log, settings);
        Navigator.Progress += m => StageChanged?.Invoke(WolStep.NavigateToWol, StageStatus.Running, m);
    }

    private void Stage(WolStep step, StageStatus status, string message)
    {
        _log.Info($"[WOL {step}] {status}: {message}");
        StageChanged?.Invoke(step, status, message);
    }

    public string WolPageUrl() => (_settings().RouterOrigin ?? "") + _settings().WolPagePath;

    public bool LooksLikeWolPage(ProbeSnapshot snap) => RouterPages.IsWolList(snap, _settings().UiText);

    private static Regex SafeRegex(string pattern)
    {
        try { return new Regex(pattern, RegexOptions.IgnoreCase); }
        catch { return new Regex(@"^PC\s*켜기$", RegexOptions.IgnoreCase); }
    }

    /// <summary>WOL 요청이 공유기 화면에서 취소될 때 보내는 최대 횟수(처음 포함). WOL 신호는 여러 번 가도 PC에는 같은 결과다.</summary>
    public const int MaxWakeAttempts = 5;

    /// <param name="skipNavigation">사용자가 이미 WOL 화면을 열어 둔 경우(수동 이동) true.</param>
    public async Task<WolOutcome> WakeAsync(bool skipNavigation, CancellationToken ct)
    {
        var s = _settings();
        var wake = SafeRegex(s.WakeButtonPattern);
        var target = new WolTarget(s.WolPcName, s.WolPcMac);
        var startedAt = DateTimeOffset.Now;

        // 1) 실행 전 세션 재확인 (공유기 API로 확정)
        Stage(WolStep.SessionCheck, StageStatus.Running, "로그인 세션 확인 중...");
        var sd = await _browser.RefreshSessionAsync(ct);
        if (sd.Result == SessionProbeResult.Unavailable)
        {
            await Task.Delay(1000, ct);
            sd = await _browser.RefreshSessionAsync(ct);
        }
        if (sd.Result == SessionProbeResult.Unauthenticated)
        {
            Stage(WolStep.SessionCheck, StageStatus.Failed, "세션이 만료되었거나 로그인되지 않았습니다.");
            return WolOutcome.Fail(WolStep.SessionCheck, "공유기 로그인 세션이 유효하지 않습니다. 공유기 화면을 펼쳐 다시 로그인하세요.");
        }
        if (sd.Result == SessionProbeResult.Unavailable)
        {
            Stage(WolStep.SessionCheck, StageStatus.Failed, sd.Reason);
            return WolOutcome.Fail(WolStep.SessionCheck, "세션 상태를 확인할 수 없습니다: " + sd.Reason + "\n공유기 화면이 공유기 주소에 열려 있는지 확인하세요.");
        }
        Stage(WolStep.SessionCheck, StageStatus.Done, "로그인 세션 유효");

        // 2) WOL 화면
        if (!skipNavigation)
        {
            Stage(WolStep.NavigateToWol, StageStatus.Running, "WOL 화면으로 이동 중...");
            var nav = await Navigator.NavigateToWolAsync(ct);
            switch (nav.Status)
            {
                case NavStatus.Ok:
                    Stage(WolStep.NavigateToWol, StageStatus.Done, nav.Message);
                    break;
                case NavStatus.NeedLogin:
                    // 화면만 보고 로그아웃을 확정하지 않는다: 공유기 API로 다시 확인해 반영
                    await _browser.RefreshSessionAsync(ct);
                    Stage(WolStep.NavigateToWol, StageStatus.Failed, nav.Message);
                    return WolOutcome.Fail(WolStep.SessionCheck, nav.Message + " 다시 로그인한 뒤 실행하세요.");
                case NavStatus.NeedUserSelect:
                    Stage(WolStep.NavigateToWol, StageStatus.Failed, nav.Message);
                    if (UserActionNeeded != null) await UserActionNeeded();
                    return WolOutcome.Fail(WolStep.NavigateToWol, nav.Message + " 그다음 [PC 켜기]를 다시 누르세요.");
                default:
                    Stage(WolStep.NavigateToWol, StageStatus.Failed, nav.Message);
                    return WolOutcome.Fail(WolStep.NavigateToWol, nav.Message);
            }
        }
        else
        {
            await _browser.EnsureSemanticsAsync(TimeSpan.FromSeconds(8), ct);
            var (kind, _) = await Navigator.ClassifyAsync(ct);
            if (kind != RouterPageKind.WolList)
            {
                Stage(WolStep.NavigateToWol, StageStatus.Failed, $"현재 화면이 WOL 목록으로 보이지 않습니다({kind}).");
                return WolOutcome.Fail(WolStep.NavigateToWol, "현재 화면이 WOL 목록 화면으로 보이지 않습니다. [특수 기능 → WOL 기능] 화면을 연 뒤 다시 시도하세요.");
            }
            Stage(WolStep.NavigateToWol, StageStatus.Done, "현재 화면 사용");
        }

        // 공유기 화면이 WOL 요청을 중간에 취소하면(net::ERR_ABORTED) 목록을 새로 불러온 뒤 다시 보낸다(모두 합쳐 최대 MaxWakeAttempts번).
        // WOL 신호는 여러 번 보내도 PC에는 같은 결과다. 대상 찾기·누르기 직전 확인은 매번 처음부터 다시 한다.
        var listSince = startedAt;
        for (var attempt = 1; ; attempt++)
        {
            // 목록 요청이 끊긴 채 누르면 공유기 화면이 WOL 요청을 곧바로 취소했다(사용자 PC 기록). 먼저 목록을 정상으로 불러온다.
            await Navigator.EnsureWolListLoadedAsync(listSince, forceRefresh: attempt > 1, ct);

            var outcome = await MatchClickAndObserveAsync(s, target, wake, ct);
            if (!outcome.RequestAborted) return outcome;
            if (attempt >= MaxWakeAttempts)
            {
                return outcome with
                {
                    Message = $"WOL 요청이 {MaxWakeAttempts}번 모두 공유기 화면에서 중간에 취소되어(net::ERR_ABORTED) 공유기가 처리했는지 확인할 수 없습니다.",
                };
            }
            _log.Warn($"WOL 요청이 공유기 화면에서 중간에 취소됨(net::ERR_ABORTED) → 목록을 새로 불러온 뒤 다시 보냅니다({attempt + 1}/{MaxWakeAttempts}번째).");
            Stage(WolStep.RouterResponse, StageStatus.Running, $"요청이 공유기 화면에서 중간에 취소되어, 목록을 새로 불러온 뒤 다시 보냅니다({attempt + 1}/{MaxWakeAttempts}번째).");
            listSince = DateTimeOffset.Now;
        }
    }

    private async Task<WolOutcome> MatchClickAndObserveAsync(AppSettings s, WolTarget target, Regex wake, CancellationToken ct)
    {
        // 목록이 그려질 때까지 잠시 대기(로딩 문구, 빈 목록 직후 채워짐)
        var (_, snap) = await _browser.WaitForAsync(p => !p.Markers.LoadingOverlay && (p.Markers.WakeButtons > 0 || p.ContainsText("등록된 WOL PC가 없습니다")),
            TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(400), ct);

        // 3) 대상 버튼 찾기
        Stage(WolStep.Match, StageStatus.Running, $"'{target.PcName}' 행의 [PC 켜기] 버튼 찾는 중...");
        await _browser.EnsureSemanticsAsync(TimeSpan.FromSeconds(3), ct);
        snap = await _browser.ProbeAsync(ct);
        var match = WolMatcher.Match(snap, target, wake);
        foreach (var d in match.Details) _log.Debug("  " + d);
        if (!match.IsFound || match.Target == null)
        {
            Stage(WolStep.Match, StageStatus.Failed, match.Message);
            return WolOutcome.Fail(WolStep.Match, match.Message, match);
        }
        Stage(WolStep.Match, StageStatus.Done, match.Message);

        // 4) 클릭 (직전 재검증)
        Stage(WolStep.Click, StageStatus.Running, "버튼 클릭 중...");
        var clickTime = DateTimeOffset.Now;
        var t = match.Target;
        (bool Clicked, string Reason) click = t.Kind == "semantics"
            ? await _browser.ClickSemanticsNodeAsync(t.NodeId, WolMatcher.Compact(t.Label), t.Rect, ct)
            : await _browser.ClickVerifiedParagraphAsync(t.Label, t.Rect, ct);
        var how = (t.Kind == "semantics" ? "접근성 노드 클릭: " : "화면 텍스트 위치 클릭: ") + click.Reason;
        if (!click.Clicked)
        {
            Stage(WolStep.Click, StageStatus.Failed, how);
            return WolOutcome.Fail(WolStep.Click, "버튼을 누르기 직전 확인에 실패해 누르지 않았습니다(" + click.Reason + "). 화면이 바뀌었을 수 있으니 다시 시도하세요.", match);
        }
        Stage(WolStep.Click, StageStatus.Done, how);

        // 5) 확인창 및 공유기 응답
        return await ObserveAfterClickAsync(s, match, clickTime, ct);
    }

    internal static bool IsAborted(ApiCall c) =>
        c.Failed && (c.ErrorMessage ?? "").Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase);

    private async Task<WolOutcome> ObserveAfterClickAsync(AppSettings s, WolMatchResult match, DateTimeOffset clickTime, CancellationToken ct)
    {
        Stage(WolStep.Confirm, StageStatus.Running, "확인창/공유기 응답 확인 중...");
        var confirmPattern = s.ConfirmDialogPattern;
        var progressPattern = s.WakeProgressPattern;
        var dialogSeen = false;
        var dialogHandled = false;
        var progressSeen = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        var manualDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        DateTime? abortedSeenAt = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // 클릭 이후에 시작된 요청만 본다(직전 실행의 응답을 이번 결과로 오인하지 않도록)
            var calls = _browser.Network.Since(clickTime, "wol/signal");
            // 결과로 삼는 요청: 정상 완료, 응답(HTTP 상태)을 받은 뒤 끊김, 응답 없이 난 진짜 네트워크 오류.
            // 응답 없이 끊긴 요청(ERR_ABORTED)만 결과로 삼지 않고, 다시 보낸 요청이 있는지 기다린다.
            var done = calls.FirstOrDefault(c => c.Completed || (c.Failed && (c.ResponseReceived || !IsAborted(c))));
            var aborted = calls.FirstOrDefault(c => IsAborted(c) && !c.ResponseReceived);
            if (done is { Failed: true, ResponseReceived: true })
            {
                // 공유기 앱은 응답 본문을 다 읽은 뒤 통신 객체를 닫는다(main.dart.js 서비스 호출 함수의 finally → abort()).
                // 그래서 공유기가 이미 응답한 요청도 브라우저에는 끊김으로 기록될 수 있다.
                if (!done.ResultChecked) { await Task.Delay(150, ct); continue; }
                _log.Info($"wol/signal: 공유기 응답(HTTP {done.Status})을 받은 뒤 공유기 화면이 연결을 닫음(브라우저 기록 {done.ErrorMessage}), 본문 {(done.ResultOk == true ? "정상" : done.ErrorCode != null ? "오류 " + done.ErrorCode : "판독 불가")}");
                if (done.ErrorCode == null && done.ResultOk != true && done.Status is >= 200 and < 300)
                {
                    // 본문은 읽지 못했지만 공유기가 정상 상태 코드로 응답함 → 처리된 것으로 본다(다시 보내지 않음)
                    if (!string.IsNullOrEmpty(done.ParamsMasked)) _log.Info("wol/signal 요청 대상: " + done.ParamsMasked);
                    Stage(WolStep.Confirm, StageStatus.Done, dialogSeen ? "확인창 처리됨" : "확인창 없이 진행");
                    Stage(WolStep.RouterResponse, StageStatus.Done, $"공유기가 WOL 요청에 응답했습니다 (HTTP {done.Status}, 응답 받은 뒤 연결 닫힘)");
                    return new WolOutcome(true, WolStep.RouterResponse, "공유기가 WOL 요청에 정상 응답했습니다. 실제 부팅은 [PC 접속]에서 RDP 포트 응답으로 확인합니다.", true, StageStatus.Done, match);
                }
            }
            if (done == null && aborted != null)
            {
                abortedSeenAt ??= DateTime.UtcNow;
                var pending = calls.Any(c => !c.Failed && !c.Completed);
                if (pending || DateTime.UtcNow - abortedSeenAt.Value < TimeSpan.FromSeconds(2.5))
                {
                    await Task.Delay(250, ct);
                    continue;
                }
                Stage(WolStep.Confirm, StageStatus.Done, dialogSeen ? "확인창 처리됨" : "확인창 없이 진행");
                Stage(WolStep.RouterResponse, StageStatus.Unknown, "wol/signal 요청이 공유기 화면에서 중간에 취소됨: " + aborted.ErrorMessage);
                return new WolOutcome(false, WolStep.RouterResponse, "WOL 요청이 공유기 화면에서 중간에 취소되었습니다(" + aborted.ErrorMessage + ").",
                    true, StageStatus.Unknown, match, RequestAborted: true);
            }
            if (done != null && ((done.Failed && !done.ResponseReceived) || done.ResultChecked))
            {
                if (done.Failed && !done.ResponseReceived)
                {
                    Stage(WolStep.RouterResponse, StageStatus.Failed, "wol/signal 요청 실패: " + done.ErrorMessage);
                    return new WolOutcome(false, WolStep.RouterResponse, "공유기에 WOL 요청을 보냈지만 네트워크 오류가 발생했습니다: " + done.ErrorMessage, true, StageStatus.Failed, match);
                }
                if (done.ErrorCode != null)
                {
                    Stage(WolStep.RouterResponse, StageStatus.Failed, $"공유기 오류 응답 {done.ErrorCode} {done.ErrorMessage}");
                    var expired = done.ErrorCode == -31998;
                    if (expired) _browser.Session.Apply(SessionProbeResult.Unauthenticated, "wol/signal 응답: 인증되지 않음");
                    return new WolOutcome(false, WolStep.RouterResponse, expired ? "공유기가 '인증되지 않음'으로 응답했습니다. 다시 로그인하세요." : $"공유기가 오류로 응답했습니다: {done.ErrorCode} {done.ErrorMessage}", true, StageStatus.Failed, match);
                }
                if (!string.IsNullOrEmpty(done.ParamsMasked)) _log.Info("wol/signal 요청 대상: " + done.ParamsMasked);
                if (done.ResultOk != true)
                {
                    Stage(WolStep.Confirm, StageStatus.Done, dialogSeen ? "확인창 처리됨" : "확인창 없이 진행");
                    Stage(WolStep.RouterResponse, StageStatus.Unknown, $"wol/signal 응답을 판독하지 못했습니다 (HTTP {done.Status})");
                    return new WolOutcome(false, WolStep.RouterResponse, "공유기에 WOL 요청은 전송됐지만 응답 내용을 확인하지 못했습니다. [PC 접속]으로 부팅 여부를 확인하세요.", true, StageStatus.Unknown, match);
                }
                Stage(WolStep.Confirm, StageStatus.Done, dialogSeen ? "확인창 처리됨" : "확인창 없이 진행");
                Stage(WolStep.RouterResponse, StageStatus.Done, $"공유기가 WOL 요청을 처리했습니다 (HTTP {done.Status})");
                return new WolOutcome(true, WolStep.RouterResponse, "공유기가 WOL 요청을 정상 처리했습니다. 실제 부팅은 [PC 접속]에서 RDP 포트 응답으로 확인합니다.", true, StageStatus.Done, match);
            }

            var snap = await _browser.ProbeAsync(ct);
            if (!progressSeen && snap.ContainsText(progressPattern))
            {
                progressSeen = true;
                _log.Info($"화면 문구 감지: '{progressPattern}'");
            }
            if (!dialogSeen && snap.ContainsText(confirmPattern))
            {
                dialogSeen = true;
                _log.Info("확인창 감지: " + confirmPattern);
                if (s.AutoConfirmWakeDialog)
                {
                    Stage(WolStep.Confirm, StageStatus.Running, "확인창의 [확인]을 자동으로 누르는 중...");
                    var (closed, how) = await AutoConfirmAsync(confirmPattern, clickTime, ct);
                    dialogHandled = closed;
                    if (closed)
                    {
                        Stage(WolStep.Confirm, StageStatus.Running, "확인창의 [확인]을 자동으로 눌렀습니다 (" + how + ").");
                    }
                    else
                    {
                        // 자동으로 닫지 못했을 때만 화면을 펼쳐 사용자에게 맡긴다.
                        if (DialogAppeared != null) await DialogAppeared();
                        Stage(WolStep.Confirm, StageStatus.Running, "확인창의 [확인]을 자동으로 누르지 못했습니다(" + how + "). 펼쳐진 화면에서 [확인]을 누르세요.");
                        manualDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
                    }
                }
                else
                {
                    if (DialogAppeared != null) await DialogAppeared();
                    Stage(WolStep.Confirm, StageStatus.Running, "공유기가 확인창을 표시했습니다. 펼쳐진 화면에서 [확인]을 누르세요.");
                }
            }

            var limit = dialogSeen && !dialogHandled ? manualDeadline : deadline;
            if (dialogSeen && dialogHandled) limit = clickTime.UtcDateTime.AddSeconds(25);
            if (DateTime.UtcNow >= limit) break;
            await Task.Delay(500, ct);
        }

        if (progressSeen)
        {
            Stage(WolStep.Confirm, StageStatus.Done, dialogSeen ? "확인창 처리됨" : "확인창 없음");
            Stage(WolStep.RouterResponse, StageStatus.Unknown, "화면에 진행 문구는 표시되었지만 API 응답을 직접 관찰하지 못했습니다.");
            return new WolOutcome(true, WolStep.RouterResponse, "화면에 'PC를 켜는 중' 문구가 표시되었습니다(공유기 API 응답은 직접 확인되지 않음).", true, StageStatus.Unknown, match);
        }
        if (dialogSeen)
        {
            Stage(WolStep.Confirm, StageStatus.Failed, "확인창이 처리되지 않았습니다.");
            return new WolOutcome(false, WolStep.Confirm, "확인창이 표시되었지만 제한 시간 안에 처리되지 않았습니다. 화면에서 직접 [확인]을 누르거나 다시 시도하세요.", true, StageStatus.Pending, match);
        }
        Stage(WolStep.Confirm, StageStatus.Unknown, "확인창/응답 관찰 안 됨");
        Stage(WolStep.RouterResponse, StageStatus.Unknown, "클릭 후 공유기 반응을 확인하지 못했습니다.");
        return new WolOutcome(false, WolStep.RouterResponse, "버튼을 클릭했지만 확인창도 공유기 API 응답도 관찰되지 않았습니다. 공유기 화면을 펼쳐 직접 [PC 켜기]를 눌러 보세요.", true, StageStatus.Unknown, match);
    }

    /// <summary>
    /// 확인창의 [확인]을 누르고 창이 실제로 닫혔는지(또는 wol/signal 요청이 시작됐는지) 확인한다.
    /// 순서: 접근성 버튼 click() → 라벨 있는 접근성 노드 위치 → 화면 텍스트 위치. [취소]는 절대 후보가 되지 않는다.
    /// </summary>
    private async Task<(bool Closed, string How)> AutoConfirmAsync(string confirmPattern, DateTimeOffset clickTime, CancellationToken ct)
    {
        var tried = new List<string>();
        foreach (var kind in new[] { "semantics", "node", "paragraph" })
        {
            ct.ThrowIfCancellationRequested();
            var snap = await _browser.ProbeAsync(ct);
            if (DialogGone(snap, confirmPattern, clickTime)) return (true, tried.Count == 0 ? "이미 닫힘" : string.Join(" → ", tried));

            var target = FindConfirmTargets(snap, confirmPattern).FirstOrDefault(x => x.Kind == kind);
            if (target == null) continue;

            (bool Clicked, string Reason) click;
            switch (kind)
            {
                case "semantics":
                    click = await _browser.ClickSemanticsNodeAsync(target.NodeId, target.Text, target.Rect, ct);
                    tried.Add("접근성 버튼");
                    break;
                case "node":
                    if (!RouterPages.IsPointSafe(snap, target.Text, target.Rect.CenterX, target.Rect.CenterY, out var why))
                    {
                        tried.Add("노드 위치 거부: " + why);
                        continue;
                    }
                    click = (await _browser.ClickAtAsync(target.Rect.CenterX, target.Rect.CenterY, ct), "clicked");
                    tried.Add("접근성 노드 위치");
                    break;
                default:
                    click = await _browser.ClickVerifiedParagraphAsync(target.Text, target.Rect, ct);
                    tried.Add("화면 텍스트 위치");
                    break;
            }
            if (!click.Clicked)
            {
                tried[^1] += "(안 누름: " + click.Reason + ")";
                continue;
            }

            var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(250, ct);
                var after = await _browser.ProbeAsync(ct);
                if (DialogGone(after, confirmPattern, clickTime)) return (true, string.Join(" → ", tried));
            }
            tried[^1] += "(창이 닫히지 않음)";
        }
        return (false, tried.Count == 0 ? "[확인] 버튼을 찾지 못함" : string.Join(" → ", tried));
    }

    private bool DialogGone(ProbeSnapshot snap, string confirmPattern, DateTimeOffset clickTime) =>
        _browser.Network.Since(clickTime, "wol/signal").Count > 0
        || (snap.IsReadable && !snap.ContainsText(confirmPattern));

    internal sealed record ConfirmTarget(string Kind, string NodeId, string Text, ProbeRect Rect);

    private static readonly Regex ConfirmLabel = new(@"^(확인|예|OK|Yes)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// 확인창 문구와 같은 창 안에 있는 [확인] 후보(선호 순서).
    /// 창 안 판정: 문구보다 아래(또는 같은 줄)이고 문구 하단에서 250px 이내, 문구 중심에서 좌우 400px 이내.
    /// 실기기 확인창(2026-09-14 화면): 문구 아래 한 줄에 [취소] [확인]이 나란히 있다.
    /// </summary>
    internal static IReadOnlyList<ConfirmTarget> FindConfirmTargets(ProbeSnapshot snap, string confirmPattern)
    {
        var textRect = snap.Paragraphs.FirstOrDefault(p => !p.Rect.IsEmpty && p.Text.Contains(confirmPattern, StringComparison.OrdinalIgnoreCase))?.Rect
            ?? snap.Nodes.Where(n => !n.Rect.IsEmpty && n.Label.Contains(confirmPattern, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Rect.W * n.Rect.H).FirstOrDefault()?.Rect;
        bool InDialog(ProbeRect r)
        {
            if (textRect == null || textRect.IsEmpty) return false;
            var dy = r.CenterY - textRect.Bottom;
            return r.CenterY > textRect.Y && dy < 250 && Math.Abs(r.CenterX - textRect.CenterX) < 400;
        }
        double Dist(ProbeRect r) => textRect == null ? 0 : Math.Abs(r.CenterY - textRect.Bottom);

        var list = new List<ConfirmTarget>();
        list.AddRange(snap.Nodes
            .Where(n => n.IsButton && n.IsUsable && ConfirmLabel.IsMatch(WolMatcher.Compact(n.Label)) && InDialog(n.Rect))
            .OrderBy(n => Dist(n.Rect))
            .Select(n => new ConfirmTarget("semantics", n.Id, WolMatcher.Compact(n.Label), n.Rect)));
        list.AddRange(snap.Nodes
            .Where(n => !n.IsButton && !n.Hidden && !n.Rect.IsEmpty && ConfirmLabel.IsMatch(WolMatcher.Compact(n.Label)) && InDialog(n.Rect))
            .OrderBy(n => Dist(n.Rect))
            .Select(n => new ConfirmTarget("node", n.Id, WolMatcher.Compact(n.Label), n.Rect)));
        list.AddRange(snap.Paragraphs
            .Where(p => !p.Rect.IsEmpty && ConfirmLabel.IsMatch(WolMatcher.Compact(p.Text)) && InDialog(p.Rect))
            .OrderBy(p => Dist(p.Rect))
            .Select(p => new ConfirmTarget("paragraph", "", WolMatcher.Compact(p.Text), p.Rect)));
        return list;
    }

    /// <summary>확인창 안의 '확인' 접근성 버튼(없으면 null).</summary>
    internal static SemanticNode? FindConfirmButton(ProbeSnapshot snap, string confirmPattern)
    {
        var t = FindConfirmTargets(snap, confirmPattern).FirstOrDefault(x => x.Kind == "semantics");
        return t == null ? null : snap.Nodes.FirstOrDefault(n => n.Id == t.NodeId);
    }

    /// <summary>확인창 안의 '확인' 화면 텍스트(없으면 null).</summary>
    internal static Paragraph? FindConfirmParagraph(ProbeSnapshot snap, string confirmPattern)
    {
        var t = FindConfirmTargets(snap, confirmPattern).FirstOrDefault(x => x.Kind == "paragraph");
        return t == null ? null : snap.Paragraphs.FirstOrDefault(p => !p.Rect.IsEmpty && p.Rect == t.Rect);
    }
}
