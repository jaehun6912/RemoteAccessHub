using RemoteAccessHub.Services;

namespace RemoteAccessHub.SelfTest;

/// <summary>자체검사·단위검사용 가짜 VPN 서비스. 실제 rasdial/rasphone을 실행하지 않는다.</summary>
public sealed class FakeVpnService : IVpnService
{
    public HashSet<string> Connected { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ConnectSucceeds { get; set; } = true;
    public string FailMessage { get; set; } = "691: 사용자 이름 또는 암호가 올바르지 않음";
    public TimeSpan ConnectDelay { get; set; } = TimeSpan.Zero;
    public int ConnectCalls { get; private set; }
    public bool DisconnectCalled { get; private set; }
    public List<string> Entries { get; } = new() { "HomeVPN", "테스트 VPN" };

    public bool IsConnected(string name) => Connected.Contains(name.Trim());

    public async Task<VpnConnectResult> ConnectAsync(string name, TimeSpan wait, IProgress<string>? progress, CancellationToken ct)
    {
        ConnectCalls++;
        progress?.Report("(가짜) 연결 시도");
        if (ConnectDelay > TimeSpan.Zero) await Task.Delay(ConnectDelay, ct);
        if (!ConnectSucceeds) return new VpnConnectResult(false, FailMessage, "fake");
        Connected.Add(name.Trim());
        return new VpnConnectResult(true, "(가짜) 연결됨", "fake");
    }

    public IReadOnlyList<string> ListEntries() => Entries;

    /// <summary>프로그램 어디에서도 호출되지 않아야 한다(호출되면 검사 실패).</summary>
    public void Disconnect() => DisconnectCalled = true;
}

/// <summary>N번째 시도부터 열리는 가짜 포트 검사기.</summary>
public sealed class FakePortProbe : IPortProbe
{
    public int OpenAfterAttempts { get; set; } = 1;
    public int Attempts { get; private set; }
    public bool NeverOpen { get; set; }
    public TimeSpan AttemptDelay { get; set; } = TimeSpan.FromMilliseconds(50);
    public List<string> Targets { get; } = new();

    public async Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        Attempts++;
        Targets.Add($"{host}:{port}");
        await Task.Delay(AttemptDelay, ct);
        if (NeverOpen) return false;
        return Attempts >= OpenAfterAttempts;
    }
}

public sealed class FakeCrdLauncher : ICrdLauncher
{
    public List<string> Opened { get; } = new();
    public int OpenCount => Opened.Count;

    /// <summary>앱이 설치된 PC를 흉내 낼 때 바꾼다.</summary>
    public CrdOpenTarget Target { get; set; } = CrdOpenTarget.Browser;

    public CrdOpenTarget Open(string? hostId)
    {
        Opened.Add(CrdLauncher.BuildUrl(hostId));
        return Target;
    }
}

public sealed class FakeRdpLauncher : IRdpLauncher
{
    public List<(string Host, int Port, bool FullScreen)> Launches { get; } = new();
    public int LaunchCount => Launches.Count;

    public void Launch(string host, int port, bool fullScreen) => Launches.Add((host, port, fullScreen));
}
