using RemoteAccessHub.Core;
using RemoteAccessHub.SelfTest;
using RemoteAccessHub.UI;

namespace RemoteAccessHub;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        ApplicationConfiguration.Initialize();
        AppPaths.EnsureDirectories();

        // 자체검사 로그는 실제 사용 로그와 섞이지 않도록 임시 폴더에 따로 남긴다.
        using var log = new AppLog(options.SelfTest
            ? Path.Combine(Path.GetTempPath(), "RemoteAccessHub-selftest-logs")
            : AppPaths.LogDirectory);
        log.Info($"RemoteAccessHub {AppInfo.Version} 시작 (모드: {options.ModeName})");

        try
        {
            if (options.SelfTest)
            {
                return SelfTestRunner.Run(options, log);
            }

            if (options.RouterCheck)
            {
                return Router.RouterCheck.Run(options, log);
            }

            Application.Run(new MainForm(options, log));
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("치명적 오류: " + ex);
            MessageBox.Show("프로그램 오류가 발생했습니다.\n\n" + ex.Message, "RemoteAccessHub",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }
}
