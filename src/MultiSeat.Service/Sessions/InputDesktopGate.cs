using System.Diagnostics;
using MultiSeat.Service.Interop;

namespace MultiSeat.Service.Sessions;

internal enum InputDesktopGateOutcome { Ready, TimedOut }

internal record InputDesktopGateResult(InputDesktopGateOutcome Outcome, double ElapsedMs, int Attempts);

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
/// Usage: MultiSeat.Service.exe --wait-input-desktop &lt;result-file&gt; &lt;timeout-seconds&gt; &lt;stable-ms&gt;
/// Session-scoped like every desktop API: run it inside the seat session, never session 0.
/// </summary>
internal static class InputDesktopGate
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal static InputDesktopGateResult Wait(
        Func<bool> canOpenInputDesktop, TimeSpan timeout, TimeSpan stable,
        Action<TimeSpan>? sleep = null, Func<TimeSpan>? clock = null)
    {
        sleep ??= Thread.Sleep;
        var sw = Stopwatch.StartNew();
        clock ??= () => sw.Elapsed;

        var attempts = 0;
        TimeSpan? openSince = null;
        while (true)
        {
            var now = clock();
            attempts++;
            if (canOpenInputDesktop())
            {
                openSince ??= now;
                if (now - openSince.Value >= stable)
                    return new(InputDesktopGateOutcome.Ready, now.TotalMilliseconds, attempts);
            }
            else
            {
                openSince = null;
            }

            if (now >= timeout)
                return new(InputDesktopGateOutcome.TimedOut, now.TotalMilliseconds, attempts);
            sleep(PollInterval);
        }
    }

    internal static bool TryOpenInputDesktop()
    {
        var h = User32.OpenInputDesktop(0, false, User32.DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return false;
        User32.CloseDesktop(h);
        return true;
    }

    /// <summary>Helper entry point. Writes "ready" or "timeout", elapsed ms and attempts to the result file.</summary>
    internal static int RunAndWriteResult(string resultPath, int timeoutSeconds, int stableMs)
    {
        var r = Wait(TryOpenInputDesktop,
            TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)),
            TimeSpan.FromMilliseconds(Math.Max(0, stableMs)));
        File.WriteAllText(resultPath,
            $"{(r.Outcome == InputDesktopGateOutcome.Ready ? "ready" : "timeout")} {r.ElapsedMs:F0} {r.Attempts}");
        return r.Outcome == InputDesktopGateOutcome.Ready ? 0 : 1;
    }

    internal static InputDesktopGateResult? ParseResult(string text)
    {
        var p = text.Trim().Split(' ');
        if (p.Length != 3 || !double.TryParse(p[1], out var ms) || !int.TryParse(p[2], out var n)) return null;
        return p[0] switch
        {
            "ready" => new(InputDesktopGateOutcome.Ready, ms, n),
            "timeout" => new(InputDesktopGateOutcome.TimedOut, ms, n),
            _ => null
        };
    }
}
