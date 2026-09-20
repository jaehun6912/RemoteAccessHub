using RemoteAccessHub.UI;
using Xunit;

namespace RemoteAccessHub.Tests;

public class CursorGuardTests
{
    private const int Showing = CursorGuard.CURSOR_SHOWING;
    private const int Suppressed = CursorGuard.CURSOR_SUPPRESSED;

    [Fact]
    public void Restores_only_hidden_cursor_over_own_window_outside_browser()
    {
        Assert.True(CursorGuard.ShouldRestore(0, pointerOverOwnWindow: true, pointerOverBrowser: false));
    }

    [Theory]
    [InlineData(Showing, true, false)]      // 이미 보임
    [InlineData(0, false, false)]           // 다른 창(설정 창·다른 프로그램) 위
    [InlineData(0, true, true)]             // 펼쳐진 공유기 화면 위: 입력 중 숨김은 정상 동작
    [InlineData(Suppressed, true, false)]   // 터치·펜 입력으로 시스템이 감춤
    public void Leaves_cursor_alone_otherwise(int flags, bool overOwn, bool overBrowser)
    {
        Assert.False(CursorGuard.ShouldRestore(flags, overOwn, overBrowser));
    }
}
