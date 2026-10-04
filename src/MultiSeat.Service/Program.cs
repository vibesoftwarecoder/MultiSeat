using MultiSeat.Service;
using MultiSeat.Service.Accounts;
using MultiSeat.Service.Api;
using MultiSeat.Service.Audio;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Diagnostics;
using MultiSeat.Service.Display;
using MultiSeat.Service.Emulators;
using MultiSeat.Service.Input;
using MultiSeat.Service.Monitoring;
using MultiSeat.Service.Sessions;
using MultiSeat.Service.Storage;
using MultiSeat.Service.Streaming;

// ── Click-dialog-by-PID helper mode ──────────────────────────────────
// Finds any visible top-level window owned by <pid> that contains a child
// button matching <buttonText>, and clicks it. Does not depend on window title.
// Usage: MultiSeat.Service.exe --click-dialog-pid <pid> "Button Text" [timeoutMs]
if (args.Length >= 3 && args[0] == "--click-dialog-pid" && int.TryParse(args[1], out var clickPid))
{
    var timeoutMs = args.Length >= 4 && int.TryParse(args[3], out var t) ? t : 8000;
    return MultiSeat.Service.Sessions.DialogClickHelper.RunByPid(clickPid, args[2], timeoutMs);
}

// ── Click-dialog helper mode ──────────────────────────────────────────
// Invoked by the service via CreateProcessAsUser in the console session.
// Polls for a window with the given title, finds the named button, and clicks it.
// Usage: MultiSeat.Service.exe --click-dialog "Window Title" "Button Text" [timeoutMs]
if (args.Length >= 3 && args[0] == "--click-dialog")
{
    var timeoutMs = args.Length == 4 && int.TryParse(args[3], out var t) ? t : 8000;
    return MultiSeat.Service.Sessions.DialogClickHelper.Run(args[1], args[2], timeoutMs);
}

// ── Mute-audio helper mode ────────────────────────────────────────────
// Invoked by the service via CreateProcessAsUser in the console session so
// that the Core Audio API sees Session 1's audio sessions (session-scoped).
// Usage: MultiSeat.Service.exe --mute-audio <pid> [timeoutMs]
// A process has no audio session until it first renders audio, which under AudioMode.PerSession
// happens well after mstsc launches — so 0 means one attempt, >0 polls until that deadline, and
// -1 watches for the target process's whole lifetime (what PerSession uses).
if (args.Length >= 2 && args[0] == "--mute-audio" && int.TryParse(args[1], out var pidToMute))
{
    var muteTimeoutMs = args.Length >= 3 && int.TryParse(args[2], out var t) ? t : 0;
    return AudioMuteHelper.MuteByPid(pidToMute, muteTimeoutMs) ? 0 : 1;
}

// ── Audio-peak reporting mode ─────────────────────────────────────────
// Reports which render endpoints are actually carrying audio, and from which process,
// by polling peak meters. This host is headless and reached over RustDesk, so "play a
// sound and listen" is not an available measurement — and RustDesk re-routes host audio,
// which would confound any listening test of audio routing. This is the objective
// substitute.
//
// Session-scoped like --mute-audio: run it INSIDE the session you want to measure
// (console session for host audio, a seat/RDP session for that session's audio).
// Usage: MultiSeat.Service.exe --audio-peaks [seconds]
if (args.Length >= 1 && args[0] == "--audio-peaks")
{
    var window = args.Length >= 2 && double.TryParse(args[1], out var secs) ? secs : 5.0;
    return AudioPeakHelper.ReportPeaks(window) ? 0 : 1;
}

// ── Loopback-capture helper mode ──────────────────────────────────────
// Records what is being played TO an output endpoint and reports the peak amplitude
// captured. --audio-peaks proves audio is FLOWING to an endpoint; this proves it can be
// CAPTURED FROM one, which is a different claim and the go/no-go gate for per-session
// audio (#10/#12): each seat would render to the private "Remote Audio" endpoint RDP
// creates in its session, with Apollo loopback-capturing that. On some Windows builds
// capture from it silently yields nothing.
//
// Session-scoped like --audio-peaks — "Remote Audio" exists only inside its own session.
// <device> is a friendly-name substring, a full endpoint id, or "default".
// Usage: MultiSeat.Service.exe --capture-loopback <device> [seconds] [out.wav]
if (args.Length >= 2 && args[0] == "--capture-loopback")
{
    var captureSeconds = args.Length >= 3 && double.TryParse(args[2], out var cs) ? cs : 10.0;
    var outPath = args.Length >= 4
        ? args[3]
        : Path.Combine(Path.GetTempPath(), "loopback-capture.wav");

    return AudioLoopbackCaptureHelper.CaptureLoopback(args[1], captureSeconds, outPath) ? 0 : 1;
}

// ── Hide-windows helper mode ──────────────────────────────────────────
// Invoked by the service via CreateProcessAsUser in the console session so that
// EnumWindows sees the console user's windows (window enumeration is per-desktop;
// Session 0 cannot see the console session's mstsc window). Hides the mstsc
// window that holds a seat's RDP session Active (GitHub issue #8).
// Usage: MultiSeat.Service.exe --hide-windows <pid>
if (args.Length == 2 && args[0] == "--hide-windows" && int.TryParse(args[1], out var pidToHide))
{
    return WindowHideHelper.HideByPid(pidToHide) ? 0 : 1;
}

// With a duration, stays resident and keeps the RDP client window hidden instead of hiding
// once: mstsc re-shows that window on connect, on reconnect, and when the session resolution
// changes, and a single hide leaves it covering the console user's screen (where closing it —
// the obvious response — disconnects the seat).
// Usage: MultiSeat.Service.exe --hide-windows <pid> <seconds | -1 for the process's lifetime>
if (args.Length == 3 && args[0] == "--hide-windows"
    && int.TryParse(args[1], out var pidToWatch) && int.TryParse(args[2], out var hideSeconds))
{
    return WindowHideHelper.WatchAndHide(pidToWatch, hideSeconds) ? 0 : 1;
}

// Started BEFORE mstsc, because starting after it is too late: mstsc shows its own window
// ~300ms in, faster than this helper can be spawned, so a PID-based watcher only ever catches
// a window that has already been on screen for about a second. Baselines the mstsc processes
// that already exist and only touches ones that appear afterwards.
// The timestamp is passed in rather than sampled here on purpose: this process takes a few
// hundred ms to boot, so anything it samples itself already includes the mstsc it is meant to
// adopt, and it would ignore it for the whole seat's life.
// Usage: MultiSeat.Service.exe --hide-windows-new <utcTicks> <seconds to wait for it to appear>
if (args.Length == 3 && args[0] == "--hide-windows-new"
    && long.TryParse(args[1], out var afterTicks) && int.TryParse(args[2], out var adoptSeconds))
{
    var startedAfter = new DateTime(afterTicks, DateTimeKind.Utc);
    return WindowHideHelper.WatchAndHideNew("mstsc", startedAfter, adoptSeconds) ? 0 : 1;
}

// ── Enum-displays helper mode ─────────────────────────────────────────
// Launched inside the console session via CreateProcessAsUser so that
// QueryDisplayConfig sees the real display topology (Session 0 has no displays).
// Usage: MultiSeat.Service.exe --enum-displays <output-json-file>
if (args.Length == 2 && args[0] == "--enum-displays")
{
    return MultiSeat.Service.Display.DisplayEnumeratorHelper.RunAndWriteToFile(args[1]);
}

// ── Advanced-colour (HDR) probe ───────────────────────────────────────
// Reports, per display target, what the session ADVERTISES as HDR-capable versus what is
// actually ACTIVE. Session-scoped like every display API, so run it inside the session being
// asked about — Session 0 sees no displays at all.
// Pass "enable" to first ASK Windows to turn Advanced Color on for the active targets and then
// re-read — the difference between "we asked and it refused" and "we never asked".
// "disable" undoes an enable that succeeded — the console control genuinely switches a display
// to 10-bit, so the probe must be reversible.
// Usage: MultiSeat.Service.exe --advanced-color <output-json-file> [enable|disable]
if (args.Length >= 2 && args[0] == "--advanced-color")
{
    bool? setState = args.Length >= 3
        ? args[2].Equals("enable", StringComparison.OrdinalIgnoreCase) ? true
        : args[2].Equals("disable", StringComparison.OrdinalIgnoreCase) ? false
        : null
        : null;
    return MultiSeat.Service.Display.AdvancedColorHelper.RunAndWriteToFile(args[1], setState);
}

// ── Set-default-capture helper mode ──────────────────────────────────
// Sets the Windows default audio capture (microphone) device for the current session.
// Usage: MultiSeat.Service.exe --set-default-capture <deviceId>
// Launch the keepalive mstsc on its own desktop, from INSIDE the console session (issue #18).
// Window stations are per-session, so the service in session 0 cannot create a desktop in the
// console session - this runs there and does it locally.
// Usage: MultiSeat.Service.exe --keepalive-mstsc <address> <pidFile>
if (args.Length == 3 && args[0] == "--keepalive-mstsc")
{
    return MultiSeat.Service.Sessions.KeepaliveDesktopHelper.Run(args[1], args[2]);
}

// -- Capture-endpoint inspection ---------------------------------------
// Lists the audio CAPTURE endpoints visible from THIS session, and whether the Steam Streaming
// Microphone that stream_mic depends on is among them. Endpoint enumeration is session-scoped,
// so this only means anything run inside a seat - session 0 sees nothing.
//
// It exists because --audio-peaks is render-only, and the claim that PerSession costs the
// microphone rests on capture-device visibility that has never actually been measured.
//
// Exit 0 = the device is visible and active here, 1 = it is not, 2 = enumeration failed and the
// run establishes nothing. Usage: MultiSeat.Service.exe --list-capture
if (args.Length >= 1 && args[0] == "--list-capture")
    return MultiSeat.Service.Diagnostics.CaptureEndpointInspector.Run();

if (args.Length == 2 && args[0] == "--set-default-capture")
{
    return MultiSeat.Service.Audio.AudioCaptureHelper.SetDefaultAudioDevice(args[1]) ? 0 : 1;
}

// ── Set-default-render helper mode ───────────────────────────────────
// Sets the Windows default audio render (output) device for the current session so
// games automatically output game audio to the assigned VAC device. Apollo
// loopback-captures that device for audio_sink streaming to Moonlight.
// Usage: MultiSeat.Service.exe --set-default-render <deviceId>
if (args.Length == 2 && args[0] == "--set-default-render")
{
    return MultiSeat.Service.Audio.AudioCaptureHelper.SetDefaultAudioDevice(args[1]) ? 0 : 1;
}

// ── Setup-display-isolation helper mode ──────────────────────────────
// Invoked inside a seat's RDP session after Apollo has created the SudoVDA display.
// Makes SudoVDA the primary display and shrinks the RDP display to 640×480 so
// TermService only encodes a tiny secondary display instead of full game content.
// The SudoVDA IddCx device path (SeatInfo.DisplayDevicePath / Apollo's output_name)
// MUST be passed — without it the helper has no safe way to disambiguate between
// multiple active SudoMaker monitor instances and refuses to run.
// Usage: MultiSeat.Service.exe --setup-display-isolation <sudovda-iddcx-path>
if (args.Length == 2 && args[0] == "--setup-display-isolation")
{
    return DisplayModeHelper.SetupDisplayIsolation(args[1]);
}

// ── Set-display-hz helper mode ────────────────────────────────────────
// Read-only: dump every display device the calling session can see, active or not.
// Usage: MultiSeat.Service.exe --list-session-displays [outputFile]
// A process launched into a seat session has nowhere to print, so pass a file.
if (args.Length >= 1 && args[0] == "--list-session-displays")
{
    return MultiSeat.Service.DisplayModeHelper.ListSessionDisplays(args.Length > 1 ? args[1] : null);
}

// 2-arg form: invoked inside a seat's RDP session — targets the session-primary display
// (Microsoft Remote Display Adapter) via ChangeDisplaySettingsEx(null, ...).
// Usage: MultiSeat.Service.exe --set-display-hz <hz>
if (args.Length == 2 && args[0] == "--set-display-hz" && int.TryParse(args[1], out var hz))
{
    return DisplayModeHelper.SetPrimaryDisplayHz(hz) ? 0 : 1;
}
// 3-arg form: invoked inside the console session — targets a specific GDI display device
// (e.g. \\.\DISPLAY5) so that IddCx Console displays (SudoVDA) can be reached from
// within WinSta0\Default where their display pipeline lives.
// Usage: MultiSeat.Service.exe --set-display-hz <deviceName> <hz>
if (args.Length == 3 && args[0] == "--set-display-hz" && int.TryParse(args[2], out var hz3))
{
    return DisplayModeHelper.SetDisplayHz(args[1], hz3) ? 0 : 1;
}

var builder = Host.CreateApplicationBuilder(args);

// ── Configuration ────────────────────────────────────────────────────
// Ensure we load config from the exe's directory, not the working directory
var exeDir = AppContext.BaseDirectory;
builder.Configuration.AddJsonFile(Path.Combine(exeDir, "appsettings.json"), optional: true, reloadOnChange: true);

// Host-local overrides, added LAST so they outrank everything above — including the shipped
// appsettings.json in this same folder.
//
// This exists because there was previously no durable way to make a host differ from the
// shipped defaults. Editing the deployed appsettings.json usually survives a deploy — the csproj
// marks it PreserveNewest, so publish leaves the newer host copy alone — but only until someone
// touches the repo's copy. The moment that file is newer, publish overwrites the host's settings
// silently. Measured: with a host edit in place, `touch` on the repo copy plus one deploy
// reverted it.
//
// Environment variables and appsettings.{Environment}.json are no escape either — the explicit
// AddJsonFile above is registered after Host.CreateApplicationBuilder's sources, so it outranks
// both. That left the deployed appsettings.json as the single effective source, and one that any
// config commit can wipe.
//
// appsettings.local.json is not in the repo and is gitignored, so publish never touches it and
// it cannot be committed by accident. Use it for anything true of THIS machine rather than of
// MultiSeat — e.g. pinning "AudioMode": "SharedHost" on a host that needs the microphone more
// than it needs its own audio to survive a seat provision.
builder.Configuration.AddJsonFile(Path.Combine(exeDir, "appsettings.local.json"), optional: true, reloadOnChange: true);
builder.Services.Configure<MultiSeatOptions>(builder.Configuration.GetSection(MultiSeatOptions.SectionName));

// ── Windows Service support ──────────────────────────────────────────
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "MultiSeatService";
});

// The same lifetime with power notifications, so a resume from sleep or hibernation re-checks the
// seats (issue #87). Registered after AddWindowsService so it is the IHostLifetime the host uses,
// and only when running as a service, like the lifetime it replaces.
builder.Services.AddSingleton<SeatReconcileRequests>();
if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
    builder.Services.AddSingleton<IHostLifetime, PowerAwareServiceLifetime>();

// ── Core services (singletons — one per host lifetime) ───────────────
builder.Services.AddSingleton<AccountManager>();
builder.Services.AddSingleton<SessionLauncher>();
builder.Services.AddSingleton<RdpWrapper>();
builder.Services.AddSingleton<ProcessInjector>();
builder.Services.AddSingleton<VirtualDisplayManager>();
builder.Services.AddSingleton<ApolloManager>();
builder.Services.AddSingleton<ApolloConfigBuilder>();
builder.Services.AddSingleton<OnConnectAppLauncher>();
builder.Services.AddSingleton<ClientResolutionFollower>();
builder.Services.AddSingleton<MultiSeat.Service.Monitoring.ApolloServerQuery>();
builder.Services.AddSingleton<MultiSeat.Service.Monitoring.HostApolloMonitor>();
builder.Services.AddSingleton<PortAllocator>();
builder.Services.AddSingleton<SeatLifecycleGate>();
// The DWM interval in effect now. Seeded from configuration once; after that the settings API
// changes it in place, and the seat manager reads it at every provision (issue #74).
builder.Services.AddSingleton(sp => new DwmFrameIntervalSetting(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MultiSeatOptions>>().Value.DwmFrameIntervalMs,
    sp.GetRequiredService<ILogger<DwmFrameIntervalSetting>>()));
builder.Services.AddSingleton<AudioDeviceEnumerator>();
builder.Services.AddSingleton<AudioRouter>();
builder.Services.AddSingleton<ControllerManager>();
builder.Services.AddSingleton<InputRouter>();
builder.Services.AddSingleton<InputHookManager>();
builder.Services.AddSingleton<HidHideConfigurator>();
builder.Services.AddSingleton<FirewallManager>();
builder.Services.AddSingleton<SeatPresetStore>();
builder.Services.AddSingleton<GpuMonitor>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddSingleton<SessionHealthCheck>();
builder.Services.AddSingleton<AutoStartProvisioner>();
builder.Services.AddSingleton<SeatReconciler>();

// Shared game library + emulator config seeders (register each seeder as IEmulatorConfigSeeder
// so SeatManager picks them up; add Dolphin/PCSX2 seeders here later with no other changes).
builder.Services.AddSingleton<SharedLibraryProvisioner>();
builder.Services.AddSingleton<IEmulatorConfigSeeder, RetroArchConfigSeeder>();

builder.Services.AddSingleton<SeatManager>();

// ── Background workers ──────────────────────────────────────────────
builder.Services.AddHostedService<MultiSeatWorker>();

// ── Embedded API server ──────────────────────────────────────────────
ApiServer.ConfigureServices(builder.Services, builder.Configuration);

var host = builder.Build();

// ── Log-filter inspection mode ────────────────────────────────────────
// Reports which levels actually reach each logging provider — above all the Windows Event
// Log, the only destination a service has. Unlike the helper modes at the top of this file
// this one runs AFTER Build(), on purpose: it must inspect the REAL host's configuration.
// Reconstructing an equivalent host here would be free to drift from the one that ships,
// which would make the diagnostic worse than useless. Building the host does not start it —
// no hosted service runs and no port is bound — so this is safe to run on a live machine.
//
// Exit code 0 = the service's Information logs reach the Event Log, 1 = they do not.
// Usage: MultiSeat.Service.exe --log-filters
if (args.Contains("--log-filters"))
    return MultiSeat.Service.Diagnostics.LogFilterInspector.Run(host.Services);

// -- HidHide inspection mode --------------------------------------------
// Reports what HidHide sees on THIS host and what a session jail would write, then exits.
// Every part of this feature has at some point been wrong while reporting success, so the
// habit is to ask the machine: MultiSeat.Service.exe --hidhide
//
// Runs after Build() for the same reason as --log-filters above: reporting on
// MultiSeat:ExcludedPhysicalPadDevicePaths (issue #92) needs the REAL configured host options,
// not a reconstruction of them — and that option is only meaningful read from the actual
// appsettings.json / appsettings.local.json this host has, the same file the service itself
// runs with. MULTISEAT_HIDHIDE_CLI still overrides the configured HidHideCliPath, for pointing
// this diagnostic at a different HidHideCLI.exe without editing config.
//
// Read-only (every call carries --cancel). Exit 0 = per-seat isolation could work here.
// Usage: MultiSeat.Service.exe --hidhide
if (args.Contains("--hidhide"))
{
    var hidHideOptions = host.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<MultiSeatOptions>>().Value;
    var hidHidePath = Environment.GetEnvironmentVariable("MULTISEAT_HIDHIDE_CLI")
        ?? hidHideOptions.HidHideCliPath;
    return HidHideInspector.Report(hidHidePath, hidHideOptions.ExcludedPhysicalPadDevicePaths);
}

// -- Configuration inspection mode -------------------------------------
// Says which binary is deployed and what it resolved each setting to, naming the file that
// won. Runs after Build() for the same reason as above: it must report the REAL host's
// configuration, not a reconstruction that is free to drift from it.
//
// The reporter of issue #18 set an option, pulled the commit, and saw no effect - because
// pulling source does not rebuild the service, and a binary that predates an option ignores
// it in silence. There was no way to tell that apart from the option being overridden.
//
// Exit code 0 = nothing here explains a setting failing to take effect, 1 = something does.
// Usage: MultiSeat.Service.exe --config
if (args.Contains("--config"))
    return MultiSeat.Service.Diagnostics.ConfigInspector.Run(host.Services, builder.Configuration);

await host.RunAsync();
return 0;
