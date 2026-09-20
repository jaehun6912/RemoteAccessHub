using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RemoteAccessHub.Core;

namespace RemoteAccessHub.SelfTest;

public sealed record MockPc(string Name, string Mac);

/// <summary>
/// 모의 ipTIME 공유기. 실제 공유기에서 확인한 API 형식을 흉내 낸다:
/// POST /cgi/service.cgi {"method":"...","params":...} → {"result":...} 또는 {"result":null,"error":{"code":-31998,"message":"Unauthenticated"}}
/// 화면은 Flutter 웹(HTML 렌더러)과 같은 DOM 구조(flutter-view / flt-glass-pane shadow / flt-semantics)를 가진 모의 페이지다.
/// </summary>
public sealed class MockRouterServer : IDisposable
{
    private readonly AppLog _log;
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private readonly HashSet<string> _sessions = new();
    private readonly List<string> _signals = new();
    private TcpListener? _fakeRdp;
    private CancellationTokenSource? _cts;

    public string BaseUrl { get; private set; } = "";
    public int FakeRdpPort { get; private set; }
    /// <summary>자체검사·모의 공유기 모드의 기본 대상 PC 이름.</summary>
    public const string DefaultTargetName = "MY-PC";

    public List<MockPc> PcList { get; set; } = new()
    {
        new("OTHER-PC-1", "00:11:22:33:44:01"),
        new(DefaultTargetName, "00:11:22:33:44:02"),
        new("OTHER-PC-2", "00:11:22:33:44:03"),
    };
    public bool LoginShouldFail { get; set; }
    public bool SignalShouldFail { get; set; }
    public bool ShowNoticeOnSignal { get; set; }
    public int LoginCount { get; private set; }
    public int LogoutCount { get; private set; }

    public IReadOnlyList<string> Signals { get { lock (_gate) return _signals.ToList(); } }
    public int SessionCount { get { lock (_gate) return _sessions.Count; } }

    public MockRouterServer(AppLog log) => _log = log;

    public void Start()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));

        _fakeRdp = new TcpListener(IPAddress.Loopback, 0);
        _fakeRdp.Start();
        FakeRdpPort = ((IPEndPoint)_fakeRdp.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public void ExpireSessions() { lock (_gate) _sessions.Clear(); }
    public void ClearSignals() { lock (_gate) _signals.Clear(); }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _fakeRdp != null)
        {
            try
            {
                var c = await _fakeRdp.AcceptTcpClientAsync(ct);
                c.Close();
            }
            catch { break; }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;
            var path = req.Url?.AbsolutePath ?? "/";
            res.Headers["Cache-Control"] = "no-store";
            if (path == "/")
            {
                Write(res, 200, "text/html; charset=utf-8", "<script>location.href=\"/ui\";</script>");
                return;
            }
            // 실제 공유기는 /ui/wol 같은 앱 경로에도 같은 index.html을 준다(2026-09-14 확인)
            if (path == "/ui" || (path.StartsWith("/ui/", StringComparison.Ordinal) && !System.IO.Path.HasExtension(path)) || path == "/ui/index.html")
            {
                Write(res, 200, "text/html; charset=utf-8", LoadResource("mock/index.html"));
                return;
            }
            if (path == "/cgi/service.cgi" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = reader.ReadToEnd();
                // ?abort=1: 모의 페이지가 곧 취소할 요청(실기기에서 본 net::ERR_ABORTED 재현).
                // 응답을 늦춰 페이지가 먼저 끊게 하고, WOL 신호는 처리하지 않은 것으로 둔다(보수적인 경우).
                if ((req.Url?.Query ?? "").Contains("abort=1", StringComparison.Ordinal))
                {
                    Thread.Sleep(600);
                    try { ctx.Response.Abort(); } catch { /* ignore */ }
                    return;
                }
                var (status, json, setCookie) = HandleApi(body, req.Cookies["mocksid"]?.Value);
                // ?hold=1: 처리하고 본문까지 보낸 뒤 연결을 잠시 열어 둔다. 페이지가 먼저 닫으면
                // 브라우저 기록에는 "응답(HTTP 200)을 받은 뒤 net::ERR_ABORTED"로 남는다.
                if ((req.Url?.Query ?? "").Contains("hold=1", StringComparison.Ordinal))
                {
                    if (setCookie != null) res.Headers.Add("Set-Cookie", setCookie);
                    res.StatusCode = status;
                    res.ContentType = "application/json; charset=utf-8";
                    res.SendChunked = true;
                    var bytes = Encoding.UTF8.GetBytes(json);
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                    res.OutputStream.Flush();
                    Thread.Sleep(1500);
                    try { res.Close(); } catch { /* 페이지가 이미 닫음 */ }
                    return;
                }
                if (setCookie != null) res.Headers.Add("Set-Cookie", setCookie);
                Write(res, status, "application/json; charset=utf-8", json);
                return;
            }
            Write(res, 404, "text/plain; charset=utf-8", "not found");
        }
        catch (Exception ex)
        {
            _log.Debug("모의 서버 오류: " + ex.Message);
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private static string Unauth() => "{\"result\":null, \"error\":{\"code\":-31998, \"message\":\"Unauthenticated\"}}";

    private (int Status, string Json, string? SetCookie) HandleApi(string body, string? sid)
    {
        string method = "";
        JsonElement? prms = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            method = doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            if (doc.RootElement.TryGetProperty("params", out var p)) prms = p.Clone();
        }
        catch
        {
            return (400, "{\"result\":null,\"error\":{\"code\":-32700,\"message\":\"Parse error\"}}", null);
        }

        bool authed;
        lock (_gate) authed = sid != null && _sessions.Contains(sid);

        switch (method)
        {
            case "session/login":
                LoginCount++;
                if (LoginShouldFail) return (200, "{\"result\":null, \"error\":{\"code\":-31999, \"message\":\"Login failed\"}}", null);
                var newSid = Guid.NewGuid().ToString("N");
                lock (_gate) _sessions.Add(newSid);
                return (200, "{\"result\":{\"login\":true}}", $"mocksid={newSid}; Path=/; HttpOnly");
            case "session/logout":
                LogoutCount++;
                if (sid != null) lock (_gate) _sessions.Remove(sid);
                return (200, "{\"result\":true}", null);
            case "session/info":
                return authed ? (200, "{\"result\":{\"user\":\"admin\",\"remain\":600}}", null) : (200, Unauth(), null);
            case "session/update":
                return authed ? (200, "{\"result\":true}", null) : (200, Unauth(), null);
            case "product/info":
                return (200, "{\"result\":{\"model\":\"MOCK-AX2004T\",\"version\":\"15.36.6\"}}", null);
            case "wol/show":
                if (!authed) return (200, Unauth(), null);
                var list = PcList.Select(pc => new { name = pc.Name, mac = pc.Mac });
                return (200, JsonSerializer.Serialize(new { result = new { list } }), null);
            case "wol/signal":
                if (!authed) return (200, Unauth(), null);
                var mac = prms is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0 ? arr[0].GetString() ?? "" : prms?.ToString() ?? "";
                if (SignalShouldFail) return (200, "{\"result\":null, \"error\":{\"code\":-32000, \"message\":\"WOL failed\"}}", null);
                lock (_gate) _signals.Add(mac);
                _log.Info("모의 공유기: wol/signal 수신 " + InputRules.MaskMac(mac));
                return (200, "{\"result\":\"ok\"}", null);
            default:
                return authed ? (200, "{\"result\":null}", null) : (200, Unauth(), null);
        }
    }

    private static void Write(HttpListenerResponse res, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }

    private static string LoadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("내장 리소스 없음: " + name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
        try { _fakeRdp?.Stop(); } catch { /* ignore */ }
    }
}
