using RemoteAccessHub.Core;


namespace RemoteAccessHub.UI;

public sealed record ModeOption(ConnectMode Mode, string Title, string Detail, bool Enabled);

/// <summary>접속 방식 선택지. 방식마다 그 방식에 필요한 설정만 검사한다.</summary>
public static class ModeOptions
{
    public static IReadOnlyList<ModeOption> For(AppSettings s)
    {
        var list = new List<ModeOption> { Build(s, ConnectMode.Direct), Build(s, ConnectMode.Vpn) };
        // 크롬 원격 데스크톱은 설정에서 켠 경우에만 선택지에 넣는다(쓰지 않는 사람에게 빈 항목을 보이지 않도록).
        if (s.UseCrd) list.Add(Build(s, ConnectMode.Crd));
        return list;
    }

    public static ModeOption Build(AppSettings s, ConnectMode mode)
    {
        var title = mode switch
        {
            ConnectMode.Vpn => "VPN 접속",
            ConnectMode.Crd => "크롬 원격 데스크톱",
            _ => "일반 접속",
        };
        if (s.ValidateConnect(mode).Count > 0)
            return new(mode, title, "설정 필요 · ⚙ 설정에서 입력", false);
        var detail = mode switch
        {
            ConnectMode.Vpn => $"{s.VpnName.Trim()} 연결 후 {InputRules.HostPort(s.VpnDesktopIp, s.VpnRdpPort)}",
            ConnectMode.Crd => CrdDetail(s),
            _ => InputRules.HostPort(s.PublicHost, s.PublicRdpPort),
        };
        return new(mode, title, detail, true);
    }

    private static string CrdDetail(AppSettings s)
    {
        var where = InputRules.NormalizeCrdHostId(s.CrdHostId) == null ? "브라우저에서 기기 고르기" : "저장된 기기로 연결";
        var check = s.CrdCheck switch
        {
            CrdBootCheck.Direct => " · 부팅 확인: 일반 접속 주소",
            CrdBootCheck.Vpn => " · 부팅 확인: VPN",
            _ => " · 부팅 확인 없음",
        };
        return where + check;
    }
}
