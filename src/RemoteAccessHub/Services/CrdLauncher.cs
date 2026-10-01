using RemoteAccessHub.Core;

namespace RemoteAccessHub.Services;

public interface ICrdLauncher
{
    /// <summary>
    /// 기본 브라우저로 크롬 원격 데스크톱을 연다. 기기 ID가 있으면 그 기기의 세션 주소를,
    /// 없으면 기기 목록 화면을 연다. 구글 로그인과 PIN 입력은 브라우저에서 사용자가 직접 한다.
    /// </summary>
    void Open(string? hostId);
}

public sealed class CrdLauncher : ICrdLauncher
{
    /// <summary>기기 목록 화면.</summary>
    public const string AccessUrl = "https://remotedesktop.google.com/access";

    private readonly AppLog? _log;

    public CrdLauncher(AppLog? log = null) => _log = log;

    /// <summary>열 주소를 만든다. 기기 ID는 16진수와 '-'만 허용하므로 주소에 그대로 넣어도 안전하다.</summary>
    public static string BuildUrl(string? hostId)
    {
        var id = InputRules.NormalizeCrdHostId(hostId);
        return id == null ? AccessUrl : $"{AccessUrl}/session/{id}";
    }

    public void Open(string? hostId)
    {
        var url = BuildUrl(hostId);
        // 셸 문자열로 조립하지 않고 explorer.exe에 인수로 넘겨 기본 브라우저가 열게 한다.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        _log?.Info("크롬 원격 데스크톱 열기: " + (url == AccessUrl ? "기기 목록" : "저장된 기기 세션"));
        using var _ = ProcessRunner.StartWindowed(explorer, new[] { url });
    }
}
