using RemoteAccessHub.Core;
using Xunit;

namespace RemoteAccessHub.Tests;

public class SessionTrackerTests
{
    [Fact]
    public void Starts_unknown_and_wake_is_not_allowed()
    {
        var t = new SessionTracker();
        Assert.Equal(SessionState.Unknown, t.State);
        Assert.False(t.IsLoggedIn);
    }

    [Fact]
    public void Ok_latches_logged_in()
    {
        var t = new SessionTracker();
        t.Apply(SessionProbeResult.Ok, "session/info");
        Assert.True(t.IsLoggedIn);
        Assert.NotNull(t.LastConfirmedAt);
    }

    [Fact]
    public void Unavailable_never_changes_state()
    {
        var t = new SessionTracker();
        t.Apply(SessionProbeResult.Unavailable, "hidden");
        Assert.Equal(SessionState.Unknown, t.State);

        t.Apply(SessionProbeResult.Ok, "ok");
        for (var i = 0; i < 10; i++) t.Apply(SessionProbeResult.Unavailable, "화면 숨김/판독 불가");
        Assert.True(t.IsLoggedIn);
        Assert.Equal(10, t.ConsecutiveUnavailable);
        Assert.Contains("상태 유지", t.Describe());

        t.Apply(SessionProbeResult.Unauthenticated, "-31998");
        Assert.Equal(SessionState.LoggedOut, t.State);
        t.Apply(SessionProbeResult.Unavailable, "x");
        Assert.Equal(SessionState.LoggedOut, t.State);
    }

    [Fact]
    public void Unauthenticated_is_definitive_logout_and_raises_event()
    {
        var t = new SessionTracker();
        var events = new List<(SessionState, SessionState)>();
        t.StateChanged += (o, n, _) => events.Add((o, n));
        t.Apply(SessionProbeResult.Ok, "ok");
        t.Apply(SessionProbeResult.Ok, "ok again"); // no event
        t.Apply(SessionProbeResult.Unauthenticated, "expired");
        Assert.Equal(2, events.Count);
        Assert.Equal((SessionState.Unknown, SessionState.LoggedIn), events[0]);
        Assert.Equal((SessionState.LoggedIn, SessionState.LoggedOut), events[1]);
    }

    [Fact]
    public void Reset_returns_to_unknown()
    {
        var t = new SessionTracker();
        t.Apply(SessionProbeResult.Ok, "ok");
        t.Reset("url changed");
        Assert.Equal(SessionState.Unknown, t.State);
        Assert.Null(t.LastConfirmedAt);
    }
}
