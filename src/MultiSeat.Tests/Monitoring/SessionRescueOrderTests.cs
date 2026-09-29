using MultiSeat.Service.Monitoring;
using MultiSeat.Service.Sessions;
using Xunit;
using static MultiSeat.Service.Monitoring.SessionHealthCheck;

namespace MultiSeat.Tests.Monitoring;

/// <summary>
/// The order in which the health check rescues a Disconnected session (Check 1b), driven through
/// the same <see cref="SessionHealthCheck.RescueDisconnectedSessionAsync"/> that production calls,
/// with a real <see cref="SeatLifecycleGate"/> and the session state faked through delegates.
///
/// The case these exist for (issue #70, seen live in 1 of 3 runs): a resize or rescale
/// disconnects the seat's session on purpose while holding the gate. A health check landing in
/// that gap saw the session Disconnected and queued for the gate; by the time it got it, the
/// resize had already relaunched the session and started Apollo, but the check did not look
/// again, so it killed that fresh Apollo and restarted it anyway.
///
/// Not covered: the lambdas in CheckSeatAsync that bind these steps to SessionLauncher, and the
/// rescue itself, which needs a live seat.
/// </summary>
public class SessionRescueOrderTests
{
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShortGateWait = TimeSpan.FromMilliseconds(50);

    /// <summary>A seat whose session the check saw Disconnected, and what the check did about it.</summary>
    private sealed class Rig
    {
        public readonly SeatLifecycleGate Gate = new();
        public readonly Guid SeatId = Guid.NewGuid();
        public int SessionId = 22;
        public readonly HashSet<int> ActiveSessions = new();
        public bool SeatStillOurs = true;
        public int Rescues;
        public bool? GateHeldDuringRescue;

        public Task<SessionRescueOutcome> CheckAsync() => RescueDisconnectedSessionAsync(
            acquireGate: c => Gate.AcquireAsync(SeatId, GateWait, c),
            seatStillOurs: () => SeatStillOurs,
            // Reads the seat's CURRENT session id, as production does.
            sessionActiveNow: () => ActiveSessions.Contains(SessionId),
            rescue: async c =>
            {
                Rescues++;
                GateHeldDuringRescue = await GateIsHeldAsync();
                ActiveSessions.Add(SessionId);
            },
            CancellationToken.None);

        private async Task<bool> GateIsHeldAsync()
        {
            try
            {
                using (await Gate.AcquireAsync(SeatId, ShortGateWait, CancellationToken.None)) { }
                return false;
            }
            catch (TimeoutException)
            {
                return true;
            }
        }
    }

    // The race itself: the resize holds the gate, the check queues behind it, and the resize
    // brings the same session back to ACTIVE before letting go.
    [Fact]
    public async Task SessionRecoveredWhileWaitingForTheGate_IsNotRescuedAgain()
    {
        var rig = new Rig();

        Task<SessionRescueOutcome> check;
        using (await rig.Gate.AcquireAsync(rig.SeatId, CancellationToken.None))
        {
            check = rig.CheckAsync();
            Assert.False(check.IsCompleted);   // queued behind the resize

            rig.ActiveSessions.Add(rig.SessionId);   // the resize's relaunch
        }

        Assert.Equal(SessionRescueOutcome.AlreadyRecovered, await check);
        Assert.Equal(0, rig.Rescues);
    }

    // A resize whose relaunch comes back on a different session id still recovered the seat.
    [Fact]
    public async Task SessionReplacedWhileWaitingForTheGate_IsNotRescuedEither()
    {
        var rig = new Rig();

        Task<SessionRescueOutcome> check;
        using (await rig.Gate.AcquireAsync(rig.SeatId, CancellationToken.None))
        {
            check = rig.CheckAsync();
            rig.SessionId = 31;
            rig.ActiveSessions.Add(31);
        }

        Assert.Equal(SessionRescueOutcome.AlreadyRecovered, await check);
        Assert.Equal(0, rig.Rescues);
    }

    // The legitimate case must still work: the PC slept, the session is still down once the
    // gate is free, so the rescue runs, once, and under the gate.
    [Fact]
    public async Task SessionStillDownAfterTheGate_IsRescuedOnce_UnderTheGate()
    {
        var rig = new Rig();

        Task<SessionRescueOutcome> check;
        using (await rig.Gate.AcquireAsync(rig.SeatId, CancellationToken.None))
        {
            check = rig.CheckAsync();
            Assert.False(check.IsCompleted);
        }

        Assert.Equal(SessionRescueOutcome.Rescued, await check);
        Assert.Equal(1, rig.Rescues);
        Assert.True(rig.GateHeldDuringRescue);

        // And the gate was released afterwards.
        using (await rig.Gate.AcquireAsync(rig.SeatId, ShortGateWait, CancellationToken.None)) { }
    }

    [Fact]
    public async Task SessionDownWithTheGateFree_IsRescued()
    {
        var rig = new Rig();

        Assert.Equal(SessionRescueOutcome.Rescued, await rig.CheckAsync());
        Assert.Equal(1, rig.Rescues);
    }

    // A teardown took the seat over while the check waited: leave it alone, even though its
    // session is certainly not Active any more.
    [Fact]
    public async Task SeatChangedWhileWaitingForTheGate_IsNotRescued()
    {
        var rig = new Rig();

        Task<SessionRescueOutcome> check;
        using (await rig.Gate.AcquireAsync(rig.SeatId, CancellationToken.None))
        {
            check = rig.CheckAsync();
            rig.SeatStillOurs = false;
        }

        Assert.Equal(SessionRescueOutcome.SeatChanged, await check);
        Assert.Equal(0, rig.Rescues);
    }

    [Fact]
    public async Task GateTimeout_RescuesNothing()
    {
        var rig = new Rig();

        using (await rig.Gate.AcquireAsync(rig.SeatId, CancellationToken.None))
        {
            await Assert.ThrowsAsync<TimeoutException>(() => RescueDisconnectedSessionAsync(
                c => rig.Gate.AcquireAsync(rig.SeatId, ShortGateWait, c),
                () => true,
                () => false,
                _ => { rig.Rescues++; return Task.CompletedTask; },
                CancellationToken.None));
        }

        Assert.Equal(0, rig.Rescues);
    }
}
