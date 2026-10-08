using MultiSeat.Service.Interop;
using MultiSeat.Service.Sessions;
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// Event-aware gate (issue #96 follow-up). A fake clock and a scripted event source drive the
/// real <c>InputDesktopGate.Wait(..., IDesktopSwitchSignal, ...)</c> loop, so no test sleeps and
/// no test touches a live desktop.
/// </summary>
public class InputDesktopGateEventTests
{
    /// <summary>
    /// Stands in for the WinEvent hook. Owns the fake clock: Wait advances it either to the
    /// next scripted event or by the whole wait, exactly like a real blocking wait would.
    /// </summary>
    private sealed class ScriptedSignal(params double[] eventSeconds) : IDesktopSwitchSignal
    {
        private readonly List<TimeSpan> _events = eventSeconds.Select(TimeSpan.FromSeconds).OrderBy(t => t).ToList();
        private int _consumed;
        public TimeSpan Now;

        /// <summary>A switch that lands right now, e.g. while the desktop test is running.</summary>
        public void RaiseNow() { _events.Add(Now); _events.Sort(); }

        public int Wait(TimeSpan maxWait)
        {
            if (maxWait > TimeSpan.Zero)
            {
                var limit = Now + maxWait;
                if (_consumed < _events.Count && _events[_consumed] <= limit)
                    Now = _events[_consumed] > Now ? _events[_consumed] : Now;
                else
                    Now = limit;
            }
            var n = 0;
            while (_consumed < _events.Count && _events[_consumed] <= Now) { _consumed++; n++; }
            return n;
        }
    }

    private static InputDesktopGateResult Run(
        ScriptedSignal signal, Func<TimeSpan, bool> openAt, double timeoutS, double stableS,
        CancellationToken ct = default)
        => InputDesktopGate.Wait(
            () => openAt(signal.Now),
            TimeSpan.FromSeconds(timeoutS), TimeSpan.FromSeconds(stableS),
            signal, () => signal.Now, ct, hookInstalled: true);

    [Fact]
    public void ASwitchEventProceedsWithoutWaitingForTheNextPoll()
    {
        // Denied until 1.1 s, then open. The event at 1.1 s wakes the loop at once, so the
        // 0.5 s window runs 1.1 -> 1.6. Polling alone would first see it open at 1.25 -> 1.75.
        var s = new ScriptedSignal(1.1);
        var r = Run(s, t => t.TotalSeconds >= 1.1, timeoutS: 60, stableS: 0.5);

        Assert.Equal(InputDesktopGateOutcome.Ready, r.Outcome);
        Assert.Equal(1600, r.ElapsedMs, precision: 3);
        Assert.Equal(1, r.SwitchEvents);
        Assert.True(r.HookInstalled);
    }

    [Fact]
    public void WithNoEventsThePollingBackstopStillProceeds()
    {
        // The hook never fires (or never installed): the desktop opens at 2 s and the gate
        // must still get there by polling alone.
        var s = new ScriptedSignal();
        var r = Run(s, t => t.TotalSeconds >= 2, timeoutS: 60, stableS: 3);

        Assert.Equal(InputDesktopGateOutcome.Ready, r.Outcome);
        Assert.Equal(5000, r.ElapsedMs, precision: 3);
        Assert.Equal(0, r.SwitchEvents);
    }

    [Fact]
    public void TimesOutAndLetsTheCallerProceedWhenNothingEverOpens()
    {
        var s = new ScriptedSignal(); // no events, never open
        var r = Run(s, _ => false, timeoutS: 10, stableS: 3);

        Assert.Equal(InputDesktopGateOutcome.TimedOut, r.Outcome);
        Assert.True(r.ElapsedMs >= 10_000);
        Assert.True(r.ElapsedMs < 10_500, $"overshot the timeout by too much: {r.ElapsedMs}");
    }

    [Fact]
    public void ASwitchWhileOpenRestartsTheStableWindow()
    {
        // Always openable, but the desktop switches at 2 s. Without the restart it would be ready
        // at 3 s; with it, the 3 s window starts again at 2 s and ends at 5 s.
        var s = new ScriptedSignal(2);
        var r = Run(s, _ => true, timeoutS: 60, stableS: 3);

        Assert.Equal(InputDesktopGateOutcome.Ready, r.Outcome);
        Assert.Equal(5000, r.ElapsedMs, precision: 3);
        Assert.Equal(1, r.SwitchEvents);
    }

    [Fact]
    public void ASwitchThatLandsDuringTheFinalTestStillBlocksReady()
    {
        // The window is complete at 1 s, and a switch lands while that very test runs - after the
        // loop last looked for events. Only the check just before returning can see it, so ready
        // moves to 2 s instead of 1 s.
        var s = new ScriptedSignal();
        var raised = false;
        var r = Run(s, t =>
        {
            if (t.TotalSeconds >= 1.0 && !raised) { raised = true; s.RaiseNow(); }
            return true;
        }, timeoutS: 60, stableS: 1);

        Assert.Equal(InputDesktopGateOutcome.Ready, r.Outcome);
        Assert.Equal(2000, r.ElapsedMs, precision: 3);
        Assert.Equal(1, r.SwitchEvents);
    }

    [Fact]
    public void ABriefOpenThenADeniedSwitchResetsTheWindow()
    {
        // The #96 shape with events: open 0-2 s, a switch at 2 s that leaves it denied until
        // 121 s. Ready must come only after a full window of the later open period.
        var s = new ScriptedSignal(2, 121);
        var r = Run(s, t => t.TotalSeconds < 2 || t.TotalSeconds >= 121, timeoutS: 300, stableS: 3);

        Assert.Equal(InputDesktopGateOutcome.Ready, r.Outcome);
        Assert.True(r.ElapsedMs >= 124_000, $"ready too early: {r.ElapsedMs}");
        Assert.Equal(2, r.SwitchEvents);
    }

    [Fact]
    public void AStormOfEventsCannotPushTheGateOutPastTheTimeout()
    {
        // A switch every 100 ms means the window never completes, but the timeout still ends it.
        var s = new ScriptedSignal(Enumerable.Range(1, 400).Select(i => i * 0.1).ToArray());
        var r = Run(s, _ => true, timeoutS: 10, stableS: 3);

        Assert.Equal(InputDesktopGateOutcome.TimedOut, r.Outcome);
        Assert.True(r.ElapsedMs >= 10_000 && r.ElapsedMs < 10_500, $"elapsed {r.ElapsedMs}");
    }

    [Fact]
    public void ACancelledTokenStopsTheWait()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var s = new ScriptedSignal();
        Assert.Throws<OperationCanceledException>(() => Run(s, _ => false, 60, 3, cts.Token));
    }

    [Fact]
    public void ZeroStableWindowStillReturnsOnTheFirstOpenWhenNoEventIsPending()
    {
        var s = new ScriptedSignal();
        var r = Run(s, _ => true, timeoutS: 10, stableS: 0);
        Assert.Equal(InputDesktopGateOutcome.Ready, r.Outcome);
        Assert.Equal(1, r.Attempts);
    }

    // -- result file ----------------------------------------------------

    [Fact]
    public void ParsesTheFiveFieldResultWithEventsAndHookState()
    {
        var r = InputDesktopGate.ParseResult("ready 5000 21 2 1")!;
        Assert.Equal(InputDesktopGateOutcome.Ready, r.Outcome);
        Assert.Equal(2, r.SwitchEvents);
        Assert.True(r.HookInstalled);

        var t = InputDesktopGate.ParseResult("timeout 180000 721 0 0")!;
        Assert.Equal(InputDesktopGateOutcome.TimedOut, t.Outcome);
        Assert.False(t.HookInstalled);
    }

    [Fact]
    public void StillParsesTheOlderThreeFieldResult()
    {
        var r = InputDesktopGate.ParseResult("ready 3000 13")!;
        Assert.Equal(0, r.SwitchEvents);
        Assert.False(r.HookInstalled);
    }

    [Theory]
    [InlineData("ready 1 2 3")]       // four fields
    [InlineData("ready 1 2 x 1")]     // events not a number
    [InlineData("ready 1 2 3 maybe")] // hook flag not 0/1
    public void RejectsMalformedFiveFieldResults(string text)
        => Assert.Null(InputDesktopGate.ParseResult(text));

    [Fact]
    public void TheHelperEntryPointWritesAResultThatParsesAndMatchesItsExitCode()
    {
        // Touches this machine's own desktop only (read-only OpenInputDesktop plus a hook on a
        // background thread); opens no window and no seat.
        var path = Path.Combine(Path.GetTempPath(), $"idg-test-{Guid.NewGuid():N}.txt");
        try
        {
            var exit = InputDesktopGate.RunAndWriteResult(path, timeoutSeconds: 1, stableMs: 0);
            var r = InputDesktopGate.ParseResult(File.ReadAllText(path));

            Assert.NotNull(r);
            Assert.Equal(exit == 0, r!.Outcome == InputDesktopGateOutcome.Ready);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
