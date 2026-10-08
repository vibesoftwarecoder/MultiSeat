using System.Runtime.InteropServices;

namespace MultiSeat.Service.Interop;

/// <summary>
/// P/Invoke for reading the DPI scale a session is actually running at (issue #93).
///
/// Three independent sources, deliberately: the DPI a per-monitor-aware process is told for each
/// monitor (shcore <c>GetDpiForMonitor</c>), the scale stored for that display's source in the
/// display configuration (<c>DisplayConfigGetDeviceInfo</c>), and the system DPI fixed when the
/// session started (<c>GetDpiForSystem</c>). They can disagree, and a disagreement is the finding.
/// </summary>
internal static class DpiApi
{
    private const string User32Lib = "user32.dll";
    private const string ShcoreLib = "shcore.dll";

    // ── Process DPI awareness ────────────────────────────────────────
    // A DPI-unaware process is told 96 for every monitor, whatever the session is set to, so the
    // reader must be per-monitor aware or its numbers mean nothing.

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, which is the handle value -4.</summary>
    public static readonly IntPtr PerMonitorAwareV2 = new(-4);

    /// <summary>DPI_AWARENESS value returned by GetAwarenessFromDpiAwarenessContext for per-monitor.</summary>
    public const int DpiAwarenessPerMonitor = 2;

    [DllImport(User32Lib, SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport(User32Lib)]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport(User32Lib)]
    public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport(User32Lib)]
    public static extern uint GetDpiForSystem();

    // ── Per-monitor DPI ──────────────────────────────────────────────

    /// <summary>MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI — the DPI the user actually sees on that monitor.</summary>
    public const int MdtEffectiveDpi = 0;

    /// <summary>Returns an HRESULT: 0 is S_OK.</summary>
    [DllImport(ShcoreLib)]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ── GetMonitorInfo with the device name ──────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public uint cbSize;
        public User32.Rect rcMonitor;
        public User32.Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport(User32Lib, EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    // ── The display configuration's stored scale for a source ────────
    // DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE is not in the SDK headers but is what Settings >
    // Display > Scale reads. The three values are steps relative to the recommended scale, not
    // percentages; see SessionScaleProbe.PercentFromRelativeScale.

    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = 0xFFFFFFFD; // -3

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigSourceDpiScale
    {
        public User32.DisplayConfigDeviceInfoHeader header;
        public int minScaleRel;
        public int curScaleRel;
        public int maxScaleRel;
    }

    [DllImport(User32Lib, SetLastError = false)]
    public static extern uint DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDpiScale scale);
}
