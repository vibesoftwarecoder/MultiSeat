namespace MultiSeat.Service.Configuration;

public sealed class MultiSeatOptions
{
    public const string SectionName = "MultiSeat";

    // ── Seats ────────────────────────────────────────────────────────
    public int MaxSeats { get; set; } = 4;
    public int PortBase { get; set; } = Shared.Constants.PortBase;

    // ── Apollo / Sunshine ────────────────────────────────────────────
    public string ApolloExePath { get; set; } = Shared.Constants.DefaultApolloPath;
    public string ApolloConfigDir { get; set; } = Shared.Constants.DefaultApolloConfigDir;

    // NVENC quality preset: 1 (P1, lowest latency) → 7 (P7, highest quality).
    // P4 is balanced — good quality without perceptible encode latency.
    // Apollo default is 1; we raise it because the NVENC hardware handles P4 at full framerate.
    public int NvencPreset { get; set; } = 4;

    /// <summary>
    /// Apollo encoder used for every seat. Left at "nvenc" nothing changes:
    /// Apollo falls back on its own where NVENC is absent. Set it explicitly on
    /// hosts where that fallback misbehaves — on AMD the AMF probe runs at
    /// startup against the RDP surface a seat provides, and can hang there
    /// before Apollo ever opens its ports. Apollo's own values: nvenc,
    /// quicksync, amdvce, software.
    /// </summary>
    public string Encoder { get; set; } = "nvenc";

    /// <summary>
    /// Apollo's own log level for every seat. "info" is what a seat needs day
    /// to day; "debug" is the only way to see why a seat's Apollo refuses a
    /// pairing or a client, since its log is the sole window into a session
    /// nobody can watch. Apollo's values: verbose, debug, info, warning, error.
    /// </summary>
    public string ApolloLogLevel { get; set; } = "info";

    // ── API ──────────────────────────────────────────────────────────
    public int ApiPort { get; set; } = Shared.Constants.DefaultApiPort;
    public string ApiKey { get; set; } = string.Empty;  // set in appsettings or env

    // NOTE: there is deliberately no RequireHttps option. One existed, defaulted to true, and was
    // never read by anything — so the config asserted that the API required HTTPS while it was
    // served as plaintext HTTP on every interface, with the API key crossing the network in
    // clear. A setting that claims a security property it does not implement is worse than no
    // setting: it answers the question an operator would otherwise go and check.
    //
    // The API is HTTP only. ApiServer warns at startup when it is bound beyond loopback so the
    // exposure is stated rather than implied. Restrict access with a firewall rule, or set
    // ApiBindLoopbackOnly.
    public string[] CorsOrigins { get; set; } = [];

    /// <summary>
    /// Bind the API to loopback only, so the dashboard is reachable on the host itself and
    /// nowhere else. Default false, which preserves the historical behaviour of listening on
    /// every interface — turning it on is the right choice for any host whose dashboard is only
    /// ever opened locally or over a remote-desktop tool.
    /// </summary>
    public bool ApiBindLoopbackOnly { get; set; } = false;

    // ── Seat account privileges ──────────────────────────────────────
    /// <summary>
    /// Put seat accounts in the local Administrators group. Default false.
    ///
    /// MultiSeat used to do this unconditionally, justified by a comment saying SudoVDA IPC
    /// requires admin. That is not true, and both halves were checked before changing it:
    ///
    ///   - Apollo's SudoVDA client (third-party/sudovda/sudovda.h) opens the device with a plain
    ///     CreateFileA for GENERIC_READ | GENERIC_WRITE. There is no admin or elevation check
    ///     anywhere in it.
    ///   - The driver's own INF ships the device SDDL
    ///     D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;WD) — Everyone is granted exactly the
    ///     GENERIC_READ | GENERIC_WRITE the client asks for.
    ///
    /// Confirmed by experiment rather than by reading: a throwaway account in Users only, not in
    /// Administrators, opened the SudoVDA interface successfully (last error 0).
    ///
    /// Seats therefore get Users + Remote Desktop Users instead. The Remote Desktop Users part is
    /// not optional — administrators are implicitly allowed to log on over RDP, so an account that
    /// loses admin without gaining that membership cannot start a session at all.
    ///
    /// Turn this on only if something in a specific setup genuinely needs it (an installer or a
    /// game that demands elevation inside the seat). It hands every seat full control of the host,
    /// including the ability to reach the credential store, which is the point of keeping it off.
    /// </summary>
    public bool GrantSeatAdministrator { get; set; } = false;

    // ── Audio ────────────────────────────────────────────────────────
    // How seat game audio reaches Moonlight. See AudioMode for the trade-off.
    //
    // Default flipped to PerSession on 2026-08-19. SharedHost was the historical behaviour, but it
    // WEDGES THE HOST'S AUDIO on every seat provision — measured: the host's SWD\MMDEVAPI endpoint
    // nodes collapse from 27 to 1, host playback goes silent, and it stays that way after the seat
    // is gone until AudioEndpointBuilder is restarted. That is a fault every user hits every time
    // they start a seat, and it is what the "no audio after sleep, only a reboot fixes it" reports
    // were. PerSession does not do it (same test, nodes stayed at 27), and it also closes #10/#12.
    //
    // The cost is real and one-way: PerSession has NO microphone path. Set SharedHost explicitly if
    // the mic matters more than the host's audio, and see scripts\fix-host-audio.ps1 for the repair.
    public AudioMode AudioMode { get; set; } = AudioMode.PerSession;

    // ── Virtual Audio Cable ──────────────────────────────────────────
    // Only used when AudioMode = SharedHost. PerSession needs no virtual cables at all.
    public int VacCableCount { get; set; } = 4;  // number of VAC cables installed

    // ── HidHide ──────────────────────────────────────────────────────
    public string HidHideCliPath { get; set; } = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";

    // Per-seat gamepad isolation, via HidHide's undocumented session jail: a blacklist entry
    // suffixed with !<sessionId> is visible only inside that session (HidHide >= 1.4.181.0,
    // Logic.c:817, documented nowhere). See HidHideSessionJail and GitHub issue #19.
    //
    // Default OFF, and deliberately so. It writes into HidHide's persistent, kernel-side
    // blacklist, and on a host where a console-side Apollo also runs, its pad is
    // indistinguishable from a seat's - so the wrong pad can be confined to a seat while the
    // dashboard reports a healthy jail and a player's controller simply stops working.
    //
    // Turning it on REPLACES the old global-hide-plus-whitelist approach, which could not isolate
    // seats from each other even in principle: the whitelist is global, cannot pair an app with a
    // device, and every seat runs the same Apollo binary.
    public bool EnableHidHideCloaking { get; set; } = false;

    // Write a seat's jail rules BEFORE its Apollo creates a pad.
    //
    // Correctness, not optimisation. HidHide filters at open time, so a rule written afterwards is
    // late by definition - measured, a pad appeared at 13:27:33 and its rule landed at 13:27:39.9.
    // Inside that window dwm, explorer and GameInputSvc of EVERY session open the new pad and keep
    // handles that never expire, and releasing the rule later does not hand the pad back.
    //
    // A rule for an absent device matches nothing, so pre-writing is inert and free. It only does
    // anything for a seat whose pad path is known by evidence - see SeatPadDevicePaths.
    public bool EnablePadRulePreWrite { get; set; } = false;

    // After writing a jail rule, check from session 0 that it took effect.
    //
    // The driver requires sessionId != 0 for a jail to match, so this service can never be the
    // jailed session and must be refused a device that is genuinely confined. That makes it a live
    // probe of an undocumented behaviour a future HidHide release could drop without a word. Cheap
    // (one CreateFile), on by default, and it only reports.
    public bool VerifyHidHideJail { get; set; } = true;

    // Known pad device instance paths per seat account, e.g.
    //   "SeatPadDevicePaths": { "MultiSeat01": [ "USB\\VID_045E&PID_028E\\01" ] }
    //
    // This is the "identity" leg of attribution. Without it a seat's pad can only be picked out by
    // elimination, which is refused whenever more than one unconfined emulated pad exists - and
    // which is never remembered when it is used, because a placement made by elimination that gets
    // written down outlives the situation that produced it.
    //
    // These paths are not stable by nature: the last section of a ViGEm XUSB path is a serial that
    // ViGEmClient picks in USER MODE by sweeping free slots from 1, so where a seat's pad is born
    // is decided by whoever connects first. Pinning it properly needs a patched Apollo
    // (gamepad_serial_no / gamepad_vid / gamepad_pid), which is a fork of a fork and deliberately
    // not taken on yet.
    public Dictionary<string, string[]> SeatPadDevicePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Input Isolation ──────────────────────────────────────────────
    public string InputHookDllPath { get; set; } = @"MultiSeatInputHook.dll";

    // Keyboard/mouse session isolation via the InputHook DLL.
    // Default OFF — it is a no-op as architected: the low-level WH_KEYBOARD_LL/WH_MOUSE_LL
    // hooks are installed from the service process (Session 0), where GetForegroundWindow()
    // returns NULL, so ShouldPassThrough() always passes the event. There is also no
    // cross-session K/M bleed to prevent (physical input goes to the console session; Moonlight
    // input is SendInput'd inside the seat session). Re-enabling is only meaningful if the hook
    // is re-architected to run inside the seat session. See CLAUDE.md "Known Constraints".
    public bool EnableKeyboardMouseIsolation { get; set; } = false;
    public bool AutoAssignControllers { get; set; } = true;

    // ── Display ──────────────────────────────────────────────────────
    // Resize a seat to whatever resolution its Moonlight client asks for.
    //
    // Apollo's own dd_resolution_option = auto cannot do this: a seat streams its RDP session
    // surface, nothing inside the session can resize it (issue #15), and Apollo logs
    // "[1610] failed to set display mode!". Only mstsc sets that size, so following the client
    // means reconnecting the session — which preserves the Windows session and everything
    // running in it, but does briefly interrupt an active stream.
    //
    // Off by default for that reason, and because the trigger (a client connecting) could not
    // be exercised on the reference host, which is headless and has never had a Moonlight
    // client attached. The resize path itself IS verified: 1280x720 -> 1920x1080 on a live
    // seat, session id preserved.
    public bool FollowClientResolution { get; set; } = false;

    // Enable Windows Advanced Color (HDR) on virtual displays at seat creation.
    // Requires SudoVDA driver v0.5+ with HDR EDID support.
    // When enabled, Apollo will stream in HDR if the Moonlight client also supports it.
    //
    // ⚠️ Currently a NO-OP for a seat — nothing reads this to enable advanced colour, and no
    // seat has ever streamed HDR.
    //
    // An earlier version of this comment said HDR was impossible for a seat because the RDP
    // surface has no EDID. That was wrong, and measuring it is what showed so: inside a live seat
    // the active RDP target reports advancedColorSupported = TRUE with advancedColorEnabled =
    // false at 8 bits per channel. The capability is advertised; what does not follow is the
    // VidPN SOURCE mode, which stays SDR.
    //
    // Nonary (Vibepollo/Vibeshine) demonstrated HDR working inside a terminal session by forcing
    // Windows to rebuild that source mode — an FP16 shared-displayable primary, then
    // D3DKMTSetVidPnSourceOwner and D3DKMTSetDisplayMode with PreserveVidPn=FALSE — all user
    // mode. See issue #15. MultiSeat does not implement that yet, which is why this flag does
    // nothing rather than why HDR is impossible.
    //
    // To check a host: GET /api/seats/{id}/diagnostics/advanced-color, or run
    // MultiSeat.Service.exe --advanced-color <file> inside the session.
    public bool EnableHdr { get; set; } = false;

    // ── Controller emulation ─────────────────────────────────────────
    // When true, MultiSeat creates a ViGEm virtual Xbox 360 controller per seat
    // and routes a host-side physical XInput controller into the session.
    // When false (default), Apollo handles controller forwarding natively
    // from the Moonlight client (e.g. ROG Ally). Enabling this alongside
    // Apollo's built-in controller forwarding causes duplicate controllers.
    public bool EnableViGEmController { get; set; } = false;

    // ── Launch-on-connect apps ───────────────────────────────────────
    // Apps launched into a seat's session when a Moonlight client connects.
    // Empty by default (feature off). Use this INSTEAD of Windows autostart for
    // game launchers (Steam Big Picture, EmulationStation, RetroBat, …): launching
    // them after the client connects guarantees Apollo's virtual controller already
    // exists, so the launcher's startup controller scan detects it. Apps autostarted
    // at login run before any stream and never see the per-stream virtual pad.
    public LaunchOnConnectApp[] LaunchOnConnect { get; set; } = [];

    // Delay after the client-connect event before launching the apps, giving Apollo
    // a moment to create the virtual controller so the apps enumerate it at startup.
    public int LaunchOnConnectDelayMs { get; set; } = 4_000;

    // Kill the launched apps when the Moonlight client disconnects. When false,
    // the apps stay running and are reused on the next connect (no relaunch while
    // still alive). Single-instance launchers like Steam tolerate either setting.
    public bool KillLaunchOnConnectAppsOnDisconnect { get; set; } = false;

    // ── Timeouts ─────────────────────────────────────────────────────
    public int SessionConnectTimeoutMs { get; set; } = 15_000;
    public int ProcessLaunchTimeoutMs { get; set; } = 10_000;
    public int HealthCheckIntervalMs { get; set; } = 5_000;

    // ── Shared game library ──────────────────────────────────────────
    // Create a shared games/ROMs location all seat accounts can read/write, so a Steam game
    // installed by one seat's account is not re-downloaded by another owning account, and ROMs
    // live in one place. Creates {SharedGameLibraryDir}\SteamLibrary and \ROMs at startup and
    // grants BUILTIN\Users Modify. Point each seat's Steam at the SteamLibrary folder manually.
    public bool EnableSharedGameLibrary { get; set; } = true;
    public string SharedGameLibraryDir { get; set; } = @"C:\MultiSeatGames";

    // ── Emulator netplay ─────────────────────────────────────────────
    // Assign each seat a deterministic, collision-free netplay port from its own port block
    // (seat.PortBase + Constants.OffsetRetroArchNetplay) and open it in the firewall. Seats
    // netplay each other over loopback (127.0.0.1:<host-seat-port>).
    public bool EnableEmulatorNetplay { get; set; } = true;

    // Opt-in: seed each seat user's retroarch.cfg with its netplay port + the shared ROM dir.
    // Off by default because it writes into a user-profile / emulator config file.
    public bool SeedRetroArchNetplayConfig { get; set; } = false;

    // Override for the seat's RetroArch config path. Empty → auto-detect
    // C:\Users\{AccountName}\AppData\Roaming\RetroArch\retroarch.cfg.
    public string RetroArchConfigPath { get; set; } = string.Empty;

    // ── Rebuild ───────────────────────────────────────────────────────
    // Absolute path to the repo root. Required for the dashboard Rebuild button.
    // Example: C:\MultiSeat-Development
    public string SourceDir { get; set; } = string.Empty;
}

/// <summary>
/// Where a seat's game audio is rendered, and therefore what Apollo captures.
/// </summary>
public enum AudioMode
{
    /// <summary>
    /// Seats render onto the HOST's audio subsystem (RDP audiomode:i:1) and each seat gets a
    /// dedicated host-side virtual cable (VB-CABLE / VoiceMeeter) that Apollo names as its
    /// virtual_sink. Requires those cables installed — one per seat, which caps seats at 4.
    ///
    /// Known limitation, and the reason PerSession exists: every seat shares the host's single
    /// audio subsystem, so an active seat suspends the console session's own playback and its
    /// audio leaks onto the console's physical output (issues #10, #12). No amount of
    /// default-device juggling fixes that — there is one global default and one shared endpoint.
    ///
    /// Supports the Moonlight → game microphone path (stream_mic).
    /// </summary>
    SharedHost,

    /// <summary>
    /// Each seat keeps its audio INSIDE its own RDP session (audiomode:i:0). Windows gives every
    /// session a private "Remote Audio" render endpoint and makes it that session's default, so
    /// games play to it automatically and Apollo loopback-captures it from within the session.
    /// The host's physical devices are never a render target for any seat, which is what makes
    /// this isolation real rather than negotiated.
    ///
    /// Needs NO virtual audio cables — no VB-CABLE, no VoiceMeeter — and therefore has no
    /// 4-seat audio ceiling. The redirected stream still reaches the console-side mstsc, so
    /// SessionLauncher.MuteMstscAudio becomes load-bearing here: it is what stops seat audio
    /// playing out of the host's speakers.
    ///
    /// Two hard-won rules, both verified in the field before we shipped this:
    ///   - Do NOT name the endpoint. Writing it to audio_sink makes Apollo re-role it; writing
    ///     it to virtual_sink makes Apollo rewrite its wave format, which breaks the endpoint
    ///     for every loopback client including Apollo itself. Leave both keys unset and Apollo
    ///     simply takes the session default, which is already the endpoint we want.
    ///   - Client-side "Play audio on host PC" must be ON (the opposite of SharedHost). That is
    ///     safe here because the "host" of a redirected session IS the seat's own session.
    ///
    /// COST: no microphone. A session that keeps its own audio cannot see the host's Steam
    /// Streaming Microphone, so stream_mic is written disabled. Game audio out works; the
    /// Moonlight → game mic path does not. Installs that need the mic should stay on SharedHost.
    /// </summary>
    PerSession,
}

/// <summary>
/// One app to launch into a seat session when a Moonlight client connects.
/// Configured under MultiSeat:LaunchOnConnect in appsettings.json.
/// </summary>
public sealed class LaunchOnConnectApp
{
    /// <summary>Absolute path to the executable (e.g. Steam.exe).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Optional command-line arguments (e.g. "-bigpicture").</summary>
    public string? Arguments { get; set; }

    /// <summary>Optional working directory; null inherits the launcher default.</summary>
    public string? WorkingDirectory { get; set; }
}
