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
