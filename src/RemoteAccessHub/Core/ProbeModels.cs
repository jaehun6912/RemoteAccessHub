using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteAccessHub.Core;

/// <summary>화면 좌표(CSS px). probe.js의 getBoundingClientRect 결과.</summary>
public sealed record ProbeRect(double X, double Y, double W, double H)
{
    public static readonly ProbeRect Empty = new(0, 0, 0, 0);

    [JsonIgnore] public bool IsEmpty => W <= 0 || H <= 0;
    [JsonIgnore] public double Right => X + W;
    [JsonIgnore] public double Bottom => Y + H;
    [JsonIgnore] public double CenterX => X + W / 2;
    [JsonIgnore] public double CenterY => Y + H / 2;

    /// <summary>세로 겹침 길이 / 둘 중 작은 높이. 0~1.</summary>
    public static double VerticalOverlapRatio(ProbeRect a, ProbeRect b)
    {
        if (a.IsEmpty || b.IsEmpty) return 0;
        var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        if (overlap <= 0) return 0;
        return overlap / Math.Min(a.H, b.H);
    }

    public static ProbeRect Union(ProbeRect a, ProbeRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new ProbeRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    public bool Contains(double px, double py) => px >= X && px <= Right && py >= Y && py <= Bottom;

    public override string ToString() => $"({X:0},{Y:0} {W:0}x{H:0})";
}

/// <summary>Flutter 접근성 노드(flt-semantics) 또는 일반 DOM의 역할 있는 요소.</summary>
public sealed class SemanticNode
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public int Parent { get; set; } = -1;
    public string Role { get; set; } = "";
    public string Label { get; set; } = "";
    public string InputType { get; set; } = "";
    public ProbeRect Rect { get; set; } = ProbeRect.Empty;
    public bool Hidden { get; set; }
    public bool Disabled { get; set; }
    public int Depth { get; set; }

    [JsonIgnore] public bool IsButton => string.Equals(Role, "button", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsUsable => !Hidden && !Disabled && !Rect.IsEmpty;

    public override string ToString() => $"#{Index} {Role} '{Label}' {Rect}";
}

/// <summary>HTML 렌더러가 장면(scene)에 그린 텍스트 조각(flt-paragraph).</summary>
public sealed class Paragraph
{
    public int Index { get; set; }
    public string Text { get; set; } = "";
    public ProbeRect Rect { get; set; } = ProbeRect.Empty;

    public override string ToString() => $"¶{Index} '{Text}' {Rect}";
}

public sealed class ProbeMarkers
{
    public bool PasswordInput { get; set; }
    public bool LoginButton { get; set; }
    public bool LogoutLabel { get; set; }
    public bool LoadingOverlay { get; set; }
    public int WakeButtons { get; set; }
    public int Inputs { get; set; }
    public List<string> DialogTexts { get; set; } = new();
}

public sealed class ProbeSnapshot
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string ReadyState { get; set; } = "";
    public bool Flutter { get; set; }
    public bool Placeholder { get; set; }
    public int SemanticsCount { get; set; }
    public int ParagraphCount { get; set; }
    public int Roots { get; set; }
    public List<SemanticNode> Nodes { get; set; } = new();
    public List<Paragraph> Paragraphs { get; set; } = new();
    public ProbeMarkers Markers { get; set; } = new();
    public string? Error { get; set; }
    public long ElapsedMs { get; set; }

    [JsonIgnore] public bool IsReadable => Error == null && (Nodes.Count > 0 || Paragraphs.Count > 0);

    /// <summary>
    /// 화면이 실제 내용을 표시하는 상태인지. 로딩 오버레이만 있는 상태(공유기 앱 부팅 중)는 false.
    /// </summary>
    [JsonIgnore]
    public bool HasMeaningfulContent =>
        Error == null
        && !Markers.LoadingOverlay
        && (Markers.PasswordInput || Markers.LoginButton || Markers.LogoutLabel || Markers.WakeButtons > 0
            || Nodes.Count(n => n.Label.Length > 0) >= 3
            || Paragraphs.Count(p => p.Text.Length > 0) >= 3);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
    };

    /// <summary>ExecuteScriptAsync 결과(JSON 문자열을 다시 JSON으로 감싼 형태 포함)를 파싱한다.</summary>
    public static ProbeSnapshot Parse(string? scriptResult)
    {
        if (string.IsNullOrWhiteSpace(scriptResult) || scriptResult == "null")
            return new ProbeSnapshot { Error = "스크립트 결과 없음" };
        try
        {
            var text = scriptResult;
            // ExecuteScriptAsync는 문자열 결과를 JSON 문자열 리터럴로 돌려준다: "\"{...}\""
            if (text.StartsWith('"'))
            {
                text = JsonSerializer.Deserialize<string>(text) ?? "";
            }
            var snap = JsonSerializer.Deserialize<ProbeSnapshot>(text, Options);
            return snap ?? new ProbeSnapshot { Error = "빈 결과" };
        }
        catch (Exception ex)
        {
            return new ProbeSnapshot { Error = "결과 파싱 실패: " + ex.Message };
        }
    }

    /// <summary>텍스트(단락+접근성 라벨)에 패턴이 포함되는지.</summary>
    public bool ContainsText(string needle)
    {
        if (string.IsNullOrEmpty(needle)) return false;
        return Paragraphs.Any(p => p.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            || Nodes.Any(n => n.Label.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<string> AllTexts()
    {
        foreach (var p in Paragraphs) yield return p.Text;
        foreach (var n in Nodes) if (n.Label.Length > 0) yield return n.Label;
    }
}
