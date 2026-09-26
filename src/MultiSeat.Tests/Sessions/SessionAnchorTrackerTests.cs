using System.Text.RegularExpressions;
using MultiSeat.Service.Sessions;
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// The session anchor is <c>timeout.exe /t 99999 /nobreak</c>, which exits on its own after
/// 27.8 hours. Before the tracker, the health check found the exited anchor on every 5-second
/// check, logged a Warning each time and never replaced it: 1,735 warnings in one day for one
/// session, in a 20 MB circular Application log.
///
/// These pin the decisions that fix it: an exited anchor is reported once per incarnation, one
/// that lived to its natural expiry is relaunched, one that died young is not, and teardown ends
/// the anchor and stops tracking it. The launch itself (CreateProcessAsUser into a seat session)
/// and the health check's call into it need a live seat and are not covered here.
/// </summary>
public class SessionAnchorTrackerTests
{
    private static readonly DateTime T0 = new(2026, 9, 25, 14, 20, 0, DateTimeKind.Utc);

    // The measured case: session 18 was created 09:20 local and its anchor expired 99999 s later.
    private static readonly TimeSpan NaturalExpiry = TimeSpan.FromSeconds(99_999);

    internal sealed class FakeAnchor(DateTime launchedUtc) : ISessionAnchor
    {
        private bool _hasExited;

        // Like Process.HasExited, which throws once the Process has been disposed.
        public bool HasExited
        {
            get
            {
                if (DisposeCount > 0 && ThrowAfterDispose is not null)
                    throw ThrowAfterDispose;
                return _hasExited;
            }
            set => _hasExited = value;
        }

        public Exception? ThrowAfterDispose { get; init; }
        public DateTime LaunchedUtc { get; } = launchedUtc;
        public int? ExitCode { get; set; }
        public int KillCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void Kill()
        {
            KillCount++;
            HasExited = true;
        }

        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void RunningAnchor_IsNotReported_AndStaysTracked()
    {
        var tracker = new SessionAnchorTracker();
        var anchor = new FakeAnchor(T0);
        tracker.Track(18, anchor);

        Assert.Null(tracker.TakeIfExited(18, T0 + TimeSpan.FromHours(5)));
        Assert.True(tracker.IsTracked(18));
        Assert.Equal(0, anchor.DisposeCount);
    }

    [Fact]
    public void ExitedAnchor_IsReportedExactlyOnce_AcrossADayOfHealthChecks()
    {
        var tracker = new SessionAnchorTracker();
        var anchor = new FakeAnchor(T0);
        tracker.Track(18, anchor);
        anchor.HasExited = true;
        anchor.ExitCode = 0;

        // One day of 5-second health checks after expiry - the window that produced 1,735
        // warnings. Nothing relaunches here, which is the worst case for a storm.
        var expiredAt = T0 + NaturalExpiry;
        var reports = 0;
        for (var i = 0; i < 17_280; i++)
        {
            if (tracker.TakeIfExited(18, expiredAt + TimeSpan.FromSeconds(5 * i)) is not null)
                reports++;
        }

        Assert.Equal(1, reports);
        Assert.False(tracker.IsTracked(18));
        Assert.Equal(1, anchor.DisposeCount);
    }

    [Fact]
    public void AnchorThatReachedTimeoutsCap_IsRelaunched()
    {
        var tracker = new SessionAnchorTracker();
        var anchor = new FakeAnchor(T0) { HasExited = true, ExitCode = 0 };
        tracker.Track(18, anchor);

        var exited = tracker.TakeIfExited(18, T0 + NaturalExpiry);

        Assert.NotNull(exited);
        Assert.True(exited.Value.ShouldRelaunch);
        Assert.Equal(NaturalExpiry, exited.Value.Lifetime);
        Assert.Equal(0, exited.Value.ExitCode);
    }

    // Ties the relaunch threshold to the command actually launched: whatever /t the anchor
    // runs with, reaching it must count as a natural expiry.
    [Fact]
    public void TheAnchorCommandsOwnExpiry_QualifiesForRelaunch()
    {
        var cmd = SessionLauncher.BuildSessionAnchorCommand();
        var match = Regex.Match(cmd, @"/t\s+(\d+)");
        Assert.True(match.Success, $"the anchor command must set a timeout: {cmd}");

        var expiry = TimeSpan.FromSeconds(int.Parse(match.Groups[1].Value));

        Assert.True(SessionAnchorTracker.ShouldRelaunch(expiry),
            $"an anchor that ran its full {expiry} would not be relaunched");
    }

    // Something that keeps killing the anchor must not turn a warning storm into a process storm.
    [Fact]
    public void AnchorThatDiedYoung_IsReportedOnce_AndNotRelaunched()
    {
        var tracker = new SessionAnchorTracker();
        var anchor = new FakeAnchor(T0) { HasExited = true, ExitCode = 1 };
        tracker.Track(18, anchor);

        var exited = tracker.TakeIfExited(18, T0 + TimeSpan.FromSeconds(30));

        Assert.NotNull(exited);
        Assert.False(exited.Value.ShouldRelaunch);
        Assert.Null(tracker.TakeIfExited(18, T0 + TimeSpan.FromSeconds(35)));
    }

    [Fact]
    public void RelaunchedAnchor_IsANewIncarnation_ReportedOnceWhenItExpiresToo()
    {
        var tracker = new SessionAnchorTracker();
        var first = new FakeAnchor(T0) { HasExited = true };
        tracker.Track(18, first);
        Assert.NotNull(tracker.TakeIfExited(18, T0 + NaturalExpiry));

        var relaunchedAt = T0 + NaturalExpiry;
        var second = new FakeAnchor(relaunchedAt);
        tracker.Track(18, second);

        // Running: nothing to report, and the first incarnation's exit is not reported again.
        Assert.Null(tracker.TakeIfExited(18, relaunchedAt + TimeSpan.FromHours(1)));

        second.HasExited = true;
        var secondExit = tracker.TakeIfExited(18, relaunchedAt + NaturalExpiry);
        Assert.NotNull(secondExit);
        Assert.True(secondExit.Value.ShouldRelaunch);
        Assert.Null(tracker.TakeIfExited(18, relaunchedAt + NaturalExpiry + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Teardown_KillsARunningAnchor_AndNothingIsReportedAfterwards()
    {
        var tracker = new SessionAnchorTracker();
        var anchor = new FakeAnchor(T0);
        tracker.Track(18, anchor);

        tracker.RemoveAndKill(18);

        Assert.Equal(1, anchor.KillCount);
        Assert.Equal(1, anchor.DisposeCount);
        Assert.False(tracker.IsTracked(18));
        // The kill made it exit; that must not be reported as an exit to relaunch.
        Assert.Null(tracker.TakeIfExited(18, T0 + NaturalExpiry));
    }

    [Fact]
    public void Teardown_DoesNotKillAnAnchorThatAlreadyExited()
    {
        var tracker = new SessionAnchorTracker();
        var anchor = new FakeAnchor(T0) { HasExited = true };
        tracker.Track(18, anchor);

        tracker.RemoveAndKill(18);

        Assert.Equal(0, anchor.KillCount);
        Assert.Equal(1, anchor.DisposeCount);
    }

    [Fact]
    public void OnlyTheSessionAskedAbout_IsAffected()
    {
        var tracker = new SessionAnchorTracker();
        var exited = new FakeAnchor(T0) { HasExited = true };
        var running = new FakeAnchor(T0);
        tracker.Track(18, exited);
        tracker.Track(19, running);

        Assert.Null(tracker.TakeIfExited(19, T0 + NaturalExpiry));
        Assert.NotNull(tracker.TakeIfExited(18, T0 + NaturalExpiry));
        Assert.True(tracker.IsTracked(19));
        Assert.Equal(0, running.DisposeCount);
    }

    // The health check looks before it waits for the lifecycle gate. That look must not consume
    // the anchor, or a gate timeout would lose it for good.
    [Fact]
    public void Peek_LeavesTheExitedAnchorTracked_AndReportsNothing()
    {
        var tracker = new SessionAnchorTracker();
        var anchor = new FakeAnchor(T0) { HasExited = true, ExitCode = 0 };
        tracker.Track(18, anchor);

        for (var i = 0; i < 100; i++)
            Assert.True(tracker.HasExitedAnchor(18));

        Assert.True(tracker.IsTracked(18));
        Assert.Equal(0, anchor.DisposeCount);
        // The one report is still there for the take to make.
        Assert.NotNull(tracker.TakeIfExited(18, T0 + NaturalExpiry));
        Assert.Null(tracker.TakeIfExited(18, T0 + NaturalExpiry));
        Assert.False(tracker.HasExitedAnchor(18));
    }

    [Fact]
    public void Peek_IsFalse_ForARunningAnchorOrNoneTracked()
    {
        var tracker = new SessionAnchorTracker();
        Assert.False(tracker.HasExitedAnchor(18));

        tracker.Track(18, new FakeAnchor(T0));
        Assert.False(tracker.HasExitedAnchor(18));
    }

    // Teardown can dispose the anchor while the health check still holds the instance it looked
    // up; Process.HasExited then throws. The peek runs outside any lock, so it must not.
    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ObjectDisposedException))]
    public void Peek_And_Take_AreSafe_WhenTheProcessWasDisposedUnderThem(Type thrown)
    {
        var tracker = new SessionAnchorTracker();
        var anchor = new FakeAnchor(T0)
        {
            HasExited = true,
            ThrowAfterDispose = (Exception)Activator.CreateInstance(thrown, "disposed")!,
        };
        tracker.Track(18, anchor);
        anchor.Dispose();

        Assert.False(tracker.HasExitedAnchor(18));
        Assert.Null(tracker.TakeIfExited(18, T0 + NaturalExpiry));
    }

    // Windows reuses session ids. A new anchor for a reused id must not leak the old handle.
    [Fact]
    public void TrackingOverAnExistingEntry_ReleasesTheDisplacedAnchor()
    {
        var tracker = new SessionAnchorTracker();
        var stale = new FakeAnchor(T0);
        var fresh = new FakeAnchor(T0 + TimeSpan.FromDays(3));
        tracker.Track(18, stale);

        tracker.Track(18, fresh);

        Assert.Equal(1, stale.DisposeCount);
        Assert.Equal(0, fresh.DisposeCount);
        fresh.HasExited = true;
        var exited = tracker.TakeIfExited(18, fresh.LaunchedUtc + NaturalExpiry);
        Assert.Equal(NaturalExpiry, exited?.Lifetime);
    }
}
