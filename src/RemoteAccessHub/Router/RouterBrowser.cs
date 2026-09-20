using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RemoteAccessHub.Core;

namespace RemoteAccessHub.Router;

public sealed record SessionProbeDetail(SessionProbeResult Result, string Reason, int HttpStatus, int? ErrorCode);

/// <summary>
/// 공유기 화면을 담는 WebView2 래퍼.
/// - InPrivate 프로필: 쿠키·자동완성이 디스크에 남지 않는다.
/// - 화면 판독(probe.js), 접근성 활성화, 세션 확인(session/info), 접근성 노드 클릭, 좌표 클릭, 네트워크 관찰.
/// - 스크립트 대화상자(confirm 등)는 접힌 화면에 숨지 않도록 프로그램 대화상자로 대신 표시한다.
/// </summary>
public sealed class RouterBrowser : IDisposable
{
    private readonly WebView2 _view;
    private readonly AppLog _log;
    private readonly Func<AppSettings> _settings;
    private readonly string _probeScript;
    private TaskCompletionSource<bool>? _navTcs;

    public SessionTracker Session { get; } = new();
    public NetworkObserver Network { get; }
    public bool IsReady { get; private set; }
    public string CurrentUrl => _view.CoreWebView2?.Source ?? "";
    public string? LastInitError { get; private set; }

    /// <summary>(종류, 메시지) → 수락 여부. null이면 confirm/prompt는 거부, alert는 수락.</summary>
    public Func<string, string, bool>? ScriptDialogHandler { get; set; }

    public event Action<string>? UrlChanged;
    public event Action<bool>? NavigationFinished;
    public event Action<string, string>? ScriptDialogShown;

    public RouterBrowser(WebView2 view, AppLog log, Func<AppSettings> settings)
    {
        _view = view;
        _log = log;
        _settings = settings;
        Network = new NetworkObserver(log);
        _probeScript = LoadResource("probe.js");
    }

    private static string LoadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("내장 리소스를 찾을 수 없습니다: " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (IsReady) return;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewUserDataDirectory, new CoreWebView2EnvironmentOptions());
            var options = env.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await _view.EnsureCoreWebView2Async(env, options);
            ct.ThrowIfCancellationRequested();

            var core = _view.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // 기본 대화상자를 끄지 않으면 ScriptDialogOpening이 발생하지 않는다(WebView2 사양).
            // 끈 상태에서 직접 처리해야 접힌 화면 뒤에 확인창이 숨는 일이 없다.
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

            core.SourceChanged += (_, _) => UrlChanged?.Invoke(CurrentUrl);
            core.NavigationCompleted += OnNavigationCompleted;
            core.ScriptDialogOpening += OnScriptDialogOpening;
            core.NewWindowRequested += (_, e) => { e.Handled = true; try { core.Navigate(e.Uri); } catch { /* ignore */ } };
            core.ServerCertificateErrorDetected += OnCertificateError;
            core.ProcessFailed += (_, e) => _log.Error($"WebView2 프로세스 오류: {e.ProcessFailedKind} {e.Reason}");

            await Network.AttachAsync(core);
            IsReady = true;
            _log.Info($"내장 브라우저 준비됨 (WebView2 {env.BrowserVersionString}, InPrivate 프로필)");
        }
        catch (Exception ex)
        {
            LastInitError = ex.Message;
            _log.Error("내장 브라우저 초기화 실패: " + ex.Message);
            throw;
        }
    }

    private void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        var s = _settings();
        var routerHost = s.RouterUri?.Host;
        var reqHost = Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var u) ? u.Host : "";
        if (s.AllowRouterCertificateError && routerHost != null && string.Equals(routerHost, reqHost, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"설정에 따라 공유기({reqHost})의 인증서 오류({e.ErrorStatus})를 허용합니다.");
            e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        }
        else
        {
            _log.Warn($"인증서 오류({e.ErrorStatus}) — 기본 동작(차단) 유지. 필요하면 설정에서 '공유기 인증서 오류 허용'을 켜세요.");
            e.Action = CoreWebView2ServerCertificateErrorAction.Default;
        }
    }

    private void OnScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        var kind = e.Kind.ToString();
        var message = e.Message ?? "";
        _log.Info($"페이지 대화상자({kind}): {message}");
        ScriptDialogShown?.Invoke(kind, message);
        bool accept;
        if (ScriptDialogHandler != null)
        {
            accept = ScriptDialogHandler(kind, message);
        }
        else
        {
            accept = e.Kind == CoreWebView2ScriptDialogKind.Alert;
        }
        if (accept) e.Accept();
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) _log.Warn($"페이지 이동 실패: {e.WebErrorStatus} (HTTP {e.HttpStatusCode})");
        _navTcs?.TrySetResult(e.IsSuccess);
        NavigationFinished?.Invoke(e.IsSuccess);
    }

    public bool IsOnRouterOrigin
    {
        get
        {
            var origin = _settings().RouterOrigin;
            if (origin == null) return false;
            return Uri.TryCreate(CurrentUrl, UriKind.Absolute, out var u)
                && string.Equals(u.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>페이지 이동 후 NavigationCompleted까지 대기(최대 timeout). 실패해도 예외를 내지 않는다.</summary>
    public async Task<bool> NavigateAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        EnsureReady();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navTcs = tcs;
        _log.Info("페이지 이동: " + url);
        try
        {
            _view.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            _log.Error("페이지 이동 오류: " + ex.Message);
            return false;
        }
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
        if (done != tcs.Task) return false;
        return await tcs.Task;
    }

    public void Reload()
    {
        EnsureReady();
        _view.CoreWebView2.Reload();
    }

    /// <summary>동기 스크립트 실행(결과는 JSON 문자열). Promise를 기다리지 않으므로 async 스크립트에는 <see cref="EvaluateAsync"/>를 쓴다.</summary>
    public async Task<string?> ExecuteAsync(string script, CancellationToken ct)
    {
        EnsureReady();
        ct.ThrowIfCancellationRequested();
        var result = await _view.CoreWebView2.ExecuteScriptAsync(script);
        return result;
    }

    /// <summary>
    /// DevTools Runtime.evaluate(awaitPromise)로 스크립트를 실행하고, Promise면 완료값을 기다린다.
    /// 반환값은 문자열 값 자체(JSON 감싸기 없음). 예외 시 null.
    /// </summary>
    public async Task<string?> EvaluateAsync(string expression, CancellationToken ct)
    {
        EnsureReady();
        ct.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true });
        var json = await _view.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("exceptionDetails", out var ex))
        {
            var text = ex.TryGetProperty("text", out var t) ? t.GetString() : "스크립트 예외";
            if (ex.TryGetProperty("exception", out var exo) && exo.TryGetProperty("description", out var d)) text = d.GetString();
            _log.Debug("스크립트 예외: " + (text ?? "").Split('\n')[0]);
            return null;
        }
        if (!root.TryGetProperty("result", out var res)) return null;
        if (res.TryGetProperty("value", out var v))
        {
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        }
        return null;
    }

    /// <summary>ExecuteScriptAsync가 돌려준 JSON 문자열 리터럴을 실제 문자열로 푼다.</summary>
    public static string UnwrapString(string? scriptResult)
    {
        if (string.IsNullOrEmpty(scriptResult) || scriptResult == "null") return "";
        if (scriptResult.StartsWith('"'))
        {
            try { return JsonSerializer.Deserialize<string>(scriptResult) ?? ""; } catch { return scriptResult; }
        }
        return scriptResult;
    }

    public async Task<ProbeSnapshot> ProbeAsync(CancellationToken ct)
    {
        if (!IsReady) return new ProbeSnapshot { Error = "브라우저 준비 안 됨" };
        try
        {
            var raw = await ExecuteAsync(_probeScript, ct);
            var snap = ProbeSnapshot.Parse(raw);
            return snap;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ProbeSnapshot { Error = "판독 실패: " + ex.Message };
        }
    }

    /// <summary>Flutter "Enable accessibility" 자리표시자를 눌러 접근성 트리를 켠다. 이미 켜져 있으면 true.</summary>
    public async Task<bool> EnsureSemanticsAsync(TimeSpan wait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + wait;
        var clicks = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var snap = await ProbeAsync(ct);
            if (snap.SemanticsCount > 0) return true;
            if (!snap.Flutter) return snap.Nodes.Count > 0; // 일반 HTML 페이지
            if (snap.Placeholder && clicks < 3)
            {
                const string js = "(function(){var p=document.querySelector('flt-semantics-placeholder');if(!p)return 'none';try{p.click();}catch(e){return 'err:'+e;}return 'clicked';})()";
                var r = UnwrapString(await ExecuteAsync(js, ct));
                clicks++;
                _log.Debug($"접근성 활성화 시도 {clicks}: {r}");
            }
            await Task.Delay(500, ct);
        }
        var last = await ProbeAsync(ct);
        return last.SemanticsCount > 0;
    }

    /// <summary>
    /// 페이지 안에서 공유기 API(session/info)를 호출해 세션 유효성을 확정적으로 확인한다.
    /// 공유기 페이지가 아니거나 네트워크 오류면 Unavailable(상태 유지).
    /// </summary>
    public async Task<SessionProbeDetail> ProbeSessionAsync(CancellationToken ct)
    {
        if (!IsReady) return new(SessionProbeResult.Unavailable, "브라우저 준비 안 됨", 0, null);
        if (!IsOnRouterOrigin) return new(SessionProbeResult.Unavailable, "현재 페이지가 공유기 주소가 아님", 0, null);
        const string js = @"(async()=>{try{const r=await fetch('/cgi/service.cgi',{method:'POST',credentials:'same-origin',cache:'no-store',headers:{'Content-Type':'application/json; charset=utf-8','Cache-Control':'no-store'},body:JSON.stringify({method:'session/info'})});const t=await r.text();let j=null;try{j=JSON.parse(t)}catch(e){}const err=(j&&j.error)?j.error:null;return JSON.stringify({status:r.status,ok:!!(j&&j.result!=null&&!err),code:err?err.code:null,msg:err?String(err.message||'').slice(0,80):null,parsed:!!j});}catch(e){return JSON.stringify({status:0,ok:false,code:null,msg:String(e).slice(0,100),parsed:false});}})()";
        try
        {
            var raw = await EvaluateAsync(js, ct);
            if (string.IsNullOrEmpty(raw)) return new(SessionProbeResult.Unavailable, "세션 확인 스크립트가 결과를 돌려주지 않음", 0, null);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            int? code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
            var msg = root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var parsed = root.TryGetProperty("parsed", out var p) && p.ValueKind == JsonValueKind.True;

            if (ok) return new(SessionProbeResult.Ok, "session/info 정상", status, null);
            if (code == -31998 || (msg != null && msg.Contains("Unauthenticated", StringComparison.OrdinalIgnoreCase)) || status is 401 or 403)
                return new(SessionProbeResult.Unauthenticated, $"공유기 응답: 인증되지 않음({code?.ToString() ?? status.ToString()})", status, code);
            if (parsed && code != null)
                return new(SessionProbeResult.Unavailable, $"공유기 오류 응답 {code} {msg}", status, code);
            return new(SessionProbeResult.Unavailable, status == 0 ? $"네트워크 오류: {msg}" : $"응답 형식 불명(HTTP {status})", status, code);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(SessionProbeResult.Unavailable, "세션 확인 스크립트 실패: " + ex.Message, 0, null);
        }
    }

    /// <summary>세션 확인 후 래치에 반영한다.</summary>
    public async Task<SessionProbeDetail> RefreshSessionAsync(CancellationToken ct)
    {
        var d = await ProbeSessionAsync(ct);
        Session.Apply(d.Result, d.Reason);
        return d;
    }

    public sealed record LogoutResult(bool Confirmed, bool AlreadyLoggedOut, string Message);

    /// <summary>
    /// 공유기 관리 세션을 끊는다: 페이지 안에서 session/logout 호출 → session/info로 "인증되지 않음"을 확인.
    /// 확인되지 않으면 Confirmed=false로 알려 준다(추측으로 성공 처리하지 않음).
    /// </summary>
    public async Task<LogoutResult> LogoutAsync(CancellationToken ct)
    {
        if (!IsReady) return new(false, false, "내장 브라우저가 준비되지 않았습니다.");
        if (!IsOnRouterOrigin) return new(false, false, "공유기 페이지가 열려 있지 않아 로그아웃을 요청할 수 없습니다.");

        var before = await ProbeSessionAsync(ct);
        if (before.Result == SessionProbeResult.Unauthenticated)
        {
            Session.Apply(before.Result, "종료 전 확인: 이미 로그아웃 상태");
            return new(true, true, "이미 로그아웃 상태입니다.");
        }

        const string js = @"(async()=>{try{const r=await fetch('/cgi/service.cgi',{method:'POST',credentials:'same-origin',cache:'no-store',headers:{'Content-Type':'application/json; charset=utf-8','Cache-Control':'no-store'},body:JSON.stringify({method:'session/logout'})});const t=await r.text();let j=null;try{j=JSON.parse(t)}catch(e){}const err=(j&&j.error)?j.error:null;return JSON.stringify({status:r.status,code:err?err.code:null,msg:err?String(err.message||'').slice(0,80):null});}catch(e){return JSON.stringify({status:0,code:null,msg:String(e).slice(0,100)});}})()";
        string logoutNote;
        try
        {
            var raw = await EvaluateAsync(js, ct);
            logoutNote = raw ?? "응답 없음";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logoutNote = "요청 실패: " + ex.Message;
        }
        _log.Info("공유기 로그아웃 요청 결과: " + AppLog.Redact(logoutNote));

        // 로그아웃 반영 확인(짧게 재시도)
        for (var i = 0; i < 3; i++)
        {
            var after = await ProbeSessionAsync(ct);
            if (after.Result == SessionProbeResult.Unauthenticated)
            {
                Session.Apply(after.Result, "사용자 종료: 공유기 로그아웃 확인");
                return new(true, false, "공유기 로그아웃을 확인했습니다.");
            }
            await Task.Delay(400, ct);
        }
        return new(false, false, "로그아웃 요청 후에도 공유기 세션이 남아 있습니다.");
    }

    /// <summary>
    /// 접근성 노드를 id로 찾아 라벨과 위치를 다시 검증한 뒤 클릭한다(검증 실패 시 클릭하지 않음).
    /// </summary>
    public async Task<(bool Clicked, string Reason)> ClickSemanticsNodeAsync(string nodeId, string expectedLabel, ProbeRect expectedRect, CancellationToken ct)
    {
        EnsureReady();
        var payload = JsonSerializer.Serialize(new { id = nodeId, label = expectedLabel, x = expectedRect.X, y = expectedRect.Y, w = expectedRect.W, h = expectedRect.H });
        var js = @"(function(p){function find(root){var el=root.getElementById?root.getElementById(p.id):null;if(el)return el;var all=root.querySelectorAll?root.querySelectorAll('*'):[];for(var i=0;i<all.length;i++){if(all[i].shadowRoot){var f=find(all[i].shadowRoot);if(f)return f;}}return null;}
var el=find(document);if(!el)return 'missing';var role=el.getAttribute('role')||'';if(role!=='button')return 'role:'+role;var label=(el.getAttribute('aria-label')||el.textContent||'').replace(/\s+/g,' ').trim();if(label!==p.label)return 'label:'+label;var r=el.getBoundingClientRect();if(Math.abs(r.x-p.x)>6||Math.abs(r.y-p.y)>6||Math.abs(r.width-p.w)>6||Math.abs(r.height-p.h)>6)return 'moved:'+Math.round(r.x)+','+Math.round(r.y);if(el.getAttribute('aria-disabled')==='true')return 'disabled';try{el.click();}catch(e){return 'err:'+e;}return 'clicked';})(" + payload + ")";
        var r = UnwrapString(await ExecuteAsync(js, ct));
        return (r == "clicked", r);
    }

    /// <summary>
    /// 페이지를 다시 불러오지 않고 공유기 앱 안에서 경로를 바꾼다.
    /// Flutter 웹 엔진은 Flutter 표식이 없는 popstate를 "새 주소 입력"으로 보고 해당 경로를 push한다.
    /// </summary>
    public async Task<bool> PushRouteAsync(string path, CancellationToken ct)
    {
        EnsureReady();
        var js = "(function(p){try{history.pushState(null,'',p);window.dispatchEvent(new PopStateEvent('popstate',{state:null}));return location.pathname;}catch(e){return 'err:'+e;}})(" + JsonSerializer.Serialize(path) + ")";
        var r = await EvaluateAsync(js, ct);
        _log.Debug($"앱 내부 경로 이동 {path} → {r}");
        return r != null && !r.StartsWith("err:", StringComparison.Ordinal);
    }

    /// <summary>
    /// 장면 텍스트(flt-paragraph) 위치를 클릭한다. 클릭 직전에 다시 판독해
    /// ① 같은 텍스트가 같은 위치(±6px)에 있고 ② 그 지점이 다른 라벨의 항목 위가 아닐 때만 누른다.
    /// </summary>
    public async Task<(bool Clicked, string Reason)> ClickVerifiedParagraphAsync(string text, ProbeRect expected, CancellationToken ct)
    {
        var snap = await ProbeAsync(ct);
        var want = RouterPages.Compact(text);
        var p = snap.Paragraphs.FirstOrDefault(q => !q.Rect.IsEmpty && RouterPages.Compact(q.Text) == want
            && Math.Abs(q.Rect.X - expected.X) <= 6 && Math.Abs(q.Rect.Y - expected.Y) <= 6);
        if (p == null) return (false, "텍스트 위치가 바뀜");
        var x = p.Rect.CenterX;
        var y = p.Rect.CenterY;
        if (!RouterPages.IsPointSafe(snap, want, x, y, out var why)) return (false, why);
        var ok = await ClickAtAsync(x, y, ct);
        return (ok, ok ? "clicked" : "클릭 전송 실패");
    }

    /// <summary>DevTools 프로토콜로 실제 마우스 클릭을 보낸다(CSS px, 뷰포트 기준).</summary>
    public async Task<bool> ClickAtAsync(double x, double y, CancellationToken ct)
    {
        EnsureReady();
        try
        {
            var core = _view.CoreWebView2;
            string P(string type, int clickCount) => JsonSerializer.Serialize(new { type, x, y, button = "left", clickCount });
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", P("mouseMoved", 0));
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", P("mousePressed", 1));
            try
            {
                await Task.Delay(40, CancellationToken.None);
            }
            finally
            {
                // 누름만 보내고 끝나면 페이지가 버튼이 눌린 채로 여긴다: 취소·오류와 상관없이 항상 뗀다.
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", P("mouseReleased", 1));
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("좌표 클릭 실패: " + ex.Message);
            return false;
        }
    }

    /// <summary>조건이 참이 될 때까지 주기적으로 판독한다. 마지막 스냅샷을 돌려준다.</summary>
    public async Task<(bool Ok, ProbeSnapshot Last)> WaitForAsync(Func<ProbeSnapshot, bool> predicate, TimeSpan timeout, TimeSpan interval, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        ProbeSnapshot last = new() { Error = "미실행" };
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            last = await ProbeAsync(ct);
            if (predicate(last)) return (true, last);
            if (DateTime.UtcNow >= deadline) return (false, last);
            await Task.Delay(interval, ct);
        }
    }

    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        if (!IsReady) return null;
        try
        {
            using var ms = new MemoryStream();
            await _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private void EnsureReady()
    {
        if (!IsReady || _view.CoreWebView2 == null) throw new InvalidOperationException("내장 브라우저가 아직 준비되지 않았습니다.");
    }

    public void Dispose()
    {
        // WebView2 컨트롤은 폼이 정리한다.
    }
}
