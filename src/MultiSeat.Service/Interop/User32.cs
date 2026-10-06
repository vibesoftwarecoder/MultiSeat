using System.Runtime.InteropServices;

namespace MultiSeat.Service.Interop;

/// <summary>
/// P/Invoke for user32.dll — display enumeration, window management.
/// </summary>
internal static class User32
{
    private const string Lib = "user32.dll";

    // ── EnumDisplayDevices ───────────────────────────────────────────
    // Session-aware: only sees displays attached to the calling session's desktop.
    // Do NOT use from Session 0 (SYSTEM) to enumerate console-session displays.

    public const uint DISPLAY_DEVICE_ACTIVE = 0x00000001;
    public const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    // ── Monitor enumeration ──────────────────────────────────────────
    // Works from Session 0 — enumerates monitors in the console session's desktop.

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;   // full monitor bounds
        public Rect rcWork;      // work area (excludes taskbar)
        public uint dwFlags;     // MONITORINFOF_PRIMARY = 1
        public const uint PRIMARY = 0x00000001;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [DllImport(Lib, SetLastError = true)]
    public static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport(Lib, SetLastError = true)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    // ── EnumDisplayDevices ───────────────────────────────────────────

    [DllImport(Lib, EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDevice lpDisplayDevice,
        uint dwFlags);

    // ── QueryDisplayConfig ───────────────────────────────────────────
    // System-level API: enumerates all active display paths regardless of session.
    // Works correctly from Session 0 (SYSTEM) service context.

    public const uint QDC_ALL_PATHS         = 0x00000001;  // all paths, active + inactive
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;  // only paths in the current active topology
    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;  // path.flags bit: path is active
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathSourceInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathTargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DisplayConfigRational refreshRate;
        public uint scanLineOrdering;
        public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo sourceInfo;
        public DisplayConfigPathTargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigModeInfo
    {
        public uint infoType;
        public uint id;
        public Luid adapterId;
        // union — 64 bytes wide (largest member is DISPLAYCONFIG_DESKTOP_IMAGE_INFO)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] modeInfo;
    }

    // Header shared by all DisplayConfigGetDeviceInfo request structs
    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigDeviceInfoHeader
    {
        public uint type;
        public uint size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName; // e.g. \\.\DISPLAY37
    }

    [DllImport(Lib, SetLastError = false)]
    public static extern uint GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport(Lib, SetLastError = false)]
    public static extern uint QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport(Lib, SetLastError = false)]
    public static extern uint DisplayConfigGetDeviceInfo(
        ref DisplayConfigTargetDeviceName deviceName);

    [DllImport(Lib, SetLastError = false)]
    public static extern uint DisplayConfigGetDeviceInfo(
        ref DisplayConfigSourceDeviceName deviceName);

    // ── Advanced Color (HDR) ─────────────────────────────────────────
    // DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO. Distinguishes what a target ADVERTISES from what
    // is actually ACTIVE, which is the whole question when asking whether a session could do
    // HDR: a target can report advanced colour supported while the source stays 8-bit SDR.
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigGetAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader header;

        /// <summary>
        /// Bitfield:
        ///   0 advancedColorSupported     — the target can do advanced colour (HDR-capable)
        ///   1 advancedColorEnabled       — advanced colour is currently ON
        ///   2 wideColorEnforced
        ///   3 advancedColorForceDisabled — policy/user turned it off
        /// </summary>
        public uint value;

        /// <summary>DISPLAYCONFIG_COLOR_ENCODING — 0 RGB, 1 YCbCr444, 2 YCbCr422, 3 YCbCr420, 4 Intensity.</summary>
        public uint colorEncoding;

        public uint bitsPerColorChannel;

        public bool AdvancedColorSupported => (value & 0x1) != 0;
        public bool AdvancedColorEnabled => (value & 0x2) != 0;
        public bool WideColorEnforced => (value & 0x4) != 0;
        public bool AdvancedColorForceDisabled => (value & 0x8) != 0;
    }

    [DllImport(Lib, SetLastError = false)]
    public static extern uint DisplayConfigGetDeviceInfo(
        ref DisplayConfigGetAdvancedColorInfo colorInfo);

    // ── ChangeDisplaySettingsEx ──────────────────────────────────────
    // Used to set the refresh rate and resolution of a virtual display.
    // Works from Session 0 against GDI device names (e.g. \\.\DISPLAY5).

    public const uint DM_POSITION         = 0x00000020;
    public const uint DM_BITSPERPEL       = 0x00040000;
    public const uint DM_PELSWIDTH        = 0x00080000;
    public const uint DM_PELSHEIGHT       = 0x00100000;
    public const uint DM_DISPLAYFREQUENCY = 0x00400000;

    public const uint CDS_UPDATEREGISTRY = 0x00000001;
    public const uint CDS_TEST           = 0x00000002;
    public const uint CDS_NORESET        = 0x10000000;
    public const uint CDS_SET_PRIMARY    = 0x00000010;

    public const uint ENUM_CURRENT_SETTINGS = 0xFFFFFFFF;

    public const int DISP_CHANGE_SUCCESSFUL = 0;
    public const int DISP_CHANGE_RESTART    = 1;
    public const int DISP_CHANGE_BADMODE    = -2;

    /// <summary>
    /// DEVMODEW — display settings structure.
    /// Only the fields needed for display mode changes are used;
    /// the rest exist to maintain the correct struct size (220 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;        // 64 bytes (32 WCHARs)
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint   dmFields;
        // union — display members: POINTL dmPosition + orientation + fixedOutput
        public int    dmPositionX;
        public int    dmPositionY;
        public uint   dmDisplayOrientation;
        public uint   dmDisplayFixedOutput;
        // common
        public short  dmColor;
        public short  dmDuplex;
        public short  dmYResolution;
        public short  dmTTOption;
        public short  dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;          // 64 bytes (32 WCHARs)
        public ushort dmLogPixels;
        public uint   dmBitsPerPel;
        public uint   dmPelsWidth;
        public uint   dmPelsHeight;
        public uint   dmDisplayFlags;      // union with dmNup
        public uint   dmDisplayFrequency;
        public uint   dmICMMethod;
        public uint   dmICMIntent;
        public uint   dmMediaType;
        public uint   dmDitherType;
        public uint   dmReserved1;
        public uint   dmReserved2;
        public uint   dmPanningWidth;
        public uint   dmPanningHeight;
    }

    [DllImport(Lib, EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettingsEx(
        string? lpszDeviceName,
        uint iModeNum,
        ref DEVMODE lpDevMode,
        uint dwFlags);

    [DllImport(Lib, EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName,
        ref DEVMODE lpDevMode,
        IntPtr hwnd,
        uint dwflags,
        IntPtr lParam);

    // Null-DEVMODE overload — used for the final "apply all" call (CDS_NORESET batch commit).
    [DllImport(Lib, EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsExApply(
        IntPtr lpszDeviceName,
        IntPtr lpDevMode,
        IntPtr hwnd,
        uint dwflags,
        IntPtr lParam);

    // ── SetDisplayConfig ─────────────────────────────────────────────
    // Kernel-level display topology API. Unlike ChangeDisplaySettingsEx,
    // SetDisplayConfig is NOT session-scoped — it works from Session 0
    // (SYSTEM service) and affects the global display topology immediately.
    // This is the correct API for changing display modes from a service.

    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    public const uint SDC_APPLY                        = 0x00000080;
    public const uint SDC_SAVE_TO_DATABASE             = 0x00000200;
    public const uint SDC_ALLOW_CHANGES                = 0x00000400;

    [DllImport(Lib, SetLastError = false)]
    public static extern uint SetDisplayConfig(
        uint numPathArrayElements,
        [In, Out] DisplayConfigPathInfo[]? pathArray,
        uint numModeInfoArrayElements,
        [In, Out] DisplayConfigModeInfo[]? modeInfoArray,
        uint flags);

    // ── DisplayConfigSetDeviceInfo (Advanced Color / HDR) ────────────
    // Enables Windows Advanced Color (HDR) on a specific display target.
    // Requires the adapterId + targetId from QueryDisplayConfig.

    // 10 per DISPLAYCONFIG_DEVICE_INFO_TYPE (…GET_ADVANCED_COLOR_INFO = 9, SET = 10).
    // This was 14 — wrong, and it had never been caught because nothing called it. A wrong
    // request type makes DisplayConfigSetDeviceInfo return 87 (ERROR_INVALID_PARAMETER), which
    // reads exactly like "Windows refused the request" and is not.
    public const uint DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigSetAdvancedColorState
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint value; // bit 0: 1 = enable Advanced Color (HDR), 0 = disable
    }

    [DllImport(Lib, SetLastError = false)]
    public static extern uint DisplayConfigSetDeviceInfo(
        ref DisplayConfigSetAdvancedColorState setPacket);

    // ── Window visibility ────────────────────────────────────────────

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport(Lib, SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport(Lib, SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport(Lib)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport(Lib)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>True when the window is minimized — i.e. already off-screen.</summary>
    [DllImport(Lib)]
    public static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// Window class name. Used to tell mstsc's RDP client window (which covers the screen and
    /// must stay hidden) from its dialogs (which must NOT be hidden — see WindowHideHelper).
    /// </summary>
    [DllImport(Lib, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    // ── Input desktop (issue #96) ─────────────────────────────────────
    // What Apollo itself calls before DXGI Desktop Duplication: OpenInputDesktop fails with
    // ERROR_ACCESS_DENIED (5) on a freshly-created seat session until the session is
    // disconnected and reconnected. See InputDesktopReadinessProbe.

    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    public const int UOI_NAME = 2;

    [DllImport(Lib, SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport(Lib, SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport(Lib, SetLastError = true)]
    public static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport(Lib, SetLastError = true)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport(Lib, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetUserObjectInformationW(
        IntPtr hObj, int nIndex, System.Text.StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}
