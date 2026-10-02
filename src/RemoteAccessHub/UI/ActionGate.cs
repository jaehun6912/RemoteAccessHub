using RemoteAccessHub.Services;

namespace RemoteAccessHub.UI;

/// <summary>동작 버튼([PC 켜고 접속]·[PC 켜기]·[PC 접속])을 누를 수 있는지 정한다.</summary>
public static class ActionGate
{
    /// <param name="loggedIn">공유기 로그인 확인됨</param>
    /// <param name="busy">PC 켜기·접속 작업 진행 중</param>
    /// <param name="exiting">[종료] 진행 중</param>
    /// <param name="preparingAdmin">로그인 직후 관리 화면 준비([관리도구] 선택) 중 — "관리 화면이 준비됐습니다"가 뜨기 전</param>
    public static (bool Wake, bool WakeConnect, bool Connect) Compute(bool loggedIn, bool busy, bool exiting, bool preparingAdmin)
    {
        var idle = !busy && !exiting;
        var preparing = loggedIn && preparingAdmin;
        var wake = loggedIn && idle && !preparing;
        // [PC 접속]은 공유기 로그인 없이도 이미 켜진 PC에 바로 접속할 수 있어야 하므로 로그인 전에는 막지 않는다.
        // 로그인 뒤 관리 화면을 준비하는 동안에만 다른 두 버튼과 함께 막는다.
        var connect = idle && !preparing;
        return (wake, wake, connect);
    }

    /// <summary>
    /// [PC 접속] 버튼을 깜빡여 눈에 띄게 할 때인지. PC가 켜진 것이 <b>확인됐을 때만</b> 깜빡인다.
    /// 응답이 없거나(꺼졌을 수도, 포트가 막혔을 수도) 아직 확인 전이면 깜빡이지 않는다.
    /// </summary>
    /// <param name="setting">설정에서 켰는지</param>
    /// <param name="power">지금 전원 배지 상태</param>
    /// <param name="connectEnabled">[PC 접속]을 누를 수 있는 상태인지</param>
    public static bool ShouldBlinkConnect(bool setting, PcPowerState power, bool connectEnabled, bool busy, bool exiting)
        => setting && power == PcPowerState.On && connectEnabled && !busy && !exiting;
}
