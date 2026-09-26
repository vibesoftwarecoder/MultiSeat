using MultiSeat.Service.Monitoring;
using MultiSeat.Service.Sessions;
using Xunit;
using static MultiSeat.Service.Monitoring.SessionHealthCheck;
using FakeAnchor = MultiSeat.Tests.Sessions.SessionAnchorTrackerTests.FakeAnchor;

namespace MultiSeat.Tests.Monitoring;

/// <summary>
/// The order in which the health check relaunches an exited session anchor, driven through the
/// same <see cref="SessionHealthCheck.RelaunchExitedAnchorAsync"/> that production calls, with a
/// real <see cref="SessionAnchorTracker"/> and a real <see cref="SeatLifecycleGate"/>.
///
/// The case these exist for: the check used to take the anchor (removing it from the tracker)
/// before waiting for the gate. A reconnect or resolution change holds that gate for 15-30 s, so
/// the wait could time out, and then the anchor was gone from the tracker and was never
/// relaunched. Taking under the gate means a timeout leaves it for the next check.
///
/// Not covered: the lambdas in EnsureSessionAnchorAsync that bind these steps to
/// SessionLauncher, and the launch itself, which needs a live seat.
/// </summary>
public class AnchorRelaunchOrderTests
{
    private const int Sid = 18;
    private static readonly DateTime T0 = new(2026, 9, 25, 14, 20, 0, DateTimeKind.Utc);
    private static readonly TimeSpan NaturalExpiry = TimeSpan.FromSeconds(99_999);
    private static readonly TimeSpan ShortGateWait = TimeSpan.FromMilliseconds(50);

    /// <summary>A seat with one tracked anchor, and counters for what the check did to it.</summary>
    private sealed class Rig
    {
        public readonly SessionAnchorTracker Tracker = new();
        public readonly SeatLifecycleGate Gate = new();
        public readonly Guid SeatId = Guid.NewGuid();
        public DateTime Now = T0 + NaturalExpiry;
        public bool SeatStillHoldsSession = true;
        public Func<Task>? LaunchBehaviour;
        public int Reports;
        public int Launches;

        public Task<AnchorRelaunchOutcome> CheckAsync() => RelaunchExitedAnchorAsync(
            hasExitedAnchor: () => Tracker.HasExitedAnchor(Sid),
            acquireGate: c => Gate.AcquireAsync(SeatId, ShortGateWait, c),
            takeExitedAnchor: () =>
            {
                // The launcher logs the exit exactly where it takes it, so a take is a report.
                var exited = Tracker.TakeIfExited(Sid, Now);
                if (exited is not null) Reports++;
                return exited;
            },
            seatStillHoldsSession: () => SeatStillHoldsSession,
            relaunch: _ =>
            {
                Launches++;
                if (LaunchBehaviour is not null) return LaunchBehaviour();
                Tracker.Track(Sid, new FakeAnchor(Now));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        /// <summary>What EnsureSessionAnchorAsync does with a failure: log it and move on.</summary>
        public async Task<AnchorRelaunchOutcome?> CheckSwallowingAsync()
        {
            try { return await CheckAsync(); }
            catch (Exception) { return null; }
        }
    }

    [Fact]
    public async Task RunningAnchor_NeverTouchesTheGate()
    {
        var rig = new Rig();
        rig.Tracker.Track(Sid, new FakeAnchor(T0));

        // Held elsewhere: a check that tried to take it would time out.
        using var held = await rig.Gate.AcquireAsync(rig.SeatId, CancellationToken.None);

        Assert.Equal(AnchorRelaunchOutcome.NoExitedAnchor, await rig.CheckAsync());
        Assert.Equal(0, rig.Reports);
    }

    [Fact]
    public async Task GateTimeout_LeavesTheAnchorTracked_AndTheNextCheckRelaunchesItOnce()
    {
        var rig = new Rig();
        rig.Tracker.Track(Sid, new FakeAnchor(T0) { HasExited = true, ExitCode = 0 });

        using (await rig.Gate.AcquireAsync(rig.SeatId, CancellationToken.None))
        {
            // A reconnect holds the gate: this check's wait times out.
            await Assert.ThrowsAsync<TimeoutException>(rig.CheckAsync);
        }

        Assert.True(rig.Tracker.IsTracked(Sid));
        Assert.Equal(0, rig.Reports);
        Assert.Equal(0, rig.Launches);

        // Five seconds later the gate is free.
        rig.Now += TimeSpan.FromSeconds(5);
        Assert.Equal(AnchorRelaunchOutcome.Relaunched, await rig.CheckAsync());
        Assert.Equal(1, rig.Reports);
        Assert.Equal(1, rig.Launches);

        // The relaunched anchor is running: nothing more to do, and the gate was released.
        rig.Now += TimeSpan.FromSeconds(5);
        Assert.Equal(AnchorRelaunchOutcome.NoExitedAnchor, await rig.CheckAsync());
        using (await rig.Gate.AcquireAsync(rig.SeatId, ShortGateWait, CancellationToken.None)) { }
        Assert.Equal(1, rig.Launches);
    }

    // The worst case for a storm: the gate is busy for the first half-minute, and then every
    // relaunch fails. One day of 5-second checks must still report the exit once and try the
    // launch once.
    [Fact]
    public async Task ReportOnce_HoldsAcrossADay_WithGateTimeoutsAndAFailingRelaunch()
    {
        var rig = new Rig { LaunchBehaviour = () => throw new InvalidOperationException("launch failed") };
        rig.Tracker.Track(Sid, new FakeAnchor(T0) { HasExited = true, ExitCode = 0 });

        var busy = await rig.Gate.AcquireAsync(rig.SeatId, CancellationToken.None);
        for (var i = 0; i < 17_280; i++)
        {
            if (i == 6) busy.Dispose();
            await rig.CheckSwallowingAsync();
            rig.Now += TimeSpan.FromSeconds(5);
        }

        Assert.Equal(1, rig.Reports);
        Assert.Equal(1, rig.Launches);
        Assert.False(rig.Tracker.IsTracked(Sid));
    }

    [Fact]
    public async Task AnchorThatDiedYoung_IsReportedOnceUnderTheGate_AndNeverLaunched()
    {
        var rig = new Rig { Now = T0 + TimeSpan.FromSeconds(30) };
        rig.Tracker.Track(Sid, new FakeAnchor(T0) { HasExited = true, ExitCode = 1 });

        Assert.Equal(AnchorRelaunchOutcome.NotRelaunched, await rig.CheckAsync());
        for (var i = 0; i < 100; i++)
            Assert.Equal(AnchorRelaunchOutcome.NoExitedAnchor, await rig.CheckAsync());

        Assert.Equal(1, rig.Reports);
        Assert.Equal(0, rig.Launches);
    }

    [Fact]
    public async Task SeatChangedWhileWaiting_IsReportedOnce_AndNotLaunched()
    {
        var rig = new Rig { SeatStillHoldsSession = false };
        rig.Tracker.Track(Sid, new FakeAnchor(T0) { HasExited = true });

        Assert.Equal(AnchorRelaunchOutcome.SeatChanged, await rig.CheckAsync());
        Assert.Equal(AnchorRelaunchOutcome.NoExitedAnchor, await rig.CheckAsync());

        Assert.Equal(1, rig.Reports);
        Assert.Equal(0, rig.Launches);
    }

    // Teardown holds the gate while it removes and kills the anchor. A check that looked just
    // before must find it gone once it gets the gate, and launch nothing.
    [Fact]
    public async Task TeardownWhileWaiting_LeavesNothingToTakeOrLaunch()
    {
        var rig = new Rig();
        rig.Tracker.Track(Sid, new FakeAnchor(T0) { HasExited = true });

        Task<AnchorRelaunchOutcome> check;
        using (await rig.Gate.AcquireAsync(rig.SeatId, CancellationToken.None))
        {
            check = RelaunchExitedAnchorAsync(
                () => rig.Tracker.HasExitedAnchor(Sid),
                c => rig.Gate.AcquireAsync(rig.SeatId, TimeSpan.FromSeconds(10), c),
                () => rig.Tracker.TakeIfExited(Sid, rig.Now),
                () => true,
                _ => { rig.Launches++; return Task.CompletedTask; },
                CancellationToken.None);

            Assert.False(check.IsCompleted);
            rig.Tracker.RemoveAndKill(Sid);
        }

        Assert.Equal(AnchorRelaunchOutcome.AlreadyGone, await check);
        Assert.Equal(0, rig.Launches);
    }
}
