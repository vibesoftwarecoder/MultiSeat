# MultiSeat — Codebase Guide

MultiSeat runs multiple simultaneous Moonlight game-streaming sessions on one Windows host. Each seat gets an isolated Windows account, virtual display (SudoVDA), virtual audio device, and a dedicated Apollo streaming instance, managed from a web dashboard.

## Stack

- **Backend:** .NET 9 / ASP.NET Core Windows Service (`src/MultiSeat.Service`) — runs as SYSTEM
- **Frontend:** React + TypeScript dashboard (`src/MultiSeat.Dashboard`) — Vite build, served on port 9550
- **Shared:** `src/MultiSeat.Shared` — constants, port layout, shared types
- **Tests:** `src/MultiSeat.Tests`
- **InputHook DLL:** `src/MultiSeat.InputHook` — C++/CMake, keyboard/mouse session isolation
- **Solution:** `src/MultiSeat.slnx`

## Key Service Components

| Class | Responsibility |
|---|---|
| `SeatManager` | Seat lifecycle — provision / teardown |
| `SessionLauncher` | RDP loopback session creation, mstsc window management |
| `ApolloManager` | Per-seat Apollo process management |
| `VirtualDisplayManager` | SudoVDA virtual display attach/detach |
| `AudioRouter` | Virtual audio device assignment per seat |
| `InputRouter` | XInput/ViGEm controller routing |
| `HidHideConfigurator` | Per-seat gamepad isolation via HidHide's session jail (off by default) |
| `InputHookManager` | Keyboard/mouse session isolation (InputHook DLL) |
| `AccountManager` | Windows local account CRUD |
| `ApiServer` | ASP.NET Core HTTP API + WebSocket |

## Port Layout

Each seat reserves a block of 30 ports: `PortBase + (seat_index × 30)`. Default `PortBase = 48100`.
- Seat 0 → 48100–48129, Seat 1 → 48130–48159, etc.
- Apollo's per-seat offsets span `-5` (GFE HTTPS) to `+26` (RTSP) around the base.
- The base sits **above** a stock Apollo's block (~47979–48010, centered on the Moonlight default 47984) so MultiSeat coexists with a standalone Apollo — see "Coexistence with a standalone Apollo" below.

## Audio: two modes (`MultiSeat:AudioMode`)

| | `SharedHost` | `PerSession` **(default)** |
|---|---|---|
| RDP | `audiomode:i:1` — play on host | `audiomode:i:0` — play on client |
| Endpoint | a host virtual cable per seat | the session's own **Remote Audio** (created by Windows) |
| Apollo config | `virtual_sink = <cable>` | **no sink named at all** |
| Prereqs | VB-CABLE + VoiceMeeter | **none** |
| Seat cap from audio | 4 (one cable each) | none |
| Microphone | works (`stream_mic`) | **not available** |
| Client "Play audio on host PC" | **off** | **on** |

`PerSession` is what actually fixes #10 and #12: seats stop sharing the host's audio subsystem, so an
active seat no longer suspends the console's playback or leaks onto its speakers. Two rules that are
not optional (both learned the hard way in issue #15):

- **Never name the endpoint.** `audio_sink` makes Apollo re-role it; `virtual_sink` makes Apollo
  rewrite its wave format, breaking loopback for every client including Apollo. Leave both unset —
  Apollo takes the session default, which is already the endpoint you want.
- **`SessionLauncher.MuteMstscAudio` is load-bearing here**, not a safety net: the redirected audio
  reaches the hidden console-side `mstsc`, and muting it is what keeps seat audio out of the host's
  speakers. Verify with `MultiSeat.Service.exe --audio-peaks` in the console session — the `mstsc`
  APP line must read `0.000000` while a seat streams.

The default is `PerSession` as of 2026-08-19. `SharedHost` was the historical default, but it
**wedges the host's audio on every seat provision** - measured, the host's `SWD\MMDEVAPI` endpoint
nodes collapse from 27 to 1 and playback stays silent after the seat is gone until
`AudioEndpointBuilder` is restarted (`scripts\fix-host-audio.ps1` lever 1). That is what the
"no audio after sleep, only a reboot fixes it" reports were. `PerSession` does not do it.
**Choosing `SharedHost` explicitly is still supported and is the only way to get the microphone** -
accept the wedge and keep the repair script handy. See `docs/design/per-session-audio.md`.

## Audio Device Layout (`SharedHost` mode only)

- Seat 0 → VB-CABLE basic "CABLE Input"
- Seat 1 → VoiceMeeter "VoiceMeeter Input"
- Seat 2 → VoiceMeeter "VoiceMeeter Aux Input"
- Seat 3 → VoiceMeeter "VoiceMeeter VAIO3 Input"

VoiceMeeter must be running — `AudioRouter.EnsureVoiceMeeterRunning` starts it in the console session
when a seat needs one of its devices. It looks under `VB\Voicemeeter` in **both** Program Files trees
(the installer uses the 32-bit one) and prefers **Potato**, since seat 3's VAIO3 device exists only
there. It is **not** registered for auto-start on the reference host — despite what this line used to
claim — so between boot and the first seat provision there is no VoiceMeeter process.

## Streaming behavior — resolution, audio, controller

Reflects fixes shipped 2026-07-24 (GitHub issues #11 / #10 / #9a):

- **Resolution follows the Moonlight client.** Each per-seat `sunshine.conf` sets Apollo's display-device keys — `dd_configuration_option = ensure_active`, `dd_resolution_option = auto`, `dd_refresh_rate_option = auto` (`ApolloConfigBuilder`). Apollo resizes the SudoVDA display to the mode the client requests on connect. The dashboard resolution is the SudoVDA creation/advertised default, **not** authoritative. **Requires the client's "Optimize game settings" (SOPS) enabled** — otherwise Apollo leaves the mode unchanged. Without the `dd_*` keys, `dd_configuration_option` defaults to `disabled` and Apollo never resizes the virtual display (it stays at the host/RDP surface size).
- **Seat audio does not hijack the host default.** The seat's virtual audio device is written as Apollo's `virtual_sink` (not `audio_sink`) with `keep_sink_default = disabled` + `auto_capture_sink = disabled`, and MultiSeat no longer runs `--set-default-render`. Windows has a **single machine-wide default output** shared by the console + all seats; Apollo still points the game at the sink during an active stream and restores the previous default afterward, without re-asserting it. Keep the client's "Play audio on host PC" **off** so `virtual_sink` is used. Not full isolation — an active seat still suspends the console's playback and leaks onto its speakers (#12). **Real isolation is `AudioMode = PerSession`** (see "Audio: two modes" above); it arrived via RDP per-session endpoints, not the per-app routing (`IAudioPolicyConfigFactory::SetPersistedDefaultAudioEndpoint`) this note used to predict.
- **Controller forwarding is native by default.** `EnableViGEmController` defaults **off** — Apollo forwards the Moonlight client's controller into the seat itself and MultiSeat creates no ViGEm pad. The dashboard shows the seat's Controller service as **"Native"** (not a down light) and the Input tab notes that XInput→seat assignment only applies when `EnableViGEmController` is on. `SeatServices.ControllerManaged` + `GET /api/input/mode` surface the mode.

## Per-seat file permissions — a seat cannot write what the SERVICE created

A seat is a standard user (S4). Under `ProgramData` the inherited ACL lets it **create** files and
makes it their owner, but gives it only read-and-execute on files **MultiSeat** created. So:

- **Apollo's own creations are fine** — `config/credentials/cakey.pem` and friends. This is why
  `pkey`/`cert` point at the seat's dir rather than Program Files, which a seat cannot write at all.
- **Files MultiSeat seeds need an explicit grant.** `sunshine_state.json` (rewritten on **every
  pairing**) and `apps.json` get one ACE for that seat's SID, machine-qualified so a domain account
  of the same name can't be hit instead.
- ⛔ **`shared_credentials.json` is deliberately NOT granted** — it is the web UI login shared by
  every seat, so one seat writing it changes the login for all of them.

⚠️ **This bit us for a fortnight and nothing logged it.** Making seats standard users meant Apollo
could not write `sunshine_state.json`; its save failed silently, the reload restored the old file,
and every client a seat paired was forgotten on reconnect. Found in the field (PR #21), not here.
The failure shape to remember: **pairing succeeds, then the next request from the same client is
refused.** Details and the still-open TLS-key exposure: `docs/security-posture.md`.

## Security posture — `docs/security-posture.md`

Read it before changing anything about accounts, the API, or the install scripts. It records what
MultiSeat changes about a host, what it deliberately does not protect against, and how to undo it.

The parts most likely to surprise you:

- **The install scripts weaken RDP machine-wide and the changes outlive an uninstall.** NLA is
  turned **off** on the `RDP-Tcp` listener — for every client, not just loopback, because the
  setting is not per-target and the seat logon cannot answer NLA's prompt. mstsc's certificate and
  unsigned-`.rdp` warnings are suppressed by **machine policy**, affecting every user on the host
  and every server they connect to. The mitigation is to keep 3389 off the network; MultiSeat needs
  no inbound rule, since loopback is not filtered.
- **Seat accounts are standard users**, not administrators (`Users` + `Remote Desktop Users`). The
  Remote Desktop Users membership is load-bearing: admins are implicitly allowed to log on over
  RDP, so without it a non-admin seat cannot start a session at all. The old belief that SudoVDA
  IPC needs admin is **false** — the driver's INF grants Everyone the access Apollo's client asks
  for. `MultiSeat:GrantSeatAdministrator` opts back in.
- **An administrator on this host can still read everything**, including seat passwords — admins
  can become SYSTEM. The file ACLs and DPAPI SYSTEM scope stop non-admin users and copies taken off
  the machine, not a local administrator.
- **`AllowAnonymous()` grants nothing** in this API — there is no `UseAuthorization()` in the
  pipeline. `ApiServer.IsAlwaysPublic` is the entire rule, and it exempts only **GET**
  `/api/system/auth`; POST on that path disables authentication and stays gated.

## Install / Deploy

Two separate scripts — prereqs and service deploy are intentionally split:

```powershell
# Step 1: Install all prerequisites (drivers, audio devices, RDPWrap, etc.)
.\prerequisites\install-prerequisites.ps1
# Reboot if prompted, then re-run to confirm clean.
# Log: prerequisites\prereq.log

# VB-CABLE + VoiceMeeter are installed ONLY for SharedHost audio. With no switch the script
# reads the deployed appsettings.local.json / appsettings.json and falls back to PerSession,
# which skips them - and skips the reboot VoiceMeeter forces. Force either way with:
.\prerequisites\install-prerequisites.ps1 -AudioMode SharedHost

# Step 2: Build and deploy the MultiSeat service (run from scripts\)
.\scripts\install-service.ps1

# ...or install a release asset instead of building. Self-contained, so this path needs NO
# .NET SDK, NO .NET runtime and NO Node on the target - the point of issue #32. Everything
# after the deploy step (RDP setup, certs, service registration) is identical either way.
.\scripts\install-service.ps1 -FromZip .\multiseat-windows-x64.zip

# Remove the service
.\scripts\install-service.ps1 -Uninstall
```

⚠️ **The shipped version comes from `version.txt` at the repo root**, read into `<Version>` by the
csproj. `.github/workflows/release.yml` refuses to publish when the git tag and that file disagree,
so `v0.5.2` requires `version.txt` to say `0.5.2`. Before 2026-09-09 there was no version file and
releases tagged `v0.5.1` shipped an assembly reporting `1.0.0`.

### RDPWrap offsets — the installer now verifies, it does not assume

RDPWrap patches `termsrv.dll` at byte offsets looked up in `rdpwrap.ini` **by DLL version**. A
Windows update moves them, the ini stops matching, and multi-session RDP dies — which takes every
seat with it, because a seat **is** an RDP session.

The prereq script now checks coverage after copying the ini and escalates only as far as needed:

1. the ini covers the running build → nothing to do
2. it does not → **re-download the community ini** (`sebaxakerhtc/rdpwrap.ini`) and refresh the cache
3. the community has not caught up either → **generate the offsets locally** from this machine's
   `termsrv.dll`, via `llccd/RDPWrapOffsetFinder` (MIT), after backing the ini up

Community offsets are preferred wherever both exist — far more hosts have exercised them.
Generation is a last resort, not the default. To ask about it without running the installer:

```powershell
.\scripts\check-rdpwrap-offsets.ps1            # read-only: 0 covered, 1 not, 2 cannot tell
.\scripts\check-rdpwrap-offsets.ps1 -Apply     # generate + merge + restart TermService
```

⚠️ **`termsrv.dll` reports two different versions and only one of them is right.** Its
StringFileInfo and its `VS_FIXEDFILEINFO` disagree — measured here: the string said
`10.0.26100.8115` while the raw said `10.0.26100.8972`, and **RDPWrap keys on the raw one**.
`.VersionInfo.FileVersion` returns the string, so checking it can report "covered" off a section
that is not the one in play. Read `FileVersionRaw`. Coverage also needs **both** `[version]` and
`[version-SLInit]`: a half-present pair is worse than none, because RDPWrap patches with what it
finds.

⛔ **Why this check exists at all.** `Get-Prerequisite` caches downloads by filename forever, which
is right for an installer and wrong for a file whose job is to track Windows builds. The cached
`rdpwrap.ini` on the reference host was from April, did not cover the running build, and was being
copied over a good one on **every** run — so "re-run the prereq script to refresh `rdpwrap.ini`"
had been doing the opposite for months, silently, because nothing checked afterwards.

## Key Runtime Paths

| Path | Purpose |
|---|---|
| `C:\Program Files\MultiSeat\` | Service install dir |
| `C:\Program Files\ApolloVibe\` | MultiSeat's own Apollo install (separate from any standalone `C:\Program Files\Apollo\`) |
| `C:\ProgramData\MultiSeat\` | Runtime data, Apollo configs, logs |
| `C:\ProgramData\MultiSeat\logs\` | `audio-helper.log` only — **not** the service log (see below) |
| `C:\ProgramData\MultiSeat\apollo\` | Per-seat Apollo config dirs (incl. per-seat `apollo.log`) |

### Host-local config: `appsettings.local.json`

Put anything true of **this machine** rather than of MultiSeat in
`C:\Program Files\MultiSeat\appsettings.local.json`. `Program.cs` loads it **last**, so it outranks
the shipped `appsettings.json` in the same folder; it is gitignored and absent from the repo, so
`dotnet publish` never overwrites it.

⚠️ **"A deploy cannot overwrite it" was only ever true of `dotnet publish`.** Until `98b5cfa`
(2026-09-10) `install-service.ps1 -FromZip` **wiped the entire install directory** —
`Get-ChildItem $InstallDir | Remove-Item -Recurse -Force` — before copying the release payload in,
so an upgrade deleted `appsettings.local.json` *and* `appsettings.json` and left the shipped
defaults behind, with no backup and no warning. The advice above was correct-sounding and did not
hold on the path people actually upgrade with. See issue #45.

Since `98b5cfa` both files are copied out before that wipe and copied back **byte for byte**
afterwards, the host's `appsettings.json` wins over the shipped one, and a timestamped copy is kept
in `C:\ProgramData\MultiSeat\config-backups\`. Settings a release adds that the host's file lacks
are listed during the install rather than silently applied — keeping the host's file is right, but
hiding a new option forever is not. ⛔ Anyone still on `0.6.3` or earlier has the destructive
installer; this protects the upgrade *after* the one that carries it.

```jsonc
{ "MultiSeat": { "AudioMode": "PerSession" } }
```

This exists because editing the deployed `appsettings.json` is **durable right up until it isn't**.
The csproj marks it `PreserveNewest`, so `dotnet publish` leaves a newer host copy alone — but the
moment anyone touches the repo's copy, the next deploy silently overwrites the host's settings.
Verified both halves: five deploys left a host edit intact, then one `touch` on the repo file
reverted it. Environment variables and `appsettings.{Environment}.json` are no escape either — the
explicit exe-dir `AddJsonFile` is registered after `Host.CreateApplicationBuilder`'s sources and
outranks both.

## A seat's Apollo dies with no reason in its log

Two different causes produce "the seat log explains nothing", and they need opposite fixes. Tell
them apart by **whether the Apollo process is still alive**, never by whether a log exists.

### It EXITS seconds after starting -> an encoder failed to open, and the reason was discarded

`h264_amf`, the QSV encoders and the software encoders are all **FFmpeg** encoders. Apollo sets
FFmpeg to `AV_LOG_QUIET` unless its log level is exactly `verbose` (`logging.cpp`: the test is
`min_log_level >= 1`). Our default is `info`, so the seat log ends at:

```
Info: Creating encoder [h264_amf]
Info: Color range: JPEG
```

...and stops. That is a failed encoder open, not a crash - `make_synced_session` returns empty on a
null encode device **without logging anything**, while every failure inside the D3D11 encode-device
setup does log. To see the real error:

```jsonc
// C:\Program Files\MultiSeat\appsettings.local.json
{ "MultiSeat": { "ApolloLogLevel": "verbose" } }
```

Restart the service, re-provision, then set it back - verbose is noisy.

- ⚠️ **`debug` does NOT work.** Apollo maps `verbose`=0, `debug`=1, `info`=2, and anything >= 1
  silences FFmpeg. Only `verbose` lifts it.
- ⛔ **Never hand-edit a seat's `sunshine.conf`** - `ApolloConfigBuilder` regenerates it on every
  provision, so the change is lost. `appsettings.local.json` survives deploys — ⚠️ but only since
  `98b5cfa`; a `-FromZip` upgrade before that deleted it too. See "Host-local config" above.

`SessionHealthCheck` now prints this hint by itself when a seat's Apollo exits within 30s of
starting (`IsStartupFailure`), so the failure is signposted rather than silent.

### It KEEPS RUNNING and serves, but writes nothing -> it cannot open its log file

A seat is a standard user. A log left by an earlier run is owned by Administrators with
`BUILTIN\Users:(RX)`, so the seat cannot write it and Apollo says nothing about that. Measured here:
a healthy seat Apollo serving TLS on its ports had written nothing anywhere. Fixed by
`EnsureSeatLogWritable`, which grants the seat Modify on an existing `apollo*.log` at provision.

⚠️ Also check the **timestamp** before trusting a seat log. A stale `apollo.log` from a previous run
looks like a current one, and the log may be under `<seatDir>\logs\apollo-<stamp>.log` rather than
`<seatDir>\apollo.log` - `ApolloManager.ResolveLogPath` handles both layouts.

## Where the service logs actually go

**The service writes no log files.** It's hosted via `AddWindowsService()`, whose default logging provider is the **Windows Event Log** — so service logs are in the Application log under two sources:

- `MultiSeat.Service` — application logging (`ILogger` categories; the detailed output)
- `MultiSeatService` — service lifecycle (started / stopped)

### The `Logging:EventLog` section is load-bearing — do not delete it

`AddWindowsService()` installs a **provider-specific** filter rule pinning the EventLog provider to
**Warning and above**. Provider-specific rules outrank every category rule under `Logging:LogLevel`
(those match any provider), so `"MultiSeat.Service": "Debug"` there does *nothing* for the Event Log
— which is the only destination a Windows Service has. Before this was fixed, 300 sampled events on
the reference host were 275 Warning + 25 Error and **zero Information**: every `LogInformation`
diagnostic we wrote was invisible to us *and* to bug reporters.

The `Logging:EventLog:LogLevel` section in `appsettings.json` overrides it — those rules are also
provider-specific and are added afterwards, so they win at equal specificity. Keep its `Default` at
`Warning`: raising it would also outrank `"Microsoft": "Warning"` and push every ASP.NET Core request
log into a machine-wide log. Our own categories all begin `MultiSeat.Service`, so one prefix rule
covers the service.

Note `LogDebug` is still not in the Event Log by design (health-check chatter would flood it) — so a
`LogDebug` call is effectively write-only on a deployed host. Use `LogInformation` for anything a
reporter may need to see.

**Don't reason about the rules — ask:**

```powershell
& 'C:\Program Files\MultiSeat\MultiSeat.Service.exe' --log-filters
```

Prints every filter rule in order, then what each provider actually accepts per category, and exits
0/1 on whether the service's own Information logs reach the Event Log. It inspects the real host
(it runs after `builder.Build()`, and building starts nothing), so the answer is the deployed one.
Worth running on a reporter's machine before asking them for logs. Covered by
`LoggingFilterTests`, which asserts the shipped `appsettings.json` still lets Information through,
still keeps ASP.NET Core request logs out, and still keeps `Debug` out.

`scripts\show-logs.ps1` reads both, plus the helper and per-seat Apollo logs. Or directly:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='MultiSeat.Service'} -MaxEvents 50
```

`C:\ProgramData\MultiSeat\logs\` receives exactly one file, `audio-helper.log`, written by `AudioCaptureHelper` inside a seat session — so it stays **empty until a seat has run**. An empty folder there is normal and not a fault. (Two external reporters have gone looking for service logs there; the docs used to point them at it.)

## Coexistence with a standalone Apollo

MultiSeat is self-contained and **non-destructive**: it works out of the box whether or not the host already runs a standalone Apollo (e.g. for the main console account). Three guarantees keep the two from colliding:

1. **Own Apollo binary.** The prereq script installs ApolloVibe to `C:\Program Files\ApolloVibe\` (`Constants.DefaultApolloPath` / `MultiSeat:ApolloExePath`); an existing `C:\Program Files\Apollo\` is never touched.
2. **Own port range.** Default `PortBase = 48100`, above a stock Apollo's block — no runtime port conflict.
3. **Never kills a non-MultiSeat Apollo.** On startup `MultiSeatWorker.KillOrphanedApolloProcesses` reaps **only** Apollo processes MultiSeat launched, identified via WMI (`GetManagedApolloPids`) by executable path (under the ApolloVibe dir) or a MultiSeat per-seat config path on the command line. It no longer stops/disables `ApolloService`, and `install-service.ps1` leaves that service alone. (WMI failure → empty set → cleanup is skipped rather than risk killing an unrelated Apollo.)

## Shared game library & emulator netplay

Because each seat is its own Windows account, games/ROMs would otherwise be siloed per account and seats couldn't easily netplay. Two provisioning helpers address this (config in `MultiSeatOptions` / `appsettings.json`):

- **Shared game library** (`EnableSharedGameLibrary`, default on; `SharedGameLibraryDir`, default `C:\MultiSeatGames`). `SharedLibraryProvisioner.EnsureSharedLibraryAsync` runs once at startup: creates `…\SteamLibrary` + `…\ROMs` and grants `BUILTIN\Users` Modify via `icacls` (well-known SID `S-1-5-32-545`). Point each seat's Steam at the `SteamLibrary` folder (Settings → Storage) so a game an owning account already installed there isn't re-downloaded. ROMs go in `…\ROMs`.
- **Emulator netplay** (`EnableEmulatorNetplay`, default on). Each seat gets a deterministic, collision-free RetroArch host port from its own block: `seat.PortBase + Constants.OffsetRetroArchNetplay` (offset 13 → seat 0 = 48113, seat 1 = 48143…), surfaced as `SeatInfo.RetroArchNetplayPort` and opened in the firewall. Seats netplay each other over **loopback**: in one seat "Host", in another "Connect to Netplay Host" → `127.0.0.1:<host-seat-port>`. Netplay requires identical core **and** content — the shared ROM dir keeps file CRCs matching.
- **RetroArch auto-config** (`SeedRetroArchNetplayConfig`, default **off** — it writes a user file). When on, `RetroArchConfigSeeder` (an `IEmulatorConfigSeeder`) upserts `netplay_ip_port`, `netplay_public_announce=false`, `netplay_nat_traversal=false`, and `rgui_browser_directory` into each seat's `retroarch.cfg` during provisioning (mirrors the RustDesk seed in `SeatManager` step 2.5). Add Dolphin/PCSX2 by registering another `IEmulatorConfigSeeder` — no `SeatManager` change.

## Architecture Notes

- Service runs as **SYSTEM** — all process creation uses `CreateProcessAsUser` with appropriate session tokens.
- Sessions are created via **RDP loopback** (`127.0.0.2`) — the only reliable way to create new interactive sessions from Session 0.
- `mstsc.exe` is launched in the console session and kept alive (hidden via `SW_HIDE`) to hold the seat session in **Active** state for the lifetime of the seat. Do NOT disconnect — Apollo calls `QueryDisplayConfig` both at startup and when each Moonlight client connects; disconnected sessions return `ERROR_ACCESS_DENIED`.
- After Apollo creates its SudoVDA monitor, `SeatManager.ApplyDisplayIsolationAsync` runs in the seat session to set SudoVDA as the session primary and shrink the RDP virtual adapter to 640×480. This drops TermService CPU from ~70% to under 5% during streaming. The helper REQUIRES `seat.DisplayDevicePath` (Apollo's `output_name`); without it the isolation is skipped to avoid grabbing the wrong virtual display.
- Temp RDP files go to `C:\ProgramData\MultiSeat\` (not `%TEMP%`) so the console user's token can read them.
- `WTSQueryUserToken` returns a filtered (medium-integrity) token for admin accounts — `SessionLauncher` fetches the linked elevated token via `GetTokenLinkedToken` for SudoVDA IPC access.

## Launch-on-connect apps

Apollo creates the virtual Xbox 360 controller (ViGEm) only **while a Moonlight client is streaming**. Game launchers that scan controllers at startup (Steam Big Picture, EmulationStation, RetroBat) will not see a pad that appears afterwards — so if they're autostarted at login (e.g. a `Steam.lnk` in the seat user's Startup folder) they run before any stream and the controller is never detected.

Fix: don't autostart launchers in the seat. Instead let MultiSeat launch them **when a client connects**, after the pad exists. `OnConnectAppLauncher` (wired into `SessionHealthCheck`) tails each seat's `apollo.log` for `CLIENT CONNECTED` / `CLIENT DISCONNECTED` and launches/kills the configured apps on those edges.

Config lives in `appsettings.json` under `MultiSeat` — **empty by default (feature off); no apps are hardcoded**:

```jsonc
"LaunchOnConnectDelayMs": 4000,                 // wait after connect so the pad exists before launch
"KillLaunchOnConnectAppsOnDisconnect": false,   // true = kill the apps when the client disconnects
"LaunchOnConnect": [
  { "Path": "C:\\Program Files (x86)\\Steam\\steam.exe", "Arguments": "-bigpicture" }
  // { "Path": "...\\EmulationStation.exe", "WorkingDirectory": "..." }
]
```

Apps launch into the seat session via `ProcessInjector.LaunchInSessionAsync`. The list is global (applies to every seat). When the array is empty the watcher returns immediately — zero I/O, no overhead.

## Per-seat gamepad isolation (HidHide session jail)

`MultiSeat:EnableHidHideCloaking`, **off by default**. Turning it on confines each seat's pad to
that seat's session using an **undocumented** HidHide feature: append `!<sessionId>` to a device
instance path in the ordinary, persistent blacklist and the device is visible **only** in that
session. Shipped in HidHide v1.4.181.0 (commit `3934d9a`); `Logic.c:817` is the whole decision and
it is byte-identical in v1.5.230.0. In no README, no CLI help, no release note. Contributed by
@jmlopezdona in issue #19 after a week of measurements, verified here against HidHide's source.

**Ask the host rather than reason about it:**

```powershell
& 'C:\Program Files\MultiSeat\MultiSeat.Service.exe' --hidhide
```

Prints the cloak state, the whitelist (flagging foreign entries), existing rules with their jailed
session, and every present gamepad with both nodes, both parents, the emulated verdict and the
exact rules a jail would write. Read-only, exits 0 when isolation could work here.

Four things about this feature are counter-intuitive and each one has already caused a silent
failure somewhere:

- **A pad is not one device, and XInput reads the node you would not think to hide.** The HID node
  is the obvious target; XInput reads the **XUSB** (`baseContainerDeviceInstancePath`) node. Hiding
  only the HID node leaves the pad fully visible. Both get a rule.
- **HidHide filters at OPEN time**, so a rule written after the pad exists is late by definition —
  and `dwm`, `explorer` and `GameInputSvc` **of every session** open each new pad inside that
  window and keep handles that never expire. Releasing a wrong rule does **not** hand the pad back;
  recovery takes a client reconnect. Hence `EnablePadRulePreWrite`, which writes a seat's rules
  before Apollo starts. A rule for an absent device matches nothing, so it is inert and free.
- **Ownership is derived, never named.** Nothing in the device tree says "ViGEm" — measured here, a
  ViGEm pad's XUSB node is called "Xbox 360 Controller for Windows" and its path starts `USB\`.
  The test is the **parent**: `ROOT\...` means emulated, a hardware bus means physical. Note the
  **HID** node's parent is the USB composite interface and looks physical even for an emulated pad,
  so the XUSB node is the one that answers. Attribution order is identity
  (`MultiSeat:SeatPadDevicePaths`), then elimination — and elimination is **refused** when more
  than one unconfined emulated pad exists, logged at Warning when used, and never remembered.
- **The application whitelist must stay empty.** It is global and cannot pair an app with a device,
  so one entry sees **every** confined pad. HidHide's own binaries are permanently whitelisted and
  `--app-unreg` on them does not stick, so the check reports *foreign* entries rather than a
  non-empty list.

⚠️ **The CLI has five traps and none of them had ever fired here**, because the old parser matched
nothing so `HidHideCLI.exe` was never actually invoked. `HidHideCli` handles them: it never gives
the tool stdout/stderr (it hangs — redirect through `cmd.exe` to a file), enforces an ~800 ms gap
with a fresh transcript per call and a retry, treats an **empty read as a failure rather than an
empty configuration** (the tell is that a healthy run always replays its cloak state), retries the
`0x0005 Access denied` that the driver's single-caller control device returns, and puts the value
directly after each switch. Reads carry `--cancel`, without which a pure listing saves over the
configuration it was asked to report on.

⚠️ **A console-side Apollo makes a seat's pad ambiguous.** Its ViGEm pad is created the same way
with the same VID/PID and is indistinguishable from a seat's. That is what makes the elimination
path dangerous — a free seat can be handed the console player's controller while everything reports
a healthy jail — and it is why this is off by default. Set `SeatPadDevicePaths` on such a host.

## Known Constraints

- NVIDIA consumer GPUs cap concurrent NVENC sessions. The old "3-5" figure here is **stale** -
  NVIDIA has raised the cap more than once, and the reference host runs driver 610.88. There is no
  query for the maximum; read the live count with
  `nvidia-smi --query-gpu=encoder.stats.sessionCount --format=csv`. Each seat stream is one
  session, so 4 seats plus a console stream is 5.
  **Encoding is rarely the limit anyway:** measured on an RTX 3080, one seat at 1920x1080
  `hevc_nvenc` 60fps costs **~10% of the video-encode engine**, so 4 seats is about 40%. VRAM and
  shader share - what the seats actually *run* - bind first.
- RDPWrap breaks after Windows updates to `termsrv.dll` — re-run the prereq script, which now
  **verifies** the ini covers the running build instead of assuming it does (see below).
- mstsc window for each seat must never be manually disconnected (session goes Disconnected, display APIs stop working).
- Single GPU only - multi-GPU not tested. A second card would add encode capacity and split
  rendering/VRAM load, but it cannot give **per-seat GPU assignment**: Apollo filters adapters by
  `adapter_name` and then requires the captured output to be attached to that adapter
  (`display_base.cpp`), and seats capture the RDP display whose GPU Windows picks machine-wide.
  `ApolloConfigBuilder` does not write `adapter_name` at all today.
- Windows 11 build 26100+ / x64 only.
- VoiceMeeter audio drivers only register after a reboot post-install.
- Keyboard/mouse session isolation (`InputHookManager` + InputHook DLL) is **disabled by default and currently a no-op**. The low-level `WH_KEYBOARD_LL`/`WH_MOUSE_LL` hooks run in the SYSTEM service (Session 0), where `GetForegroundWindow()` returns NULL, so `ShouldPassThrough()` always passes — the filter never blocks. With the RDP-loopback design there is no cross-session K/M bleed anyway: physical input goes to the console session, and Moonlight input is `SendInput`'d inside the seat session. Re-enabling is only meaningful if the hook is re-architected to run inside the seat session.

## Required Prerequisites

- Apollo (Sunshine fork with multi-instance support) — NOT upstream Sunshine
- SudoVDA virtual display driver
- VB-CABLE basic (seat 0 audio) — **`SharedHost` audio only; not needed under `PerSession`**
- VoiceMeeter Potato (seats 1–3 audio) — **`SharedHost` audio only; not needed under `PerSession`**
- HidHide v1.5.230 (controller isolation)
- ViGEmBus v1.22.0 EXE — not MSI (virtual controller bus)
- RDPWrap (multi-session RDP on Windows Home/Pro)
- .NET 9 SDK — **source builds only.** A release zip is self-contained; `-FromZip` needs no SDK and
  no runtime.
- Node.js 20+ — **source builds only.** A release zip ships the dashboard already built.
