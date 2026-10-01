using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace RemoteAccessHub.Services;

/// <summary>
/// 이 PC에 설치된 "Chrome 원격 데스크톱" 앱(브라우저의 [앱으로 설치]로 만든 PWA) 정보.
/// </summary>
/// <param name="PackageFamilyName">패키지 패밀리 이름. 앱 실행 ID(AUMID)를 만드는 데 쓴다.</param>
/// <param name="DisplayName">설정 &gt; 앱에 보이는 이름.</param>
/// <param name="BrowserAppId">앱을 띄울 때 브라우저에 넘기는 앱 ID(32자). 못 읽으면 null.</param>
/// <param name="ProfileDirectory">앱이 설치된 브라우저 프로필 폴더 이름. 못 읽으면 null.</param>
/// <param name="IconPath">앱 아이콘 PNG 경로. 못 찾으면 null.</param>
public sealed record CrdInstalledApp(string PackageFamilyName, string DisplayName, string? BrowserAppId, string? ProfileDirectory, string? IconPath)
{
    /// <summary>앱 실행 ID. explorer.exe에 shell:AppsFolder\&lt;AUMID&gt; 로 넘기면 앱이 열린다.</summary>
    public string Aumid => PackageFamilyName + "!App";

    /// <summary>브라우저에 앱 ID를 넘겨 특정 주소로 열 수 있는지.</summary>
    public bool CanOpenUrlInApp => BrowserAppId != null;
}

/// <summary>
/// 설치된 크롬 원격 데스크톱 앱을 찾는다. 레지스트리의 패키지 목록(HKCU, 읽기 전용)만 보고
/// 아무것도 바꾸지 않는다. 찾지 못하면 null을 돌려주고 호출한 쪽이 기본 브라우저로 넘어간다.
/// </summary>
public static class CrdAppFinder
{
    /// <summary>PWA 패키지 이름은 "&lt;사이트 주소&gt;-&lt;해시&gt;" 형식이다.</summary>
    private const string PackagePrefix = "remotedesktop.google.com-";

    private const string PackagesKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>앱 ID는 Chromium 확장 ID 형식(a~p 32자)만 허용한다. 명령줄에 넣기 전 검증용.</summary>
    private static readonly Regex AppIdPattern = new("^[a-p]{32}$", RegexOptions.Compiled);

    private static readonly object Gate = new();
    private static bool _looked;
    private static CrdInstalledApp? _cached;

    /// <summary>설치된 앱(없으면 null). 처음 한 번만 찾고 결과를 기억한다.</summary>
    public static CrdInstalledApp? Find()
    {
        lock (Gate)
        {
            if (_looked) return _cached;
            _looked = true;
            try { _cached = Look(); }
            catch { _cached = null; }
            return _cached;
        }
    }

    /// <summary>다시 찾는다(설정 창에서 앱을 설치한 뒤 확인할 때).</summary>
    public static CrdInstalledApp? Refresh()
    {
        lock (Gate) { _looked = false; _cached = null; }
        return Find();
    }

    /// <summary>자체검사용: 찾기 결과를 지정한 값으로 고정한다.</summary>
    internal static void OverrideForTest(CrdInstalledApp? app)
    {
        lock (Gate) { _looked = true; _cached = app; }
    }

    private static CrdInstalledApp? Look()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(PackagesKey);
        if (packages == null) return null;
        foreach (var name in packages.GetSubKeyNames())
        {
            if (!name.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            using var key = packages.OpenSubKey(name);
            if (key == null) continue;
            var root = key.GetValue("PackageRootFolder") as string;
            var display = key.GetValue("DisplayName") as string;
            var family = FamilyName(key.GetValue("PackageID") as string ?? name);
            if (family == null) continue;
            var (appId, profile) = ReadManifest(root);
            return new CrdInstalledApp(family, string.IsNullOrWhiteSpace(display) ? "Chrome 원격 데스크톱" : display,
                appId, profile, FindIcon(root));
        }
        return null;
    }

    /// <summary>"이름_버전_아키텍처__게시자" 전체 이름에서 "이름_게시자"를 만든다.</summary>
    internal static string? FamilyName(string packageId)
    {
        var parts = packageId.Split('_');
        if (parts.Length < 2) return null;
        var name = parts[0];
        var publisher = parts[^1];
        if (name.Length == 0 || publisher.Length == 0) return null;
        return name + "_" + publisher;
    }

    /// <summary>패키지 설명 파일에서 브라우저 앱 ID와 프로필 폴더를 읽는다.</summary>
    private static (string? AppId, string? Profile) ReadManifest(string? root)
    {
        if (string.IsNullOrEmpty(root)) return (null, null);
        try
        {
            var path = Path.Combine(root, "AppxManifest.xml");
            if (!File.Exists(path)) return (null, null);
            return ParseManifest(File.ReadAllText(path));
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>패키지 설명(XML)에서 브라우저 앱 ID와 프로필 폴더를 뽑는다. 형식이 어긋나면 null.</summary>
    internal static (string? AppId, string? Profile) ParseManifest(string text)
    {
        try
        {
            var doc = XDocument.Parse(text);
            // 앱 ID: Application 요소의 Parameters 특성에 "--app-id=<32자>" 형태로 들어 있다.
            string? appId = null;
            foreach (var attr in doc.Descendants().Attributes())
            {
                // 뒤에 글자가 더 붙은 긴 토큰의 앞 32자를 잘라 쓰지 않도록 경계를 둔다.
                var m = Regex.Match(attr.Value, @"--app-id=([a-p]{32})(?![A-Za-z0-9])");
                if (!m.Success) continue;
                appId = m.Groups[1].Value;
                break;
            }
            if (appId != null && !AppIdPattern.IsMatch(appId)) appId = null;
            // 프로필 폴더: 확장 정보에 "profile-directory?<이름>;" 형태로 들어 있다.
            string? profile = null;
            var pm = Regex.Match(text, @"profile-directory\?([^;""&<]{1,64})");
            if (pm.Success)
            {
                var value = pm.Groups[1].Value.Trim();
                if (value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')) profile = value;
            }
            return (appId, profile);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>패키지 Images 폴더에서 가장 큰 정사각 아이콘을 고른다.</summary>
    internal static string? FindIcon(string? root)
    {
        if (string.IsNullOrEmpty(root)) return null;
        try
        {
            var dir = Path.Combine(root, "Images");
            if (!Directory.Exists(dir)) return null;
            var best = Directory.EnumerateFiles(dir, "Square44x44Logo.targetsize-*.png")
                .Where(f => !f.Contains("altform", StringComparison.OrdinalIgnoreCase))
                .Select(f => (File: f, Size: SizeFromName(f)))
                .OrderByDescending(x => x.Size)
                .Select(x => x.File)
                .FirstOrDefault();
            if (best != null) return best;
            foreach (var name in new[] { "Square150x150Logo.png", "StoreLogo.png", "SmallTile.png" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static int SizeFromName(string file)
    {
        var m = Regex.Match(Path.GetFileName(file), @"targetsize-(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }
}
