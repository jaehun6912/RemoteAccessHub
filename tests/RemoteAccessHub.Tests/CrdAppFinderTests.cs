using RemoteAccessHub.Services;
using Xunit;

namespace RemoteAccessHub.Tests;

/// <summary>설치된 "Chrome 원격 데스크톱" 앱 찾기. 실제 레지스트리 대신 패키지 설명 샘플로 검사한다.</summary>
public class CrdAppFinderTests
{
    // 실제 PWA 패키지 설명과 같은 구조(식별자는 예시 값).
    private const string Manifest = """
        <?xml version="1.0"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10">
          <Identity Name="remotedesktop.google.com-1A2B3C4D" Publisher="CN=remotedesktop.google.com" Version="1.0.0.2" ProcessorArchitecture="neutral"/>
          <Applications>
            <Application Id="App" uap10:HostId="PWA" uap10:Parameters="--app-id=abcdefghijklmnopabcdefghijklmnop --ip-edge-aumid=Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!MSEDGE">
              <Extensions>
                <uap3:Extension Category="windows.appExtension">
                  <uap3:AppExtension Name="com.ms.webapp.internals.4" Id="remotedesktop.google.com-1A2B3C4D" PublicFolder="Public"
                    Description="parameters?--app-id=abcdefghijklmnopabcdefghijklmnop;profile-directory?Profile 2;start-url?https://remotedesktop.google.com/"/>
                </uap3:Extension>
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """;

    [Fact]
    public void Manifest_gives_browser_app_id_and_profile()
    {
        var (appId, profile) = CrdAppFinder.ParseManifest(Manifest);
        Assert.Equal("abcdefghijklmnopabcdefghijklmnop", appId);
        Assert.Equal("Profile 2", profile);
    }

    [Fact]
    public void Manifest_without_app_id_or_with_bad_id_gives_null()
    {
        // 앱 ID 형식(a~p 32자)이 아니면 명령줄에 넣지 않는다.
        foreach (var badId in new[] { "ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ", "zzzz zzzz --no-sandbox zzzzzzzzzz", "abcdefghijklmnop", "abcdefghijklmnopabcdefghijklmnopq" })
        {
            var bad = Manifest.Replace("abcdefghijklmnopabcdefghijklmnop", badId);
            Assert.Null(CrdAppFinder.ParseManifest(bad).AppId);
        }

        var none = Manifest.Replace("--app-id=abcdefghijklmnopabcdefghijklmnop", "");
        Assert.Null(CrdAppFinder.ParseManifest(none).AppId);

        Assert.Equal((null, null), CrdAppFinder.ParseManifest("이것은 XML이 아님"));
    }

    [Fact]
    public void Profile_with_odd_characters_is_refused()
    {
        var odd = Manifest.Replace("profile-directory?Profile 2;", @"profile-directory?..\..\Windows;");
        Assert.Null(CrdAppFinder.ParseManifest(odd).Profile);
    }

    [Theory]
    [InlineData("remotedesktop.google.com-906A0B3D_1.0.0.2_neutral__h2dphjv1brgng", "remotedesktop.google.com-906A0B3D_h2dphjv1brgng")]
    [InlineData("name_1.0.0.0_x64__pub123", "name_pub123")]
    public void Package_full_name_becomes_family_name(string full, string expected)
        => Assert.Equal(expected, CrdAppFinder.FamilyName(full));

    [Theory]
    [InlineData("단어하나")]
    [InlineData("_x64__pub")]
    public void Bad_package_name_gives_null_family(string full) => Assert.Null(CrdAppFinder.FamilyName(full));

    [Fact]
    public void Aumid_is_built_from_the_package_family_name()
    {
        var app = new CrdInstalledApp("remotedesktop.google.com-1A2B_pub", "Chrome 원격 데스크톱", "abcdefghijklmnopabcdefghijklmnop", "Default", null);
        Assert.Equal("remotedesktop.google.com-1A2B_pub!App", app.Aumid);
    }

    [Fact]
    public void App_window_is_opened_at_the_exact_address()
    {
        var app = new CrdInstalledApp("remotedesktop.google.com-1A2B_pub", "Chrome 원격 데스크톱", "abcdefghijklmnopabcdefghijklmnop", "Profile 2", null);
        var url = CrdLauncher.BuildUrl("1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d");
        var args = CrdLauncher.AppArgs(app, url);
        Assert.Equal("--profile-directory=Profile 2", args[0]);
        Assert.Equal("--app=" + url, args[1]);
        // --app-id는 앱 첫 화면만 열고 주소를 무시하므로 쓰지 않는다.
        Assert.DoesNotContain(args, a => a.StartsWith("--app-id", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--app-url", StringComparison.Ordinal));
        Assert.Contains("/session/1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d", args[1]);
    }

    [Fact]
    public void Unknown_profile_is_simply_left_out()
    {
        var app = new CrdInstalledApp("remotedesktop.google.com-1A2B_pub", "Chrome 원격 데스크톱", null, null, null);
        var args = CrdLauncher.AppArgs(app, CrdLauncher.AccessUrl);
        Assert.Single(args);
        Assert.Equal("--app=" + CrdLauncher.AccessUrl, args[0]);
    }

    [Fact]
    public void Largest_square_icon_is_chosen_and_altform_ignored()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteAccessHub-crd-icon-" + Guid.NewGuid().ToString("N"));
        var images = Path.Combine(root, "Images");
        Directory.CreateDirectory(images);
        try
        {
            foreach (var name in new[] { "Square44x44Logo.targetsize-16.png", "Square44x44Logo.targetsize-256.png", "Square44x44Logo.targetsize-48.png", "Square44x44Logo.targetsize-512_altform-unplated.png", "StoreLogo.png" })
                File.WriteAllBytes(Path.Combine(images, name), new byte[] { 1 });
            Assert.Equal(Path.Combine(images, "Square44x44Logo.targetsize-256.png"), CrdAppFinder.FindIcon(root));

            foreach (var f in Directory.GetFiles(images, "Square44x44Logo*")) File.Delete(f);
            Assert.Equal(Path.Combine(images, "StoreLogo.png"), CrdAppFinder.FindIcon(root));

            File.Delete(Path.Combine(images, "StoreLogo.png"));
            Assert.Null(CrdAppFinder.FindIcon(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
        Assert.Null(CrdAppFinder.FindIcon(null));
        Assert.Null(CrdAppFinder.FindIcon(Path.Combine(Path.GetTempPath(), "RemoteAccessHub-없는폴더")));
    }
}
