using System.Text.RegularExpressions;
using RemoteAccessHub.Core;
using RemoteAccessHub.Router;
using Xunit;

namespace RemoteAccessHub.Tests;

/// <summary>
/// 2026-09-14 실제 AX2004T(15.36.6)에서 사용자가 로그인 후 WOL 화면에서 내보낸 진단 파일의 구조를 옮긴 검사.
/// MAC 주소와 주소는 가짜 값으로 바꿨다. 좌표·부모 관계·라벨 형식은 실제 값 그대로다.
/// </summary>
public class RealDeviceTests
{
    private static readonly Regex Wake = new(@"^PC\s*켜기$");
    private const string FakeMac = "02:00:AA:BB:CC:BF";
    private static readonly RouterUiText Ui = new();

    private sealed class Builder
    {
        public readonly ProbeSnapshot Snap = new() { Flutter = true, Title = "AX2004T", ReadyState = "complete" };
        public int Node(int parent, string role, string label, double x, double y, double w, double h, string? id = null)
        {
            var i = Snap.Nodes.Count;
            Snap.Nodes.Add(new SemanticNode { Index = i, Id = id ?? $"flt-semantic-node-r{i}", Parent = parent, Role = role, Label = label, Rect = new ProbeRect(x, y, w, h) });
            return i;
        }
        public void Para(string text, double x, double y, double w, double h) =>
            Snap.Paragraphs.Add(new Paragraph { Index = Snap.Paragraphs.Count, Text = text, Rect = new ProbeRect(x, y, w, h) });
    }

    /// <summary>실제 왼쪽 메뉴(특수 기능 펼침, 스크롤된 상태). 메뉴 항목의 접근성 위치는 실기기처럼 모두 y=68.</summary>
    private static (Builder B, int ContentParent) RealShell(bool withWolContent)
    {
        var b = new Builder();
        var n0 = b.Node(-1, "", "", 0, 0, 1180, 720);
        var n1 = b.Node(n0, "", "", 0, 0, 1180, 720);
        var n2 = b.Node(n1, "dialog", "", 0, 0, 1180, 720);
        var menu = b.Node(n2, "", "메뉴 접기", 0, 0, 310, 720);
        b.Node(menu, "button", "", 256, 20, 40, 40);
        var scroll = b.Node(menu, "", "", 0, 68, 310, 517);
        var group = b.Node(scroll, "group", "", 0, 68, 310, 517);
        foreach (var (label, w, h) in new[] { ("기본 메뉴", 310.0, 42.0), ("시스템 요약 정보", 112, 42), ("무선랜 관리", 78, 42), ("NAT/라우터 관리", 114, 42), ("보안 기능", 63, 42) })
            b.Node(group, "", label, 0, 68, w, h);
        var expert = b.Node(group, "", "", 0, 68, 108, 42);
        b.Node(expert, "", "특수 기능", 0, 68, 63, 42);
        b.Node(expert, "", "DDNS 설정", 0, 68, 77, 24);
        b.Node(expert, "", "IPTV 설정", 0, 68, 67, 24);
        var fav = b.Node(expert, "group", "즐겨찾기에 추가 WOL 기능", 0, 68, 0, 0);
        b.Node(fav, "button", "", 0, 68, 0, 0);
        b.Node(expert, "", "호스트 검색", 0, 68, 78, 24);
        b.Node(group, "", "VPN 설정", 0, 68, 65, 42);
        b.Node(menu, "", "홈으로 이동", 0, 615, 310, 42);
        b.Node(menu, "", "로그아웃", 0, 668, 310, 42);

        foreach (var (t, x, y, w, h) in new[] { ("무선랜 관리", 62.0, 89.0, 70.0, 20.0), ("NAT/라우터 관리", 62, 173, 102, 20), ("보안 기능", 62, 215, 57, 20), ("특수 기능", 62, 257, 57, 20),
                     ("DDNS 설정", 62, 299, 69, 20), ("IPTV 설정", 62, 341, 60, 20), ("WOL 기능", 62, 383, 61, 20), ("호스트 검색", 62, 425, 70, 20), ("VPN 설정", 62, 551, 58, 20),
                     ("홈으로 이동", 64, 626, 70, 20), ("로그아웃", 64, 679, 52, 20), ("", 267, 382, 22, 22), ("AX2004T", 335, 680, 59, 20) })
            b.Para(t, x, y, w, h);

        var content = b.Node(n2, "", "", 310, 0, 870, 660);
        var contentDialog = b.Node(content, "dialog", "", 310, 0, 870, 660);
        b.Node(n2, "", "라이트 모드로 전환", 421, 670, 40, 40);
        b.Node(b.Snap.Nodes.Count - 1, "button", "", 421, 670, 40, 40);
        b.Node(n2, "", "AX2004T", 335, 680, 59, 20);
        b.Snap.Markers.LogoutLabel = true;

        if (!withWolContent)
        {
            b.Node(contentDialog, "", "시스템 요약 정보", 350, 13, 760, 31);
            b.Para("시스템 요약 정보", 350, 13, 160, 31);
        }
        return (b, contentDialog);
    }

    /// <summary>실제 WOL 화면. rows = (이름, MAC). 실기기 행 간격은 첫 행만 관찰(105)했으므로 이후 행은 62px 간격으로 가정.</summary>
    private static ProbeSnapshot RealWolPage(params (string Name, string Mac)[] rows)
    {
        var (b, cd) = RealShell(withWolContent: true);
        b.Node(cd, "", "WOL 기능", 350, 13, 760, 31);
        var refresh = b.Node(cd, "", "페이지 새로고침", 1110, 9, 40, 40);
        b.Node(refresh, "button", "", 1110, 9, 40, 40);
        var list1 = b.Node(cd, "", "", 310, 58, 870, 602);
        var list2 = b.Node(list1, "", "", 310, 58, 870, 602);
        b.Node(list2, "", $"PC 이름 ({rows.Length}/500)", 340, 69, 530, 24);
        b.Node(list2, "", "MAC 주소", 870, 69, 170, 24);
        b.Node(list2, "", "검색", 1040, 66, 30, 30);
        b.Node(list2, "", "삭제", 1080, 66, 30, 30);
        b.Node(list2, "", "추가", 1120, 66, 30, 30);
        b.Para("WOL 기능", 350, 13, 96, 31);
        b.Para("PC 이름", 360, 71, 49, 20);
        b.Para($"({rows.Length}/500)", 409, 71, 52, 20);
        b.Para("MAC 주소", 890, 71, 60, 20);
        for (var i = 0; i < rows.Length; i++)
        {
            var y = 105 + i * 62;
            var row = b.Node(list2, "group", rows[i].Name + " " + rows[i].Mac, 330, y, 830, 52, $"row{i}");
            b.Node(row, "button", "PC 켜기", 1094, y + 10, 56, 32, $"wake{i}");
            b.Para(rows[i].Name, 360, y + 16, 79, 20);
            b.Para(rows[i].Mac, 890, y + 16, 127, 20);
            b.Para("PC 켜기", 1098, y + 19, 48, 14);
        }
        b.Snap.Markers.WakeButtons = rows.Length;
        b.Snap.SemanticsCount = b.Snap.Nodes.Count;
        b.Snap.ParagraphCount = b.Snap.Paragraphs.Count;
        return b.Snap;
    }

    [Fact]
    public void Real_wol_page_is_classified_as_wol_list()
    {
        var snap = RealWolPage(("MY-PC", FakeMac));
        Assert.Equal(RouterPageKind.WolList, RouterPages.Classify(snap, Ui));
    }

    [Fact]
    public void Real_wol_page_matches_structurally()
    {
        var snap = RealWolPage(("MY-PC", FakeMac));
        var r = WolMatcher.Match(snap, new WolTarget("MY-PC", null), Wake);
        Assert.True(r.IsFound, r.Message + " | " + string.Join(" / ", r.Details));
        Assert.Equal("구조", r.Target!.Strategy);
        Assert.Equal("wake0", r.Target.NodeId);
        Assert.Equal(new ProbeRect(1094, 115, 56, 32), r.Target.Rect);
    }

    [Fact]
    public void Real_structure_with_configured_mac()
    {
        var snap = RealWolPage(("MY-PC", FakeMac));
        Assert.True(WolMatcher.Match(snap, new WolTarget("MY-PC", "02-00-aa-bb-cc-bf"), Wake).IsFound);
        Assert.Equal(WolMatchStatus.MacMismatch, WolMatcher.Match(snap, new WolTarget("MY-PC", "02:00:AA:BB:CC:00"), Wake).Status);
    }

    [Fact]
    public void Real_structure_multiple_rows_picks_own_row_button()
    {
        var snap = RealWolPage(("OFFICE-PC", "02:00:AA:BB:CC:01"), ("MY-PC", FakeMac), ("NAS", "02:00:AA:BB:CC:03"));
        var r = WolMatcher.Match(snap, new WolTarget("MY-PC", null), Wake);
        Assert.True(r.IsFound, r.Message);
        Assert.Equal("wake1", r.Target!.NodeId);

        var miss = WolMatcher.Match(snap, new WolTarget("GAME-PC", null), Wake);
        Assert.Equal(WolMatchStatus.TargetNotFound, miss.Status);
        Assert.Contains("OFFICE-PC", miss.Message);
        Assert.Contains("NAS", miss.Message);
    }

    [Fact]
    public void Real_structure_duplicate_names_need_mac()
    {
        var snap = RealWolPage(("MY-PC", "02:00:AA:BB:CC:01"), ("MY-PC", FakeMac));
        Assert.Equal(WolMatchStatus.Ambiguous, WolMatcher.Match(snap, new WolTarget("MY-PC", null), Wake).Status);
        var withMac = WolMatcher.Match(snap, new WolTarget("MY-PC", FakeMac), Wake);
        Assert.True(withMac.IsFound);
        Assert.Equal("wake1", withMac.Target!.NodeId);
    }

    [Fact]
    public void Geometric_fallback_on_real_layout_ignores_big_menu_container()
    {
        // 부모 관계를 끊어 좌표 매칭을 강제한다. "메뉴 접기"(0,0 310x720) 같은 큰 컨테이너가 모호함을 만들면 안 된다.
        var snap = RealWolPage(("OFFICE-PC", "02:00:AA:BB:CC:01"), ("MY-PC", FakeMac));
        foreach (var n in snap.Nodes.Where(n => n.IsButton)) n.Parent = -1;
        var r = WolMatcher.Match(snap, new WolTarget("MY-PC", null), Wake);
        Assert.True(r.IsFound, r.Message + " | " + string.Join(" / ", r.Details));
        Assert.Equal("좌표", r.Target!.Strategy);
        Assert.Equal("wake1", r.Target.NodeId);
    }

    [Fact]
    public void Real_admin_page_without_wol_content_is_admin_main()
    {
        var (b, _) = RealShell(withWolContent: false);
        Assert.Equal(RouterPageKind.AdminMain, RouterPages.Classify(b.Snap, Ui));
    }

    [Fact]
    public void Menu_click_guard_uses_real_menu_geometry()
    {
        var (b, _) = RealShell(withWolContent: false);
        var snap = b.Snap;
        // 실제로 보이는 'WOL 기능' 텍스트 위치는 안전
        Assert.True(RouterPages.IsPointSafe(snap, "WOL 기능", 62 + 30, 383 + 10, out var r1), r1);
        Assert.True(RouterPages.IsPointSafe(snap, "특수 기능", 62 + 28, 257 + 10, out var r2), r2);
        // 스크롤로 가려져 로그아웃 줄에 걸친 위치는 거부
        Assert.False(RouterPages.IsPointSafe(snap, "WOL 기능", 62 + 30, 680 + 10, out var r3));
        Assert.Contains("로그아웃", r3);
        Assert.False(RouterPages.IsPointSafe(snap, "WOL 기능", 62 + 30, 626 + 10, out var r4));
        Assert.Contains("홈으로 이동", r4);
        // 메뉴 위쪽(메뉴 접기 영역만 포함)도 거부
        Assert.False(RouterPages.IsPointSafe(snap, "WOL 기능", 30, 40, out _));
    }

    [Fact]
    public void Real_login_page_is_login()
    {
        var b = new Builder();
        var root = b.Node(-1, "", "", 0, 0, 1184, 753);
        var form = b.Node(root, "", "", 414, 132, 355, 455);
        b.Node(form, "", "AX2004T", 688, 148, 82, 29);
        b.Node(form, "", "로그인 이름", 414, 206, 70, 20);
        var pw = b.Node(form, "", "", 414, 301, 355, 38);
        b.Snap.Nodes[pw].InputType = "password";
        b.Node(form, "button", "로그인", 414, 543, 355, 44);
        b.Snap.Markers.PasswordInput = true;
        b.Snap.Markers.LoginButton = true;
        Assert.Equal(RouterPageKind.Login, RouterPages.Classify(b.Snap, Ui));
    }

    [Fact]
    public void Mode_select_with_button_widgets()
    {
        // 공유기 앱 코드 기준 추정 구조: 버튼 위젯, 라벨 = 제목 + 부제목
        var b = new Builder();
        var root = b.Node(-1, "", "", 0, 0, 1184, 753);
        b.Node(root, "", "AX2004T", 688, 148, 82, 29);
        b.Node(root, "button", "관리도구\n버전 15.36.6", 414, 250, 355, 70, "admin");
        b.Node(root, "button", "설정마법사\n간편설정", 414, 340, 355, 70, "wizard");
        b.Para("관리도구", 500, 264, 60, 20);
        b.Para("버전 15.36.6", 500, 288, 90, 20);
        b.Para("설정마법사", 500, 354, 75, 20);
        b.Para("간편설정", 500, 378, 60, 20);
        Assert.Equal(RouterPageKind.ModeSelect, RouterPages.Classify(b.Snap, Ui));
        var t = RouterPages.FindAdminToolTargets(b.Snap, Ui);
        Assert.Single(t);
        Assert.Equal("semantics", t[0].Kind);
        Assert.Equal("admin", t[0].NodeId);
        Assert.Equal("관리도구 버전 15.36.6", t[0].Text);
    }

    [Fact]
    public void Mode_select_with_text_only()
    {
        var b = new Builder();
        b.Para("AX2004T", 688, 148, 82, 29);
        b.Para("관리도구", 500, 264, 60, 20);
        b.Para("버전 15.36.6", 500, 288, 90, 20);
        b.Para("설정마법사", 500, 354, 75, 20);
        var t = RouterPages.FindAdminToolTargets(b.Snap, Ui);
        Assert.Equal(RouterPageKind.ModeSelect, RouterPages.Classify(b.Snap, Ui));
        Assert.Single(t);
        Assert.Equal("paragraph", t[0].Kind);
    }

    [Fact]
    public void Admin_label_inside_other_text_is_not_mode_select()
    {
        var (b, _) = RealShell(withWolContent: false);
        b.Para("관리도구", 400, 300, 60, 20); // 관리 화면(로그아웃 표시) 안의 같은 글자는 선택 화면이 아님
        Assert.Equal(RouterPageKind.AdminMain, RouterPages.Classify(b.Snap, Ui));
    }

    [Fact]
    public void Settings_migrate_old_hash_route_to_real_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "rah-mig-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"RouterUrl\":\"http://r.example:8080/\",\"WolPageRoute\":\"#/wol\"}");
            var s = AppSettings.Load(path);
            Assert.Equal("/ui/wol", s.WolPageRoute);
            Assert.Equal("/ui/wol", s.WolPagePath);
            Assert.True(s.AutoSelectAdminTool);
            Assert.Equal("관리도구", s.AdminToolLabel);
        }
        finally { File.Delete(path); }

        Assert.Equal("/ui/wol", new AppSettings { WolPageRoute = "#/wol" }.WolPagePath);
        Assert.Equal("/ui/wol", new AppSettings { WolPageRoute = "wol" }.WolPagePath);
        Assert.Equal("/x/wol", new AppSettings { WolPageRoute = "/x/wol" }.WolPagePath);
        Assert.Equal("/ui/wol", new AppSettings { WolPageRoute = "http://r/ui/wol" }.WolPagePath);
    }

    /// <summary>실기기 확인창(2026-09-14 화면): 제목 "알림", 문구, 아래 한 줄에 [취소] [확인]이 반씩.</summary>
    private static ProbeSnapshot RealConfirmDialog(bool buttonRole, bool withSemantics = true)
    {
        var (b, cd) = RealShell(withWolContent: true);
        if (withSemantics)
        {
            var dlg = b.Node(0, "dialog", "", 426, 288, 320, 143);
            b.Node(dlg, "", "알림", 446, 302, 30, 20);
            b.Node(dlg, "", "PC를 켜시겠습니까?", 480, 344, 140, 20);
            b.Node(dlg, buttonRole ? "button" : "", "취소", 426, 388, 159, 43, "cancel");
            b.Node(dlg, buttonRole ? "button" : "", "확인", 586, 388, 160, 43, "ok");
        }
        else
        {
            b.Snap.Nodes.Clear();
        }
        b.Para("알림", 446, 302, 30, 20);
        b.Para("PC를 켜시겠습니까?", 480, 344, 140, 20);
        b.Para("취소", 492, 400, 28, 20);
        b.Para("확인", 652, 400, 28, 20);
        return b.Snap;
    }

    [Fact]
    public void Confirm_on_real_dialog_picks_ok_never_cancel()
    {
        var t = WolAutomation.FindConfirmTargets(RealConfirmDialog(buttonRole: true), "PC를 켜시겠습니까");
        Assert.NotEmpty(t);
        Assert.Equal("semantics", t[0].Kind);
        Assert.Equal("ok", t[0].NodeId);
        Assert.DoesNotContain(t, x => x.Text == "취소" || x.NodeId == "cancel");
        Assert.Contains(t, x => x.Kind == "paragraph" && x.Rect.X == 652);
    }

    [Fact]
    public void Confirm_on_real_dialog_without_button_role_uses_node_then_text()
    {
        var snap = RealConfirmDialog(buttonRole: false);
        var t = WolAutomation.FindConfirmTargets(snap, "PC를 켜시겠습니까");
        Assert.Null(WolAutomation.FindConfirmButton(snap, "PC를 켜시겠습니까"));
        Assert.Equal("node", t[0].Kind);
        Assert.Equal("ok", t[0].NodeId);
        Assert.True(RouterPages.IsPointSafe(snap, "확인", t[0].Rect.CenterX, t[0].Rect.CenterY, out var why), why);
        // [취소] 위치는 안전 검사에서도 거부된다
        Assert.False(RouterPages.IsPointSafe(snap, "확인", 505, 409, out _));
    }

    [Fact]
    public void Confirm_on_real_dialog_text_only()
    {
        var snap = RealConfirmDialog(buttonRole: true, withSemantics: false);
        var t = WolAutomation.FindConfirmTargets(snap, "PC를 켜시겠습니까");
        Assert.Single(t);
        Assert.Equal("paragraph", t[0].Kind);
        Assert.Equal(652, t[0].Rect.X);
    }

    [Fact]
    public void Confirm_ignores_ok_buttons_outside_the_dialog()
    {
        var snap = RealConfirmDialog(buttonRole: true);
        // 창 밖(위쪽, 먼 오른쪽 아래)의 '확인'은 후보가 아니다
        snap.Nodes.Add(new SemanticNode { Index = snap.Nodes.Count, Id = "far1", Parent = 0, Role = "button", Label = "확인", Rect = new ProbeRect(1000, 20, 80, 36) });
        snap.Nodes.Add(new SemanticNode { Index = snap.Nodes.Count, Id = "far2", Parent = 0, Role = "button", Label = "확인", Rect = new ProbeRect(1100, 690, 60, 30) });
        var t = WolAutomation.FindConfirmTargets(snap, "PC를 켜시겠습니까");
        Assert.DoesNotContain(t, x => x.NodeId is "far1" or "far2");
        Assert.Equal("ok", t[0].NodeId);
    }

    [Fact]
    public void Settings_turn_on_auto_confirm_once_for_pre_1_1_1_files()
    {
        var path = Path.Combine(Path.GetTempPath(), "rah-ac-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // 1.1.0 이전 파일: 버전 없음 + 옛 기본값 false
            File.WriteAllText(path, "{\"RouterUrl\":\"http://r.example/\",\"AutoConfirmWakeDialog\":false}");
            var s = AppSettings.Load(path);
            Assert.True(s.AutoConfirmWakeDialog);

            // 새 버전으로 저장된 뒤 사용자가 끈 값은 유지
            s.AutoConfirmWakeDialog = false;
            s.Save(path);
            var again = AppSettings.Load(path);
            Assert.False(again.AutoConfirmWakeDialog);
            Assert.Equal(AppSettings.CurrentSettingsVersion, again.SettingsVersion);
        }
        finally { File.Delete(path); }
        Assert.True(new AppSettings().AutoConfirmWakeDialog);
    }

    [Fact]
    public void Diagnostics_mask_hosts_in_log_lines_like_the_real_leak()
    {
        var s = new AppSettings { RouterUrl = "http://myhome.iptime.org:8080/", PublicHost = "myhome.iptime.org", VpnDesktopIp = "192.168.0.2" };
        var lines = new[]
        {
            "14:32:10.496 [정보] 페이지 이동: http://myhome.iptime.org:8080/",
            "14:36:16.292 [정보] RDP 포트 응답 확인: 192.168.0.2:3390",
            "14:36:16.295 [정보] 원격 데스크톱 실행: mstsc /v:192.168.0.2:3390 /f",
            "other http://another-host.example.net/x",
        };
        foreach (var l in lines)
        {
            var m = DiagnosticsExporter.MaskHostsInText(l, s);
            Assert.DoesNotContain("myhome", m);
            Assert.DoesNotContain("174.2", m);
            Assert.DoesNotContain("another-host", m);
        }
        Assert.DoesNotContain("myhome", DiagnosticsExporter.MaskUrl("http://myhome.iptime.org:8080/cgi/service.cgi"));
    }
}
