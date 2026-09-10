using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Streaming;

/// <summary>
/// PID reuse: a PID captured when a seat was provisioned can name a completely different process
/// once the original exits, because Windows recycles PIDs. Before <see cref="ProcessIdentity"/>,
/// both of ApolloManager's kill paths did <c>Process.GetProcessById(pid).Kill(entireProcessTree)</c>
/// with no check that the PID still meant what it did at launch — so a recycled PID meant killing
/// a stranger's process tree, and nothing would have said so.
///
/// ⭐ <see cref="TryKillIdentifiedProcess_WithMismatchedStartTime_RefusesAndLeavesProcessRunning"/>
/// is the test that matters. The others pin the primitive; that one proves the wrong process
/// survives, which is the entire point of the change.
///
/// Ported as the minimal primitive from @Dani6ca-T's MultiSeat-Extended (issue #29, PR B) without
/// the process-tracking subsystem built around it there.
/// </summary>
public class ProcessIdentityTests
{
    private sealed class NoopLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex,
            Func<TState, Exception?, string> f) { }
    }

    private static ApolloManager NewManager() => new(
        new NoopLogger<ApolloManager>(),
        Options.Create(new MultiSeatOptions()),
        configBuilder: null!,      // untouched by the kill paths under test
        processInjector: null!);

    /// <summary>
    /// A manager whose configured Apollo executable IS the victim process.
    ///
    /// ⭐ This is what makes the stale-identity test mean anything. Stop falls back to a
    /// process-NAME check when it has no identity, and with the default ApolloExePath
    /// ("sunshine.exe") that check refuses to kill a "ping" process all by itself — so the test
    /// would pass whether or not the identity branch existed. Pointing the config at ping.exe
    /// makes the name check say YES, leaving the identity comparison as the only thing that can
    /// still stop the kill.
    /// </summary>
    private static ApolloManager NewManagerTargetingVictim() => new(
        new NoopLogger<ApolloManager>(),
        Options.Create(new MultiSeatOptions { ApolloExePath = @"C:\Windows\System32\ping.exe" }),
        configBuilder: null!,
        processInjector: null!);

    /// <summary>A real, long-lived child process to act on. Killed by the caller or by dispose.</summary>
    private static Process StartVictim() =>
        Process.Start(new ProcessStartInfo("ping.exe", "127.0.0.1 -n 120")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;

    // ── the primitive ───────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_RejectsNonPositivePid()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessIdentity(0, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessIdentity(-1, now));
    }

    [Fact]
    public void Matches_RequiresBothPidAndStartTime()
    {
        var started = DateTimeOffset.UtcNow;
        var identity = new ProcessIdentity(1234, started);

        Assert.True(identity.Matches(1234, started));
        Assert.False(identity.Matches(1234, started.AddTicks(1)));   // the reuse case
        Assert.False(identity.Matches(5678, started));
    }

    [Fact]
    public void SamePidDifferentStartTime_AreNotEqual()
    {
        var started = DateTimeOffset.UtcNow;
        Assert.NotEqual(
            new ProcessIdentity(1234, started),
            new ProcessIdentity(1234, started.AddSeconds(1)));
    }

    // ── reading the OS start time ───────────────────────────────────────────────

    [Fact]
    public void GetProcessStartTime_ReturnsUtcForALiveProcess()
    {
        using var self = Process.GetCurrentProcess();
        var startedAt = ApolloManager.GetProcessStartTime(self.Id);

        Assert.NotNull(startedAt);
        Assert.Equal(TimeSpan.Zero, startedAt!.Value.Offset);   // must be UTC, not local
    }

    [Fact]
    public void GetProcessStartTime_ReturnsNullForAFreePid()
    {
        // Never fabricate a timestamp when this fails — an identity carrying a made-up start
        // time can compare equal to a recycled PID by coincidence.
        Assert.Null(ApolloManager.GetProcessStartTime(0));
        Assert.Null(ApolloManager.GetProcessStartTime(-1));
    }

    // ── the behaviour the change exists for ─────────────────────────────────────

    [Fact]
    public void TryKillIdentifiedProcess_WithMismatchedStartTime_RefusesAndLeavesProcessRunning()
    {
        using var victim = StartVictim();
        try
        {
            var realStart = ApolloManager.GetProcessStartTime(victim.Id);
            Assert.NotNull(realStart);

            // Same PID, wrong start time — exactly what a recycled PID looks like.
            var stale = new ProcessIdentity(victim.Id, realStart!.Value.AddSeconds(-30));

            var outcome = NewManager().TryKillIdentifiedProcess(stale, "test", waitMs: 2000);

            Assert.Equal(ApolloKillOutcome.IdentityMismatch, outcome);

            // The point: the unrelated process is untouched. Before this change it was killed.
            victim.Refresh();
            Assert.False(victim.HasExited);
        }
        finally
        {
            if (!victim.HasExited) victim.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void TryKillIdentifiedProcess_WithMatchingIdentity_Kills()
    {
        using var victim = StartVictim();
        try
        {
            var realStart = ApolloManager.GetProcessStartTime(victim.Id);
            Assert.NotNull(realStart);

            var outcome = NewManager().TryKillIdentifiedProcess(
                new ProcessIdentity(victim.Id, realStart!.Value), "test", waitMs: 5000);

            Assert.Equal(ApolloKillOutcome.Killed, outcome);
            victim.Refresh();
            Assert.True(victim.HasExited);
        }
        finally
        {
            if (!victim.HasExited) victim.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void TryKillIdentifiedProcess_WhenAlreadyExited_ReportsAlreadyGone()
    {
        var victim = StartVictim();
        var realStart = ApolloManager.GetProcessStartTime(victim.Id);
        Assert.NotNull(realStart);
        var identity = new ProcessIdentity(victim.Id, realStart!.Value);

        victim.Kill(entireProcessTree: true);
        victim.WaitForExit(5000);
        victim.Dispose();

        var outcome = NewManager().TryKillIdentifiedProcess(identity, "test", waitMs: 1000);

        // Either the PID is free, or it is held by something else and fails the identity check.
        // Both are non-destructive; what must never happen is Killed.
        Assert.NotEqual(ApolloKillOutcome.Killed, outcome);
    }

    // ── PR D: the identity survives on the seat, so a restart does not lose it ──

    [Fact]
    public void Stop_WithNoInstanceRecord_UsesTheIdentityCarriedOnTheSeat()
    {
        // The state after a service restart: _instances is empty, the seat and its Apollo are
        // both still alive. Before SeatInfo.ApolloIdentity this fell back to a name check.
        using var victim = StartVictim();
        try
        {
            var realStart = ApolloManager.GetProcessStartTime(victim.Id);
            Assert.NotNull(realStart);

            var seat = new SeatInfo
            {
                Id = Guid.NewGuid(),
                AccountName = "GuestTest",
                ApolloProcessId = victim.Id,
                ApolloIdentity = new ProcessIdentity(victim.Id, realStart!.Value)
            };

            NewManager().Stop(seat);

            victim.Refresh();
            Assert.True(victim.HasExited);
        }
        finally
        {
            if (!victim.HasExited) victim.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Stop_WithAStaleIdentityOnTheSeat_LeavesTheProcessRunning()
    {
        // ⭐ The one that proves the seat's identity is VERIFIED rather than merely present.
        // A recycled PID reaches here looking exactly like this.
        using var victim = StartVictim();
        try
        {
            var realStart = ApolloManager.GetProcessStartTime(victim.Id);
            Assert.NotNull(realStart);

            var seat = new SeatInfo
            {
                Id = Guid.NewGuid(),
                AccountName = "GuestTest",
                ApolloProcessId = victim.Id,
                ApolloIdentity = new ProcessIdentity(victim.Id, realStart!.Value.AddSeconds(-30))
            };

            // Configured so the name-check fallback WOULD kill this process. The only thing
            // left that can spare it is the identity comparison.
            NewManagerTargetingVictim().Stop(seat);

            victim.Refresh();
            Assert.False(victim.HasExited);
        }
        finally
        {
            if (!victim.HasExited) victim.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void TryKillIdentifiedProcess_NeverThrows()
    {
        var identity = new ProcessIdentity(int.MaxValue, DateTimeOffset.UtcNow);
        var outcome = NewManager().TryKillIdentifiedProcess(identity, "test", waitMs: 1000);
        Assert.Equal(ApolloKillOutcome.AlreadyGone, outcome);
    }
}
