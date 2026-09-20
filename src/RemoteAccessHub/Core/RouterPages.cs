using System.Text.RegularExpressions;

namespace RemoteAccessHub.Core;

public enum RouterPageKind
{
    /// <summary>판독 불가(오류, 빈 화면).</summary>
    Unknown,
    /// <summary>공유기 앱 로딩 중.</summary>
    Loading,
    /// <summary>로그인 화면(비밀번호 입력칸).</summary>
    Login,
    /// <summary>로그인 직후 [관리도구] / [설정마법사] 등을 고르는 화면.</summary>
    ModeSelect,
    /// <summary>관리도구 본 화면(왼쪽 메뉴, 로그아웃).</summary>
    AdminMain,
    /// <summary>WOL 목록 화면.</summary>
    WolList,
}

/// <summary>화면 판정에 쓰는 공유기 UI 문구. 설정에서 바꿀 수 있다.</summary>
public sealed record RouterUiText(
    string AdminToolLabel = "관리도구",
    string WolMenuLabel = "WOL 기능",
    string WolMenuGroupLabel = "특수 기능",
    string WakeButtonPattern = @"^PC\s*켜기$",
    string EmptyListText = "등록된 WOL PC가 없습니다");

/// <summary>
/// 화면 종류 판정. 2026-09-14 실제 AX2004T(15.36.6) 진단 파일의 구조를 기준으로 만들었다.
/// - 로그인 화면: 비밀번호 입력칸이 있는 접근성 노드
/// - 관리 화면: 왼쪽 메뉴의 "로그아웃" 라벨
/// - WOL 화면: role=button "PC 켜기", 또는 "PC 이름 (n/500)" 머리글 + "MAC 주소"
/// - 선택 화면: 공유기 앱 코드상 버튼 위젯(라벨 = "관리도구" + 버전 문구). 실제 화면은 아직 관찰하지 못했다.
/// </summary>
public static class RouterPages
{
    public static RouterPageKind Classify(ProbeSnapshot s, RouterUiText t)
    {
        if (s.Error != null) return RouterPageKind.Unknown;
        if (s.Markers.LoadingOverlay) return RouterPageKind.Loading;
        if (!s.IsReadable) return RouterPageKind.Unknown;
        if (s.Markers.PasswordInput) return RouterPageKind.Login;
        if (IsWolList(s, t)) return RouterPageKind.WolList;
        if (!s.Markers.LogoutLabel && FindAdminToolTargets(s, t).Count > 0) return RouterPageKind.ModeSelect;
        if (s.Markers.LogoutLabel) return RouterPageKind.AdminMain;
        return RouterPageKind.Unknown;
    }

    public static bool IsWolList(ProbeSnapshot s, RouterUiText t)
    {
        if (s.Markers.PasswordInput) return false;
        var wake = SafeRegex(t.WakeButtonPattern);
        if (s.Nodes.Any(n => n.IsButton && n.IsUsable && wake.IsMatch(Compact(n.Label)))) return true;
        if (s.Paragraphs.Any(p => !p.Rect.IsEmpty && wake.IsMatch(Compact(p.Text)))) return true;
        if (s.ContainsText(t.EmptyListText)) return true;
        // 실기기 머리글: "PC 이름 (1/500)" + "MAC 주소"
        var header = s.AllTexts().Any(x => Regex.IsMatch(Compact(x), @"^PC 이름\s*\(\d+/\d+\)$"));
        return header && s.ContainsText("MAC 주소");
    }

    public sealed record ClickTarget(string Kind, string NodeId, string Text, ProbeRect Rect);

    /// <summary>
    /// 선택 화면의 [관리도구] 후보. 접근성 버튼(라벨이 "관리도구"로 시작)이 우선이고,
    /// 없으면 정확히 "관리도구"인 장면 텍스트를 쓴다.
    /// </summary>
    public static IReadOnlyList<ClickTarget> FindAdminToolTargets(ProbeSnapshot s, RouterUiText t)
    {
        var label = Compact(t.AdminToolLabel);
        if (label.Length == 0) return Array.Empty<ClickTarget>();
        var starts = new Regex("^" + Regex.Escape(label) + @"(\s|$)");
        var buttons = s.Nodes
            .Where(n => n.IsButton && n.IsUsable && starts.IsMatch(Compact(n.Label)))
            .Select(n => new ClickTarget("semantics", n.Id, Compact(n.Label), n.Rect))
            .ToList();
        if (buttons.Count > 0) return buttons;
        return s.Paragraphs
            .Where(p => !p.Rect.IsEmpty && Compact(p.Text) == label)
            .Select(p => new ClickTarget("paragraph", "", label, p.Rect))
            .ToList();
    }

    /// <summary>화면에 보이는 메뉴 텍스트(정확히 일치) 후보.</summary>
    public static IReadOnlyList<Paragraph> FindParagraphs(ProbeSnapshot s, string text)
    {
        var want = Compact(text);
        return s.Paragraphs.Where(p => !p.Rect.IsEmpty && Compact(p.Text) == want).ToList();
    }

    /// <summary>
    /// 좌표 클릭 안전 검사. 클릭 지점을 포함하는 가장 작은 접근성 노드가
    /// 다른 라벨(예: "로그아웃", "홈으로 이동")을 가지고 있으면 클릭하지 않는다.
    /// 실기기에서 스크롤 메뉴 항목이 가려진 위치에 걸쳐 있을 때 엉뚱한 항목을 누르는 것을 막는다.
    /// </summary>
    public static bool IsPointSafe(ProbeSnapshot s, string intendedText, double x, double y, out string reason)
    {
        reason = "";
        if (x < 0 || y < 0) { reason = "화면 밖 좌표"; return false; }
        var want = Compact(intendedText);
        var containing = s.Nodes
            .Where(n => !n.Rect.IsEmpty && !n.Hidden && n.Rect.Contains(x, y))
            .OrderBy(n => n.Rect.W * n.Rect.H)
            .ToList();
        if (containing.Count == 0) return true; // 접근성 트리가 없으면 장면 텍스트 검증만으로 판단
        var smallest = containing[0];
        var lbl = Compact(smallest.Label);
        if (lbl.Length == 0) return true;
        if (lbl.Contains(want, StringComparison.OrdinalIgnoreCase)) return true;
        reason = $"클릭 지점이 다른 항목('{lbl}') 위에 있음";
        return false;
    }

    public static string Compact(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    private static Regex SafeRegex(string pattern)
    {
        try { return new Regex(pattern, RegexOptions.IgnoreCase); }
        catch { return new Regex(@"^PC\s*켜기$", RegexOptions.IgnoreCase); }
    }
}
