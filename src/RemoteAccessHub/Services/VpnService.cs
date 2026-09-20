using System.Diagnostics;
using System.Net.NetworkInformation;
using RemoteAccessHub.Core;

namespace RemoteAccessHub.Services;

public sealed record VpnConnectResult(bool Success, string Message, string Method);

public interface IVpnService
{
    /// <summary>지정한 이름의 Windows VPN이 실제로 연결되어 있는지(네트워크 인터페이스 기준).</summary>
    bool IsConnected(string name);

    /// <summary>
    /// 연결되어 있지 않으면 저장된 자격 증명으로 rasdial 연결을 시도하고, 실패하면 Windows 연결 창(rasphone)을 띄운 뒤
    /// 실제 연결될 때까지 기다린다. 어떤 경우에도 기존 연결을 끊지 않는다.
    /// </summary>
    Task<VpnConnectResult> ConnectAsync(string name, TimeSpan wait, IProgress<string>? progress, CancellationToken ct);

    /// <summary>Windows에 등록된 VPN 연결 이름 목록(전화번호부 파일 기준). 실패 시 빈 목록.</summary>
    IReadOnlyList<string> ListEntries();
}

public sealed class VpnService : IVpnService
{
    private readonly AppLog _log;

    public VpnService(AppLog log) => _log = log;

    public bool IsConnected(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (string.Equals(ni.Name, name, StringComparison.OrdinalIgnoreCase) && ni.OperationalStatus == OperationalStatus.Up)
                    return true;
            }
        }
        catch (Exception ex)
        {
            _log.Debug("네트워크 인터페이스 조회 실패: " + ex.Message);
        }
        return false;
    }

    public async Task<VpnConnectResult> ConnectAsync(string name, TimeSpan wait, IProgress<string>? progress, CancellationToken ct)
    {
        if (!InputRules.IsValidVpnName(name))
            return new VpnConnectResult(false, "VPN 연결 이름이 올바르지 않습니다.", "검증");
        name = name.Trim();

        if (IsConnected(name))
            return new VpnConnectResult(true, $"VPN '{name}'이(가) 이미 연결되어 있어 재사용합니다.", "기존 연결");

        // 1단계: rasdial (저장된 자격 증명 사용). 표준 입력을 닫아 자격 증명 프롬프트에서 멈추지 않게 한다.
        progress?.Report($"VPN '{name}' 연결 시도 중 (저장된 자격 증명)...");
        ProcessResult r;
        try
        {
            r = await ProcessRunner.RunAsync(ProcessRunner.SystemExe("rasdial.exe"), new[] { name }, TimeSpan.FromSeconds(75), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new VpnConnectResult(false, "rasdial 실행 실패: " + ex.Message, "rasdial");
        }

        if (r.TimedOut)
        {
            _log.Warn("rasdial 응답 시간 초과");
        }
        else
        {
            _log.Info($"rasdial 종료 코드 {r.ExitCode}");
        }

        if (!r.TimedOut && r.ExitCode == 0 && await WaitConnectedAsync(name, TimeSpan.FromSeconds(10), ct))
            return new VpnConnectResult(true, $"VPN '{name}' 연결됨 (rasdial).", "rasdial");

        if (!r.TimedOut && r.ExitCode == 0)
            _log.Warn("rasdial은 성공했지만 인터페이스가 아직 활성화되지 않았습니다. 연결 창으로 재시도합니다.");

        var reason = r.TimedOut ? "시간 초과" : DescribeRasError(r.ExitCode, r.Output);

        // 2단계: Windows 연결 창(rasphone -d). 추가 인증(사용자 이름/암호/OTP 등)은 사용자가 직접 입력한다.
        progress?.Report($"자동 연결 실패({reason}). Windows VPN 연결 창을 엽니다. 창에서 연결을 완료하세요.");
        Process? dialog = null;
        try
        {
            dialog = ProcessRunner.StartWindowed(ProcessRunner.SystemExe("rasphone.exe"), new[] { "-d", name });
        }
        catch (Exception ex)
        {
            return new VpnConnectResult(false, $"자동 연결 실패({reason}); 연결 창 실행도 실패: {ex.Message}", "rasphone");
        }

        try
        {
            var deadline = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (IsConnected(name))
                    return new VpnConnectResult(true, $"VPN '{name}' 연결됨 (Windows 연결 창).", "rasphone");
                if (dialog.HasExited)
                {
                    // 창이 닫힌 직후 인터페이스가 올라오는 데 몇 초 걸릴 수 있다.
                    if (await WaitConnectedAsync(name, TimeSpan.FromSeconds(8), ct))
                        return new VpnConnectResult(true, $"VPN '{name}' 연결됨 (Windows 연결 창).", "rasphone");
                    return new VpnConnectResult(false, $"VPN 연결 창이 닫혔지만 '{name}'이(가) 연결되지 않았습니다(취소 또는 실패). 자동 연결 실패 사유: {reason}", "rasphone");
                }
                await Task.Delay(1500, ct);
            }
            return new VpnConnectResult(false, $"VPN 연결 대기 시간({wait.TotalSeconds:0}초)을 초과했습니다.", "rasphone");
        }
        finally
        {
            dialog.Dispose(); // 창은 그대로 둔다(강제 종료하지 않음)
        }
    }

    private async Task<bool> WaitConnectedAsync(string name, TimeSpan wait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (IsConnected(name)) return true;
            await Task.Delay(1000, ct);
        }
        return IsConnected(name);
    }

    public IReadOnlyList<string> ListEntries()
    {
        var names = new List<string>();
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
        };
        foreach (var file in candidates)
        {
            try
            {
                if (!File.Exists(file)) continue;
                foreach (var line in File.ReadLines(file))
                {
                    var t = line.Trim();
                    if (t.Length > 2 && t[0] == '[' && t[^1] == ']')
                    {
                        var n = t[1..^1];
                        if (!names.Contains(n)) names.Add(n);
                    }
                }
            }
            catch { /* ignore */ }
        }
        return names;
    }

    /// <summary>rasdial 종료 코드(RAS 오류 번호) 설명. 자격 증명 값은 절대 포함하지 않는다.</summary>
    public static string DescribeRasError(int code, string? output = null)
    {
        var known = code switch
        {
            0 => "성공",
            619 => "619: 원격 서버에 연결할 수 없음(포트 연결 종료)",
            623 => "623: 지정한 이름의 VPN 항목을 찾을 수 없음",
            668 => "668: 연결이 종료됨",
            691 => "691: 사용자 이름 또는 암호가 올바르지 않음(저장된 자격 증명 없음 가능)",
            720 => "720: PPP 제어 프로토콜 구성 실패",
            741 or 742 => $"{code}: 암호화 설정 불일치",
            789 => "789: L2TP 보안 계층 협상 실패(사전 공유 키 확인)",
            800 => "800: VPN 터널을 만들 수 없음(서버 주소/방화벽 확인)",
            807 => "807: 네트워크 연결이 중단됨",
            809 => "809: VPN 서버가 응답하지 않음(NAT/방화벽)",
            812 => "812: 서버 인증 정책에 의해 거부됨",
            13801 => "13801: IKE 인증 자격 증명이 허용되지 않음",
            13806 => "13806: 컴퓨터 인증서 없음(IKEv2)",
            _ => $"{code}",
        };
        if (!string.IsNullOrWhiteSpace(output))
        {
            var firstLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.Contains("Connecting", StringComparison.OrdinalIgnoreCase));
            if (firstLine != null && firstLine.Length < 160) known += " / " + AppLog.Redact(firstLine);
        }
        return known;
    }
}
