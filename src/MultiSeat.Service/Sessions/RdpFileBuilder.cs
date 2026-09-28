using MultiSeat.Service.Configuration;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Sessions;

/// <summary>
/// The geometry mstsc should connect a seat's session with.
///
/// This is the ONLY way a seat's desktop size gets set. A seat streams its RDP session
/// surface — Windows does not let a console-created virtual display join an RDP session's
/// topology, so there is no virtual display in the seat to resize (issue #15) — and that
/// surface's size is fixed by mstsc at connect time. From inside the session it cannot be
/// changed: <c>ChangeDisplaySettingsEx</c> returns <c>DISP_CHANGE_SUCCESSFUL</c> and does
/// nothing, which is what Apollo reports as <c>[1610] failed to set display mode!</c>.
///
/// Without these keys mstsc picks its own size, which tracks the CONSOLE desktop — so a seat
/// on a 3440x1440 host streamed 3440x1440 no matter what the dashboard said (issue #11, and
/// the symptom @darkjezza chased through #14).
/// </summary>
/// <param name="Width">Desktop width in physical pixels.</param>
/// <param name="Height">Desktop height in physical pixels.</param>
/// <param name="ScaleFactor">DPI scaling percentage — one of the values RDP accepts.</param>
/// <param name="ScaleSource">Where <paramref name="ScaleFactor"/> came from.</param>
public sealed record RdpGeometry(
    int Width, int Height, int ScaleFactor,
    ScaleFactorSource ScaleSource = ScaleFactorSource.Derived)
{
    /// <summary>Values <c>desktopscalefactor</c> accepts; anything else is ignored by mstsc.</summary>
    private static readonly int[] AllowedScaleFactors = [100, 125, 150, 175, 200, 250, 300, 400, 500];

    /// <summary>The scale factors a seat or the host default may be set to.</summary>
    public static IReadOnlyList<int> AllowedScales => AllowedScaleFactors;

    /// <summary>True when mstsc would honour this <c>desktopscalefactor</c>.</summary>
    public static bool IsAllowedScaleFactor(int scale) => AllowedScaleFactors.Contains(scale);

    /// <summary>
    /// The error text for a scale factor outside the allowed set. One wording for the API, the
    /// seat manager and the config check, so a caller always learns which values would work.
    /// </summary>
    public static string ScaleFactorError(int scale) =>
        $"Scale factor {scale} is not one RDP accepts. Use one of: " +
        $"{string.Join(", ", AllowedScaleFactors)} (percent).";

    public static RdpGeometry ForClient(int width, int height) =>
        new(width, height, DeriveScaleFactor(width));

    /// <summary>
    /// The geometry for a seat, with its scale picked in this order:
    ///
    ///   1. the seat's own override, when it has one;
    ///   2. the host-wide default (<c>MultiSeat:DefaultScaleFactor</c>), when that is set;
    ///   3. otherwise the width heuristic, <see cref="DeriveScaleFactor"/>.
    ///
    /// The overrides exist because the heuristic only knows the desktop's width in pixels, not
    /// how big the client's screen is. A tablet such as an iPad Pro renders its own UI at about
    /// 200%, so a 1920-wide seat derived at 100% looks tiny there (issue #70). Moonlight never
    /// reports the screen's physical size, so the user has to be able to say it.
    ///
    /// A value outside the allowed set is NOT clamped or rounded here — it throws. Callers
    /// validate first and report the problem; reaching this with a bad value is a bug.
    /// </summary>
    public static RdpGeometry ForSeat(int width, int height, int? seatScale, int? defaultScale)
    {
        if (seatScale is { } seat)
        {
            if (!IsAllowedScaleFactor(seat))
                throw new ArgumentException(ScaleFactorError(seat), nameof(seatScale));
            return new(width, height, seat, ScaleFactorSource.Seat);
        }

        if (defaultScale is { } host)
        {
            if (!IsAllowedScaleFactor(host))
                throw new ArgumentException(ScaleFactorError(host), nameof(defaultScale));
            return new(width, height, host, ScaleFactorSource.HostDefault);
        }

        return new(width, height, DeriveScaleFactor(width), ScaleFactorSource.Derived);
    }

    /// <summary>
    /// Pick a DPI scale for a desktop of this width.
    ///
    /// Needed because the seat's desktop is viewed on the client's screen at the client's size:
    /// connect a 3024-wide client and, unscaled, Windows renders 3024 logical pixels of UI and
    /// everything is microscopic. The thresholds mirror what Windows itself recommends for
    /// panels of each width, which is the closest thing to a correct answer available without
    /// knowing the client's physical screen size — something Moonlight never tells us.
    /// </summary>
    public static int DeriveScaleFactor(int width) => width switch
    {
        <= 1920 => 100,
        <= 2560 => 125,
        <= 3200 => 150,
        _       => 200,
    };

    /// <summary>True when this geometry is usable — mstsc silently ignores nonsense sizes.</summary>
    public bool IsValid =>
        Width >= 640 && Height >= 480 &&
        Width <= 8192 && Height <= 8192 &&
        AllowedScaleFactors.Contains(ScaleFactor);
}

/// <summary>
/// Builds the contents of <c>Default.rdp</c>.
///
/// It has to be <c>Default.rdp</c> in the console user's Documents, and it has to be shared:
/// mstsc treats that one file as trusted user settings, while ANY .rdp passed as an argument
/// raises the "Unknown publisher" warning — which is not suppressible on Windows 11
/// (signing the file and trusting the certificate were both tried and neither works). So a
/// per-seat file is not an option, and callers must write this immediately before launching
/// mstsc for that seat.
/// </summary>
public static class RdpFileBuilder
{
    public static string Build(AudioMode audioMode, RdpGeometry? geometry)
    {
        // audiomode decides where the seat's audio is rendered, and it is the single switch
        // that separates the two AudioModes:
        //
        //   i:1 (SharedHost) — play audio on the remote computer (host). Makes host audio
        //     devices (VB-CABLE, VoiceMeeter) visible inside the session via WASAPI so Apollo
        //     can loopback-capture a named virtual_sink. Side effect, and the reason
        //     PerSession exists: seats then share the host's audio subsystem, suspending the
        //     console's playback and leaking seat audio to the host's speakers (#10, #12).
        //
        //   i:0 (PerSession) — redirect to the client. Windows creates a PRIVATE "Remote Audio"
        //     render endpoint inside this session and makes it the session default; Apollo,
        //     running in-session, loopback-captures it. Host devices are invisible here — which
        //     is the point, not a limitation. The redirected stream still arrives at the
        //     console-side mstsc, so MuteMstscAudio is what keeps it off the host's speakers.
        //
        // NOTE (both modes): do NOT add audiocapturemode:i:1 here — it triggers a Windows
        // mic-access security dialog that DismissMstscSecurityDialog cannot catch, hanging the
        // RDP connection. Mic under SharedHost is handled via the VAC pair instead; PerSession
        // has no mic path at all (see AudioMode.PerSession).
        //
        // ── SharedHost's cost, measured 2026-08-19: i:1 WEDGES THE HOST'S AUDIO ──────────────
        //
        // Provisioning a seat under i:1 collapses the host's SWD\MMDEVAPI endpoint nodes from 27
        // to 1. Host audio goes silent — audio still enters CABLE In 16ch (app meter 0.08) but
        // nothing comes out (endpoint 0.00003, loopback 100% silent) — and it STAYS that way
        // after the seat is torn down. Repair is `scripts\fix-host-audio.ps1`, whose lever 1
        // restarts AudioEndpointBuilder and rebuilds the nodes. No reboot needed, despite years
        // of believing otherwise.
        //
        // i:2 ("do not play") was tried as a fix and REJECTED. It does avoid the wedge — a full
        // provision + teardown left all 27 nodes and healthy audio — but it also makes the host's
        // audio devices invisible inside the session, which is the one thing SharedHost needs:
        //
        //     endpoints enumerated inside the seat session, --audio-peaks
        //       i:1  → 14, including CABLE In 16ch   (and the host wedges)
        //       i:2  →  0                             (and the host is fine)
        //
        // So the mechanism that makes SharedHost work IS the mechanism that wedges the host, and
        // no audiomode value gives both. The escape is PerSession (i:0), which uses the session's
        // own Remote Audio endpoint and never touches the host's — at the cost of the microphone.
        var audio = audioMode == AudioMode.PerSession ? 0 : 1;

        var content =
            "authentication level:i:0\r\n" +
            "prompt for credentials:i:0\r\n" +
            $"audiomode:i:{audio}\r\n" +
            // The mstsc window is hidden — RDP stream quality has zero user-visible impact.
            // These settings minimize TermService encoding CPU on the host:
            //   session bpp:i:8      → 8-bit color (256 colors): 1/4 the pixel data vs 32-bit
            //   connection type:i:1  → modem quality: simplest RDP compression algorithm,
            //                          suppresses RemoteFX/H.264 RDP codec entirely
            //   disable wallpaper    → solid black background on the RDP display: nothing to encode
            //   disable themes/anims → no Aero rendering overhead on the RDP virtual display
            "session bpp:i:8\r\n" +
            "connection type:i:1\r\n" +
            "disable wallpaper:i:1\r\n" +
            "disable full window drag:i:1\r\n" +
            "disable menu anims:i:1\r\n" +
            "disable themes:i:1\r\n" +
            "allow font smoothing:i:0\r\n" +
            "allow desktop composition:i:0\r\n";

        if (geometry is null || !geometry.IsValid)
            return content;

        // Pin the session to exactly this size.
        //
        // smart sizing and dynamic resolution both let the SESSION's resolution follow the
        // mstsc WINDOW. That window is hidden and minimized here, so leaving either on lets a
        // window we deliberately never show dictate the resolution a player streams at. Off,
        // desktopwidth/desktopheight are authoritative and the surface is deterministic.
        // screen mode id:i:1 (windowed) is REQUIRED, and it was measured, not assumed. Full
        // screen — mstsc's default when the key is absent — sizes the session from the local
        // monitor and ignores desktopwidth/desktopheight entirely: with the key removed and
        // 1280x720 requested, the seat came up 1920x1080, the console's size. That is the very
        // mechanism this fix exists to defeat.
        //
        // The cost is that mstsc now has a real window it can show on the console desktop, where
        // it covers the screen of whoever is using the host. It is not enough to hide it once
        // after connecting: mstsc re-shows it later. SessionLauncher therefore starts a resident
        // watcher (--hide-windows <pid> -1) that keeps it hidden for the process's lifetime.
        content +=
            $"desktopwidth:i:{geometry.Width}\r\n" +
            $"desktopheight:i:{geometry.Height}\r\n" +
            $"desktopscalefactor:i:{geometry.ScaleFactor}\r\n" +
            "smart sizing:i:0\r\n" +
            "dynamic resolution:i:0\r\n" +
            "screen mode id:i:1\r\n";

        return content;
    }
}
