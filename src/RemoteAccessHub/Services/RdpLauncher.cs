using RemoteAccessHub.Core;

namespace RemoteAccessHub.Services;

public interface IRdpLauncher
{
    /// <summary>Windows 기본 원격 데스크톱(mstsc.exe)을 실행한다. 자격 증명은 mstsc가 직접 묻는다.</summary>
    void Launch(string host, int port, bool fullScreen);
}

public sealed class RdpLauncher : IRdpLauncher
{
    private readonly AppLog _log;

    public RdpLauncher(AppLog log) => _log = log;

    public void Launch(string host, int port, bool fullScreen)
    {
        if (!InputRules.IsValidHost(host)) throw new ArgumentException("RDP 주소가 올바르지 않습니다.", nameof(host));
        if (!InputRules.IsValidPort(port)) throw new ArgumentException("RDP 포트가 올바르지 않습니다.", nameof(port));

        var args = new List<string> { "/v:" + InputRules.HostPort(host, port) };
        if (fullScreen) args.Add("/f");

        var exe = ProcessRunner.SystemExe("mstsc.exe");
        _log.Info($"원격 데스크톱 실행: mstsc {string.Join(' ', args)}");
        using var p = ProcessRunner.StartWindowed(exe, args);
    }
}
