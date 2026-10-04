namespace RemoteAccessHub.UI;

/// <summary>
/// 공유기 로그인·관리 화면을 띄우는 별도 창.
/// 사용자가 닫아도 WebView를 버리지 않고 숨기기만 한다(로그인 세션과 쿠키를 잃지 않도록).
/// 로그인이 확인되면 메인 창이 이 창을 숨긴다.
/// </summary>
public sealed class RouterWindow : Form
{
    private readonly Panel _host = new();
    private bool _closeForReal;
    private bool _placed;

    /// <param name="browser">이 창이 담을 공유기 화면(WebView) 컨트롤.</param>
    public RouterWindow(Control browser, Size client, Size minimum)
    {
        Text = "공유기 화면";
        AppIcon.ApplyTo(this);
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = client;
        MinimumSize = new Size(minimum.Width, minimum.Height);

        _host.Dock = DockStyle.Fill;
        browser.Dock = DockStyle.Fill;
        _host.Controls.Add(browser);
        Controls.Add(_host);

        HandleCreated += (_, _) => Theme.ApplyTitleBar(this);
        ApplyTheme();
    }

    /// <summary>커서 처리에서 "공유기 화면 위"를 판단할 때 쓰는 영역.</summary>
    public Control BrowserHost => _host;

    public void ApplyTheme()
    {
        BackColor = Theme.Current.Background;
        _host.BackColor = Theme.Current.Background;
        Theme.ApplyTitleBar(this);
    }

    /// <summary>메인 창 가운데에 띄운다(처음 띄울 때만 위치를 잡고, 그 뒤에는 사용자가 옮긴 자리를 지킨다).</summary>
    public void ShowFor(Form owner, string title)
    {
        Text = title;
        if (!_placed)
        {
            _placed = true;
            var area = Screen.FromControl(owner).WorkingArea;
            var x = owner.Left + (owner.Width - Width) / 2;
            var y = owner.Top + owner.Height + 8;
            // 메인 창 아래가 좁으면 화면 안으로 끌어온다.
            if (y + Height > area.Bottom) y = Math.Max(area.Top, area.Bottom - Height);
            Location = new Point(
                Math.Max(area.Left, Math.Min(x, area.Right - Width)),
                Math.Max(area.Top, y));
        }
        Owner = owner;
        if (!Visible) Show(owner);
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
    }

    /// <summary>프로그램을 끝낼 때만 진짜로 닫는다.</summary>
    public void CloseForReal()
    {
        _closeForReal = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // X로 닫아도 로그인 세션을 잃지 않도록 숨기기만 한다. 프로그램 종료 때는 그대로 닫힌다.
        if (!_closeForReal && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}
