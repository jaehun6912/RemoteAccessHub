using System.Diagnostics;
using System.Text;

namespace RemoteAccessHub.Services;

public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut);

/// <summary>
/// 외부 프로세스 실행 도우미. 항상 UseShellExecute=false + ArgumentList를 사용해 셸 해석을 거치지 않는다.
/// </summary>
public static class ProcessRunner
{
    public static string SystemExe(string name) => Path.Combine(Environment.SystemDirectory, name);

    public static ProcessStartInfo Build(string file, IEnumerable<string> args, bool redirect)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = redirect,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            RedirectStandardInput = redirect,
        };
        if (redirect)
        {
            // rasdial 등은 콘솔 코드페이지(한국어 949)로 출력하므로 기본 인코딩을 따른다.
            try
            {
                psi.StandardOutputEncoding = Encoding.GetEncoding(GetOemCodePage());
                psi.StandardErrorEncoding = psi.StandardOutputEncoding;
            }
            catch
            {
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static int GetOemCodePage()
    {
        try { return System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage; }
        catch { return 437; }
    }

    /// <summary>표준 입력을 즉시 닫고 종료까지 기다린다. 취소나 시간 초과 시 프로세스를 종료한다.</summary>
    public static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var psi = Build(file, args, redirect: true);
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.Start();
        try { p.StandardInput.Close(); } catch { /* ignore */ }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            if (ct.IsCancellationRequested) throw;
            string partial; lock (sb) partial = sb.ToString();
            return new ProcessResult(-1, partial, TimedOut: true);
        }
        // 출력 스트림 마무리
        try { await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
        string output; lock (sb) output = sb.ToString();
        return new ProcessResult(p.ExitCode, output, TimedOut: false);
    }

    /// <summary>창을 띄우는 프로세스(mstsc, rasphone)를 시작하고 Process 객체를 돌려준다.</summary>
    public static Process StartWindowed(string file, IEnumerable<string> args)
    {
        var psi = Build(file, args, redirect: false);
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} 실행에 실패했습니다.");
        return p;
    }
}
