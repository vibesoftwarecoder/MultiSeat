using System.Diagnostics;
using MultiSeat.Service.Interop;

namespace MultiSeat.Service.Sessions;

internal enum InputDesktopGateOutcome { Ready, TimedOut }

/// <param name="SwitchEvents">Desktop-switch events seen while waiting (0 when the hook never fired).</param>
/// <param name="HookInstalled">Whether the desktop-switch hook was running. False means polling only.</param>
internal record InputDesktopGateResult(
    InputDesktopGateOutcome Outcome, double ElapsedMs, int Attempts,
    int SwitchEvents = 0, bool HookInstalled = false);

/// <summary>
/// Waits, inside a seat session, until the input desktop can be opened and STAYS openable for a
/// stable window. Apollo's capture setup needs that desktop, and issue #96 showed it can be
/// denied for minutes right after logon (a third-party elevation prompt on the Secure Desktop)
/// and then return by itself. Starting Apollo into the denied window makes it fail once and
/// never retry, while the readiness check still reports the seat Ready.
///
/// The stable window matters: on the reporter's host the desktop opened once, then was denied
/// again about two seconds later. A single successful open proves nothing.
///
/// EVENT-AWARE: the wait blocks on an <see cref="IDesktopSwitchSignal"/> (a WinEvent hook on
/// EVENT_SYSTEM_DESKTOPSWITCH in the real helper) for at most one poll interval. A desktop switch
/// wakes the loop at once, re-tests the desktop, and restarts the stable window: a switch is
/// proof that the desktop changed under us, even if it is openable again by the time we look.
/// Polling stays as the backstop, so a hook that is not installed or never fires costs nothing
/// and the gate behaves exactly as it did before. The timeout is unchanged.
///
/// Usage: MultiSeat.Service.exe --wait-input-desktop &lt;result-file&gt; &lt;timeout-seconds&gt; &lt;stable-ms&gt;
/// Session-scoped like every desktop API: run it inside the seat session, never session 0.
/// </summary>
internal static class InputDesktopGate
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Polling-only form (no event source). Kept for callers and tests that sleep.</summary>
    internal static InputDesktopGateResult Wait(
        Func<bool> canOpenInputDesktop, TimeSpan timeout, TimeSpan stable,
        Action<TimeSpan>? sleep = null, Func<TimeSpan>? clock = null)
        => Wait(canOpenInputDesktop, timeout, stable,
            new SleepSignal(sleep ?? Thread.Sleep), clock, CancellationToken.None);

    /// <summary>
    /// Event-aware form. <paramref name="signal"/> replaces the sleep between polls.
    /// <paramref name="ct"/> is checked once per loop turn (at most one poll interval of latency).
    /// </summary>
    internal static InputDesktopGateResult Wait(
        Func<bool> canOpenInputDesktop, TimeSpan timeout, TimeSpan stable,
        IDesktopSwitchSignal signal, Func<TimeSpan>? clock, CancellationToken ct,
        bool hookInstalled = false)
    {
        var sw = Stopwatch.StartNew();
        clock ??= () => sw.Elapsed;

        var attempts = 0;
        var events = 0;
        TimeSpan? openSince = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = clock();
            attempts++;
            if (canOpenInputDesktop())
            {
                openSince ??= now;
                if (now - openSince.Value >= stable)
                {
                    // A switch that landed after the test above but before we return must still
                    // count: collect anything already pending before declaring ready.
                    var late = signal.Wait(TimeSpan.Zero);
                    if (late == 0)
                        return new(InputDesktopGateOutcome.Ready, now.TotalMilliseconds, attempts, events, hookInstalled);
                    events += late;
                    openSince = null;
                    continue;
                }
            }
            else
            {
                openSince = null;
            }

            if (now >= timeout)
                return new(InputDesktopGateOutcome.TimedOut, now.TotalMilliseconds, attempts, events, hookInstalled);

            var n = signal.Wait(PollInterval);
            if (n > 0)
            {
                events += n;
                openSince = null; // the desktop changed: prove stability again from the next test
            }
        }
    }

    private sealed class SleepSignal(Action<TimeSpan> sleep) : IDesktopSwitchSignal
    {
        public int Wait(TimeSpan maxWait)
        {
            if (maxWait > TimeSpan.Zero) sleep(maxWait);
            return 0;
        }
    }

    internal static bool TryOpenInputDesktop()
    {
        var h = User32.OpenInputDesktop(0, false, User32.DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return false;
        User32.CloseDesktop(h);
        return true;
    }

    /// <summary>
    /// Helper entry point. Writes "ready" or "timeout", elapsed ms, attempts, desktop-switch
    /// events and whether the hook was installed (1/0) to the result file.
    /// </summary>
    internal static int RunAndWriteResult(string resultPath, int timeoutSeconds, int stableMs)
    {
        // The hook is a bonus: if it cannot start, Wait degrades to polling.
        using var hook = DesktopSwitchHook.Start();
        var r = Wait(TryOpenInputDesktop,
            TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)),
            TimeSpan.FromMilliseconds(Math.Max(0, stableMs)),
            hook, clock: null, CancellationToken.None, hookInstalled: hook.Installed);
        File.WriteAllText(resultPath,
            $"{(r.Outcome == InputDesktopGateOutcome.Ready ? "ready" : "timeout")} {r.ElapsedMs:F0} {r.Attempts} " +
            $"{r.SwitchEvents} {(r.HookInstalled ? 1 : 0)}");
        return r.Outcome == InputDesktopGateOutcome.Ready ? 0 : 1;
    }

    /// <summary>Parses the result file: the original 3 fields, or the 5-field form with events + hook.</summary>
    internal static InputDesktopGateResult? ParseResult(string text)
    {
        var p = text.Trim().Split(' ');
        if (p.Length is not (3 or 5) || !double.TryParse(p[1], out var ms) || !int.TryParse(p[2], out var n)) return null;
        var events = 0;
        var hook = false;
        if (p.Length == 5)
        {
            if (!int.TryParse(p[3], out events) || p[4] is not ("0" or "1")) return null;
            hook = p[4] == "1";
        }
        return p[0] switch
        {
            "ready" => new(InputDesktopGateOutcome.Ready, ms, n, events, hook),
            "timeout" => new(InputDesktopGateOutcome.TimedOut, ms, n, events, hook),
            _ => null
        };
    }
}
