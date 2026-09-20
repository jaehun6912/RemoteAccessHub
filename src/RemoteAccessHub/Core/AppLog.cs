using System.Text;
using System.Text.RegularExpressions;

namespace RemoteAccessHub.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Message)
{
    public string Format() =>
        $"{Time:HH:mm:ss} [{LevelText}] {Message}";

    public string LevelText => Level switch
    {
        LogLevel.Debug => "디버그",
        LogLevel.Info => "정보",
        LogLevel.Warn => "경고",
        LogLevel.Error => "오류",
        _ => "?",
    };
}

/// <summary>
/// 화면 로그 + 파일 로그. 모든 메시지는 <see cref="Redact"/>를 거쳐 비밀번호·쿠키·토큰·URL 쿼리·MAC을 가린다.
/// </summary>
public sealed partial class AppLog : IDisposable
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = new();
    private StreamWriter? _writer;
    private const int MaxEntries = 2000;

    public event Action<LogEntry>? Appended;

    public AppLog(string? logDirectory)
    {
        if (logDirectory == null) return;
        try
        {
            Directory.CreateDirectory(logDirectory);
            var file = Path.Combine(logDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
            _writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            _writer = null; // 파일 로그 실패는 무시 (화면 로그는 계속)
        }
    }

    public void Debug(string message) => Append(LogLevel.Debug, message);
    public void Info(string message) => Append(LogLevel.Info, message);
    public void Warn(string message) => Append(LogLevel.Warn, message);
    public void Error(string message) => Append(LogLevel.Error, message);

    private void Append(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, Redact(message));
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            try { _writer?.WriteLine(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + entry.Level + "] " + entry.Message); }
            catch { /* ignore */ }
        }
        Appended?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot(int max = 500)
    {
        lock (_gate)
        {
            var start = Math.Max(0, _entries.Count - max);
            return _entries.Skip(start).ToList();
        }
    }

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|pw|cookie|set-cookie|token|secret|captcha|authorization)\b\s*[=:]\s*[^\s;,&]+")]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"\?[^\s""'<>]*")]
    private static partial Regex QueryRegex();

    /// <summary>비밀정보 후보를 가린다. URL의 쿼리 문자열은 통째로 제거하고 MAC 주소는 마스킹한다.</summary>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        var s = SecretRegex().Replace(message, m => m.Groups[1].Value + "=<가림>");
        s = QueryRegex().Replace(s, "?<쿼리 제거>");
        s = InputRules.MaskMac(s);
        return s;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
