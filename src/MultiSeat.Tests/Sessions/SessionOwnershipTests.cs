using MultiSeat.Service.Interop;
using MultiSeat.Service.Sessions;
using Xunit;
using static MultiSeat.Service.Sessions.SessionLauncher;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// Issue #87, defence in depth: a seat knows its session only by number, so "is the session
/// alive?" must also ask whether the session is still logged on as the seat's account. Otherwise
/// a number Windows had handed to another logon would be taken for the seat's own session.
/// The WTS lookups are faked, so these run without a Windows session.
/// </summary>
public class SessionOwnershipTests
{
    private const int Sid = 7;

    private static SessionCheck Classify(
        WtsApi.WtsConnectState? state, string? owner, string account = "Seat1") =>
        ClassifySession(Sid, account, _ => state, _ => owner);

    [Fact]
    public void ExistingSession_WithTheSeatsAccount_IsAlive()
    {
        Assert.Equal(SessionVerdict.Active, Classify(WtsApi.WtsConnectState.Active, "Seat1").Verdict);
        Assert.Equal(SessionVerdict.Disconnected,
            Classify(WtsApi.WtsConnectState.Disconnected, "Seat1").Verdict);
    }

    [Fact]
    public void ExistingSession_WithAnotherAccount_IsNotTheSeats()
    {
        var check = Classify(WtsApi.WtsConnectState.Active, "Seat2");

        Assert.Equal(SessionVerdict.NotOurs, check.Verdict);
        Assert.Equal("Seat2", check.Owner);   // named in the log
        Assert.Equal(SessionVerdict.NotOurs,
            Classify(WtsApi.WtsConnectState.Disconnected, "Seat2").Verdict);
    }

    [Fact]
    public void ExistingSession_WithNobodyLoggedOn_IsNotTheSeats()
    {
        Assert.Equal(SessionVerdict.NotOurs, Classify(WtsApi.WtsConnectState.Active, "").Verdict);
    }

    // Theory data is passed as int: the WTS and verdict enums are internal, and a public test
    // method cannot take an internal type as a parameter.
    [Theory]
    [InlineData(-1)]   // the query failed: no such session
    [InlineData((int)WtsApi.WtsConnectState.Down)]
    [InlineData((int)WtsApi.WtsConnectState.Listen)]
    [InlineData((int)WtsApi.WtsConnectState.Init)]
    public void NoLiveSession_IsGone_WhoeverOwnsIt(int state)
    {
        // Unchanged from before the owner check: only Active and Disconnected are alive.
        WtsApi.WtsConnectState? s = state < 0 ? null : (WtsApi.WtsConnectState)state;
        Assert.Equal(SessionVerdict.Gone, Classify(s, "Seat1").Verdict);
    }

    [Fact]
    public void TheAccountName_IsComparedWithoutCase()
    {
        // Windows account names are case-insensitive; FindExistingSession compares the same way.
        Assert.Equal(SessionVerdict.Active, Classify(WtsApi.WtsConnectState.Active, "SEAT1").Verdict);
    }

    [Fact]
    public void AFailedUserQuery_FallsBackToTheState()
    {
        // No information is not evidence of another owner. A failing call must not put a
        // healthy seat into Error.
        Assert.Equal(SessionVerdict.Active, Classify(WtsApi.WtsConnectState.Active, null).Verdict);
    }

    [Fact]
    public void ANegativeSessionId_IsGone_WithoutAskingWindows()
    {
        var asked = false;
        var check = ClassifySession(-1, "Seat1",
            _ => { asked = true; return WtsApi.WtsConnectState.Active; },
            _ => { asked = true; return "Seat1"; });

        Assert.Equal(SessionVerdict.Gone, check.Verdict);
        Assert.False(asked);
    }

    [Theory]
    [InlineData((int)SessionVerdict.Active, true)]
    [InlineData((int)SessionVerdict.Disconnected, true)]
    [InlineData((int)SessionVerdict.Gone, true)]
    [InlineData((int)SessionVerdict.NotOurs, false)]
    [InlineData(-1, true)]   // could not ask: released, as before the check existed
    public void Teardown_ReleasesTheSession_OnlyWhenItIsNotSomeoneElses(int verdict, bool released)
    {
        SessionVerdict? v = verdict < 0 ? null : (SessionVerdict)verdict;
        Assert.Equal(released, SeatManager.MayReleaseSession(v));
    }
}
