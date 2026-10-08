using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiSeat.Service.Interop;

namespace MultiSeat.Service.Diagnostics;

/// <summary>
/// Reads the DPI scale a seat's session is ACTUALLY running at, from inside the session, so it
/// can be set beside the scale MultiSeat asked for (issue #93).
///
/// WHY THIS EXISTS
///
/// Until now the scale a seat reported was the scale MultiSeat wrote into <c>Default.rdp</c>
/// (<c>SeatManager.RecordScale</c>), and the log line after a resolution change printed the same
/// recorded value. Neither says what Windows did with it. A resolution change reconnects to the
/// existing session; that a reconnect applies a new SIZE was measured, that it applies a new
/// SCALE never was. A report that the dashboard shows 200% while the seat renders at 100% is
/// exactly what you would see if it does not, and nothing in the service could tell the two apart.
///
/// WHAT IT READS, AND WHY THREE SOURCES
///
/// Per monitor: the effective DPI a per-monitor-aware process is given (shcore GetDpiForMonitor),
/// and the scale the display configuration stores for that display's source. These are two
/// different parts of Windows describing one thing. They agree on a healthy desktop; if they do
/// not, the stored scale has changed without the DPI that apps render with following it, which
/// is the shape a "scale applied on connect only" behaviour would have. The system DPI (fixed
/// when the session started) is recorded too: a session that was reconnected at a new scale
/// but still reports its old system DPI was not rebuilt, only resized.
///
/// The verdict compares the effective scale of the RDP display with the intended one: that is the
/// display mstsc's <c>desktopscalefactor</c> lands on, and it stays the same display whether or
/// not display isolation has since made another one primary. A session with no RDP display (a
/// console session, a test host) falls back to the primary monitor. Every monitor is still
/// recorded, because the one a client actually sees (Apollo's virtual display) is a different
/// display with a scale of its own.
///
/// Session-scoped like every display API: run inside the seat session, never session 0.
/// Usage: MultiSeat.Service.exe --read-applied-scale &lt;output-json-file&gt;
/// </summary>
internal static class SessionScaleProbe
{
    /// <summary>The percentages Windows' display scale list steps through, in order.</summary>
    internal static readonly int[] DpiScaleSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>96 DPI is 100%.</summary>
    internal static int PercentFromDpi(uint dpi) => (int)Math.Round(dpi * 100.0 / 96.0);

    /// <summary>
    /// The percentage the display configuration's three relative values describe. They count steps
    /// along <see cref="DpiScaleSteps"/> from the RECOMMENDED scale, so the current index is
    /// <c>curScaleRel - minScaleRel</c> (the minimum sits at index 0). Null when the values do
    /// not land inside the list, which means the layout is not what this assumes.
    /// </summary>
    internal static int? PercentFromRelativeScale(int minScaleRel, int curScaleRel)
    {
        var index = curScaleRel - minScaleRel;
        return index >= 0 && index < DpiScaleSteps.Length ? DpiScaleSteps[index] : null;
    }

    /// <summary>What the observation says about the scale MultiSeat intended.</summary>
    internal static ScaleVerdict Evaluate(int intendedPercent, SessionScaleObservation? observation)
    {
        if (observation?.AppliedPercent is not { } applied)
            return ScaleVerdict.Unknown;
        return applied == intendedPercent ? ScaleVerdict.Match : ScaleVerdict.Mismatch;
    }

    /// <summary>
    /// Reads the session as it is now. Never throws: a failure is reported in
    /// <see cref="SessionScaleObservation.Error"/>, which makes the verdict Unknown rather than
    /// a wrong Match or Mismatch.
    /// </summary>
    internal static SessionScaleObservation Observe()
    {
        try
        {
            // Must be per-monitor aware, or Windows reports 96 for everything. Setting it fails
            // harmlessly when the process already has an awareness (a manifest, an earlier call).
            DpiApi.SetProcessDpiAwarenessContext(DpiApi.PerMonitorAwareV2);
            var awareness = DpiApi.GetAwarenessFromDpiAwarenessContext(DpiApi.GetThreadDpiAwarenessContext());
            if (awareness != DpiApi.DpiAwarenessPerMonitor)
                return new SessionScaleObservation(0, 0, [], $"process is not per-monitor DPI aware (awareness {awareness}); its DPI readings would all be 96");

            var systemDpi = DpiApi.GetDpiForSystem();
            var stored = ReadStoredScalesByGdiName();

            var adapters = ReadAdapterNamesByGdiName();
            var monitors = new List<MonitorScale>();
            User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref User32.Rect rect, IntPtr _) =>
            {
                var info = new DpiApi.MonitorInfoEx { cbSize = (uint)Marshal.SizeOf<DpiApi.MonitorInfoEx>() };
                if (!DpiApi.GetMonitorInfoEx(hMonitor, ref info))
                    return true;
                if (DpiApi.GetDpiForMonitor(hMonitor, DpiApi.MdtEffectiveDpi, out var dpiX, out _) != 0)
                    return true;

                monitors.Add(new MonitorScale(
                    GdiName: info.szDevice,
                    Adapter: adapters.TryGetValue(info.szDevice, out var adapter) ? adapter : "",
                    Primary: (info.dwFlags & User32.MonitorInfo.PRIMARY) != 0,
                    EffectiveDpi: dpiX,
                    EffectivePercent: PercentFromDpi(dpiX),
                    StoredPercent: stored.TryGetValue(info.szDevice, out var s) ? s : null,
                    WidthPx: info.rcMonitor.Width,
                    HeightPx: info.rcMonitor.Height));
                return true;
            }, IntPtr.Zero);

            return new SessionScaleObservation(
                systemDpi, PercentFromDpi(systemDpi), monitors.ToArray(),
                monitors.Count == 0 ? "no monitors visible to this process" : null);
        }
        catch (Exception ex)
        {
            return new SessionScaleObservation(0, 0, [], $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The display adapter's name for each display, keyed by GDI name.</summary>
    private static Dictionary<string, string> ReadAdapterNamesByGdiName()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < 64; i++)
        {
            var device = new User32.DisplayDevice { cb = Marshal.SizeOf<User32.DisplayDevice>() };
            if (!User32.EnumDisplayDevices(null, i, ref device, 0))
                break;
            result[device.DeviceName] = device.DeviceString;
        }
        return result;
    }

    /// <summary>The scale the display configuration stores, per active source, keyed by GDI name.</summary>
    private static Dictionary<string, int> ReadStoredScalesByGdiName()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (User32.GetDisplayConfigBufferSizes(User32.QDC_ONLY_ACTIVE_PATHS, out var numPaths, out var numModes)
            != User32.ERROR_SUCCESS)
            return result;

        var paths = new User32.DisplayConfigPathInfo[numPaths];
        var modes = new User32.DisplayConfigModeInfo[numModes];
        if (User32.QueryDisplayConfig(User32.QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero)
            != User32.ERROR_SUCCESS)
            return result;

        foreach (var path in paths.Take((int)numPaths))
        {
            var sourceName = new User32.DisplayConfigSourceDeviceName
            {
                header = new User32.DisplayConfigDeviceInfoHeader
                {
                    type = User32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<User32.DisplayConfigSourceDeviceName>(),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id,
                }
            };
            if (User32.DisplayConfigGetDeviceInfo(ref sourceName) != User32.ERROR_SUCCESS)
                continue;

            var scale = new DpiApi.DisplayConfigSourceDpiScale
            {
                header = new User32.DisplayConfigDeviceInfoHeader
                {
                    type = DpiApi.DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                    size = (uint)Marshal.SizeOf<DpiApi.DisplayConfigSourceDpiScale>(),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id,
                }
            };
            if (DpiApi.DisplayConfigGetDeviceInfo(ref scale) != User32.ERROR_SUCCESS)
                continue;

            if (PercentFromRelativeScale(scale.minScaleRel, scale.curScaleRel) is { } percent)
                result[sourceName.viewGdiDeviceName] = percent;
        }

        return result;
    }

    /// <summary>Helper entry point. Writes the observation as one JSON document; exit 0 when it holds a reading.</summary>
    internal static int RunAndWriteToFile(string outputPath)
    {
        var observation = Observe();
        File.WriteAllText(outputPath, JsonSerializer.Serialize(observation));
        return observation.Error is null ? 0 : 1;
    }

    internal static SessionScaleObservation? ParseResult(string json)
    {
        try { return JsonSerializer.Deserialize<SessionScaleObservation>(json); }
        catch (JsonException) { return null; }
    }
}

internal enum ScaleVerdict
{
    /// <summary>The session runs at the scale MultiSeat intended.</summary>
    Match,

    /// <summary>The session runs at a different scale than MultiSeat intended.</summary>
    Mismatch,

    /// <summary>The scale could not be read, so nothing is claimed either way.</summary>
    Unknown,
}

/// <summary>One monitor as the session sees it.</summary>
/// <param name="EffectivePercent">From the DPI a per-monitor-aware process is given.</param>
/// <param name="StoredPercent">From the display configuration; null when that query had no answer.</param>
internal record MonitorScale(
    string GdiName, string Adapter, bool Primary, uint EffectiveDpi, int EffectivePercent,
    int? StoredPercent, int WidthPx, int HeightPx)
{
    /// <summary>The display mstsc's session surface is on.</summary>
    [JsonIgnore]
    internal bool IsRdpDisplay => Adapter.Contains("Remote Display", StringComparison.OrdinalIgnoreCase);
}

/// <param name="SystemPercent">The system DPI, fixed when the session started.</param>
/// <param name="Error">Set when the session could not be read; the other fields are then not a reading.</param>
internal record SessionScaleObservation(
    uint SystemDpi, int SystemPercent, MonitorScale[] Monitors, string? Error)
{
    /// <summary>
    /// The scale of the display <c>desktopscalefactor</c> applies to: the RDP display, or the
    /// primary monitor when the session has none. Null when there is no reading.
    /// </summary>
    [JsonIgnore]
    internal int? AppliedPercent =>
        Error is null
            ? (Monitors.FirstOrDefault(m => m.IsRdpDisplay) ?? Monitors.FirstOrDefault(m => m.Primary))
                ?.EffectivePercent
            : null;

    /// <summary>
    /// Monitors whose stored scale and effective scale differ: the display configuration says one
    /// thing and apps are rendering at another. On a healthy desktop there are none.
    /// </summary>
    [JsonIgnore]
    internal IEnumerable<MonitorScale> Disagreements =>
        Monitors.Where(m => m.StoredPercent is { } stored && stored != m.EffectivePercent);

    /// <summary>One line for the log: every monitor, then the system scale.</summary>
    internal string Describe() =>
        Error is not null
            ? $"unreadable ({Error})"
            : string.Join("; ", Monitors.Select(m =>
                  $"{m.GdiName}{(m.Primary ? " (primary)" : "")}{(m.Adapter.Length > 0 ? $" [{m.Adapter}]" : "")} {m.WidthPx}x{m.HeightPx} effective {m.EffectivePercent}%"
                  + (m.StoredPercent is { } s
                      ? $", stored {s}%{(s != m.EffectivePercent ? " (DIFFERS from effective)" : "")}"
                      : ", stored n/a")))
              + $"; system {SystemPercent}%";
}
