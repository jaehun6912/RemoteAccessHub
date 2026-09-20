using System.Text.RegularExpressions;

namespace RemoteAccessHub.Core;

public sealed record WolTarget(string PcName, string? Mac);

public enum WolMatchStatus
{
    Found,
    NothingReadable,
    ListEmpty,
    TargetNotFound,
    Ambiguous,
    MacMismatch,
    NoWakeButton,
}

/// <summary>클릭할 대상. Kind = "semantics"(flt-semantics 버튼 id로 click) 또는 "paragraph"(검증된 좌표 클릭).</summary>
public sealed record WolClickTarget(string Kind, string NodeId, int ParagraphIndex, string Label, ProbeRect Rect, ProbeRect RowBand, string RowText, string Strategy);

public sealed record WolMatchResult(WolMatchStatus Status, string Message, WolClickTarget? Target, IReadOnlyList<string> Details)
{
    public bool IsFound => Status == WolMatchStatus.Found && Target != null;
}

/// <summary>
/// WOL 목록에서 설정된 PC 이름(및 선택적 MAC)에 대응하는 [PC 켜기] 버튼을 찾는다.
///
/// 1순위 — 구조 매칭: 실제 AX2004T(15.36.6)에서는 행이 role=group 노드이고 라벨이 "이름 MAC",
///   [PC 켜기] 버튼이 그 행의 자식 노드다(2026-09-14 진단 파일로 확인). 버튼의 가장 가까운
///   "행 크기" 라벨 조상으로 소속을 판단한다.
/// 2순위 — 좌표 매칭: 구조 정보가 없으면(평탄한 트리, 접근성 미사용) 세로 띠 겹침으로 판단한다.
///
/// 대상이 없거나 구분할 수 없으면 절대 임의의 버튼을 고르지 않는다.
/// </summary>
public static class WolMatcher
{
    private const double RowOverlap = 0.5;
    private const double MaxRowHeight = 150;

    public static WolMatchResult Match(ProbeSnapshot snap, WolTarget target, Regex wakePattern, string emptyListText = "등록된 WOL PC가 없습니다")
    {
        var details = new List<string>();
        var name = (target.PcName ?? "").Trim();
        var mac = InputRules.NormalizeMac(target.Mac);
        if (name.Length == 0)
            return new(WolMatchStatus.TargetNotFound, "WOL 대상 PC 이름이 설정되지 않았습니다.", null, details);

        var nodes = snap.Nodes ?? new List<SemanticNode>();
        var paras = snap.Paragraphs ?? new List<Paragraph>();
        if (nodes.Count == 0 && paras.Count == 0)
            return new(WolMatchStatus.NothingReadable, "화면에서 읽을 수 있는 요소가 없습니다. 접근성 트리가 비어 있거나 페이지가 아직 로딩 중입니다.", null, details);

        var semButtons = nodes.Where(n => n.IsButton && n.IsUsable && wakePattern.IsMatch(Compact(n.Label))).ToList();
        details.Add($"접근성 [PC 켜기] 버튼 {semButtons.Count}개");

        // ---------- 1순위: 구조 매칭 ----------
        if (semButtons.Count > 0)
        {
            var byIndex = new Dictionary<int, SemanticNode>();
            foreach (var n in nodes) byIndex.TryAdd(n.Index, n);
            var rows = semButtons.Select(b => (Button: b, Row: RowAncestor(b, byIndex))).ToList();
            if (rows.All(r => r.Row != null))
            {
                details.Add("구조 매칭 사용(버튼이 행 노드 안에 있음)");
                var structural = MatchStructural(rows!, name, mac, paras, details);
                if (structural != null) return structural;
            }
            else
            {
                details.Add($"구조 정보 부족(행 노드 없는 버튼 {rows.Count(r => r.Row == null)}개) → 좌표 매칭");
            }
        }

        // ---------- 2순위: 좌표 매칭 ----------
        return MatchGeometric(nodes, paras, semButtons, name, mac, wakePattern, emptyListText, snap, details);
    }

    /// <summary>버튼의 가장 가까운 라벨 있는 조상 중 "행 크기"이고 버튼을 세로로 포함하는 노드.</summary>
    private static SemanticNode? RowAncestor(SemanticNode button, Dictionary<int, SemanticNode> byIndex)
    {
        var cur = button;
        for (var depth = 0; depth < 4; depth++)
        {
            if (cur.Parent < 0 || !byIndex.TryGetValue(cur.Parent, out var parent)) return null;
            if (Compact(parent.Label).Length > 0)
            {
                var rowSized = !parent.Rect.IsEmpty
                    && parent.Rect.H <= Math.Max(MaxRowHeight, button.Rect.H * 3)
                    && ProbeRect.VerticalOverlapRatio(parent.Rect, button.Rect) >= RowOverlap;
                return rowSized ? parent : null;
            }
            cur = parent;
        }
        return null;
    }

    private static WolMatchResult? MatchStructural(List<(SemanticNode Button, SemanticNode Row)> rows, string name, string? mac, List<Paragraph> paras, List<string> details)
    {
        var nameHits = rows.Where(r => MatchesName(r.Row.Label, name)).ToList();
        details.Add($"이름 '{name}' 일치 행 {nameHits.Count}개 / 전체 행 {rows.Count}개");

        // 같은 행 노드에 [PC 켜기]가 둘 이상이면 구분 불가
        foreach (var g in nameHits.GroupBy(r => r.Row.Index))
        {
            if (g.Count() > 1)
                return new(WolMatchStatus.Ambiguous, $"'{name}' 행 안에 [PC 켜기] 버튼이 {g.Count()}개 있어 구분할 수 없습니다.", null, details);
        }

        if (nameHits.Count == 0)
        {
            var visible = rows.Select(r => FirstToken(r.Row.Label)).Where(x => x.Length > 0).Distinct().Take(10).ToList();
            var hint = visible.Count > 0 ? $" 화면에 보이는 이름: {string.Join(", ", visible)}" : "";
            return new(WolMatchStatus.TargetNotFound, $"WOL 목록에서 '{name}' 이름의 PC를 찾지 못했습니다.{hint}", null, details);
        }

        var kept = nameHits;
        if (mac != null)
        {
            kept = new();
            foreach (var r in nameHits)
            {
                var macs = new HashSet<string>(InputRules.ExtractMacs(r.Row.Label), StringComparer.OrdinalIgnoreCase);
                foreach (var p in paras.Where(p => ProbeRect.VerticalOverlapRatio(p.Rect, r.Row.Rect) >= RowOverlap))
                    foreach (var m in InputRules.ExtractMacs(p.Text)) macs.Add(m);
                if (macs.Count == 0 || macs.Contains(mac)) kept.Add(r);
                else details.Add($"행 {r.Row.Rect}: MAC 불일치 ({string.Join(",", macs.Select(InputRules.MaskMac))})");
            }
            if (kept.Count == 0)
                return new(WolMatchStatus.MacMismatch, $"'{name}' 행의 MAC 주소가 설정값과 다릅니다. 설정의 MAC 주소를 확인하세요.", null, details);
        }

        if (kept.Count > 1)
        {
            var msg = mac == null
                ? $"같은 이름 '{name}'의 PC가 {kept.Count}개 있어 구분할 수 없습니다. 설정에 MAC 주소를 입력하세요."
                : $"'{name}' 이름과 MAC이 모두 일치하는 행이 {kept.Count}개라 구분할 수 없습니다.";
            return new(WolMatchStatus.Ambiguous, msg, null, details);
        }

        var hit = kept[0];
        var t = new WolClickTarget("semantics", hit.Button.Id, -1, hit.Button.Label, hit.Button.Rect, hit.Row.Rect, hit.Row.Label, "구조");
        return new(WolMatchStatus.Found, $"'{name}' 행의 [PC 켜기] 버튼을 찾았습니다 {hit.Button.Rect} (구조 매칭).", t, details);
    }

    private static WolMatchResult MatchGeometric(List<SemanticNode> nodes, List<Paragraph> paras, List<SemanticNode> semButtons, string name, string? mac, Regex wakePattern, string emptyListText, ProbeSnapshot snap, List<string> details)
    {
        var buttons = semButtons.Select(n => new ButtonCandidate("semantics", n.Id, -1, n.Label, n.Rect)).ToList();
        if (buttons.Count == 0)
        {
            foreach (var p in paras)
            {
                if (p.Rect.IsEmpty) continue;
                if (wakePattern.IsMatch(Compact(p.Text))) buttons.Add(new ButtonCandidate("paragraph", "", p.Index, p.Text, p.Rect));
            }
        }
        details.Add($"PC 켜기 버튼 후보 {buttons.Count}개 (접근성 {buttons.Count(b => b.Kind == "semantics")}개)");

        var hits = new List<(ProbeRect Rect, string Text)>();
        foreach (var p in paras)
        {
            if (p.Rect.IsEmpty || p.Rect.H > MaxRowHeight) continue;
            if (MatchesName(p.Text, name)) hits.Add((p.Rect, p.Text));
        }
        foreach (var n in nodes)
        {
            if (n.Rect.IsEmpty || n.IsButton || n.Hidden || n.Rect.H > MaxRowHeight) continue;
            if (MatchesName(n.Label, name)) hits.Add((n.Rect, n.Label));
        }
        details.Add($"이름 '{name}' 일치 요소 {hits.Count}개");

        var rows = ClusterRows(hits);
        details.Add($"이름 일치 행 {rows.Count}개");
        if (rows.Count == 0)
        {
            if (snap.ContainsText(emptyListText))
                return new(WolMatchStatus.ListEmpty, "공유기에 등록된 WOL PC가 없습니다. 공유기 WOL 화면에서 PC를 먼저 등록하세요.", null, details);
            var visible = VisibleNames(nodes, paras, buttons);
            var hint = visible.Count > 0 ? $" 화면에 보이는 이름: {string.Join(", ", visible)}" : "";
            return new(WolMatchStatus.TargetNotFound, $"WOL 목록에서 '{name}' 이름의 PC를 찾지 못했습니다.{hint}", null, details);
        }

        if (mac != null)
        {
            var kept = new List<Row>();
            foreach (var row in rows)
            {
                var macs = MacsInBand(row.Band, nodes, paras);
                if (macs.Count == 0) { kept.Add(row); continue; }
                if (macs.Contains(mac)) kept.Add(row);
                else details.Add($"행 {row.Band}: MAC 불일치 ({string.Join(",", macs.Select(InputRules.MaskMac))})");
            }
            if (kept.Count == 0)
                return new(WolMatchStatus.MacMismatch, $"'{name}' 행의 MAC 주소가 설정값과 다릅니다. 설정의 MAC 주소를 확인하세요.", null, details);
            rows = kept;
        }

        if (rows.Count > 1)
        {
            var msg = mac == null
                ? $"같은 이름 '{name}'의 PC가 {rows.Count}개 있어 구분할 수 없습니다. 설정에 MAC 주소를 입력하세요."
                : $"'{name}' 이름과 MAC이 모두 일치하는 행이 {rows.Count}개라 구분할 수 없습니다.";
            return new(WolMatchStatus.Ambiguous, msg, null, details);
        }

        var targetRow = rows[0];
        var inBand = buttons.Where(b => ProbeRect.VerticalOverlapRatio(b.Rect, targetRow.Band) >= RowOverlap).ToList();
        details.Add($"행 {targetRow.Band} 안 버튼 {inBand.Count}개");
        if (inBand.Count == 0)
        {
            var msg = buttons.Count == 0
                ? "화면에서 [PC 켜기] 버튼을 찾지 못했습니다. 접근성 트리가 활성화되지 않았거나 화면 구성이 다릅니다."
                : $"'{name}' 행에 대응하는 [PC 켜기] 버튼이 없습니다. 목록을 스크롤해야 하거나 화면 구성이 다를 수 있습니다.";
            return new(WolMatchStatus.NoWakeButton, msg, null, details);
        }
        if (inBand.Count > 1)
            return new(WolMatchStatus.Ambiguous, $"'{name}' 행 안에 [PC 켜기] 버튼이 {inBand.Count}개 있어 구분할 수 없습니다.", null, details);

        var chosen = inBand[0];
        foreach (var other in OtherNameRows(nodes, paras, name, chosen))
        {
            if (ProbeRect.VerticalOverlapRatio(chosen.Rect, other) > ProbeRect.VerticalOverlapRatio(chosen.Rect, targetRow.Band))
                return new(WolMatchStatus.Ambiguous, "선택된 버튼이 다른 PC 행과 더 가깝게 겹칩니다. 안전을 위해 클릭하지 않습니다.", null, details);
        }

        var t = new WolClickTarget(chosen.Kind, chosen.NodeId, chosen.ParagraphIndex, chosen.Label, chosen.Rect, targetRow.Band, targetRow.Text, "좌표");
        return new(WolMatchStatus.Found, $"'{name}' 행의 [PC 켜기] 버튼을 찾았습니다 {chosen.Rect} (좌표 매칭).", t, details);
    }

    private sealed record ButtonCandidate(string Kind, string NodeId, int ParagraphIndex, string Label, ProbeRect Rect);

    private sealed class Row
    {
        public ProbeRect Band = ProbeRect.Empty;
        public string Text = "";
    }

    private static List<Row> ClusterRows(List<(ProbeRect Rect, string Text)> hits)
    {
        var rows = new List<Row>();
        foreach (var h in hits.OrderBy(h => h.Rect.Y))
        {
            var row = rows.FirstOrDefault(r => ProbeRect.VerticalOverlapRatio(r.Band, h.Rect) >= RowOverlap);
            if (row == null)
            {
                rows.Add(new Row { Band = h.Rect, Text = h.Text });
            }
            else
            {
                row.Band = ProbeRect.Union(row.Band, h.Rect);
                if (!row.Text.Contains(h.Text, StringComparison.Ordinal)) row.Text += " | " + h.Text;
            }
        }
        return rows;
    }

    private static HashSet<string> MacsInBand(ProbeRect band, List<SemanticNode> nodes, List<Paragraph> paras)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paras)
            if (ProbeRect.VerticalOverlapRatio(p.Rect, band) >= RowOverlap)
                foreach (var m in InputRules.ExtractMacs(p.Text)) set.Add(m);
        foreach (var n in nodes)
            if (!n.IsButton && n.Rect.H <= MaxRowHeight && ProbeRect.VerticalOverlapRatio(n.Rect, band) >= RowOverlap)
                foreach (var m in InputRules.ExtractMacs(n.Label)) set.Add(m);
        return set;
    }

    /// <summary>선택한 버튼과 같은 세로 띠에 있는, 대상이 아닌 이름 후보(행 크기 요소만).</summary>
    private static IEnumerable<ProbeRect> OtherNameRows(List<SemanticNode> nodes, List<Paragraph> paras, string name, ButtonCandidate b)
    {
        var maxH = Math.Max(60, b.Rect.H * 3);
        var texts = paras.Where(p => p.Rect.H <= maxH && ProbeRect.VerticalOverlapRatio(p.Rect, b.Rect) >= RowOverlap).Select(p => (p.Rect, p.Text))
            .Concat(nodes.Where(n => !n.IsButton && !n.Rect.IsEmpty && n.Rect.H <= maxH && ProbeRect.VerticalOverlapRatio(n.Rect, b.Rect) >= RowOverlap).Select(n => (n.Rect, Text: n.Label)));
        foreach (var (rect, text) in texts)
        {
            if (!MatchesName(text, name) && LooksLikeName(text)) yield return rect;
        }
    }

    private static List<string> VisibleNames(List<SemanticNode> nodes, List<Paragraph> paras, List<ButtonCandidate> buttons)
    {
        var names = new List<string>();
        foreach (var b in buttons)
        {
            foreach (var p in paras)
            {
                if (ProbeRect.VerticalOverlapRatio(p.Rect, b.Rect) < RowOverlap) continue;
                if (!LooksLikeName(p.Text)) continue;
                var first = FirstToken(p.Text);
                if (first.Length > 0 && !names.Contains(first)) names.Add(first);
            }
        }
        return names.Take(10).ToList();
    }

    private static string FirstToken(string text) => Compact(text).Split(' ')[0];

    private static bool LooksLikeName(string text)
    {
        var t = Compact(text);
        if (t.Length is 0 or > 64) return false;
        if (InputRules.MacRegex().IsMatch(t) && InputRules.MacRegex().Replace(t, "").Trim().Length == 0) return false;
        if (Regex.IsMatch(t, @"^(PC\s*켜기|삭제|수정|추가|검색|확인|취소|PC 이름.*|MAC 주소|WOL 기능|WOL PC 추가)$")) return false;
        if (t.Length == 1 && char.GetUnicodeCategory(t[0]) == System.Globalization.UnicodeCategory.PrivateUse) return false; // 아이콘 글꼴
        return true;
    }

    internal static bool MatchesName(string? text, string name)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = Compact(text);
        var n = Compact(name);
        if (n.Length == 0) return false;
        if (string.Equals(t, n, StringComparison.OrdinalIgnoreCase)) return true;
        var idx = 0;
        while ((idx = t.IndexOf(n, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = idx == 0 ? ' ' : t[idx - 1];
            var afterIdx = idx + n.Length;
            var after = afterIdx >= t.Length ? ' ' : t[afterIdx];
            if (!IsNameChar(before) && !IsNameChar(after)) return true;
            idx += n.Length;
        }
        return false;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';

    internal static string Compact(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
}
