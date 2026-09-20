using RemoteAccessHub.Core;

namespace RemoteAccessHub.Router;

public enum NavStatus
{
    Ok,
    NeedLogin,
    NeedUserSelect,
    Failed,
}

public sealed record NavResult(NavStatus Status, string Message, RouterPageKind Page, string Method = "");

/// <summary>[PC 켜기] 전 WOL 목록 불러오기 확인 결과.</summary>
public enum WolListLoad
{
    /// <summary>이미 정상 응답을 받음</summary>
    Loaded,
    /// <summary>[페이지 새로고침]으로 다시 불러와 정상 응답을 받음</summary>
    Reloaded,
    /// <summary>확인하지 못함(새로고침 버튼 없음 등) — 그대로 진행</summary>
    NotConfirmed,
}

/// <summary>
/// 공유기 앱 안의 화면 이동.
/// - 로그인 직후 선택 화면에서 [관리도구] 선택
/// - 관리 화면에서 WOL 화면으로 이동: 앱 내부 경로 이동 → 메뉴 클릭(검증된 좌표) → 주소 직접 열기 순서
/// 모든 클릭은 대상 텍스트·위치를 확인한 뒤에만 수행하고, 이동 결과를 화면 판정으로 다시 확인한다.
/// </summary>
public sealed class RouterNavigator
{
    private readonly RouterBrowser _browser;
    private readonly AppLog _log;
    private readonly Func<AppSettings> _settings;

    public event Action<string>? Progress;

    public RouterNavigator(RouterBrowser browser, AppLog log, Func<AppSettings> settings)
    {
        _browser = browser;
        _log = log;
        _settings = settings;
    }

    private void Report(string m)
    {
        _log.Info("[화면 이동] " + m);
        Progress?.Invoke(m);
    }

    public async Task<(RouterPageKind Kind, ProbeSnapshot Snap)> ClassifyAsync(CancellationToken ct)
    {
        var snap = await _browser.ProbeAsync(ct);
        return (RouterPages.Classify(snap, _settings().UiText), snap);
    }

    /// <summary>화면이 로딩/판독 불가 상태를 벗어날 때까지 기다린다(접근성 트리도 켠다).</summary>
    public async Task<(RouterPageKind Kind, ProbeSnapshot Snap)> WaitSettledAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var semanticsTried = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (kind, snap) = await ClassifyAsync(ct);
            if (kind is not (RouterPageKind.Loading or RouterPageKind.Unknown)) return (kind, snap);
            if (!semanticsTried && snap.Flutter && snap.SemanticsCount == 0)
            {
                semanticsTried = true;
                await _browser.EnsureSemanticsAsync(TimeSpan.FromSeconds(3), ct);
                continue;
            }
            if (DateTime.UtcNow >= deadline) return (kind, snap);
            await Task.Delay(500, ct);
        }
    }

    private async Task<(bool Ok, RouterPageKind Kind)> WaitForKindAsync(Func<RouterPageKind, bool> ok, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = RouterPageKind.Unknown;
        var semanticsTried = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (kind, snap) = await ClassifyAsync(ct);
            last = kind;
            if (ok(kind)) return (true, kind);
            if (kind == RouterPageKind.Login) return (false, kind);
            if (!semanticsTried && snap.Flutter && snap.SemanticsCount == 0)
            {
                semanticsTried = true;
                await _browser.EnsureSemanticsAsync(TimeSpan.FromSeconds(3), ct);
            }
            if (DateTime.UtcNow >= deadline) return (false, last);
            await Task.Delay(500, ct);
        }
    }

    /// <summary>
    /// 선택 화면이면 [관리도구]를 누른다. 이미 관리 화면이면 그대로 둔다.
    /// allowAutoClick=false면 누르지 않고 NeedUserSelect를 돌려준다.
    /// </summary>
    public async Task<NavResult> EnsureAdminToolAsync(bool allowAutoClick, CancellationToken ct)
    {
        var s = _settings();
        var (kind, snap) = await WaitSettledAsync(TimeSpan.FromSeconds(15), ct);
        switch (kind)
        {
            case RouterPageKind.Login:
                return new(NavStatus.NeedLogin, "로그인 화면이 표시되어 있습니다.", kind);
            case RouterPageKind.AdminMain:
            case RouterPageKind.WolList:
                return new(NavStatus.Ok, "관리 화면이 이미 표시되어 있습니다.", kind);
            case RouterPageKind.ModeSelect:
                break;
            default:
                return new(NavStatus.Failed, "공유기 화면 종류를 판단하지 못했습니다(로딩 중이거나 구조가 다름).", kind);
        }

        var label = s.AdminToolLabel;
        if (!allowAutoClick)
            return new(NavStatus.NeedUserSelect, $"공유기 화면에서 [{label}]를 눌러 주세요.", kind);

        // 선택 화면이 막 그려진 직후에는 공유기 앱 내부 상태가 준비되지 않았을 수 있다
        // (공유기 앱의 [관리도구] 처리기는 내부 로그인 정보가 없으면 관리 화면으로 가지 않는다).
        // 잠깐 기다린 뒤 누르고, 반응이 없으면 몇 번 더 시도한다.
        await Task.Delay(700, ct);
        LogModeSelectStructure(snap, label);

        const int rounds = 3;
        var delivered = false;
        var reasons = new List<string>();
        for (var round = 1; round <= rounds; round++)
        {
            if (round > 1)
            {
                await Task.Delay(1500, ct);
                await _browser.EnsureSemanticsAsync(TimeSpan.FromSeconds(3), ct);
            }

            var (kindNow, snapNow) = await ClassifyAsync(ct);
            if (kindNow is RouterPageKind.AdminMain or RouterPageKind.WolList)
                return new(NavStatus.Ok, "관리 화면이 표시되었습니다.", kindNow, round == 1 ? "이미 이동" : "지연 반영");
            if (kindNow == RouterPageKind.Login)
                return new(NavStatus.NeedLogin, "관리도구 선택 중 로그인 화면이 표시되었습니다.", kindNow);
            if (kindNow != RouterPageKind.ModeSelect)
            {
                reasons.Add($"{round}회차: 화면 {kindNow}");
                continue;
            }

            var semTargets = snapNow.Nodes.Where(n => n.IsButton && n.IsUsable &&
                    System.Text.RegularExpressions.Regex.IsMatch(RouterPages.Compact(n.Label), "^" + System.Text.RegularExpressions.Regex.Escape(RouterPages.Compact(label)) + @"(\s|$)"))
                .ToList();
            var paraTargets = RouterPages.FindParagraphs(snapNow, label);
            if (semTargets.Count != 1 && paraTargets.Count != 1)
            {
                var why = semTargets.Count == 0 && paraTargets.Count == 0 ? "찾지 못함" : $"접근성 {semTargets.Count}개·텍스트 {paraTargets.Count}개라 구분 불가";
                reasons.Add($"{round}회차: {why}");
                if (round == 1 && semTargets.Count > 1)
                    return new(NavStatus.NeedUserSelect, $"[{label}] 항목을 자동으로 누르지 못했습니다({why}). 공유기 화면에서 직접 눌러 주세요.", kindNow);
                continue;
            }

            Report(round == 1 ? $"선택 화면 감지 → [{label}] 선택" : $"[{label}] 다시 선택 ({round}/{rounds}회차)");

            if (semTargets.Count == 1)
            {
                var n = semTargets[0];
                var at = DateTimeOffset.Now;
                var c = await _browser.ClickSemanticsNodeAsync(n.Id, RouterPages.Compact(n.Label), n.Rect, ct);
                _log.Info($"[{label}] {round}회차 접근성 클릭 결과: {c.Reason} · {n.Rect} '{RouterPages.Compact(n.Label)}'");
                if (c.Clicked)
                {
                    delivered = true;
                    var (ok, after) = await WaitAfterAdminClickAsync(ct);
                    LogAfterClick(label, $"{round}회차 접근성", after, at);
                    if (ok) return new(NavStatus.Ok, $"[{label}]를 선택해 관리 화면으로 이동했습니다.", after, "semantics");
                    if (after == RouterPageKind.Login) return new(NavStatus.NeedLogin, "관리도구 선택 후 로그인 화면이 표시되었습니다.", after);
                    reasons.Add($"{round}회차 접근성 클릭 후 {after}");
                }
                else
                {
                    reasons.Add($"{round}회차 접근성 안 누름: {c.Reason}");
                }
            }

            if (paraTargets.Count == 1)
            {
                var (kindMid, snapMid) = await ClassifyAsync(ct);
                if (kindMid is RouterPageKind.AdminMain or RouterPageKind.WolList)
                    return new(NavStatus.Ok, $"[{label}]를 선택해 관리 화면으로 이동했습니다.", kindMid, "semantics");
                var para = RouterPages.FindParagraphs(snapMid, label);
                if (kindMid == RouterPageKind.ModeSelect && para.Count == 1)
                {
                    var at = DateTimeOffset.Now;
                    var c = await _browser.ClickVerifiedParagraphAsync(label, para[0].Rect, ct);
                    _log.Info($"[{label}] {round}회차 텍스트 위치 클릭 결과: {c.Reason} · {para[0].Rect}");
                    if (c.Clicked)
                    {
                        delivered = true;
                        var (ok, after) = await WaitAfterAdminClickAsync(ct);
                        LogAfterClick(label, $"{round}회차 텍스트 위치", after, at);
                        if (ok) return new(NavStatus.Ok, $"[{label}]를 선택해 관리 화면으로 이동했습니다.", after, "paragraph");
                        if (after == RouterPageKind.Login) return new(NavStatus.NeedLogin, "관리도구 선택 후 로그인 화면이 표시되었습니다.", after);
                        reasons.Add($"{round}회차 텍스트 클릭 후 {after}");
                    }
                    else
                    {
                        reasons.Add($"{round}회차 텍스트 안 누름: {c.Reason}");
                    }
                }
            }
        }

        _log.Warn($"[{label}] 자동 선택 실패 요약: {string.Join(" / ", reasons)}");
        var (k3, _) = await ClassifyAsync(ct);
        if (k3 is RouterPageKind.AdminMain or RouterPageKind.WolList)
            return new(NavStatus.Ok, "관리 화면이 표시되었습니다.", k3, "지연 반영");
        if (!delivered)
            return new(NavStatus.NeedUserSelect, $"[{label}]를 자동으로 누르지 못했습니다({reasons.FirstOrDefault()}). 공유기 화면에서 직접 눌러 주세요.", k3);
        return new(NavStatus.NeedUserSelect, $"[{label}]를 {rounds}번 눌렀지만 관리 화면으로 바뀌지 않았습니다. 공유기 화면에서 직접 눌러 주세요.", k3);
    }

    /// <summary>선택 화면의 구조 요약(버튼 노드·같은 글자 텍스트)을 기록한다. 라벨과 좌표만 남기고 입력값은 없다.</summary>
    private void LogModeSelectStructure(ProbeSnapshot snap, string label)
    {
        var buttons = snap.Nodes.Where(n => n.IsButton).Take(8)
            .Select(n => $"#{n.Index}(부모 {n.Parent}) '{RouterPages.Compact(n.Label)}' {n.Rect}{(n.Disabled ? " 비활성" : "")}{(n.Hidden ? " 숨김" : "")}");
        var paras = RouterPages.FindParagraphs(snap, label).Select(p => p.Rect.ToString());
        var labelled = snap.Nodes.Where(n => RouterPages.Compact(n.Label).Contains(label, StringComparison.Ordinal)).Take(5)
            .Select(n => $"#{n.Index} role={n.Role} '{RouterPages.Compact(n.Label)}' {n.Rect}");
        _log.Info($"[{label}] 선택 화면 구조: 접근성 노드 {snap.SemanticsCount}개, 버튼 [{string.Join("; ", buttons)}]");
        _log.Info($"[{label}] 라벨 포함 노드 [{string.Join("; ", labelled)}], 같은 글자 텍스트 위치 [{string.Join("; ", paras)}]");
    }

    private void LogAfterClick(string label, string which, RouterPageKind after, DateTimeOffset clickAt)
    {
        var calls = _browser.Network.Since(clickAt).Select(c => c.Method).Where(m => m.Length > 0).Distinct().Take(10);
        _log.Info($"[{label}] {which} 클릭 후 화면: {after}, 이후 공유기 API [{string.Join(", ", calls)}]");
    }

    /// <summary>
    /// [관리도구]를 누른 뒤 관리 화면을 기다린다. 화면이 바뀌는 중(로딩)이면 최대 12초까지 기다리지만,
    /// 4초가 지나도 선택 화면 그대로면 클릭이 반영되지 않은 것으로 보고 곧바로 돌아간다.
    /// </summary>
    private async Task<(bool Ok, RouterPageKind Kind)> WaitAfterAdminClickAsync(CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var hardDeadline = start + TimeSpan.FromSeconds(12);
        var unchangedDeadline = start + TimeSpan.FromSeconds(4);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (kind, _) = await ClassifyAsync(ct);
            if (kind is RouterPageKind.AdminMain or RouterPageKind.WolList) return (true, kind);
            if (kind == RouterPageKind.Login) return (false, kind);
            var now = DateTime.UtcNow;
            if (now >= hardDeadline) return (false, kind);
            if (kind == RouterPageKind.ModeSelect && now >= unchangedDeadline) return (false, kind);
            await Task.Delay(400, ct);
        }
    }

    private async Task<(bool Clicked, string Reason)> ClickTargetAsync(RouterPages.ClickTarget t, CancellationToken ct)
    {
        if (t.Kind == "semantics")
            return await _browser.ClickSemanticsNodeAsync(t.NodeId, t.Text, t.Rect, ct);
        return await _browser.ClickVerifiedParagraphAsync(t.Text, t.Rect, ct);
    }

    /// <summary>WOL 목록 화면으로 이동한다. 이미 WOL 화면이면 그대로 쓴다.</summary>
    public async Task<NavResult> NavigateToWolAsync(CancellationToken ct)
    {
        var s = _settings();
        var path = s.WolPagePath;
        var lastKind = RouterPageKind.Unknown;

        // 1회차: 현재 페이지에서 시도. 2회차: 주소를 직접 연(새로 불러온) 페이지에서 다시 시도.
        for (var round = 0; round < 2; round++)
        {
            var reloaded = round == 1;
            if (reloaded)
            {
                var url = (s.RouterOrigin ?? "") + path;
                Report("주소 직접 열기(페이지 새로 불러오기): " + path);
                await _browser.NavigateAsync(url, TimeSpan.FromSeconds(30), ct);
            }

            var ensure = await EnsureAdminToolAsync(s.AutoSelectAdminTool, ct);
            lastKind = ensure.Page;
            if (ensure.Status != NavStatus.Ok) return ensure;
            if (ensure.Page == RouterPageKind.WolList)
            {
                if (reloaded) return new(NavStatus.Ok, "WOL 화면으로 이동했습니다(주소 직접 열기).", RouterPageKind.WolList, "주소");
                // 이미 열려 있던 WOL 화면은 목록이 오래됐을 수 있으므로 화면의 [페이지 새로고침]으로 다시 불러온다.
                var refreshed = await RefreshWolListAsync(ct);
                return new(NavStatus.Ok, refreshed ? "열려 있던 WOL 화면의 목록을 새로 불러왔습니다." : "WOL 화면이 이미 표시되어 있습니다(새로고침 버튼 없음).", RouterPageKind.WolList, refreshed ? "현재 화면+새로고침" : "현재 화면");
            }

            // 앱 내부 경로 이동
            Report($"앱 내부 경로 이동 시도: {path}");
            if (await _browser.PushRouteAsync(path, ct))
            {
                var (ok, kind) = await WaitForKindAsync(k => k == RouterPageKind.WolList, TimeSpan.FromSeconds(8), ct);
                lastKind = kind;
                if (ok) return new(NavStatus.Ok, "WOL 화면으로 이동했습니다(앱 내부 경로).", kind, reloaded ? "주소+경로" : "경로");
                if (kind == RouterPageKind.Login) return new(NavStatus.NeedLogin, "WOL 화면 대신 로그인 화면이 표시되었습니다.", kind);
            }

            // 메뉴 클릭(장면 텍스트 위치와 클릭 지점 검증)
            var menu = await ClickMenuAsync(s, ct);
            if (menu.Status == NavStatus.Ok) return menu with { Method = reloaded ? "주소+메뉴" : "메뉴" };
            if (menu.Status == NavStatus.NeedLogin) return menu;
        }

        return new(NavStatus.Failed, $"WOL 화면으로 자동 이동하지 못했습니다. 공유기 화면에서 [{s.WolMenuGroupLabel} → {s.WolMenuLabel}]을 직접 연 뒤 [현재 화면에서 PC 켜기]를 누르세요.", lastKind);
    }

    /// <summary>
    /// WOL 화면의 [페이지 새로고침] 버튼을 눌러 목록(wol/show)을 다시 불러온다.
    /// 실기기 구조: 라벨 "페이지 새로고침" 노드 안에 라벨 없는 버튼 노드. 버튼 자체에 라벨이 붙은 경우도 허용.
    /// </summary>
    public async Task<bool> RefreshWolListAsync(CancellationToken ct)
    {
        var label = _settings().RefreshLabel;
        var (_, snap) = await ClassifyAsync(ct);
        var byIndex = snap.Nodes.ToDictionary(n => n.Index);
        var candidates = snap.Nodes.Where(n => n.IsButton && n.IsUsable && (
                RouterPages.Compact(n.Label) == label
                || (RouterPages.Compact(n.Label).Length == 0 && n.Parent >= 0 && byIndex.TryGetValue(n.Parent, out var p) && RouterPages.Compact(p.Label) == label)))
            .ToList();
        if (candidates.Count != 1)
        {
            _log.Debug($"[{label}] 버튼 {candidates.Count}개 → 새로고침 생략");
            return false;
        }
        var b = candidates[0];
        var since = DateTimeOffset.Now;
        var (clicked, reason) = await _browser.ClickSemanticsNodeAsync(b.Id, RouterPages.Compact(b.Label), b.Rect, ct);
        if (!clicked)
        {
            _log.Debug($"[{label}] 클릭 안 함: {reason}");
            return false;
        }
        // 목록 요청이 "끝났다"가 아니라 "정상 응답을 받았다"를 기다린다(중간에 끊긴 요청은 성공으로 치지 않음).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var shows = _browser.Network.Since(since, "wol/show");
            if (shows.Any(IsOkListLoad))
            {
                await Task.Delay(300, ct); // 목록 다시 그리기
                Report("WOL 목록 새로고침");
                return true;
            }
            if (shows.Count > 0 && shows.All(c => c.Failed))
            {
                _log.Info($"[{label}] 누른 뒤 목록 요청도 끊김: {shows[^1].ErrorMessage}");
                return false;
            }
            await Task.Delay(200, ct);
        }
        _log.Debug("새로고침 후 wol/show 정상 응답을 관찰하지 못함");
        return false;
    }

    /// <summary>목록 응답을 받았는지. 공유기 앱이 응답을 읽은 뒤 연결을 닫아 "끊김"으로 기록된 경우도 응답을 받았으면 성공으로 본다.</summary>
    private static bool IsOkListLoad(ApiCall c) => (c.Completed || c.Failed) && c.Status is >= 200 and < 300;

    /// <summary>
    /// [PC 켜기]를 누르기 전에 WOL 목록 요청(wol/show)이 정상으로 끝났는지 확인하고, 끊겼거나 확인되지 않으면
    /// 화면의 [페이지 새로고침]으로 다시 불러온다(최대 2번).
    /// 근거(2026-09-15 사용자 PC 기록 7회): 목록 요청이 정상(200)으로 끝난 뒤 누른 2번은 모두 wol/signal 정상,
    /// 목록 요청이 중간에 끊긴(net::ERR_ABORTED) 채 누른 3번은 모두 wol/signal이 0.1초 안에 끊겼다.
    /// </summary>
    public async Task<WolListLoad> EnsureWolListLoadedAsync(DateTimeOffset since, bool forceRefresh, CancellationToken ct)
    {
        // 진행 중인 목록 요청이 있으면 끝날 때까지 잠시 기다린다.
        var wait = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (DateTime.UtcNow < wait && _browser.Network.Since(since, "wol/show").Any(c => !c.Completed && !c.Failed))
            await Task.Delay(150, ct);

        var last = _browser.Network.Since(since, "wol/show").LastOrDefault(c => c.Completed || c.Failed);
        if (!forceRefresh && last != null && IsOkListLoad(last)) return WolListLoad.Loaded;

        var why = forceRefresh ? "다시 보내기 전에 목록을 새로 불러옴"
            : last == null ? "목록 요청이 보이지 않음"
            : last.Failed ? $"목록 요청이 중간에 끊김({last.ErrorMessage ?? "실패"})"
            : $"목록 요청 응답 HTTP {last.Status}";
        for (var i = 0; i < 2; i++)
        {
            Report($"WOL 목록 다시 불러오기: {why}");
            if (await RefreshWolListAsync(ct)) return WolListLoad.Reloaded;
        }
        _log.Warn($"WOL 목록을 정상으로 불러왔는지 확인하지 못했습니다. 그대로 진행합니다({why}).");
        return WolListLoad.NotConfirmed;
    }

    private async Task<NavResult> ClickMenuAsync(AppSettings s, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (kind, snap) = await ClassifyAsync(ct);
            if (kind == RouterPageKind.WolList) return new(NavStatus.Ok, "WOL 화면 표시됨", kind, "메뉴");
            if (kind == RouterPageKind.Login) return new(NavStatus.NeedLogin, "로그인 화면이 표시되었습니다.", kind);

            var items = RouterPages.FindParagraphs(snap, s.WolMenuLabel)
                .Where(p => RouterPages.IsPointSafe(snap, s.WolMenuLabel, p.Rect.CenterX, p.Rect.CenterY, out _))
                .ToList();
            if (items.Count > 0)
            {
                // 같은 이름의 메뉴(즐겨찾기/특수 기능)는 모두 같은 화면으로 이동한다.
                var item = items[0];
                Report($"메뉴 [{s.WolMenuLabel}] 클릭 {item.Rect}");
                var c = await _browser.ClickVerifiedParagraphAsync(s.WolMenuLabel, item.Rect, ct);
                if (!c.Clicked)
                {
                    _log.Warn("메뉴 클릭 안 함: " + c.Reason);
                }
                else
                {
                    var (ok, k2) = await WaitForKindAsync(k => k == RouterPageKind.WolList, TimeSpan.FromSeconds(10), ct);
                    if (ok) return new(NavStatus.Ok, "WOL 화면으로 이동했습니다(메뉴).", k2, "메뉴");
                    if (k2 == RouterPageKind.Login) return new(NavStatus.NeedLogin, "로그인 화면이 표시되었습니다.", k2);
                }
            }
            else if (attempt == 0)
            {
                var groups = RouterPages.FindParagraphs(snap, s.WolMenuGroupLabel)
                    .Where(p => RouterPages.IsPointSafe(snap, s.WolMenuGroupLabel, p.Rect.CenterX, p.Rect.CenterY, out _))
                    .ToList();
                if (groups.Count == 1)
                {
                    Report($"메뉴 [{s.WolMenuGroupLabel}] 펼치기");
                    await _browser.ClickVerifiedParagraphAsync(s.WolMenuGroupLabel, groups[0].Rect, ct);
                    await Task.Delay(800, ct);
                    continue;
                }
                var visible = RouterPages.FindParagraphs(snap, s.WolMenuLabel).Count;
                _log.Warn($"메뉴 [{s.WolMenuLabel}] 안전하게 누를 위치 없음(보이는 텍스트 {visible}개), 그룹 [{s.WolMenuGroupLabel}] {groups.Count}개");
            }
            break;
        }
        return new(NavStatus.Failed, "메뉴로 WOL 화면을 열지 못했습니다.", RouterPageKind.Unknown);
    }
}
