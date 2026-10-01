using RemoteAccessHub.Core;
using Xunit;

namespace RemoteAccessHub.Tests;

public class InputRulesTests
{
    [Theory]
    [InlineData("myhome.iptime.org", true)]
    [InlineData("192.168.0.10", true)]
    [InlineData("fe80::1", true)]
    [InlineData("host with space", false)]
    [InlineData("host;calc.exe", false)]
    [InlineData("host&&whoami", false)]
    [InlineData("", false)]
    [InlineData("-bad.example", false)]
    [InlineData("a.b.c.d.example.com", true)]
    public void Host_validation(string host, bool expected) => Assert.Equal(expected, InputRules.IsValidHost(host));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3389, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void Port_validation(int port, bool expected) => Assert.Equal(expected, InputRules.IsValidPort(port));

    [Theory]
    [InlineData("HomeVPN", true)]
    [InlineData("집 VPN", true)]
    [InlineData("", false)]
    [InlineData("/disconnect", false)]
    [InlineData("-x", false)]
    [InlineData("name\"quote", false)]
    [InlineData("a|b", false)]
    public void Vpn_name_validation(string name, bool expected) => Assert.Equal(expected, InputRules.IsValidVpnName(name));

    [Theory]
    [InlineData("00:11:22:33:44:55", "00:11:22:33:44:55")]
    [InlineData("00-11-22-33-44-55", "00:11:22:33:44:55")]
    [InlineData("aabbccddeeff", "AA:BB:CC:DD:EE:FF")]
    [InlineData("00:11:22:33:44", null)]
    [InlineData("not a mac", null)]
    [InlineData("", null)]
    public void Mac_normalization(string input, string? expected) => Assert.Equal(expected, InputRules.NormalizeMac(input));

    [Fact]
    public void Mac_masking_hides_middle()
    {
        Assert.Equal("PC 00:11:**:**:**:55 켜기", InputRules.MaskMac("PC 00:11:22:33:44:55 켜기"));
        Assert.Equal("00-11:**:**:**:55", InputRules.MaskMac("00-11-22-33-44-55").Replace("00:11", "00-11"));
    }

    [Theory]
    [InlineData("http://192.168.0.1/", true)]
    [InlineData("https://myhome.iptime.org:8443/", true)]
    [InlineData("http://myhome.iptime.org:8080", true)]
    [InlineData("ftp://x/", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("", false)]
    public void Router_url_validation(string url, bool expected) => Assert.Equal(expected, InputRules.TryParseRouterUrl(url, out _));

    [Fact]
    public void HostPort_formats_ipv6_with_brackets()
    {
        Assert.Equal("[fe80::1]:3389", InputRules.HostPort("fe80::1", 3389));
        Assert.Equal("host:3390", InputRules.HostPort("host", 3390));
    }

    [Fact]
    public void Settings_validation_is_mode_specific()
    {
        var s = new AppSettings { PublicHost = "", PublicRdpPort = 3389, VpnName = "HomeVPN", VpnDesktopIp = "192.168.0.10", VpnRdpPort = 3389 };
        Assert.Empty(s.ValidateConnect(ConnectMode.Vpn));
        Assert.NotEmpty(s.ValidateConnect(ConnectMode.Direct));
        var d = new AppSettings { PublicHost = "myhome.iptime.org", PublicRdpPort = 41000, VpnName = "" };
        Assert.Empty(d.ValidateConnect(ConnectMode.Direct));
        Assert.NotEmpty(d.ValidateConnect(ConnectMode.Vpn));
    }

    [Fact]
    public void Settings_roundtrip_and_no_secret_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), "rah-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new AppSettings { RouterUrl = "http://10.0.0.1:8080/", WolPcName = "PC-1", WolPcMac = "00:11:22:33:44:55", VpnName = "HomeVPN" };
            s.Save(path);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie", text, StringComparison.OrdinalIgnoreCase);
            var back = AppSettings.Load(path);
            Assert.Equal("PC-1", back.WolPcName);
            Assert.Equal("http://10.0.0.1:8080", back.RouterOrigin);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Export_and_import_round_trip_without_window_position()
    {
        var s = new AppSettings
        {
            RouterUrl = "http://myhome.iptime.org:8080/", WolPcName = "MY-PC", WolPcMac = "02:00:AA:BB:CC:01",
            PublicHost = "myhome.iptime.org", PublicRdpPort = 41000, VpnName = "HomeVPN", VpnDesktopIp = "192.168.0.10",
            Theme = "dark", WindowLeft = 100, WindowTop = 200, SetupCompleted = true,
        };
        var json = s.ToExportJson();
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        var back = AppSettings.FromExportJson(json, out var error);
        Assert.Null(error);
        Assert.NotNull(back);
        Assert.Equal("MY-PC", back!.WolPcName);
        Assert.Equal(41000, back.PublicRdpPort);
        Assert.Equal("HomeVPN", back.VpnName);
        Assert.Equal("dark", back.Theme);
        Assert.True(back.SetupCompleted);
        Assert.Null(back.WindowLeft);
        Assert.Null(back.WindowTop);
    }

    [Theory]
    [InlineData("{\"foo\":1}")]
    [InlineData("[1,2,3]")]
    [InlineData("not json")]
    public void Import_rejects_files_that_are_not_settings(string json)
    {
        Assert.Null(AppSettings.FromExportJson(json, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Old_settings_with_valid_router_skip_first_run_setup()
    {
        var configured = AppSettings.FromExportJson("{\"SettingsVersion\":2,\"RouterUrl\":\"http://10.0.0.1:8080/\",\"WolPcName\":\"PC-1\"}", out _)!;
        Assert.True(configured.SetupCompleted);
        var empty = AppSettings.FromExportJson("{\"SettingsVersion\":2,\"RouterUrl\":\"\"}", out _)!;
        Assert.False(empty.SetupCompleted);
        Assert.False(new AppSettings().SetupCompleted);
        Assert.Equal("", new AppSettings().WolPcName);
    }
}

public class CrdSettingsTests
{
    [Theory]
    [InlineData("7f3a1b9c2d4e5f60", "7f3a1b9c2d4e5f60")]
    [InlineData("  7F3A1B9C2D4E5F60  ", "7F3A1B9C2D4E5F60")]
    [InlineData("\"7f3a1b9c2d4e5f60\"", "7f3a1b9c2d4e5f60")]
    [InlineData("1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d", "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d")]
    [InlineData("https://remotedesktop.google.com/access/session/7f3a1b9c2d4e5f60", "7f3a1b9c2d4e5f60")]
    [InlineData("https://remotedesktop.google.com/access/session/7f3a1b9c2d4e5f60?hl=ko", "7f3a1b9c2d4e5f60")]
    [InlineData("remotedesktop.google.com/access/session/7f3a1b9c2d4e5f60#top", "7f3a1b9c2d4e5f60")]
    public void Crd_host_id_accepts_id_or_pasted_session_url(string input, string expected)
        => Assert.Equal(expected, InputRules.NormalizeCrdHostId(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("7f3a1b9c")]                                 // 너무 짧음
    [InlineData("7f3a1b9c2d4e5f60zz")]                       // 16진수가 아닌 글자
    [InlineData("7f3a1b9c2d4e5f60 && calc")]                 // 셸 메타문자
    [InlineData("--------------------")]                     // 16진수 숫자가 없음
    [InlineData("javascript:alert(1)")]
    [InlineData("https://evil.example.com/access/session/x")]
    public void Crd_host_id_rejects_anything_else(string? input)
        => Assert.Null(InputRules.NormalizeCrdHostId(input));

    private static AppSettings Crd(string check) => new()
    {
        UseCrd = true, CrdHostId = "7f3a1b9c2d4e5f60", CrdBootCheckMode = check,
        PublicHost = "myhome.iptime.org", PublicRdpPort = 41000,
        VpnName = "HomeVPN", VpnDesktopIp = "192.168.0.10", VpnRdpPort = 3389,
        BootWaitSeconds = 180, VpnWaitSeconds = 60,
    };

    [Theory]
    [InlineData("none", CrdBootCheck.None)]
    [InlineData("direct", CrdBootCheck.Direct)]
    [InlineData("VPN", CrdBootCheck.Vpn)]
    [InlineData("", CrdBootCheck.None)]
    [InlineData("무엇이든", CrdBootCheck.None)]
    public void Crd_boot_check_mode_is_parsed(string stored, CrdBootCheck expected)
        => Assert.Equal(expected, new AppSettings { CrdBootCheckMode = stored }.CrdCheck);

    [Fact]
    public void Crd_mode_checks_only_what_it_uses()
    {
        // 부팅 확인을 안 하면 일반 접속·VPN 설정이 비어 있어도 막지 않는다.
        var bare = new AppSettings { UseCrd = true };
        Assert.Empty(bare.ValidateConnect(ConnectMode.Crd));
        Assert.NotEmpty(bare.ValidateConnect(ConnectMode.Direct));

        Assert.Empty(Crd("none").ValidateConnect(ConnectMode.Crd));
        Assert.Empty(Crd("direct").ValidateConnect(ConnectMode.Crd));
        Assert.Empty(Crd("vpn").ValidateConnect(ConnectMode.Crd));
    }

    [Fact]
    public void Crd_mode_reports_missing_pieces()
    {
        var off = Crd("none");
        off.UseCrd = false;
        Assert.NotEmpty(off.ValidateConnect(ConnectMode.Crd));

        var badId = Crd("none");
        badId.CrdHostId = "not a device id";
        Assert.Contains("기기 ID", string.Join("\n", badId.ValidateConnect(ConnectMode.Crd)));

        var noDirect = Crd("direct");
        noDirect.PublicHost = "";
        Assert.NotEmpty(noDirect.ValidateConnect(ConnectMode.Crd));

        var noVpn = Crd("vpn");
        noVpn.VpnDesktopIp = "";
        Assert.NotEmpty(noVpn.ValidateConnect(ConnectMode.Crd));

        var badWait = Crd("direct");
        badWait.BootWaitSeconds = 1;
        Assert.NotEmpty(badWait.ValidateConnect(ConnectMode.Crd));
        // 부팅 확인을 안 하면 부팅 대기 시간은 쓰이지 않으므로 검사하지 않는다.
        var skipWait = Crd("none");
        skipWait.BootWaitSeconds = 1;
        Assert.Empty(skipWait.ValidateConnect(ConnectMode.Crd));
    }

    [Theory]
    [InlineData("direct", ConnectMode.Direct)]
    [InlineData("vpn", ConnectMode.Vpn)]
    [InlineData("crd", ConnectMode.Crd)]
    [InlineData("CRD", ConnectMode.Crd)]
    [InlineData("", ConnectMode.Direct)]
    public void Last_connect_mode_round_trips(string stored, ConnectMode expected)
        => Assert.Equal(expected, new AppSettings { LastConnectMode = stored }.LastMode);

    [Fact]
    public void Crd_settings_survive_export_and_import()
    {
        var json = Crd("vpn").ToExportJson();
        var back = AppSettings.FromExportJson(json, out var error);
        Assert.Null(error);
        Assert.True(back!.UseCrd);
        Assert.Equal("7f3a1b9c2d4e5f60", back.CrdHostId);
        Assert.Equal(CrdBootCheck.Vpn, back.CrdCheck);
    }
}
