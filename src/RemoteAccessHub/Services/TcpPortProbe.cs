using System.Net.Sockets;

namespace RemoteAccessHub.Services;

public interface IPortProbe
{
    /// <summary>TCP 연결이 timeout 안에 성립하면 true. 취소 시 OperationCanceledException.</summary>
    Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct);
}

public sealed class TcpPortProbe : IPortProbe
{
    public async Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 시간 초과, 연결 거부, 이름 확인 실패 등은 "아직 열리지 않음"
            return false;
        }
    }
}
