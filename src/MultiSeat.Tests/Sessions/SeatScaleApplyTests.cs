using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Diagnostics;
using MultiSeat.Service.Emulators;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// Issue #93: after a client-triggered reconnect the seat's dashboard scale read 100%. The scale
/// MultiSeat reports is the one it wrote into Default.rdp, and nothing had ever read back what
/// Windows applied. A reconnect keeps the Windows session and is known to apply a new size; that
/// it applies a new scale was never measured. So the seat reads the scale from the session after
/// a reconnect and recreates the session when the two disagree.
///
/// These fake the session through the same delegates the seat manager passes in, and record the
/// ORDER of operations: the recreate must follow a mismatch that lasted, and the create must
/// follow the logoff having finished, or it would reconnect to the very session it was meant to
/// replace.
/// </summary>
public class SeatScaleApplyTests
{
    private static SessionScaleObservation Reading(int rdpPercent) => new(
        SystemDpi: 96, SystemPercent: 100,
        Monitors:
        [
            new MonitorScale(@"\\.\DISPLAY1", "Microsoft Remote Display Adapter", true,
                (uint)(rdpPercent * 96 / 100), rdpPercent, rdpPercent, 1920, 1080),
        ],
        Error: null);

    private sealed class Script
    {
        public List<string> Trace { get; } = new();
        public RecordingLogger Logger { get; } = new();

        /// <summary>Scale the session reports after each observation, in order; the last repeats.</summary>
        public Queue<int?> ReadingsBeforeRecreate { get; init; } = new();
        public int ReadingAfterRecreate { get; init; } = 200;
        public bool Recreated { get; private set; }

        public Task<int> Reconnect(CancellationToken _)
        {
            Trace.Add("reconnect");
            return Task.FromResult(22);
        }

        public Task<SessionScaleObservation?> Observe(int sessionId, CancellationToken _)
        {
            Trace.Add($"observe:{sessionId}");
            int? percent;
            if (Recreated) percent = ReadingAfterRecreate;
            else if (ReadingsBeforeRecreate.Count > 1) percent = ReadingsBeforeRecreate.Dequeue();
            else percent = ReadingsBeforeRecreate.Peek();
            return Task.FromResult(percent is { } p ? Reading(p) : null);
        }

        public Task<int> Recreate(int sessionId, CancellationToken _)
        {
            Trace.Add($"recreate:{sessionId}");
            Recreated = true;
            return Task.FromResult(31);
        }

        public Task<SeatManager.ScaleApplyResult> Run(int wanted) =>
            SeatManager.ReconnectAndVerifyScaleAsync(
                wanted, Reconnect, Observe, Recreate, Logger, CancellationToken.None,
                settleAttempts: 3, settleDelayMs: 1);
    }

    private static Queue<int?> Readings(params int?[] values) => new(values);

    // ── The reconnect took: nothing is recreated ───────────────────────────────

    [Fact]
    public async Task ReconnectThatAppliedTheScaleLeavesTheSessionAlone()
    {
        var script = new Script { ReadingsBeforeRecreate = Readings(200) };

        var result = await script.Run(wanted: 200);

        Assert.False(result.Recreated);
        Assert.Equal(22, result.SessionId);
        Assert.Equal(ScaleVerdict.Match, result.Verdict);
        Assert.Equal(["reconnect", "observe:22"], script.Trace);
        Assert.DoesNotContain(script.Logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    // ── The reconnect did not take: recreate, once, after the reconnect ────────

    [Fact]
    public async Task ReconnectThatKeptTheOldScaleIsFollowedByARecreateAndAFreshReading()
    {
        var script = new Script { ReadingsBeforeRecreate = Readings(100), ReadingAfterRecreate = 200 };

        var result = await script.Run(wanted: 200);

        Assert.True(result.Recreated);
        Assert.Equal(31, result.SessionId);
        Assert.Equal(ScaleVerdict.Match, result.Verdict);
        Assert.Equal(200, result.Observation!.AppliedPercent);

        // Reconnect, read it for the whole settle window, only then recreate, then read the NEW
        // session (id 31), not the old one.
        Assert.Equal(
            ["reconnect", "observe:22", "observe:22", "observe:22", "recreate:22", "observe:31"],
            script.Trace);
    }

    [Fact]
    public async Task TheLogShowsWhatWasAskedForBesideWhatWindowsApplied()
    {
        var script = new Script { ReadingsBeforeRecreate = Readings(100), ReadingAfterRecreate = 200 };

        await script.Run(wanted: 200);

        Assert.Contains(script.Logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("asked for 200%")
            && e.Message.Contains("runs at 100%"));
        Assert.Contains(script.Logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("logging it off and creating it again"));
        Assert.Contains(script.Logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("running at 200% as asked"));
    }

    [Fact]
    public async Task ReadingThatSettlesWithinTheWindowDoesNotCostThePlayerTheirSession()
    {
        // The session turns Active before its displays have settled. A first reading of the old
        // scale followed by the right one is a slow reconnect, not a failed one.
        var script = new Script { ReadingsBeforeRecreate = Readings(100, 200) };

        var result = await script.Run(wanted: 200);

        Assert.False(result.Recreated);
        Assert.Equal(["reconnect", "observe:22", "observe:22"], script.Trace);
        Assert.Equal(ScaleVerdict.Match, result.Verdict);
    }

    [Fact]
    public async Task ASessionRecreatedAtTheWrongScaleIsReportedNotRecreatedAgain()
    {
        var script = new Script { ReadingsBeforeRecreate = Readings(100), ReadingAfterRecreate = 100 };

        var result = await script.Run(wanted: 200);

        Assert.True(result.Recreated);
        Assert.Equal(ScaleVerdict.Mismatch, result.Verdict);
        Assert.Single(script.Trace, t => t.StartsWith("recreate"));   // never a loop
        Assert.Contains(script.Logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("after recreate")
            && e.Message.Contains("asked for 200%") && e.Message.Contains("runs at 100%"));
    }

    // ── No reading: unverified, never a guess either way ───────────────────────

    [Fact]
    public async Task AnUnreadableSessionIsNotRecreatedOnAGuess()
    {
        var script = new Script { ReadingsBeforeRecreate = Readings((int?)null) };

        var result = await script.Run(wanted: 200);

        Assert.False(result.Recreated);
        Assert.Equal(ScaleVerdict.Unknown, result.Verdict);
        Assert.DoesNotContain(script.Trace, t => t.StartsWith("recreate"));
        Assert.Contains(script.Logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("unverified"));
    }

    // ── Logoff, then create: the create must wait for the logoff ───────────────

    [Fact]
    public async Task CreateWaitsUntilTheOldSessionIsGone()
    {
        var trace = new List<string>();
        var existsPolls = 0;
        var gone = false;

        var id = await SeatManager.LogoffAndCreateAsync(
            sessionId: 22,
            logoff: sid => trace.Add($"logoff:{sid}"),
            sessionExists: _ =>
            {
                // Windows takes a few polls to finish the logoff.
                if (++existsPolls >= 4) gone = true;
                return !gone;
            },
            create: _ =>
            {
                trace.Add(gone ? "create:after-gone" : "create:TOO-EARLY");
                return Task.FromResult(31);
            },
            new RecordingLogger(), CancellationToken.None, pollMs: 1, timeoutMs: 5_000);

        Assert.Equal(31, id);
        Assert.Equal(["logoff:22", "create:after-gone"], trace);
        Assert.True(existsPolls >= 4, "the session's existence was never waited on");
    }

    [Fact]
    public async Task CreatesAnywayWithAWarningWhenTheOldSessionWillNotGo()
    {
        var logger = new RecordingLogger();
        var creates = 0;

        var id = await SeatManager.LogoffAndCreateAsync(
            22, _ => { }, _ => true,
            _ => { creates++; return Task.FromResult(31); },
            logger, CancellationToken.None, pollMs: 1, timeoutMs: 30);

        Assert.Equal(1, creates);   // a timeout must not leave the seat without a session
        Assert.Equal(31, id);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // ── What the seat reports ──────────────────────────────────────────────────

    [Fact]
    public void ASeatOnlyReportsAMismatchWhenItHasActuallyBeenRead()
    {
        var seat = new SeatInfo { AccountName = "GuestTest", ScaleFactor = 200 };

        Assert.False(seat.ScaleMismatch);          // never read: no alarm, no claim

        seat.AppliedScaleFactor = 200;
        Assert.False(seat.ScaleMismatch);

        seat.AppliedScaleFactor = 100;
        Assert.True(seat.ScaleMismatch);
    }

    [Fact]
    public void RelaunchingASessionDropsTheReadingThatDescribedTheOldOne()
    {
        var mgr = new SeatManager(
            new RecordingLogger<SeatManager>(),
            Options.Create(new MultiSeatOptions()),
            accounts: null!, sessionLauncher: null!, processInjector: null!, displayManager: null!,
            apolloManager: null!, configBuilder: null!, portAllocator: null!, firewall: null!,
            audioRouter: null!, controllerManager: null!, inputRouter: null!, inputHookManager: null!,
            hidHide: null!, onConnectApps: null!, serverQuery: null!, hostApollo: null!,
            emulatorSeeders: Array.Empty<IEmulatorConfigSeeder>(),
            lifecycleGate: new SeatLifecycleGate());
        var seat = new SeatInfo
        {
            AccountName = "GuestTest", Width = 1920, Height = 1080,
            ScaleFactorOverride = 200, AppliedScaleFactor = 100,
            AppliedScaleCheckedAt = DateTimeOffset.UtcNow,
        };

        // Every launch and relaunch asks for the geometry first. The old reading says nothing
        // about the session that is about to replace the one it was taken from.
        var geometry = mgr.GeometryFor(seat);

        Assert.Equal(200, geometry.ScaleFactor);
        Assert.Null(seat.AppliedScaleFactor);
        Assert.Null(seat.AppliedScaleCheckedAt);
        Assert.False(seat.ScaleMismatch);
    }

    private class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class RecordingLogger<T> : RecordingLogger, ILogger<T> { }
}
