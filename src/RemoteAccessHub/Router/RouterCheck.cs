using Microsoft.Web.WebView2.WinForms;
using RemoteAccessHub.Core;

namespace RemoteAccessHub.Router;

/// <summary>
/// `--router-check [출력파일]` 모드.
/// 로그인하지 않은 상태에서 공유기 관리자 페이지의 구조만 확인해 민감정보를 제외한 진단 파일을 만든다.
/// 아이디·비밀번호·캡차를 입력하지 않으며, 어떤 설정도 바꾸지 않는다.
/// 로그인 후의 WOL 화면 구조가 필요하면 본 프로그램에서 로그인한 뒤 [진단 내보내기]를 사용한다.
/// </summary>
public static class RouterCheck
{
    public static int Run(LaunchOptions options, AppLog log)
    {
        var settingsPath = options.SettingsPath ?? AppPaths.SettingsFile;
        var settings = AppSettings.Load(settingsPath, log);
        if (!string.IsNullOrWhiteSpace(options.RouterUrlOverride)) settings.RouterUrl = options.RouterUrlOverride!;

        if (!InputRules.TryParseRouterUrl(settings.RouterUrl, out var uri) || uri == null)
        {
            log.Error($"공유기 URL이 올바르지 않습니다: '{settings.RouterUrl}'. --url 로 지정하거나 설정에서 입력하세요.");
            return 2;
        }

        var outPath = options.OutputPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), DiagnosticsExporter.DefaultFileName());
        var exit = 1;

        using var form = new Form
        {
            Text = "RemoteAccessHub — 공유기 구조 확인",
            Width = 1200,
            Height = 820,
            StartPosition = FormStartPosition.CenterScreen,
            Font = new Font("Malgun Gothic", 9.5f),
        };
        UI.AppIcon.ApplyTo(form);
        var status = new Label { Dock = DockStyle.Top, Height = 28, Text = "공유기 페이지를 여는 중...", TextAlign = ContentAlignment.MiddleLeft };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Controls.Add(status);

        var browser = new RouterBrowser(view, log, () => settings);
        form.Shown += async (_, _) =>
        {
            try
            {
                await browser.InitializeAsync(CancellationToken.None);
                browser.ScriptDialogHandler = (_, _) => false; // 확인 창은 자동 수락하지 않는다
                status.Text = "페이지 이동: " + DiagnosticsExporter.MaskUrl(uri.ToString());
                await browser.NavigateAsync(uri.ToString(), TimeSpan.FromSeconds(30), CancellationToken.None);

                status.Text = "화면 로딩 대기 중(최대 90초)...";
                var (loaded, waited) = await browser.WaitForAsync(p => p.HasMeaningfulContent, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(1), CancellationToken.None);
                if (!loaded) log.Warn($"화면이 준비되지 않았습니다(로딩 중). flutter={waited.Flutter} 단락={waited.ParagraphCount} 접근성={waited.SemanticsCount}");

                status.Text = "접근성 트리 활성화 중...";
                var semantics = await browser.EnsureSemanticsAsync(TimeSpan.FromSeconds(25), CancellationToken.None);

                status.Text = "세션 상태 확인 중...";
                var session = await browser.RefreshSessionAsync(CancellationToken.None);
                var snap = await browser.ProbeAsync(CancellationToken.None);

                log.Info($"결과: flutter={snap.Flutter} 접근성노드={snap.SemanticsCount} 단락={snap.ParagraphCount} 세션={session.Result}({session.Reason})");
                log.Info($"마커: 비밀번호칸={snap.Markers.PasswordInput} 로그인버튼={snap.Markers.LoginButton} 로그아웃={snap.Markers.LogoutLabel} PC켜기버튼={snap.Markers.WakeButtons}");

                var json = await DiagnosticsExporter.BuildAsync(browser, settings, log, CancellationToken.None);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                await File.WriteAllTextAsync(outPath, json);
                log.Info("진단 파일 저장: " + outPath);

                // 화면 캡처는 사용자가 직접 확인할 때만 도움이 되므로 같은 폴더에 저장한다.
                // 로그인 전 화면이므로 입력값은 없다. 캡차 이미지가 포함될 수 있으니 공유 전 확인하도록 안내한다.
                var png = await browser.CaptureScreenshotAsync();
                if (png != null)
                {
                    var shot = Path.ChangeExtension(outPath, ".png");
                    await File.WriteAllBytesAsync(shot, png);
                    log.Info("화면 캡처 저장: " + shot + " (공유 전 내용 확인 권장)");
                }

                var ok = snap.IsReadable && session.Result != SessionProbeResult.Unavailable;
                if (!semantics) log.Warn("접근성 트리를 켜지 못했습니다. 장면 텍스트(좌표) 경로로 동작합니다.");
                exit = ok ? 0 : 1;
                status.Text = ok ? "확인 완료. 진단 파일을 저장했습니다." : "확인 실패. 로그를 확인하세요.";
            }
            catch (Exception ex)
            {
                log.Error("공유기 구조 확인 실패: " + ex.Message);
                exit = 2;
            }
            finally
            {
                await Task.Delay(400);
                form.Close();
            }
        };

        Application.Run(form);
        return exit;
    }
}
