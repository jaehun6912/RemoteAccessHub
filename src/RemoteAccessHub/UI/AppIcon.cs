namespace RemoteAccessHub.UI;

/// <summary>
/// 프로그램 아이콘(Assets\app.ico, tools\make-icon.ps1로 생성).
/// 실행 파일 아이콘은 프로젝트의 ApplicationIcon으로 넣고, 창 제목 표시줄·작업 표시줄 아이콘은 여기서 읽어 쓴다.
/// 여러 크기(16~256px)가 들어 있어 Windows가 DPI에 맞는 크기를 고른다.
/// </summary>
public static class AppIcon
{
    public const string ResourceName = "app.ico";
    public static readonly int[] ExpectedSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    private static readonly Lazy<Icon?> _icon = new(Load);

    /// <summary>창에 붙일 아이콘. 읽지 못하면 null(Windows 기본 아이콘 유지).</summary>
    public static Icon? Current => _icon.Value;

    public static Stream? OpenStream() => typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);

    private static Icon? Load()
    {
        try
        {
            using var s = OpenStream();
            return s == null ? null : new Icon(s);
        }
        catch
        {
            return null;
        }
    }

    public static void ApplyTo(Form form)
    {
        if (Current is { } icon) form.Icon = icon;
    }

    /// <summary>ICO 머리글에서 들어 있는 크기 목록을 읽는다(256은 0으로 기록됨).</summary>
    public static IReadOnlyList<int> ReadSizes(Stream s)
    {
        using var r = new BinaryReader(s, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt16() != 0 || r.ReadUInt16() != 1) throw new InvalidDataException("ICO 형식이 아닙니다.");
        var count = r.ReadUInt16();
        var sizes = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            int w = r.ReadByte();
            r.ReadBytes(15);
            sizes.Add(w == 0 ? 256 : w);
        }
        return sizes;
    }
}
