using System.Reflection;

namespace RemoteAccessHub.Core;

/// <summary>명령줄 옵션. 예: --selftest [결과파일] / --mock / --settings 경로</summary>
public sealed class LaunchOptions
{
    public bool SelfTest { get; private set; }
    public bool Mock { get; private set; }
    public bool RouterCheck { get; private set; }
    public string? OutputPath { get; private set; }
    public string? SettingsPath { get; private set; }
    public string? ScreenshotDirectory { get; private set; }
    public string? RouterUrlOverride { get; private set; }

    public string ModeName => SelfTest ? "자체검사" : RouterCheck ? "공유기 구조 확인" : Mock ? "모의 공유기" : "일반";

    public static LaunchOptions Parse(string[] args)
    {
        var o = new LaunchOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--selftest":
                    o.SelfTest = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) o.OutputPath = args[++i];
                    break;
                case "--mock":
                    o.Mock = true;
                    break;
                case "--router-check":
                    o.RouterCheck = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) o.OutputPath = args[++i];
                    break;
                case "--url":
                    if (i + 1 < args.Length) o.RouterUrlOverride = args[++i];
                    break;
                case "--settings":
                    if (i + 1 < args.Length) o.SettingsPath = args[++i];
                    break;
                case "--screenshots":
                    if (i + 1 < args.Length) o.ScreenshotDirectory = args[++i];
                    break;
            }
        }
        return o;
    }
}

public static class AppInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
}

public static class AppPaths
{
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteAccessHub");

    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    public static string LocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAccessHub");

    public static string LogDirectory => Path.Combine(LocalDirectory, "logs");

    /// <summary>WebView2 런타임 작업 폴더. 프로필은 InPrivate로 만들어 쿠키를 디스크에 남기지 않는다.</summary>
    public static string WebViewUserDataDirectory => Path.Combine(LocalDirectory, "WebView2");

    public static void EnsureDirectories()
    {
        foreach (var d in new[] { SettingsDirectory, LocalDirectory, LogDirectory, WebViewUserDataDirectory })
        {
            try { Directory.CreateDirectory(d); } catch { /* 권한 문제 등은 실행 자체를 막지 않는다 */ }
        }
    }
}
