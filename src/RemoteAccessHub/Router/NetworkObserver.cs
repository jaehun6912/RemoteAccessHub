using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using RemoteAccessHub.Core;

namespace RemoteAccessHub.Router;

/// <summary>공유기 API 호출 1건의 관찰 기록. 본문은 저장하지 않고 method 이름과 결과만 남긴다.</summary>
public sealed class ApiCall
{
    public string RequestId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string? ParamsMasked { get; set; }
    public int? Status { get; set; }
    public bool Completed { get; set; }
    public bool Failed { get; set; }
    public int? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>응답 본문을 판독한 경우에만 값이 있다(true=정상, false=result 없음).</summary>
    public bool? ResultOk { get; set; }
    /// <summary>응답 본문 판독을 시도했는지(성공·실패 무관).</summary>
    public bool ResultChecked { get; set; }

    /// <summary>공유기가 응답(HTTP 상태)을 돌려줬는지. 브라우저에 "끊김"으로 기록돼도 응답을 받았을 수 있다.</summary>
    public bool ResponseReceived => Status != null;

    public override string ToString() =>
        $"{StartedAt:HH:mm:ss} {Method} → {(Completed ? (Status?.ToString() ?? "?") : Failed ? (Status != null ? $"{Status} 받은 뒤 끊김({ErrorMessage})" : $"실패({ErrorMessage})") : "진행중")}" +
        (ErrorCode != null ? $" 오류 {ErrorCode} {ErrorMessage}" : ResultOk == true ? " 정상" : "");
}

/// <summary>
/// DevTools 프로토콜(Network.*)로 공유기 API(/cgi/service.cgi) 호출을 관찰한다.
/// 요청 본문에서 "method"만 추출하고, wol/signal 등 관심 호출의 응답 본문에서 오류 여부만 판정한다.
/// </summary>
public sealed class NetworkObserver
{
    private readonly object _gate = new();
    private readonly List<ApiCall> _calls = new();
    private readonly Dictionary<string, ApiCall> _byId = new();
    private readonly AppLog _log;
    private CoreWebView2? _core;
    private const int MaxCalls = 300;

    private static readonly HashSet<string> BodyMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "wol/signal", "session/login", "session/logout", "session/update", "session/info",
    };

    public event Action<ApiCall>? CallCompleted;

    public NetworkObserver(AppLog log) => _log = log;

    public async Task AttachAsync(CoreWebView2 core)
    {
        _core = core;
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived += OnRequestWillBeSent;
        core.GetDevToolsProtocolEventReceiver("Network.responseReceived").DevToolsProtocolEventReceived += OnResponseReceived;
        core.GetDevToolsProtocolEventReceiver("Network.loadingFinished").DevToolsProtocolEventReceived += OnLoadingFinished;
        core.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += OnLoadingFailed;
    }

    public IReadOnlyList<ApiCall> Snapshot(int max = 50)
    {
        lock (_gate)
        {
            var start = Math.Max(0, _calls.Count - max);
            return _calls.Skip(start).Select(Clone).ToList();
        }
    }

    public IReadOnlyList<ApiCall> Since(DateTimeOffset time, string? method = null)
    {
        lock (_gate)
        {
            return _calls.Where(c => c.StartedAt >= time && (method == null || string.Equals(c.Method, method, StringComparison.OrdinalIgnoreCase)))
                .Select(Clone).ToList();
        }
    }

    private static ApiCall Clone(ApiCall c) => new()
    {
        RequestId = c.RequestId, StartedAt = c.StartedAt, Method = c.Method, Path = c.Path, ParamsMasked = c.ParamsMasked,
        Status = c.Status, Completed = c.Completed, Failed = c.Failed, ErrorCode = c.ErrorCode, ErrorMessage = c.ErrorMessage, ResultOk = c.ResultOk, ResultChecked = c.ResultChecked,
    };

    private void OnRequestWillBeSent(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;
            var requestId = root.GetProperty("requestId").GetString() ?? "";
            var request = root.GetProperty("request");
            var url = request.GetProperty("url").GetString() ?? "";
            if (!url.Contains("service.cgi", StringComparison.OrdinalIgnoreCase)) return;
            var path = url.Split('?')[0];
            var method = "";
            string? paramsMasked = null;
            if (request.TryGetProperty("postData", out var pd) && pd.ValueKind == JsonValueKind.String)
            {
                (method, paramsMasked) = ParseBody(pd.GetString());
            }
            var call = new ApiCall { RequestId = requestId, StartedAt = DateTimeOffset.Now, Method = method, Path = path, ParamsMasked = paramsMasked };
            lock (_gate)
            {
                _calls.Add(call);
                _byId[requestId] = call;
                if (_calls.Count > MaxCalls)
                {
                    var removed = _calls[0];
                    _calls.RemoveAt(0);
                    _byId.Remove(removed.RequestId);
                }
            }
            if (string.IsNullOrEmpty(method) && request.TryGetProperty("hasPostData", out var hp) && hp.ValueKind == JsonValueKind.True)
            {
                _ = FetchPostDataAsync(requestId, call);
            }
        }
        catch (Exception ex)
        {
            _log.Debug("requestWillBeSent 처리 실패: " + ex.Message);
        }
    }

    private async Task FetchPostDataAsync(string requestId, ApiCall call)
    {
        try
        {
            if (_core == null) return;
            var json = await _core.CallDevToolsProtocolMethodAsync("Network.getRequestPostData", JsonSerializer.Serialize(new { requestId }));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("postData", out var pd))
            {
                var (m, p) = ParseBody(pd.GetString());
                lock (_gate) { call.Method = m; call.ParamsMasked = p; }
            }
        }
        catch { /* ignore */ }
    }

    internal static (string Method, string? ParamsMasked) ParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return ("", null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            string? pm = null;
            if (root.TryGetProperty("params", out var p))
            {
                // 값은 MAC 마스킹 후 길이 제한. 로그인 호출의 params(자격 증명)는 절대 기록하지 않는다.
                pm = method.StartsWith("session/login", StringComparison.OrdinalIgnoreCase) ? "<가림>" : InputRules.MaskMac(p.GetRawText()).Replace("\n", " ");
                if (pm.Length > 120) pm = pm[..120] + "…";
            }
            return (method, pm);
        }
        catch
        {
            return ("", null);
        }
    }

    private void OnResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var requestId = doc.RootElement.GetProperty("requestId").GetString() ?? "";
            var status = doc.RootElement.GetProperty("response").GetProperty("status").GetInt32();
            lock (_gate)
            {
                if (_byId.TryGetValue(requestId, out var call)) call.Status = status;
            }
        }
        catch { /* ignore */ }
    }

    private void OnLoadingFailed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var requestId = doc.RootElement.GetProperty("requestId").GetString() ?? "";
            ApiCall? call;
            lock (_gate)
            {
                if (!_byId.TryGetValue(requestId, out call)) return;
                call.Failed = true;
                call.ErrorMessage = doc.RootElement.TryGetProperty("errorText", out var et) ? et.GetString() : "실패";
            }
            // 공유기 앱은 응답 본문을 다 읽은 뒤 통신 객체를 닫는다(abort). 그 순간이 브라우저의 완료 처리보다 빠르면
            // 이미 응답(HTTP 상태)을 받은 요청도 net::ERR_ABORTED로 기록된다. 응답을 받은 경우는 본문 판정을 시도한다.
            if (call.Status != null && BodyMethods.Contains(call.Method))
            {
                _ = ReadResultAsync(requestId, call);
                return;
            }
            CallCompleted?.Invoke(Clone(call));
        }
        catch { /* ignore */ }
    }

    private void OnLoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var requestId = doc.RootElement.GetProperty("requestId").GetString() ?? "";
            ApiCall? call;
            lock (_gate)
            {
                if (!_byId.TryGetValue(requestId, out call)) return;
                call.Completed = true;
            }
            if (BodyMethods.Contains(call.Method))
            {
                _ = ReadResultAsync(requestId, call);
            }
            else
            {
                CallCompleted?.Invoke(Clone(call));
            }
        }
        catch { /* ignore */ }
    }

    private async Task ReadResultAsync(string requestId, ApiCall call)
    {
        try
        {
            if (_core != null)
            {
                var json = await _core.CallDevToolsProtocolMethodAsync("Network.getResponseBody", JsonSerializer.Serialize(new { requestId }));
                using var doc = JsonDocument.Parse(json);
                var body = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : null;
                var isBase64 = doc.RootElement.TryGetProperty("base64Encoded", out var b64) && b64.ValueKind == JsonValueKind.True;
                if (body != null && isBase64)
                {
                    try { body = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(body)); } catch { body = null; }
                }
                if (body != null)
                {
                    var (ok, code, msg) = ParseResult(body);
                    lock (_gate)
                    {
                        call.ResultOk = ok;
                        call.ErrorCode = code;
                        call.ErrorMessage = msg;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug("응답 본문 판정 실패: " + ex.Message);
        }
        lock (_gate) call.ResultChecked = true;
        CallCompleted?.Invoke(Clone(call));
    }

    /// <summary>{"result":...} / {"result":null,"error":{"code":..,"message":..}} 판정. 본문 내용은 보관하지 않는다.</summary>
    internal static (bool Ok, int? Code, string? Message) ParseResult(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                int? code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
                var msg = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                if (msg != null && msg.Length > 80) msg = msg[..80];
                return (false, code, msg);
            }
            var hasResult = root.TryGetProperty("result", out var res) && res.ValueKind != JsonValueKind.Null;
            return (hasResult, null, null);
        }
        catch
        {
            return (false, null, "응답 형식 불명");
        }
    }
}
