using System.Runtime.InteropServices;

namespace MultiSeat.Service.Input;

/// <summary>
/// The undocumented HidHide "session jail".
///
/// Append <c>!&lt;sessionId&gt;</c> to a device instance path in HidHide's ordinary, persistent
/// blacklist and the device becomes visible ONLY in that WTS session:
///
/// <code>
/// USB\VID_045E&amp;PID_028E\01!2                       XUSB node -> visible only in session 2
/// HID\VID_045E&amp;PID_028E&amp;IG_00\3&amp;130C1E12&amp;0&amp;0000!2  HID node  -> visible only in session 2
/// </code>
///
/// Shipped in HidHide v1.4.181.0 (2023-10-31, commit 3934d9a "Add WTS session jail support",
/// contributed by DuoStream's author) and present in no README, no CLI help and no release note.
/// <c>HidHide/src/Logic.c:817</c> is the entire decision, and it is byte-identical in v1.5.230.0:
///
/// <code>
/// return (sessionId != 0 &amp;&amp; jailSessionId != 0 &amp;&amp; sessionId == jailSessionId ? FALSE : TRUE);
/// </code>
///
/// Two consequences fall straight out of that line and both are load-bearing:
///
///   * <b>Session 0 never matches</b>, so a service running there can use "can I still open this
///     device?" as a live probe that a rule took effect — see <see cref="Probe"/>. Worth having
///     for a feature that is undocumented and could be dropped by a future release without a word.
///   * On a session match <c>Blacklisted()</c> returns FALSE, so the <b>whitelist is never
///     consulted</b> for a confined device. That removes the whitelist's global-hole problem for
///     jailed pads — but only while the whitelist stays empty of foreign entries, because one
///     whitelisted application sees EVERY confined pad.
///
/// Reported with a week of measurements by @jmlopezdona in issue #19, and verified here against
/// the HidHide source before any of it was believed.
/// </summary>
public static class HidHideSessionJail
{
    /// <summary>
    /// The jail suffix. Session 0 is refused by the driver itself, so asking for it would write a
    /// rule that silently behaves as a plain global hide — the pad would vanish from every
    /// session including the seat's, which is a far louder failure than it looks in a log.
    /// </summary>
    public static string Confine(string deviceInstancePath, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceInstancePath);
        if (sessionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionId),
                "The HidHide session jail requires sessionId != 0; session 0 never matches, so a " +
                "rule written for it hides the device everywhere instead of confining it.");

        return $"{deviceInstancePath}!{sessionId}";
    }

    /// <summary>
    /// Every rule needed to confine one pad to one session: the HID node and the XUSB node.
    /// Hiding only the HID node leaves the pad fully visible to XInput.
    /// </summary>
    public static List<string> ConfineAll(HidHideDevice device, int sessionId) =>
        device.Nodes.Select(node => Confine(node, sessionId)).ToList();

    /// <summary>
    /// Splits a blacklist entry back into its path and jail session, so a rule read from HidHide
    /// can be attributed. Returns null for the session when the entry is a plain global hide.
    /// </summary>
    public static (string Path, int? SessionId) Split(string blacklistEntry)
    {
        var bang = blacklistEntry.LastIndexOf('!');
        if (bang <= 0 || bang == blacklistEntry.Length - 1)
            return (blacklistEntry, null);

        return int.TryParse(blacklistEntry[(bang + 1)..], out var session)
            ? (blacklistEntry[..bang], session)
            : (blacklistEntry, null);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  OWNERSHIP — derived, never configured, and never by name
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Is this pad emulated (ViGEm and friends) rather than a physical controller?
    ///
    /// ⚠️ <b>The word "emulated" appears nowhere in the device tree.</b> A ViGEm pad's XUSB node
    /// is called "Xbox 360 Controller for Windows" and its instance path starts with <c>USB\</c>,
    /// exactly like a real one — measured on this host. @jmlopezdona's implementation looked for
    /// the vendor name, read both seats' emulated pads as physical, confined them to a session
    /// neither seat was in, and <b>reported success</b>.
    ///
    /// The real test is the PARENT: a physical pad hangs off a hardware bus (<c>USB\</c>,
    /// <c>BTHENUM\</c>, <c>PCI\</c>), an emulated one off a PnP root enumerator (<c>ROOT\</c>).
    /// The reference host's live pad reports parent <c>ROOT\VIGEMBUS\0002</c>; his reported
    /// <c>ROOT\SYSTEM\0001</c>. Neither says "ViGEm" anywhere, and the test does not care.
    /// </summary>
    public static bool IsEmulated(string deviceInstancePath)
    {
        var parent = GetParentInstanceId(deviceInstancePath);
        return parent is not null && parent.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Is this PAD emulated? Ask every node, not just the obvious one.
    ///
    /// ⚠️ Measured on the reference host, and it is not intuitive: the HID node's parent is the
    /// USB composite interface
    /// (<c>USB\VID_045E&amp;PID_028E&amp;IG_00\2&amp;DEE0F28&amp;0&amp;00</c>), which looks exactly
    /// like a physical controller. Only the XUSB node's parent reveals the software bus
    /// (<c>ROOT\VIGEMBUS\0002</c>). Testing the HID node alone therefore reports an emulated
    /// pad as physical — which is the reading that leaves seats sharing one pad while the code
    /// believes it politely left a real controller alone.
    /// </summary>
    public static bool IsEmulatedPad(HidHideDevice device) => device.Nodes.Any(IsEmulated);

    /// <summary>
    /// The PnP parent's instance id, or null when the device cannot be located (an absent node,
    /// typically). Null is deliberately NOT treated as emulated by <see cref="IsEmulated"/> —
    /// guessing here confines a pad to a session on no evidence, which is the failure mode that
    /// cost issue #19's reporter the most.
    /// </summary>
    public static string? GetParentInstanceId(string deviceInstancePath)
    {
        if (CM_Locate_DevNodeW(out var devInst, deviceInstancePath, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
            return null;

        if (CM_Get_Parent(out var parentInst, devInst, 0) != CR_SUCCESS)
            return null;

        if (CM_Get_Device_ID_Size(out var size, parentInst, 0) != CR_SUCCESS || size == 0)
            return null;

        var buffer = new char[size + 1];
        if (CM_Get_Device_IDW(parentInst, buffer, (uint)buffer.Length, 0) != CR_SUCCESS)
            return null;

        return new string(buffer).TrimEnd('\0');
    }

    /// <summary>
    /// What a session-0 probe of a device found.
    /// </summary>
    public enum JailProbe
    {
        /// <summary>The device opened. Whatever is on paper, the confinement is not in effect.</summary>
        Open,

        /// <summary>
        /// The open was <b>refused</b>. Session 0 can never match a jail, so this is what a live
        /// rule looks like from outside it.
        /// </summary>
        Confined,

        /// <summary>
        /// The open failed, but nothing refused it — most often the device is simply not there.
        /// Nothing was proved either way.
        ///
        /// <para>Kept apart from <see cref="Confined"/> deliberately: see <see cref="Probe"/>.</para>
        /// </summary>
        Unreachable,
    }

    /// <summary>One probe: the verdict, and the Win32 error that produced it (0 when it opened).</summary>
    public readonly record struct JailProbeResult(JailProbe Verdict, int Error);

    /// <summary>
    /// Ask, from this process's session, whether the device can still be opened.
    ///
    /// Run from the service (session 0) this is a live check that a jail rule really took effect:
    /// session 0 never matches the jail, so a confined device must become unopenable here while
    /// staying open inside its seat.
    ///
    /// ⚠️ <b>Which failure it was, is the whole answer.</b> A jail refuses the open with
    /// <c>ERROR_ACCESS_DENIED</c> (5) and nothing else. An absent device — or a malformed path —
    /// fails with <c>ERROR_FILE_NOT_FOUND</c> (2). Reading only "did the handle come back invalid"
    /// turns both into "the jail is holding", so <b>a rule that matches nothing verifies as a rule
    /// being enforced</b>: the exact silent success this probe exists to prevent, sitting inside
    /// the probe.
    ///
    /// That distinction is also a cheap version of the "open an ordinary file first" control: a
    /// probe that has broken and can open nothing returns 2, not 5, so it cannot fake a confined
    /// device. It is the weaker of the two checks — it only catches breakage in the path rather
    /// than in the process — but it costs one integer.
    ///
    /// Note what a <see cref="JailProbe.Confined"/> does and does not prove. It says session 0 is
    /// being refused, which is what a working rule looks like from here — it does NOT prove the
    /// seat can still see the pad. A plain global hide (or a rule whose suffix was stripped)
    /// produces the same refusal.
    /// </summary>
    public static JailProbeResult Probe(string symbolicLink)
    {
        // An empty path is not a refusal. Returning "confined" for one would report a jail on a
        // device that was never named.
        if (string.IsNullOrWhiteSpace(symbolicLink))
            return new JailProbeResult(JailProbe.Unreachable, ERROR_FILE_NOT_FOUND);

        using var handle = CreateFileW(
            symbolicLink,
            0,                       // no access requested: this asks "may I open it", not "give me it"
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        // Immediately, and before anything else runs: the next managed call is free to overwrite
        // the thread's last error.
        var error = Marshal.GetLastWin32Error();

        if (!handle.IsInvalid) return new JailProbeResult(JailProbe.Open, 0);

        return new JailProbeResult(
            error == ERROR_ACCESS_DENIED ? JailProbe.Confined : JailProbe.Unreachable,
            error);
    }

    // ── interop ──────────────────────────────────────────────────────
    private const uint CR_SUCCESS = 0;
    private const uint CM_LOCATE_DEVNODE_PHANTOM = 1;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_ACCESS_DENIED = 5;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint dnDevInst, char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
