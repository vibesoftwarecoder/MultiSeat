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
/// Issue #93: a seat's dashboard scale did not match what the session rendered at. After a
/// reconnect the seat reads the scale from inside the session and reports it beside the scale
/// asked for.
///
/// It reports and does nothing else. The 0.6.19 draft logged the session off and created it again
/// over a mismatch, on the theory that Windows applies a scale at creation but not on a reconnect.
/// A live seat disproved that: the new session ran at the same wrong scale, because mstsc never
/// sent the scale at all (no devicescalefactor in Default.rdp; see RdpFileBuilderTests). The
/// recreate only closed the player's programs. ReconnectAndVerifyScaleAsync no longer takes a
/// recreate step at all, so these check what it reads, logs and returns.
/// </summary>
public class SeatScaleApplyTests
{
    private static SessionScaleObservation Reading(int rdpPercent, int systemPercent = 100) => new(
        SystemDpi: (uint)(systemPercent * 96 / 100), SystemPercent: systemPercent,
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

        /// <summary>Scale the session reports at each observation, in order; the last repeats.</summary>
        public Queue<int?> Readings { get; init; } = new();

        public Task<int> Reconnect(CancellationToken _)
        {
            Trace.Add("reconnect");
            return Task.FromResult(22);
        }

        public Task<SessionScaleObservation?> Observe(int sessionId, CancellationToken _)
        {
            Trace.Add($"observe:{sessionId}");
            var percent = Readings.Count > 1 ? Readings.Dequeue() : Readings.Peek();
            return Task.FromResult(percent is { } p ? Reading(p) : null);
        }

        public Task<SeatManager.ScaleApplyResult> Run(int wanted) =>
            SeatManager.ReconnectAndVerifyScaleAsync(
                wanted, Reconnect, Observe, Logger, CancellationToken.None,
                settleAttempts: 3, settleDelayMs: 1);
    }

    private static Queue<int?> Readings(params int?[] values) => new(values);

    [Fact]
    public async Task AReconnectThatAppliedTheScaleIsReadOnceAndLoggedAsAMatch()
    {
        var script = new Script { Readings = Readings(200) };

        var result = await script.Run(wanted: 200);

        Assert.Equal(22, result.SessionId);
        Assert.Equal(ScaleVerdict.Match, result.Verdict);
        Assert.Equal(["reconnect", "observe:22"], script.Trace);
        Assert.DoesNotContain(script.Logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(script.Logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("running at 200% as asked"));
    }

    // The live case from 2026-10-08: asked for 200%, the session ran at 100%. The seat keeps its
    // session (same id), the reading is returned, and the log says what was asked, what was read,
    // and that the session was left alone. Nothing but reads follows the reconnect.
    [Fact]
    public async Task AMismatchIsReportedAndTheSessionIsKept()
    {
        var script = new Script { Readings = Readings(100) };

        var result = await script.Run(wanted: 200);

        Assert.Equal(22, result.SessionId);
        Assert.Equal(ScaleVerdict.Mismatch, result.Verdict);
        Assert.Equal(100, result.Observation!.AppliedPercent);
        Assert.Equal(["reconnect", "observe:22", "observe:22", "observe:22"], script.Trace);
        Assert.Contains(script.Logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("asked for 200%")
            && e.Message.Contains("runs at 100%")
            && e.Message.Contains("left the session running"));
    }

    [Fact]
    public async Task AReadingThatSettlesWithinTheWindowIsAMatch()
    {
        // The session turns Active before its displays have settled. A first reading of the old
        // scale followed by the right one is a slow reconnect, not a failed one.
        var script = new Script { Readings = Readings(100, 200) };

        var result = await script.Run(wanted: 200);

        Assert.Equal(["reconnect", "observe:22", "observe:22"], script.Trace);
        Assert.Equal(ScaleVerdict.Match, result.Verdict);
        Assert.DoesNotContain(script.Logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AnUnreadableSessionIsUnverifiedNotAMismatch()
    {
        var script = new Script { Readings = Readings((int?)null) };

        var result = await script.Run(wanted: 200);

        Assert.Equal(ScaleVerdict.Unknown, result.Verdict);
        Assert.Null(result.Observation);
        Assert.Contains(script.Logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("unverified"));
    }

    // ── The note the dashboard shows ───────────────────────────────────────────

    [Fact]
    public void AFullMatchNeedsNoNote()
    {
        Assert.Null(SessionScaleProbe.Explain(200, Reading(200, systemPercent: 200)));
    }

    [Fact]
    public void AMismatchNoteSaysWhatRanWhatWasAskedAndThatNothingWasClosed()
    {
        var note = SessionScaleProbe.Explain(200, Reading(100));

        Assert.NotNull(note);
        Assert.Contains("at 100%", note);
        Assert.Contains("200%", note);
        Assert.Contains("left the session running", note);
    }

    // Windows fixes a session's system DPI at sign-in. A reconnect at a new scale moves the
    // display's DPI, so the verdict is a Match, but programs that only read the system DPI keep
    // the old size. That is worth saying, and it is the user's call to sign out.
    [Fact]
    public void AMatchWithAnOldSystemScaleSaysSigningOutFinishesIt()
    {
        var reading = Reading(200, systemPercent: 100);

        Assert.Equal(ScaleVerdict.Match, SessionScaleProbe.Evaluate(200, reading));
        var note = SessionScaleProbe.Explain(200, reading);
        Assert.NotNull(note);
        Assert.Contains("system scale is still 100%", note);
        Assert.Contains("signs out", note);
    }

    [Fact]
    public void NoReadingSaysUnverified()
    {
        Assert.Contains("not verified", SessionScaleProbe.Explain(200, null));
        Assert.Contains("not verified", SessionScaleProbe.Explain(
            200, new SessionScaleObservation(0, 0, [], "no monitors visible to this process")));
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
            ScaleNote = "Windows runs this seat's session at 100%",
        };

        // Every launch and relaunch asks for the geometry first. The old reading, and the note
        // about it, say nothing about the session that is about to replace the one it described.
        var geometry = mgr.GeometryFor(seat);

        Assert.Equal(200, geometry.ScaleFactor);
        Assert.Null(seat.AppliedScaleFactor);
        Assert.Null(seat.AppliedScaleCheckedAt);
        Assert.Null(seat.ScaleNote);
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
