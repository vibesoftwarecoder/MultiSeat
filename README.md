# MultiSeat

**Multi-seat headless game streaming for Windows using Moonlight/Apollo.**

MultiSeat lets you run multiple simultaneous Moonlight game-streaming sessions on a single Windows machine. Each "seat" gets its own isolated Windows user account, virtual display, virtual audio cable, and Apollo (Sunshine) streaming instance — all managed from a single web dashboard.

---

## How It Works

1. You create or link a Windows local account for each streaming seat.
2. MultiSeat provisions a seat: launches a dedicated Apollo process in the account's RDP session, attaches a virtual display (SudoVDA), and routes a virtual audio cable (VB-CABLE) to it.
3. The Moonlight client connects to the seat's Apollo instance using the host's IP and the seat's assigned port.
4. Each seat streams independently with isolated input, audio, and display.

```
Host Machine
├── Seat 0 (MultiSeat01)  →  Apollo :48100  →  Moonlight Client A
├── Seat 1 (MultiSeat02)  →  Apollo :48130  →  Moonlight Client B
└── Seat 2 (MultiSeat03)  →  Apollo :48160  →  Moonlight Client C
```

---

## Requirements

See [REQUIREMENTS.md](REQUIREMENTS.md) for the full hardware and software requirements.

**Quick summary:**
- Windows 11 (build 26100+ recommended) or Windows 10 (build 19041+)
- x64 CPU with 2+ cores per seat; 4+ GB RAM per seat
- NVIDIA GTX 1060+ or AMD RX 580+ GPU with hardware encoding (NVENC/AMF)
- .NET 9 Runtime
- Apollo (Sunshine fork with multi-instance support)
- SudoVDA virtual display driver (one virtual display per seat)
- VB-CABLE virtual audio (one per seat) — only under `SharedHost` audio; the default `PerSession` mode needs none
- HidHide (controller isolation)
- ViGEmBus (virtual controller driver)
- RDPWrap (multi-session RDP on Windows Home/Pro)
- PowerShell 7+ (`winget install Microsoft.PowerShell`)

---

## Installation

> **All commands must be run as Administrator in PowerShell 7+.** Windows PowerShell 5 is not supported.
> Install PowerShell 7 if needed: `winget install Microsoft.PowerShell`

### Installing from a release — no SDK, no runtime, no Node

Download `multiseat-windows-x64.zip` from the [latest release](https://github.com/vibesoftwarecoder/MultiSeat/releases/latest)
and point the installer at it:

```powershell
.\scripts\install-service.ps1 -FromZip .\multiseat-windows-x64.zip
```

The asset is **self-contained**, so this path needs no .NET SDK, no .NET runtime and no Node —
only the prerequisites from Step 2 below, which are drivers and are needed either way. Everything
else the installer does (RDP configuration, certificates, service registration) is identical to a
source install.

The installer refuses a zip that is missing the service, the dashboard or the bundled runtime, and
it checks that **before** touching an existing install — so a bad download cannot leave you with a
half-installed host.

Build from source instead if you are developing MultiSeat, or want a build of a commit that has no
release. That is what the rest of this section covers.

### Step 1 — Clone the repository

```powershell
git clone https://github.com/vibesoftwarecoder/MultiSeat.git
cd MultiSeat
```

### Step 2 — Install prerequisites

```powershell
.\prerequisites\install-prerequisites.ps1
```

This script automatically downloads and installs everything:

> **Running headless (no physical monitor)?**
> If the host machine has no physical display attached, Windows may not expose a GPU output for Apollo to capture.
> Install a persistent virtual display so the machine always has an active monitor, even before any seat is provisioned:
> ```powershell
> .\prerequisites\install-virtual-display.ps1
> ```
> This downloads and installs the [Virtual Display Driver by itsmikethetech](https://github.com/itsmikethetech/Virtual-Display-Driver) — a persistent IddCx virtual monitor that appears at boot without any streaming client connected.
> A reboot may be required after installation.

| Software | Purpose |
|----------|---------|
| ViGEmBus | Virtual Xbox controller driver |
| HidHide | Hides physical controllers from the host |
| VB-CABLE (basic) | Virtual audio device for seat 0 — **`SharedHost` audio only**, skipped by default (free, auto-downloaded) |
| VoiceMeeter Potato | 3 additional virtual audio devices for seats 1–3 — **`SharedHost` audio only**, skipped by default (free, auto-downloaded) |
| RDPWrap + rdpwrap.ini | Enables concurrent RDP sessions on Windows Home/Pro |
| Apollo | Sunshine fork with multi-instance streaming support |
| SudoVDA | Virtual display driver (one display per seat) |
| .NET 9 SDK | Required only to **build from source**. Not needed with `-FromZip`, which installs a self-contained release asset. |
| Node.js LTS | Required only to **build the dashboard from source**. Not needed with `-FromZip`, which ships it prebuilt. |

It also enables Remote Desktop and opens the required firewall ports automatically.

> **Reboot** when prompted — HidHide and RDPWrap require it before the service will work.

### Step 3 — Install the MultiSeat service

```powershell
.\scripts\install-service.ps1
```

This script:
- Installs dashboard npm packages if needed
- Builds and publishes `MultiSeat.Service`
- Builds the web dashboard
- Registers `MultiSeatService` as a Windows auto-start service running as SYSTEM
- Starts the service immediately

### Step 4 — Open the dashboard

Open a browser and navigate to:

```
http://localhost:9550
```

From any other device on the same LAN:

```
http://<host-ip>:9550
```

### Step 5 — Enter your API key

The first time the service starts it auto-generates a random API key and saves it to:

```
C:\ProgramData\MultiSeat\api-key.txt
```

Read it in PowerShell:

```powershell
Get-Content "C:\ProgramData\MultiSeat\api-key.txt"
```

Then open the dashboard, click the **Settings** gear icon (top-right), paste the key, and click **Save**. The key is stored in your browser's `localStorage` — you only need to enter it once per browser.

> **Fixed key:** Set `"ApiKey": "yourkey"` in `appsettings.json` before first start and the auto-generated file will never be created.
>
> **No auth:** Set `"ApiKey": "disabled"` to skip authentication entirely. Only do this on a fully trusted private network — the API can create Windows accounts and launch executables on the host.

### Step 6 — Create accounts and provision seats

1. Go to the **Accounts** tab — create a Windows local account for each seat (e.g., `MultiSeat01`, `MultiSeat02`).
2. Go to the **Seats** tab — click **+ New Seat**, select an account, and choose resolution and FPS.
3. Wait ~15 seconds for the seat to reach **Ready** status.

### Step 7 — Connect with Moonlight

**Recommended: use [MoonlightVibe](https://github.com/vibesoftwarecoder/MoonlightVibe/releases/latest)** — the companion Moonlight fork for MultiSeat. It auto-discovers all active seats from the local MultiSeat service; each seat appears as a separate server in the computer list within ~15 seconds of becoming ready. No manual host entry needed. Also includes mic passthrough support for use with ApolloVibe.

**Standard Moonlight:** add the host manually using its IP address and the seat's assigned port:

```
<host-ip>:<seat-port>
```

The port for each seat is shown in the dashboard. Default ports:

| Seat | Port |
|------|------|
| Seat 0 | 48100 |
| Seat 1 | 48130 |
| Seat 2 | 48160 |

---

## Configuration

Edit `appsettings.json` in `C:\Program Files\MultiSeat\` (restart the service after changes):

| Key | Default | Description |
|-----|---------|-------------|
| `MaxSeats` | `4` | Maximum concurrent seats |
| `PortBase` | `48100` | First Apollo HTTPS port (above a stock Apollo's block, so MultiSeat coexists with a standalone Apollo) |
| `ApolloExePath` | `C:\Program Files\ApolloVibe\sunshine.exe` | Path to MultiSeat's own Apollo (separate from any standalone `C:\Program Files\Apollo`) |
| `ApolloConfigDir` | `C:\ProgramData\MultiSeat\apollo` | Per-seat config directory |
| `Encoder` | `nvenc` | Apollo encoder for every seat. **AMD hosts must set this** (`amdvce` or `software`): Apollo's own fallback lands on AMF, whose startup probe runs against the seat's RDP surface and hangs *before* any port opens — the seat reports `Ready` with nothing listening. Values: `nvenc`, `quicksync`, `amdvce`, `software`. |
| `KeepaliveOnSeparateDesktop` | `true` | Run the hidden keepalive `mstsc` on its own desktop (`WinSta0\MultiSeatKeepalive`) instead of the console's. Fixes issue #18: an RDP client repositions its local cursor from the server's pointer-position message, so on the console desktop it mirrored the seat's pointer onto the console user's screen. Set `false` to revert; the launcher also falls back on its own if the desktop cannot be created. |
| `RotateSharedSeatTls` | `false` | Replace a seat's TLS identity if it is still the copy MultiSeat seeded from the console Apollo. **Off because it un-pairs every client on that seat** - a client pins the server certificate it was given at pairing. New seats always generate their own; this is only for older ones. |
| `ApolloLogLevel` | `info` | Apollo's own log level per seat. `debug` is the only way to see why a seat refuses a pairing or a client. Values: `verbose`, `debug`, `info`, `warning`, `error`. |
| `ApiPort` | `9550` | Dashboard port |
| `ApiKey` | *(auto-generated)* | API key required to access the dashboard. Auto-generated on first start and saved to `C:\ProgramData\MultiSeat\api-key.txt`. Set a fixed value here to override. Set to `disabled` to turn off authentication entirely (only safe on a fully trusted private network). |
| `VacCableCount` | `4` | Number of installed VB-CABLE devices |
| `EnableKeyboardMouseIsolation` | `false` | Keyboard/mouse session isolation (no-op as architected — see Known Constraints in CLAUDE.md) |
| `EnableSharedGameLibrary` | `true` | Create a shared games/ROMs folder all seats can use |
| `SharedGameLibraryDir` | `C:\MultiSeatGames` | Root of the shared library (`\SteamLibrary` + `\ROMs`) |
| `EnableEmulatorNetplay` | `true` | Assign + open a per-seat RetroArch netplay port (seats connect over `127.0.0.1`) |
| `SeedRetroArchNetplayConfig` | `false` | Auto-write each seat's `retroarch.cfg` (netplay port + shared ROM dir) |

---

## Uninstall

```powershell
.\scripts\install-service.ps1 -Uninstall
```

Then delete the data directories if desired:

```powershell
Remove-Item "C:\Program Files\MultiSeat" -Recurse -Force
Remove-Item "C:\ProgramData\MultiSeat"   -Recurse -Force
```

---

## Building from Source

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) - **source builds only**; a release zip bundles the runtime
- [Node.js 20+](https://nodejs.org/) - **source builds only**; a release zip ships the dashboard already built
- **Optional:** [CMake 3.20+](https://cmake.org/) and [MSYS2 UCRT64](https://www.msys2.org/) with `mingw-w64-ucrt-x86_64-gcc` and `ninja` — only for the InputHook DLL, which is off by default and currently inert. Skip these unless you're working on that component; `install-service.ps1` builds it automatically if MSYS2 happens to be present at `C:\msys64`.

> If you ran `prerequisites\install-prerequisites.ps1`, .NET SDK and Node.js are already installed.

### Build and deploy

```powershell
# Builds the service, installs npm deps, builds the dashboard,
# registers the Windows service, and starts it.
.\scripts\install-service.ps1
```

### Individual build steps

```powershell
# Restore .NET packages
dotnet restore src\MultiSeat.slnx

# Build the service
dotnet build src\MultiSeat.slnx

# Install and build the dashboard
cd src\MultiSeat.Dashboard
node install.cjs   # installs npm packages
node build.cjs     # compiles TypeScript + bundles with Vite
cd ..\..

# (Optional) Build the InputHook DLL. Not needed for a normal install: the feature is
# off by default and currently inert (see Troubleshooting), so skipping this is safe.
# install-service.ps1 builds it automatically when MSYS2 is present at C:\msys64.
# To build manually, open an MSYS2 UCRT64 terminal and run:
#   cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release && cmake --build build
```

### Run tests

```powershell
dotnet test src\MultiSeat.Tests\MultiSeat.Tests.csproj
```

---

## Architecture

```
MultiSeatService (Windows Service, SYSTEM)
├── SeatManager           — seat lifecycle (provision/teardown)
├── SessionLauncher       — RDP session + mstsc window management
├── ApolloManager         — per-seat Apollo process management
├── VirtualDisplayManager — SudoVDA display attach/detach
├── AudioRouter           — VB-CABLE assignment per seat
├── InputRouter           — XInput/ViGEm controller routing
├── HidHideConfigurator   — controller cloaking
├── InputHookManager      — keyboard/mouse session isolation
├── AccountManager        — Windows local account CRUD
├── ApiServer             — ASP.NET Core HTTP API + WebSocket
└── MultiSeat.Dashboard   — React/TypeScript web dashboard
```

The service runs as SYSTEM. Each seat's Apollo process runs inside its own RDP session, which is kept permanently Active via a managed `mstsc` connection so that the virtual display pipeline stays available to the streaming encoder.

---

## Port Layout

Each seat reserves a block of 10 ports starting at `PortBase + (seat_index × 10)`:

| Offset | Protocol | Use |
|--------|----------|-----|
| +0 | TCP | Apollo HTTPS (Moonlight pairing) |
| +1 | TCP | Apollo HTTP |
| +2 | TCP/UDP | RTP video |
| +3 | TCP/UDP | RTP audio |
| +4 | TCP/UDP | Control channel |

Default `PortBase` = 48100 (each seat reserves a 30-port block). Seat 0 = 48100, Seat 1 = 48130, Seat 2 = 48160, etc. The base sits above a stock Apollo's port block so MultiSeat coexists with a standalone Apollo.

---

## Troubleshooting

**Moonlight shows "Failed to initialize video capture"**
The seat's RDP session became Disconnected. The health check will recover it automatically within ~5 seconds. If it persists, check the Apollo log under `C:\ProgramData\MultiSeat\apollo\`.

**Seat stuck at Provisioning**
Check the service log (see **Where the logs are** below). Common causes: SudoVDA not installed, Apollo path wrong in `appsettings.json`, or insufficient virtual displays.

**Where the logs are**
The service writes no log files — it logs to the **Windows Event Log**. Easiest way to read everything:

```powershell
.\scripts\show-logs.ps1
```

Or directly:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='MultiSeat.Service'} -MaxEvents 50
```

`C:\ProgramData\MultiSeat\logs\` is **not** the service log. It receives only `audio-helper.log`, and only after a seat has run — an empty folder there is normal. Per-seat Apollo logs are under `C:\ProgramData\MultiSeat\apollo\<account>\apollo.log`.

**Controller input not isolated between seats**
Controller isolation is handled by HidHide, not the InputHook DLL. Ensure HidHide is installed and the service has been restarted since. Note that by default Apollo forwards the Moonlight client's controller into the seat natively (`EnableViGEmController` is off), so the dashboard shows the seat's Controller service as **Native** — XInput→seat assignment only applies when `EnableViGEmController` is on.

**Keyboard/mouse not isolated between seats**
`EnableKeyboardMouseIsolation` is off by default, and turning it on currently does nothing: the low-level hooks are installed from the service process in Session 0, where `GetForegroundWindow()` returns NULL, so the filter always passes the event through. There is also no cross-session bleed to prevent in the RDP-loopback design — physical input goes to the console session, and Moonlight input is injected inside the seat session. Making this meaningful requires re-architecting the hook to run inside the seat session; a missing `MultiSeatInputHook.dll` is therefore harmless.

**RDPWrap shows "Not supported" after a Windows update**
Re-run `prerequisites\install-prerequisites.ps1` — it will fetch the latest `rdpwrap.ini` automatically.

**Multiple VB-CABLE devices needed**
Each seat requires one VB-CABLE. After installing the first one via the prerequisites script, run `VBCABLE_Setup_x64.exe` manually for each additional seat (found in the extracted `VBCABLE_Driver_Pack45.zip`).

---

## A Note from the Author

MultiSeat started as a personal project — I built it because I wanted to run multiple game streaming sessions on one machine for myself and couldn't find anything that did exactly what I needed. I never expected others to find it useful, so I'm genuinely glad if it's working for you too.

Since I use this daily, it gets real-world testing every day. When something breaks I feel it immediately, so bugs tend to get fixed fast. If you run into an issue, open a GitHub issue and I'll take a look — no promises on timelines, but if it's something I can reproduce it'll get fixed.

Thanks for trying it out.

---

## License

MIT — see [LICENSE](LICENSE) for details.

---

## Support

If you find this project useful, consider sending a tip:

**Bitcoin:** `12uGJ1YBFZGprhw9JrVSEEjEWkAHLaaaMU`
