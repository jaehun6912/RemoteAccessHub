using RemoteAccessHub.Services;

namespace RemoteAccessHub.UI;

/// <summary>
/// 접속 방식 목록에 쓰는 크롬 원격 데스크톱 아이콘.
/// 이 PC에 설치된 앱의 아이콘 파일을 그대로 읽어 쓴다(프로그램에 구글 로고를 포함하지 않는다).
/// 앱이 없으면 null이고, 그때는 기존 글리프를 그린다.
/// </summary>
public static class CrdIcon
{
    private static readonly object Gate = new();
    private static bool _loaded;
    private static Image? _image;

    /// <summary>설치된 앱의 아이콘(없으면 null). 처음 한 번만 읽고 기억한다.</summary>
    public static Image? Current
    {
        get
        {
            lock (Gate)
            {
                if (_loaded) return _image;
                _loaded = true;
                _image = Load(CrdAppFinder.Find()?.IconPath);
                return _image;
            }
        }
    }

    /// <summary>앱을 새로 설치했을 수 있을 때 다시 읽는다.</summary>
    public static Image? Reload()
    {
        lock (Gate)
        {
            _image?.Dispose();
            _image = null;
            _loaded = false;
        }
        CrdAppFinder.Refresh();
        return Current;
    }

    private static Image? Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            // 파일을 계속 잠그지 않도록 복사본을 만든다.
            using var stream = File.OpenRead(path);
            using var original = Image.FromStream(stream);
            return new Bitmap(original);
        }
        catch
        {
            return null;
        }
    }
}
