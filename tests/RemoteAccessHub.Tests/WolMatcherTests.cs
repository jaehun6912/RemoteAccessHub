using System.Text.RegularExpressions;
using RemoteAccessHub.Core;
using Xunit;

namespace RemoteAccessHub.Tests;

public class WolMatcherTests
{
    private static readonly Regex Wake = new(@"^PC\s*켜기$");
    private const string Target = "MY-PC";

    /// <summary>
    /// 실제 Flutter 표에서 관찰되는 평탄한 구조를 흉내 낸 스냅샷:
    /// 행 노드(이름+MAC 결합 라벨)들이 먼저 나오고 버튼 노드들은 뒤에 역순으로 온다. 장면 텍스트는 이름/MAC이 별도 단락.
    /// </summary>
    private static ProbeSnapshot Build(params (string Name, string Mac)[] rows)
    {
        var snap = new ProbeSnapshot { Flutter = true, SemanticsCount = 1 };
        var idx = 0;
        var buttons = new List<SemanticNode>();
        for (var i = 0; i < rows.Length; i++)
        {
            var y = 150 + i * 56;
            snap.Paragraphs.Add(new Paragraph { Index = snap.Paragraphs.Count, Text = rows[i].Name, Rect = new ProbeRect(60, y, 300, 20) });
            snap.Paragraphs.Add(new Paragraph { Index = snap.Paragraphs.Count, Text = rows[i].Mac, Rect = new ProbeRect(400, y, 200, 20) });
            snap.Paragraphs.Add(new Paragraph { Index = snap.Paragraphs.Count, Text = "PC 켜기", Rect = new ProbeRect(712, y, 76, 20) });
            snap.Nodes.Add(new SemanticNode { Index = idx++, Id = "row" + i, Label = rows[i].Name + " " + rows[i].Mac, Rect = new ProbeRect(50, y - 10, 560, 44) });
            buttons.Add(new SemanticNode { Index = 0, Id = "wake" + i, Role = "button", Label = "PC 켜기", Rect = new ProbeRect(700, y - 8, 100, 36) });
            buttons.Add(new SemanticNode { Index = 0, Id = "del" + i, Role = "button", Label = "삭제", Rect = new ProbeRect(820, y - 8, 80, 36) });
        }
        buttons.Reverse();
        foreach (var b in buttons) { b.Index = idx++; snap.Nodes.Add(b); }
        snap.Nodes.Add(new SemanticNode { Index = idx++, Id = "add", Role = "button", Label = "WOL PC 추가", Rect = new ProbeRect(40, 60, 130, 34) });
        snap.Paragraphs.Add(new Paragraph { Index = snap.Paragraphs.Count, Text = "WOL 기능", Rect = new ProbeRect(40, 20, 100, 24) });
        return snap;
    }

    [Fact]
    public void Finds_button_on_target_row_only()
    {
        var snap = Build(("OTHER-1", "00:11:22:33:44:01"), (Target, "00:11:22:33:44:02"), ("OTHER-2", "00:11:22:33:44:03"));
        var r = WolMatcher.Match(snap, new WolTarget(Target, null), Wake);
        Assert.True(r.IsFound, r.Message);
        Assert.Equal("wake1", r.Target!.NodeId);
        Assert.Equal("semantics", r.Target.Kind);
    }

    [Fact]
    public void Target_missing_refuses()
    {
        var snap = Build(("OTHER-1", "00:11:22:33:44:01"), ("OTHER-2", "00:11:22:33:44:03"));
        var r = WolMatcher.Match(snap, new WolTarget(Target, null), Wake);
        Assert.Equal(WolMatchStatus.TargetNotFound, r.Status);
        Assert.Null(r.Target);
        Assert.Contains("OTHER-1", r.Message);
    }

    [Fact]
    public void Duplicate_names_without_mac_is_ambiguous()
    {
        var snap = Build((Target, "00:11:22:33:44:01"), ("X", "00:11:22:33:44:09"), (Target, "00:11:22:33:44:02"));
        var r = WolMatcher.Match(snap, new WolTarget(Target, null), Wake);
        Assert.Equal(WolMatchStatus.Ambiguous, r.Status);
        Assert.Null(r.Target);
    }

    [Fact]
    public void Duplicate_names_resolved_by_mac()
    {
        var snap = Build((Target, "00:11:22:33:44:01"), ("X", "00:11:22:33:44:09"), (Target, "00:11:22:33:44:02"));
        var r = WolMatcher.Match(snap, new WolTarget(Target, "00-11-22-33-44-02"), Wake);
        Assert.True(r.IsFound, r.Message);
        Assert.Equal("wake2", r.Target!.NodeId);
    }

    [Fact]
    public void Wrong_mac_refuses()
    {
        var snap = Build((Target, "00:11:22:33:44:02"));
        var r = WolMatcher.Match(snap, new WolTarget(Target, "AA:BB:CC:DD:EE:FF"), Wake);
        Assert.Equal(WolMatchStatus.MacMismatch, r.Status);
        Assert.Null(r.Target);
    }

    [Fact]
    public void Partial_name_does_not_match()
    {
        var snap = Build(("MY-PC2", "00:11:22:33:44:01"), ("MYMY-PC", "00:11:22:33:44:02"));
        var r = WolMatcher.Match(snap, new WolTarget(Target, null), Wake);
        Assert.Equal(WolMatchStatus.TargetNotFound, r.Status);
    }

    [Fact]
    public void Name_match_is_case_insensitive_and_token_based()
    {
        Assert.True(WolMatcher.MatchesName("my-pc\n00:11:22:33:44:55", Target));
        Assert.True(WolMatcher.MatchesName("MY-PC", "my-pc"));
        Assert.False(WolMatcher.MatchesName("MY-PCX", Target));
        Assert.False(WolMatcher.MatchesName("", Target));
    }

    [Fact]
    public void Falls_back_to_paragraph_click_when_no_semantics()
    {
        var snap = Build(("OTHER-1", "00:11:22:33:44:01"), (Target, "00:11:22:33:44:02"));
        snap.Nodes.Clear();
        snap.SemanticsCount = 0;
        var r = WolMatcher.Match(snap, new WolTarget(Target, null), Wake);
        Assert.True(r.IsFound, r.Message);
        Assert.Equal("paragraph", r.Target!.Kind);
        Assert.InRange(r.Target.Rect.CenterY, 150 + 56 - 5, 150 + 56 + 25);
    }

    [Fact]
    public void Two_wake_buttons_in_same_row_is_ambiguous()
    {
        var snap = Build((Target, "00:11:22:33:44:02"));
        snap.Nodes.Add(new SemanticNode { Index = 99, Id = "extra", Role = "button", Label = "PC 켜기", Rect = new ProbeRect(900, 142, 100, 36) });
        var r = WolMatcher.Match(snap, new WolTarget(Target, null), Wake);
        Assert.Equal(WolMatchStatus.Ambiguous, r.Status);
    }

    [Fact]
    public void Empty_snapshot_is_not_readable()
    {
        var r = WolMatcher.Match(new ProbeSnapshot(), new WolTarget(Target, null), Wake);
        Assert.Equal(WolMatchStatus.NothingReadable, r.Status);
    }

    [Fact]
    public void Empty_list_message()
    {
        var snap = new ProbeSnapshot { Flutter = true };
        snap.Paragraphs.Add(new Paragraph { Index = 0, Text = "등록된 WOL PC가 없습니다.", Rect = new ProbeRect(60, 160, 250, 20) });
        var r = WolMatcher.Match(snap, new WolTarget(Target, null), Wake);
        Assert.Equal(WolMatchStatus.ListEmpty, r.Status);
    }

    [Fact]
    public void Disabled_or_hidden_buttons_are_ignored()
    {
        var snap = Build((Target, "00:11:22:33:44:02"));
        foreach (var n in snap.Nodes.Where(n => n.Id.StartsWith("wake"))) n.Disabled = true;
        snap.Paragraphs.RemoveAll(p => p.Text == "PC 켜기");
        var r = WolMatcher.Match(snap, new WolTarget(Target, null), Wake);
        Assert.Equal(WolMatchStatus.NoWakeButton, r.Status);
    }

    [Fact]
    public void Row_band_prevents_neighbor_button_selection()
    {
        // 대상 행의 버튼이 없고(스크롤 밖) 이웃 행의 버튼만 있는 경우: 이웃 버튼을 고르면 안 된다.
        var snap = Build(("OTHER-1", "00:11:22:33:44:01"), (Target, "00:11:22:33:44:02"));
        snap.Nodes.RemoveAll(n => n.Id == "wake1");
        snap.Paragraphs.RemoveAll(p => p.Text == "PC 켜기" && Math.Abs(p.Rect.Y - 206) < 1);
        var r = WolMatcher.Match(snap, new WolTarget(Target, null), Wake);
        Assert.Equal(WolMatchStatus.NoWakeButton, r.Status);
        Assert.Null(r.Target);
    }
}
