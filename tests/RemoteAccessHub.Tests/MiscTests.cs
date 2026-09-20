using RemoteAccessHub.Core;
using RemoteAccessHub.Router;
using RemoteAccessHub.Services;
using Xunit;

namespace RemoteAccessHub.Tests;

public class MiscTests
{
    [Fact]
    public void NetworkObserver_parses_method_and_masks_params()
    {
        var (m, p) = NetworkObserver.ParseBody("{\"method\":\"wol/signal\",\"params\":[\"00:11:22:33:44:55\"]}");
        Assert.Equal("wol/signal", m);
        Assert.NotNull(p);
        Assert.DoesNotContain("22:33:44", p);
        Assert.Contains("00:11:**:**:**:55", p);

        var (m2, p2) = NetworkObserver.ParseBody("{\"method\":\"session/login\",\"params\":{\"id\":\"admin\",\"pw\":\"secret\"}}");
        Assert.Equal("session/login", m2);
        Assert.Equal("<가림>", p2);

        var (m3, _) = NetworkObserver.ParseBody("not json");
        Assert.Equal("", m3);
    }

    [Fact]
    public void NetworkObserver_parses_router_result_format()
    {
        var ok = NetworkObserver.ParseResult("{\"result\":\"ok\"}");
        Assert.True(ok.Ok);
        var unauth = NetworkObserver.ParseResult("{\"result\":null, \"error\":{\"code\":-31998, \"message\":\"Unauthenticated\"}}");
        Assert.False(unauth.Ok);
        Assert.Equal(-31998, unauth.Code);
        Assert.Equal("Unauthenticated", unauth.Message);
        var nul = NetworkObserver.ParseResult("{\"result\":null}");
        Assert.False(nul.Ok);
        Assert.Null(nul.Code);
    }

    [Fact]
    public void Log_redaction_hides_secrets_queries_and_macs()
    {
        var s = AppLog.Redact("login password=hunter2 cookie: efm_session_id=abc token=xyz url http://r/x?sid=123 mac 00:11:22:33:44:55");
        Assert.DoesNotContain("hunter2", s);
        Assert.DoesNotContain("abc", s);
        Assert.DoesNotContain("xyz", s);
        Assert.DoesNotContain("sid=123", s);
        Assert.DoesNotContain("22:33:44", s);
    }

    [Fact]
    public void Diagnostics_masks_hosts_and_urls()
    {
        Assert.Equal("192.168.*.*", DiagnosticsExporter.MaskHost("192.168.0.1"));
        var m = DiagnosticsExporter.MaskHost("myhome.iptime.org");
        Assert.DoesNotContain("myhome", m);
        Assert.EndsWith("org", m);
        var u = DiagnosticsExporter.MaskUrl("http://myhome.iptime.org:8080/ui/#/wol?x=1");
        Assert.DoesNotContain("myhome", u);
        Assert.Contains(":8080", u);
        Assert.DoesNotContain("x=1", u);
    }

    [Fact]
    public void ProbeSnapshot_parses_script_result_wrapped_as_json_string()
    {
        var inner = "{\"url\":\"http://r/ui/#/wol\",\"flutter\":true,\"semanticsCount\":2,\"nodes\":[{\"index\":0,\"id\":\"a\",\"role\":\"button\",\"label\":\"PC 켜기\",\"rect\":{\"x\":1,\"y\":2,\"w\":3,\"h\":4}}],\"paragraphs\":[],\"markers\":{\"passwordInput\":false}}";
        var wrapped = System.Text.Json.JsonSerializer.Serialize(inner);
        var snap = ProbeSnapshot.Parse(wrapped);
        Assert.Null(snap.Error);
        Assert.True(snap.Flutter);
        Assert.Single(snap.Nodes);
        Assert.Equal(3, snap.Nodes[0].Rect.W);
        Assert.True(snap.Nodes[0].IsButton);

        var bad = ProbeSnapshot.Parse("null");
        Assert.NotNull(bad.Error);
    }

    [Fact]
    public void Confirm_button_is_chosen_below_dialog_text()
    {
        var snap = new ProbeSnapshot();
        snap.Paragraphs.Add(new Paragraph { Index = 0, Text = "PC를 켜시겠습니까?", Rect = new ProbeRect(330, 240, 300, 20) });
        snap.Nodes.Add(new SemanticNode { Index = 0, Id = "top-ok", Role = "button", Label = "확인", Rect = new ProbeRect(900, 20, 90, 36) });
        snap.Nodes.Add(new SemanticNode { Index = 1, Id = "cancel", Role = "button", Label = "취소", Rect = new ProbeRect(480, 320, 90, 36) });
        snap.Nodes.Add(new SemanticNode { Index = 2, Id = "ok", Role = "button", Label = "확인", Rect = new ProbeRect(590, 320, 90, 36) });
        var b = WolAutomation.FindConfirmButton(snap, "PC를 켜시겠습니까");
        Assert.NotNull(b);
        Assert.Equal("ok", b!.Id);
    }

    [Fact]
    public void Ras_error_description_never_includes_credentials()
    {
        var d = VpnService.DescribeRasError(691, "Connecting to HomeVPN...\r\nUser name: admin Password: secret");
        Assert.Contains("691", d);
        Assert.DoesNotContain("secret", d);
    }

    [Fact]
    public void Rect_overlap_and_union()
    {
        var a = new ProbeRect(0, 100, 10, 20);
        var b = new ProbeRect(50, 110, 10, 20);
        Assert.Equal(0.5, ProbeRect.VerticalOverlapRatio(a, b), 3);
        var u = ProbeRect.Union(a, b);
        Assert.Equal(100, u.Y);
        Assert.Equal(30, u.H);
        Assert.Equal(0, ProbeRect.VerticalOverlapRatio(a, new ProbeRect(0, 200, 5, 5)));
    }
}

public class AbortedRequestTests
{
    [Theory]
    [InlineData(true, "net::ERR_ABORTED", true)]
    [InlineData(true, "net::ERR_CONNECTION_RESET", false)]   // 진짜 네트워크 오류는 그대로 오류로 보고
    [InlineData(false, "net::ERR_ABORTED", false)]           // 실패하지 않은 요청
    public void Only_page_cancelled_requests_count_as_aborted(bool failed, string error, bool expected)
    {
        var call = new RemoteAccessHub.Router.ApiCall { Failed = failed, ErrorMessage = error };
        Assert.Equal(expected, RemoteAccessHub.Router.WolAutomation.IsAborted(call));
    }
}
