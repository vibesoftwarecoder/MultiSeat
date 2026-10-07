using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MultiSeat.Service.Display;
using MultiSeat.Service.Interop;

namespace MultiSeat.Service.Diagnostics;

/// <summary>
/// Continuously samples whether this seat session's input desktop is accessible, what the active
/// display identity is, and — directly, not inferred — which desktops exist in this session's
/// window station and what is running on each one, from the moment it starts until
/// <paramref name="seconds"/> have elapsed. Written as it runs, so a crash or an early kill still
/// leaves a real partial timeline rather than nothing.
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
/// A later measurement on the reporter's machine found a third-party elevation/Secure Desktop
/// transition correlated with the failure (an ETW ConsentUI_SwitchDesktop event), but that is a
/// correlation from a SEPARATE trace, not something this probe observed directly — OpenInputDesktop
/// failing does not by itself show WHAT currently owns the desktop. Checking that by timing alone
/// (does access ever return within some window) only narrows which KIND of fix applies; it does not
/// show the mechanism. So this probe also enumerates the window station's desktops and, for each,
/// the processes with windows on it — EnumDesktopsW needs WINSTA_ENUMDESKTOPS on the window
/// station, a different and usually less restricted right than the DESKTOP_READOBJECTS
/// OpenInputDesktop needs on the desktop object itself, so the former can succeed even while the
/// latter is denied. If a UAC-style Secure Desktop is genuinely present and holding things up, this
/// shows it directly: its name, and — access permitting — what is actually on it.
///
/// This probe is launched inside the seat session right after the session exists (in parallel
/// with, not instead of, Apollo's own startup — it must not perturb the race it is trying to
/// observe), and samples all of the above together at a fixed interval for the whole window. A
/// true deadlock (the state never changes without an external reconnect) and a slow race (it would
/// have resolved on its own, just not within the 10s anyone had tried) produce different fix
/// shapes — a readiness wait versus a forced recovery step — and the desktop/window observation is
/// what tells us WHY, not just WHETHER.
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
                .ToArray(),
            DesktopsInStation: EnumerateDesktopsAndWindows());
    }

    private static string? ReadObjectName(IntPtr handle)
    {
        var sb = new StringBuilder(256);
        return User32.GetUserObjectInformationW(handle, User32.UOI_NAME, sb, sb.Capacity, out _)
            ? sb.ToString()
            : null;
    }

    /// <summary>
    /// Every desktop currently in this session's window station, and — access permitting — every
    /// process with a window on each one. A denial opening a specific named desktop is recorded
    /// as evidence (which desktop, which Win32 error), not swallowed: a Secure Desktop refusing
    /// DESKTOP_ENUMERATE while still existing is itself the finding.
    /// </summary>
    private static DesktopObservation[] EnumerateDesktopsAndWindows()
    {
        var names = new List<string>();
        var hWinsta = User32.GetProcessWindowStation();
        if (hWinsta != IntPtr.Zero)
        {
            try
            {
                User32.EnumDesktopsW(hWinsta, (name, _) => { names.Add(name); return true; }, IntPtr.Zero);
            }
            catch
            {
                // Best-effort: a probe sample must never throw and abort the whole run over this.
            }
        }

        var results = new List<DesktopObservation>(names.Count);
        foreach (var name in names)
        {
            var hDesk = User32.OpenDesktopW(name, 0, false, User32.DESKTOP_ENUMERATE);
            if (hDesk == IntPtr.Zero)
            {
                results.Add(new DesktopObservation(name, false, Marshal.GetLastWin32Error(), []));
                continue;
            }

            var windows = new List<DesktopWindow>();
            try
            {
                User32.EnumDesktopWindows(hDesk, (hwnd, _) =>
                {
                    User32.GetWindowThreadProcessId(hwnd, out var pid);
                    var sb = new StringBuilder(256);
                    User32.GetClassName(hwnd, sb, sb.Capacity);

                    string? processName = null;
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                        processName = proc.ProcessName;
                    }
                    catch { /* process gone, or access denied to query it - PID/class still recorded */ }

                    windows.Add(new DesktopWindow((int)pid, processName, sb.ToString()));
                    return true;
                }, IntPtr.Zero);
            }
            finally
            {
                User32.CloseDesktop(hDesk);
            }

            // Dedupe exact repeats (one process commonly owns several windows on one desktop) so
            // the file stays readable without losing any distinct process/class combination.
            var distinctWindows = windows
                .Distinct()
                .ToArray();
            results.Add(new DesktopObservation(name, true, null, distinctWindows));
        }

        return results.ToArray();
    }
}

/// <summary>One timestamped readiness sample. Serialized as one JSON line per sample.</summary>
internal record ProbeSample(
    int Index,
    double ElapsedMs,
    bool OpenInputDesktopSucceeded,
    int Win32Error,
    string? DesktopName,
    ProbeDisplay[] ActiveDisplays,
    DesktopObservation[] DesktopsInStation);

/// <summary>The RDP-side display identity bits the reporter tracked by hand (DISPLAY1 → DISPLAY17,
/// adapter LUID low part) — captured here per sample instead of eyeballed once per reconnect.</summary>
internal record ProbeDisplay(string GdiName, uint AdapterLow, int AdapterHigh, uint TargetId);

/// <summary>
/// One desktop found in this session's window station at sample time. <paramref name="Windows"/>
/// is empty either because the desktop genuinely has none, or because <see cref="EnumerationDenied"/>
/// is true and <see cref="Win32ErrorIfDenied"/> says why — the two cases are distinguished, not
/// collapsed, because a desktop that EXISTS but refuses enumeration is itself a finding.
/// </summary>
internal record DesktopObservation(
    string DesktopName,
    bool EnumerationSucceeded,
    int? Win32ErrorIfDenied,
    DesktopWindow[] Windows)
{
    internal bool EnumerationDenied => !EnumerationSucceeded;
}

/// <summary>One window found on a desktop: its owning process (by id, and by name when the probe
/// could still query it) and its window class — enough to name what is actually sitting there
/// (a UAC consent dialog's class, an elevation helper's process name) rather than just "something
/// is open".</summary>
internal record DesktopWindow(int ProcessId, string? ProcessName, string ClassName);

/// <summary>Final line of the file: how long the probe actually ran and how many samples it took,
/// so a reader can tell a clean finish from a probe that was killed mid-run.</summary>
internal record ProbeSummary(string Kind, double TotalElapsedMs, int SampleCount);
