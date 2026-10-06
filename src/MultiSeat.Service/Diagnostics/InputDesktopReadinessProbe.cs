using System.Runtime.InteropServices;
using System.Text.Json;
using MultiSeat.Service.Display;
using MultiSeat.Service.Interop;

namespace MultiSeat.Service.Diagnostics;

/// <summary>
/// Continuously samples whether this seat session's input desktop is accessible, and what the
/// active display identity is, from the moment it starts until <paramref name="seconds"/> have
/// elapsed — written as it runs the moment it starts, so a crash or an early kill still leaves a
/// real partial timeline rather than nothing.
///
/// WHY THIS EXISTS (issue #96)
///
/// The reporter found that on a freshly-created seat session, Apollo's very first
/// <c>OpenInputDesktop</c>/<c>DuplicateOutput</c> call can fail with ACCESS_DENIED, and that only a
/// disconnect/reconnect of the session clears it — reproducibly, across many session numbers, with
/// <see cref="Configuration.MultiSeatOptions.KeepaliveOnSeparateDesktop"/> (the issue #18 fix) on.
/// Their own testing covered discrete checkpoints (0/1/3/5/10s delays, all failed) but never asked
/// whether the state resolves on its own given enough time, nor captured the display identity
/// change they separately observed (RDP output going from DISPLAY1 to DISPLAY17 across a reconnect)
/// as a continuous, timestamped series.
///
/// This probe answers that: it is launched inside the seat session right after the session exists
/// (in parallel with, not instead of, Apollo's own startup — it must not perturb the race it is
/// trying to observe), and polls both facts together at a fixed interval for the whole window. A
/// true deadlock (the state never changes without an external reconnect) and a slow race (it would
/// have resolved on its own, just not within the 10s anyone had tried) produce different fix
/// shapes — a readiness wait versus a forced recovery step — and this is what tells them apart.
///
/// Session-scoped like every display API: run inside the seat session, never session 0.
/// Usage: MultiSeat.Service.exe --input-desktop-probe &lt;output-jsonl-file&gt; &lt;seconds&gt;
/// </summary>
internal static class InputDesktopReadinessProbe
{
    /// <summary>Time between samples. Fine enough to catch a transition without flooding the file.</summary>
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

    internal static int RunAndWriteToFile(string outputPath, int seconds)
    {
        using var writer = new StreamWriter(outputPath, append: false) { AutoFlush = true };

        var start = DateTime.UtcNow;
        var deadline = start + TimeSpan.FromSeconds(Math.Max(1, seconds));
        var sampleIndex = 0;

        while (DateTime.UtcNow < deadline)
        {
            var sample = TakeSample(start, sampleIndex++);
            writer.WriteLine(JsonSerializer.Serialize(sample));
            Thread.Sleep(SampleInterval);
        }

        writer.WriteLine(JsonSerializer.Serialize(new ProbeSummary(
            "summary", (DateTime.UtcNow - start).TotalMilliseconds, sampleIndex)));

        return 0;
    }

    /// <summary>
    /// One sample: can this thread open the input desktop right now, and what does the active
    /// display topology look like. Exposed internally so unit tests can check the record shape
    /// without any real desktop — the Win32 calls themselves are not unit-testable off a live
    /// session, matching how <c>KeepaliveDesktopHelper.Run</c> is exercised only by hand.
    /// </summary>
    internal static ProbeSample TakeSample(DateTime start, int index)
    {
        var elapsedMs = (DateTime.UtcNow - start).TotalMilliseconds;
        var hDesktop = User32.OpenInputDesktop(0, false, User32.DESKTOP_READOBJECTS);

        bool opened;
        int win32Error;
        string? desktopName = null;

        if (hDesktop == IntPtr.Zero)
        {
            opened = false;
            win32Error = Marshal.GetLastWin32Error();
        }
        else
        {
            opened = true;
            win32Error = 0;
            desktopName = ReadObjectName(hDesktop);

            // Mirror syncThreadDesktop(): if Apollo's own next move is attaching the calling
            // thread to whatever it just opened, a probe that only opens and never attaches
            // would miss a failure mode scoped to SetThreadDesktop specifically.
            User32.SetThreadDesktop(hDesktop);
            User32.CloseDesktop(hDesktop);
        }

        List<DisplayPathRecord> displays;
        try { displays = DisplayEnumeratorHelper.EnumerateAllPaths(); }
        catch { displays = []; }

        return new ProbeSample(
            Index: index,
            ElapsedMs: elapsedMs,
            OpenInputDesktopSucceeded: opened,
            Win32Error: win32Error,
            DesktopName: desktopName,
            ActiveDisplays: displays
                .Where(d => d.Active)
                .Select(d => new ProbeDisplay(d.GdiName, d.AdapterLow, d.AdapterHigh, d.TargetId))
                .ToArray());
    }

    private static string? ReadObjectName(IntPtr handle)
    {
        var sb = new System.Text.StringBuilder(256);
        return User32.GetUserObjectInformationW(handle, User32.UOI_NAME, sb, sb.Capacity, out _)
            ? sb.ToString()
            : null;
    }
}

/// <summary>One timestamped readiness sample. Serialized as one JSON line per sample.</summary>
internal record ProbeSample(
    int Index,
    double ElapsedMs,
    bool OpenInputDesktopSucceeded,
    int Win32Error,
    string? DesktopName,
    ProbeDisplay[] ActiveDisplays);

/// <summary>The RDP-side display identity bits the reporter tracked by hand (DISPLAY1 → DISPLAY17,
/// adapter LUID low part) — captured here per sample instead of eyeballed once per reconnect.</summary>
internal record ProbeDisplay(string GdiName, uint AdapterLow, int AdapterHigh, uint TargetId);

/// <summary>Final line of the file: how long the probe actually ran and how many samples it took,
/// so a reader can tell a clean finish from a probe that was killed mid-run.</summary>
internal record ProbeSummary(string Kind, double TotalElapsedMs, int SampleCount);
