using System.Text.Json;
using RemoteAccessHub.Core;

namespace RemoteAccessHub.Router;

/// <summary>
/// 민감정보를 제외한 진단 파일 생성. 비밀번호·쿠키·세션 토큰·원본 HTML·입력값은 포함하지 않는다.
/// 호스트/IP/MAC은 마스킹하고, 접근성 라벨과 장면 텍스트는 길이를 제한해 담는다.
/// </summary>
public static class DiagnosticsExporter
{
    public static string DefaultFileName() => $"RemoteAccessHub-진단-{DateTime.Now:yyyyMMdd-HHmmss}.json";

    public static async Task<string> BuildAsync(RouterBrowser? browser, AppSettings settings, AppLog log, CancellationToken ct)
    {
        ProbeSnapshot? snap = null;
        SessionProbeDetail? session = null;
        if (browser is { IsReady: true })
        {
            try { snap = await browser.ProbeAsync(ct); } catch { /* ignore */ }
            try { session = await browser.ProbeSessionAsync(ct); } catch { /* ignore */ }
        }

        var doc = new
        {
            app = "RemoteAccessHub",
            version = AppInfo.Version,
            createdAt = DateTimeOffset.Now,
            os = Environment.OSVersion.VersionString,
            settings = new
            {
                routerUrl = MaskUrl(settings.RouterUrl),
                wolPcName = settings.WolPcName,
                wolPcMac = InputRules.MaskMac(settings.WolPcMac),
                publicHost = MaskHost(settings.PublicHost),
                publicRdpPort = settings.PublicRdpPort,
                vpnName = settings.VpnName,
                vpnDesktopIp = MaskHost(settings.VpnDesktopIp),
                vpnRdpPort = settings.VpnRdpPort,
                bootWaitSeconds = settings.BootWaitSeconds,
                vpnWaitSeconds = settings.VpnWaitSeconds,
                rdpFullScreen = settings.RdpFullScreen,
                autoCollapseAfterLogin = settings.AutoCollapseAfterLogin,
                autoConfirmWakeDialog = settings.AutoConfirmWakeDialog,
                allowRouterCertificateError = settings.AllowRouterCertificateError,
                wolPageRoute = settings.WolPageRoute,
                wolPagePath = settings.WolPagePath,
                autoSelectAdminTool = settings.AutoSelectAdminTool,
                adminToolLabel = settings.AdminToolLabel,
                wolMenuGroupLabel = settings.WolMenuGroupLabel,
                wolMenuLabel = settings.WolMenuLabel,
                wakeButtonPattern = settings.WakeButtonPattern,
                sessionProbeIntervalSeconds = settings.SessionProbeIntervalSeconds,
            },
            browser = browser == null ? null : new
            {
                ready = browser.IsReady,
                initError = browser.LastInitError,
                url = MaskUrl(browser.CurrentUrl),
                onRouterOrigin = browser.IsOnRouterOrigin,
                session = new
                {
                    state = browser.Session.State.ToString(),
                    describe = browser.Session.Describe(),
                    lastReason = browser.Session.LastReason,
                    probeNow = session == null ? null : new { result = session.Result.ToString(), reason = session.Reason, http = session.HttpStatus, code = session.ErrorCode },
                },
            },
            probe = snap == null ? null : new
            {
                url = MaskUrl(snap.Url),
                title = snap.Title,
                readyState = snap.ReadyState,
                flutter = snap.Flutter,
                placeholder = snap.Placeholder,
                roots = snap.Roots,
                semanticsCount = snap.SemanticsCount,
                paragraphCount = snap.ParagraphCount,
                markers = snap.Markers,
                error = snap.Error,
                elapsedMs = snap.ElapsedMs,
                nodes = snap.Nodes.Select(n => new
                {
                    n.Index, n.Id, n.Parent, n.Depth, n.Role, n.InputType, n.Hidden, n.Disabled,
                    label = InputRules.MaskMac(n.Label),
                    rect = new { x = Math.Round(n.Rect.X), y = Math.Round(n.Rect.Y), w = Math.Round(n.Rect.W), h = Math.Round(n.Rect.H) },
                }),
                paragraphs = snap.Paragraphs.Select(p => new
                {
                    p.Index,
                    text = InputRules.MaskMac(p.Text),
                    rect = new { x = Math.Round(p.Rect.X), y = Math.Round(p.Rect.Y), w = Math.Round(p.Rect.W), h = Math.Round(p.Rect.H) },
                }),
            },
            network = browser?.Network.Snapshot(60).Select(c => new
            {
                time = c.StartedAt,
                c.Method,
                path = MaskUrl(c.Path),
                c.Status,
                c.Completed,
                c.Failed,
                c.ErrorCode,
                c.ErrorMessage,
                c.ResultOk,
                paramsMasked = c.ParamsMasked,
            }),
            pageKind = snap == null ? null : RouterPages.Classify(snap, settings.UiText).ToString(),
            log = log.Snapshot(300).Select(e => MaskHostsInText(e.Time.ToString("HH:mm:ss.fff") + " [" + e.LevelText + "] " + e.Message, settings)),
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    public static string MaskHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "";
        var h = host.Trim();
        if (System.Net.IPAddress.TryParse(h, out _))
        {
            var parts = h.Split('.');
            if (parts.Length == 4) return parts[0] + "." + parts[1] + ".*.*";
            return h[..Math.Min(4, h.Length)] + "…";
        }
        var labels = h.Split('.');
        if (labels.Length >= 2)
        {
            var first = labels[0];
            var masked = first.Length <= 2 ? first[..1] + "*" : first[..2] + new string('*', Math.Min(6, first.Length - 2));
            return masked + "." + string.Join('.', labels.Skip(1).Select((l, i) => i == labels.Length - 2 ? l : "*")) ;
        }
        return h.Length <= 2 ? "*" : h[..2] + "***";
    }

    /// <summary>
    /// 로그 문장 안의 주소를 가린다: 설정에 있는 호스트(공유기·일반 접속·VPN 내부 IP),
    /// 그리고 문장에 나타나는 모든 URL 호스트와 IPv4 주소.
    /// </summary>
    public static string MaskHostsInText(string text, AppSettings settings)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = text;
        var hosts = new List<string>();
        if (settings.RouterUri?.Host is { Length: > 0 } rh) hosts.Add(rh);
        if (!string.IsNullOrWhiteSpace(settings.PublicHost)) hosts.Add(settings.PublicHost.Trim());
        if (!string.IsNullOrWhiteSpace(settings.VpnDesktopIp)) hosts.Add(settings.VpnDesktopIp.Trim());
        foreach (var h in hosts.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(h => h.Length))
            s = System.Text.RegularExpressions.Regex.Replace(s, System.Text.RegularExpressions.Regex.Escape(h), MaskHost(h), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?i)\b(https?://)([^/\s:?#]+)", m => m.Groups[1].Value + MaskHost(m.Groups[2].Value));
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b", m => $"{m.Groups[1].Value}.{m.Groups[2].Value}.*.*");
        return s;
    }

    public static string MaskUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "<url>";
        var port = u.IsDefaultPort ? "" : ":" + u.Port;
        var fragment = u.Fragment.Split('?')[0]; // 해시 경로 뒤의 쿼리도 제거
        return $"{u.Scheme}://{MaskHost(u.Host)}{port}{u.AbsolutePath}{fragment}";
    }
}
