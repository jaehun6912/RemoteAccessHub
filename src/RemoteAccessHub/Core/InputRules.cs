using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace RemoteAccessHub.Core;

/// <summary>
/// 설정값 검증 규칙. 검증을 통과한 값만 외부 프로세스(mstsc, rasdial, rasphone)에 전달한다.
/// 셸을 거치지 않고 ArgumentList로 넘기지만, 그래도 형식이 어긋난 값은 여기서 차단한다.
/// </summary>
public static partial class InputRules
{
    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.?$")]
    private static partial Regex HostnameRegex();

    [GeneratedRegex(@"(?i)\b([0-9a-f]{2})[:-]([0-9a-f]{2})[:-]([0-9a-f]{2})[:-]([0-9a-f]{2})[:-]([0-9a-f]{2})[:-]([0-9a-f]{2})\b")]
    public static partial Regex MacRegex();

    [GeneratedRegex(@"^[0-9A-Fa-f]{12}$")]
    private static partial Regex BareMacRegex();

    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim();
        if (host.Length > 253) return false;
        if (IPAddress.TryParse(host, out var ip))
        {
            return ip.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork
                or System.Net.Sockets.AddressFamily.InterNetworkV6;
        }
        return HostnameRegex().IsMatch(host);
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    public static bool IsValidVpnName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (name.Length > 256) return false;
        if (name.StartsWith('-') || name.StartsWith('/')) return false;
        foreach (var ch in name)
        {
            if (char.IsControl(ch)) return false;
            if (ch is '"' or '\\' or '|' or '&' or '<' or '>' or '^' or '%') return false;
        }
        return true;
    }

    /// <summary>MAC 주소를 "AA:BB:CC:DD:EE:FF" 형식으로 정규화한다. 형식이 아니면 null.</summary>
    public static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var s = mac.Trim();
        var m = MacRegex().Match(s);
        if (m.Success && m.Value.Length == s.Length)
        {
            return string.Join(":", Enumerable.Range(1, 6).Select(i => m.Groups[i].Value.ToUpperInvariant()));
        }
        if (BareMacRegex().IsMatch(s))
        {
            s = s.ToUpperInvariant();
            return string.Join(":", Enumerable.Range(0, 6).Select(i => s.Substring(i * 2, 2)));
        }
        return null;
    }

    /// <summary>텍스트 안의 모든 MAC 주소를 정규화하여 반환.</summary>
    public static IReadOnlyList<string> ExtractMacs(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (Match m in MacRegex().Matches(text))
        {
            var n = NormalizeMac(m.Value);
            if (n != null && !list.Contains(n)) list.Add(n);
        }
        return list;
    }

    /// <summary>진단·로그용 MAC 마스킹: AA:BB:**:**:**:FF</summary>
    public static string MaskMac(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        return MacRegex().Replace(text, m =>
            $"{m.Groups[1].Value}:{m.Groups[2].Value}:**:**:**:{m.Groups[6].Value}");
    }

    public static bool TryParseRouterUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (!IsValidHost(u.Host.Trim('[', ']')) && u.HostNameType != UriHostNameType.IPv6) return false;
        uri = u;
        return true;
    }

    /// <summary>mstsc /v: 인수용 host:port 문자열 (IPv6는 대괄호).</summary>
    public static string HostPort(string host, int port)
    {
        host = host.Trim();
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return $"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}";
        return $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
    }
}
