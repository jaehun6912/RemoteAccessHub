using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteAccessHub.Core;

public enum ConnectMode
{
    Direct,
    Vpn,
    /// <summary>크롬 원격 데스크톱(구글 중계). 포트포워딩·VPN 없이 브라우저로 연결한다.</summary>
    Crd,
}

/// <summary>윗줄 배지에 PC 전원 상태를 보여 줄 때 어디로 확인할지.</summary>
public enum PowerSource
{
    /// <summary>VPN이 이미 연결되어 있으면 내부 IP로, 아니면 일반 접속 주소로 확인.</summary>
    Auto,
    /// <summary>일반 접속 주소·포트로만 확인.</summary>
    Direct,
    /// <summary>VPN이 연결되어 있을 때 내부 IP·포트로만 확인(확인하려고 VPN을 연결하지는 않는다).</summary>
    Vpn,
    /// <summary>확인하지 않음.</summary>
    Off,
}

/// <summary>크롬 원격 데스크톱으로 접속할 때 PC가 켜졌는지 확인하는 방법.</summary>
public enum CrdBootCheck
{
    /// <summary>확인하지 않고 바로 연다(크롬 원격 데스크톱은 열어 둘 포트가 없다).</summary>
    None,
    /// <summary>일반 접속 주소·포트가 응답하는지로 확인.</summary>
    Direct,
    /// <summary>VPN을 연결한 뒤 내부 IP·포트가 응답하는지로 확인.</summary>
    Vpn,
}

/// <summary>
/// 사용자 설정. 비밀번호·캡차·쿠키·세션 토큰은 어떤 필드에도 저장하지 않는다.
/// </summary>
public sealed class AppSettings
{
    /// <summary>설정 파일 형식 버전. 저장할 때 현재 버전으로 기록한다. 없으면(0) 1.1.0 이전 파일.</summary>
    public const int CurrentSettingsVersion = 3;
    public int SettingsVersion { get; set; }

    /// <summary>첫 실행 시작 설정을 마쳤는지. 마치기 전에는 실행할 때마다 시작 설정 창을 연다. 3 이전 파일은 공유기 설정이 유효하면 마친 것으로 본다.</summary>
    public bool SetupCompleted { get; set; }

    // --- 공유기 --- (첫 실행 때 시작 설정에서 입력)
    public string RouterUrl { get; set; } = "";
    public string WolPcName { get; set; } = "";
    public string WolPcMac { get; set; } = "";
    public bool AllowRouterCertificateError { get; set; } = false;

    // --- 일반(직접) RDP 접속 ---
    public string PublicHost { get; set; } = "";
    public int PublicRdpPort { get; set; } = 3389;

    // --- VPN 접속 ---
    public string VpnName { get; set; } = "";
    public string VpnDesktopIp { get; set; } = "";
    public int VpnRdpPort { get; set; } = 3389;
    public int VpnWaitSeconds { get; set; } = 120;

    // --- 크롬 원격 데스크톱(Chrome Remote Desktop) ---
    /// <summary>접속 방식에 "크롬 원격 데스크톱"을 넣을지.</summary>
    public bool UseCrd { get; set; }

    /// <summary>크롬 원격 데스크톱 기기 ID. 비어 있으면 기기 목록 화면을 연다.</summary>
    public string CrdHostId { get; set; } = "";

    /// <summary>"none" | "direct" | "vpn". 부팅 확인 방법.</summary>
    public string CrdBootCheckMode { get; set; } = "none";

    // --- PC 전원 상태 배지 ---
    /// <summary>"auto" | "direct" | "vpn" | "off".</summary>
    public string PowerCheckMode { get; set; } = "auto";

    /// <summary>전원 상태를 다시 확인하는 주기(초).</summary>
    public int PowerCheckSeconds { get; set; } = 60;

    /// <summary>
    /// 알림이 필요하면 버튼을 천천히 깜빡인다(PC가 켜지면 [PC 접속], 공유기 로그인이 풀리면 [공유기 화면]).
    /// 설정 파일의 이름은 1.6.1 때 쓰던 것을 그대로 둬 기존 설정과 호환된다.
    /// </summary>
    [JsonPropertyName("BlinkConnectWhenPcOn")]
    public bool BlinkAttentionButtons { get; set; } = true;

    // --- 공통 ---
    public int BootWaitSeconds { get; set; } = 180;
    public bool RdpFullScreen { get; set; } = true;
    public bool AutoCollapseAfterLogin { get; set; } = true;
    /// <summary>WOL 확인창(PC를 켜시겠습니까?)의 [확인]을 자동으로 누른다. 1.1.1부터 기본 켜짐.</summary>
    public bool AutoConfirmWakeDialog { get; set; } = true;
    /// <summary>로그인 직후 [관리도구]/[설정마법사] 선택 화면에서 [관리도구]를 자동으로 누른다.</summary>
    public bool AutoSelectAdminTool { get; set; } = true;
    public string LastConnectMode { get; set; } = "direct";

    // --- 화면 ---
    /// <summary>"system"(Windows 앱 모드를 따름) | "dark" | "light"</summary>
    public string Theme { get; set; } = "system";
    public bool ShowLog { get; set; } = false;
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }

    // --- 고급(공유기 화면 구조가 바뀌면 조정) ---
    /// <summary>
    /// WOL 화면 경로. 실제 AX2004T(15.36.6)는 경로 방식 주소(/ui/wol)를 쓴다(2026-09-14 진단으로 확인).
    /// 이전 버전의 기본값 "#/wol"은 불러올 때 "/ui/wol"로 바꾼다.
    /// </summary>
    public string WolPageRoute { get; set; } = "/ui/wol";
    public string AdminToolLabel { get; set; } = "관리도구";
    public string RefreshLabel { get; set; } = "페이지 새로고침";
    public string WolMenuGroupLabel { get; set; } = "특수 기능";
    public string WolMenuLabel { get; set; } = "WOL 기능";
    public string WakeButtonPattern { get; set; } = @"^PC\s*켜기$";
    public string ConfirmDialogPattern { get; set; } = "PC를 켜시겠습니까";
    public string WakeProgressPattern { get; set; } = "PC를 켜는 중";
    public int SessionProbeIntervalSeconds { get; set; } = 15;

    [JsonIgnore]
    public PowerSource PowerCheck => (PowerCheckMode ?? "").Trim().ToLowerInvariant() switch
    {
        "direct" => PowerSource.Direct,
        "vpn" => PowerSource.Vpn,
        "off" => PowerSource.Off,
        _ => PowerSource.Auto,
    };

    [JsonIgnore]
    public CrdBootCheck CrdCheck => (CrdBootCheckMode ?? "").Trim().ToLowerInvariant() switch
    {
        "direct" => CrdBootCheck.Direct,
        "vpn" => CrdBootCheck.Vpn,
        _ => CrdBootCheck.None,
    };

    [JsonIgnore]
    public ConnectMode LastMode => (LastConnectMode ?? "").Trim().ToLowerInvariant() switch
    {
        "vpn" => ConnectMode.Vpn,
        "crd" => ConnectMode.Crd,
        _ => ConnectMode.Direct,
    };

    [JsonIgnore]
    public Uri? RouterUri => InputRules.TryParseRouterUrl(RouterUrl, out var u) ? u : null;

    /// <summary>API 호출 origin (scheme://host:port). 공유기 URL이 잘못되면 null.</summary>
    [JsonIgnore]
    public string? RouterOrigin => RouterUri?.GetLeftPart(UriPartial.Authority);

    [JsonIgnore]
    public RouterUiText UiText => new(AdminToolLabel, WolMenuLabel, WolMenuGroupLabel, WakeButtonPattern);

    /// <summary>WOL 화면의 origin 기준 경로(항상 "/"로 시작). 예: "/ui/wol".</summary>
    [JsonIgnore]
    public string WolPagePath
    {
        get
        {
            var r = (WolPageRoute ?? "").Trim();
            if (r.Length == 0) return "/ui/wol";
            if (r.StartsWith("#/", StringComparison.Ordinal)) return "/ui/" + r[2..];
            if (r.StartsWith('/')) return r;
            if (Uri.TryCreate(r, UriKind.Absolute, out var u)) return u.AbsolutePath;
            return "/ui/" + r.TrimStart('#');
        }
    }

    /// <summary>불러온 설정의 알려진 옛 기본값을 현재 기본값으로 바꾼다.</summary>
    public void Migrate()
    {
        if (SettingsVersion < 2)
        {
            // 1.1.0 이전에는 확인창 자동 확인이 기본 꺼짐이었고, 사용자가 켜 달라고 요청했다(2026-09-14).
            // 옛 파일의 false는 사용자가 고른 값이 아니라 옛 기본값이므로 한 번만 켠다.
            AutoConfirmWakeDialog = true;
        }
        if (SettingsVersion < 3 && ValidateRouter().Count == 0)
        {
            // 시작 설정이 생기기 전(1.3.x 이하)부터 쓰던 사람에게는 시작 설정을 다시 띄우지 않는다.
            SetupCompleted = true;
        }
        if (SettingsVersion < CurrentSettingsVersion) SettingsVersion = CurrentSettingsVersion;
        if (string.Equals(WolPageRoute?.Trim(), "#/wol", StringComparison.Ordinal)) WolPageRoute = "/ui/wol";
        if (string.IsNullOrWhiteSpace(AdminToolLabel)) AdminToolLabel = "관리도구";
        if (string.IsNullOrWhiteSpace(WolMenuGroupLabel)) WolMenuGroupLabel = "특수 기능";
        if (string.IsNullOrWhiteSpace(RefreshLabel)) RefreshLabel = "페이지 새로고침";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load(string path, AppLog? log = null)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            s.Migrate();
            return s;
        }
        catch (Exception ex)
        {
            log?.Warn($"설정 파일을 읽지 못해 기본값을 사용합니다: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        SettingsVersion = CurrentSettingsVersion;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    /// <summary>공유기 로그인/WOL에 필요한 설정 검증.</summary>
    public IReadOnlyList<string> ValidateRouter()
    {
        var errors = new List<string>();
        if (!InputRules.TryParseRouterUrl(RouterUrl, out _))
            errors.Add("공유기 관리자 페이지 URL이 올바르지 않습니다. 예: http://ddns.example.com:8080/ 또는 https://192.168.0.1/");
        if (string.IsNullOrWhiteSpace(WolPcName))
            errors.Add("WOL 대상 PC 이름을 입력하세요.");
        if (!string.IsNullOrWhiteSpace(WolPcMac) && InputRules.NormalizeMac(WolPcMac) == null)
            errors.Add("WOL 대상 MAC 주소 형식이 올바르지 않습니다. 예: 00:11:22:33:44:55");
        try { _ = new System.Text.RegularExpressions.Regex(WakeButtonPattern); }
        catch { errors.Add("PC 켜기 버튼 패턴(정규식)이 올바르지 않습니다."); }
        if (PowerCheckSeconds is < 15 or > 3600)
            errors.Add("PC 전원 확인 주기는 15~3600초 사이여야 합니다.");
        if (SessionProbeIntervalSeconds is < 5 or > 600)
            errors.Add("세션 확인 주기는 5~600초 사이여야 합니다.");
        return errors;
    }

    /// <summary>선택한 접속 모드에 필요한 설정만 검증한다. 다른 모드의 설정이 비어 있어도 막지 않는다.</summary>
    public IReadOnlyList<string> ValidateConnect(ConnectMode mode)
    {
        var errors = new List<string>();
        if (mode == ConnectMode.Crd)
        {
            if (!UseCrd)
                errors.Add("크롬 원격 데스크톱 접속이 꺼져 있습니다.");
            if (CrdHostId.Length > 0 && InputRules.NormalizeCrdHostId(CrdHostId) == null)
                errors.Add("크롬 원격 데스크톱 기기 ID 형식이 올바르지 않습니다.");
            if (CrdCheck == CrdBootCheck.Direct)
            {
                if (!InputRules.IsValidHost(PublicHost)) errors.Add("부팅 확인에 쓸 일반 접속 주소가 올바르지 않습니다.");
                if (!InputRules.IsValidPort(PublicRdpPort)) errors.Add("부팅 확인에 쓸 일반 접속 포트는 1~65535 사이여야 합니다.");
            }
            else if (CrdCheck == CrdBootCheck.Vpn)
            {
                if (!InputRules.IsValidVpnName(VpnName)) errors.Add("부팅 확인에 쓸 Windows VPN 연결 이름이 올바르지 않습니다.");
                if (!InputRules.IsValidHost(VpnDesktopIp)) errors.Add("부팅 확인에 쓸 데스크톱 내부 IP가 올바르지 않습니다.");
                if (!InputRules.IsValidPort(VpnRdpPort)) errors.Add("부팅 확인에 쓸 내부 포트는 1~65535 사이여야 합니다.");
                if (VpnWaitSeconds is < 10 or > 900) errors.Add("VPN 연결 대기 시간은 10~900초 사이여야 합니다.");
            }
            if (CrdCheck != CrdBootCheck.None && BootWaitSeconds is < 10 or > 3600)
                errors.Add("부팅 대기 시간은 10~3600초 사이여야 합니다.");
            return errors;
        }
        if (mode == ConnectMode.Direct)
        {
            if (!InputRules.IsValidHost(PublicHost))
                errors.Add("일반 접속 주소(DDNS 또는 공인 IP)가 올바르지 않습니다.");
            if (!InputRules.IsValidPort(PublicRdpPort))
                errors.Add("일반 접속 RDP 외부 포트는 1~65535 사이여야 합니다.");
        }
        else
        {
            if (!InputRules.IsValidVpnName(VpnName))
                errors.Add("Windows VPN 연결 이름이 올바르지 않습니다.");
            if (!InputRules.IsValidHost(VpnDesktopIp))
                errors.Add("VPN 연결 후 사용할 데스크톱 내부 IP가 올바르지 않습니다.");
            if (!InputRules.IsValidPort(VpnRdpPort))
                errors.Add("내부 RDP 포트는 1~65535 사이여야 합니다.");
            if (VpnWaitSeconds is < 10 or > 900)
                errors.Add("VPN 연결 대기 시간은 10~900초 사이여야 합니다.");
        }
        if (BootWaitSeconds is < 10 or > 3600)
            errors.Add("부팅 대기 시간은 10~3600초 사이여야 합니다.");
        return errors;
    }

    /// <summary>
    /// 설정 내보내기용 JSON. 비밀번호·보안문자·쿠키는 원래 설정에 없으므로 들어가지 않는다.
    /// 창 위치처럼 이 PC에만 의미 있는 값은 뺀다.
    /// </summary>
    public string ToExportJson()
    {
        var copy = Clone();
        copy.SettingsVersion = CurrentSettingsVersion;
        copy.WindowLeft = null;
        copy.WindowTop = null;
        return JsonSerializer.Serialize(copy, JsonOptions);
    }

    /// <summary>내보낸 설정 파일을 읽는다. 이 프로그램의 설정 파일이 아니면 null과 이유를 돌려준다.</summary>
    public static AppSettings? FromExportJson(string json, out string? error)
    {
        error = null;
        try
        {
            using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                var root = doc.RootElement;
                var known = root.ValueKind == JsonValueKind.Object
                    && root.EnumerateObject().Any(p => p.Name.Equals(nameof(RouterUrl), StringComparison.OrdinalIgnoreCase)
                                                    || p.Name.Equals(nameof(SettingsVersion), StringComparison.OrdinalIgnoreCase));
                if (!known)
                {
                    error = "RemoteAccessHub 설정 파일이 아닙니다.";
                    return null;
                }
            }
            var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (s == null)
            {
                error = "설정 내용을 읽지 못했습니다.";
                return null;
            }
            s.Migrate();
            return s;
        }
        catch (JsonException ex)
        {
            error = "JSON 형식이 올바르지 않습니다: " + ex.Message;
            return null;
        }
    }
}
