# Steam isolation (experimental, opt in)

MultiSeat can launch Steam through its own native isolation launcher inside a seat's existing Windows user and session. This feature does not change Steam installations, replace system DLLs, or affect unrelated applications.

The native launcher and hooks live in `C:\Program Files\MultiSeat\SteamIsolation`, and you do not normally build or place them yourself:

- **A release asset (`multiseat-windows-x64.zip`) already contains them.** `install-service.ps1 -FromZip` installs them beside the service. The release build runs the same native smoke tests that run on every push to `src/MultiSeat.SteamIsolation`, and refuses to publish an asset that lacks any of the four files.
- **A source install builds them only when asked:** `.\scripts\install-service.ps1 -BuildSteamIsolation` (Visual Studio 2022 C++ tools and CMake are required). They are built fresh on that run and never taken from an earlier build's output. If the build fails, the install fails, rather than leaving a host with the feature on and no helper.
- **An upgrade never silently deletes them.** The installer replaces the whole install folder. A payload that ships `SteamIsolation\` replaces the old copy, and one that does not (an older release, or a source install without the switch) leaves the existing `SteamIsolation\` folder as it was. A payload that ships only some of the four files is refused before anything is touched.

The folder must contain the x64 `MultiSeat.SteamLauncher.exe` and `MultiSeat.SteamHook.dll`, and for 32-bit Steam also `MultiSeat.SteamLauncher32.exe` and `MultiSeat.SteamHook32.dll`. Keep these binaries in a directory standard seat accounts cannot modify, while granting them read/execute access.

Enable it in the deployed `appsettings.local.json`, then restart MultiSeat:

```json
{
  "MultiSeat": {
    "SteamIsolationEnabled": true,
    "SteamIsolationLauncherPath": "C:\\Program Files\\MultiSeat\\SteamIsolation\\MultiSeat.SteamLauncher.exe"
  }
}
```

The dashboard's app launch and `MultiSeat:LaunchOnConnect` automatically wrap an explicit, absolute path whose filename is exactly `steam.exe` (case insensitive). Other executables keep their existing behavior. Relative Steam paths are refused when isolation is enabled. Steam's original arguments and the launch working directory are preserved. Missing binaries, invalid PE files, and mismatched hook architecture prevent the isolated launch; there is no fallback that silently starts unisolated Steam.

For example, configure a launch-on-connect entry with an absolute installed Steam path and `-bigpicture`. The launcher supervises the whole process tree, including replacement Steam processes, and returns the initial Steam process's exit code after the tree exits. MultiSeat tracks the supervisor PID, so reconnect duplicate detection and disconnect process-tree cleanup continue to cover the isolated Steam process.

The service waits for an explicit native initialization signal, rather than assuming a running launcher means isolation succeeded. It creates a unique protected `Global\MultiSeatSteamReady-<guid>` event and grants only SYSTEM, administrators and the exact seat account access (the seat can signal/wait only). The launcher receives `--ready-event <name>` and signals it after successful hook initialization. Startup exit before the signal, cancellation, or an initialization timeout fails the launch and terminates the supervisor, whose job cleans up Steam and its children. The timeout is `SteamIsolationReadyTimeoutSeconds` (default 180, kept between 10 and 900). The wait holds the seat's lifecycle gate, so other operations on that seat queue behind it; lower the value to fail a stuck launch sooner, raise it for a slow host. Through the API a timeout answers `504 Gateway Timeout` and a launcher that exits early answers `500`; a bad Steam path or a missing helper binary still answers `400`, because it is refused before anything starts. There is no unisolated retry. The launcher is a Windows-subsystem program, so starting it opens no console window in the seat. Because there is no console, it records every failure in `%LOCALAPPDATA%\MultiSeat\SteamIsolation\launcher.log` in the seat user's profile (the file restarts after 256 KB). It also writes the message to stderr when that is redirected, and to the parent's console when it was started from a terminal. Failures after readiness are visible there; the service does not continuously monitor the launcher's exit code.

Apollo launches its Moonlight app entries directly, so those commands do not pass through the managed service. MultiSeat therefore rewrites the Steam entries when it copies the source Apollo app list into a seat, which happens each time the seat is provisioned. With isolation on, the Steam tile in Moonlight starts through the launcher with no action from you. The rewrite covers:

- an entry whose `cmd` is `steam.exe` (a full path, or the bare name, which is resolved to the installed Steam), with its arguments kept;
- an entry with no `cmd` whose `detached` list holds a `steam://` link, such as the stock "Steam Big Picture" entry. The link moves to `cmd` as an argument, so `steam://open/bigpicture` becomes `"...\MultiSeat.SteamLauncher.exe" --steam "...\steam.exe" -- steam://open/bigpicture`;
- a `steam://` link in a `do` or `undo` prep command, which is blanked. It would run through Windows' protocol handler without the hook. When the launcher's tree ends, the supervisor's job closes Steam and its children.

Everything else is left alone: other executables, entries with their own non-Steam `cmd`, and the Desktop entry. The installed Steam is found from the registry (`SOFTWARE\Valve\Steam\InstallPath`), else `C:\Program Files (x86)\Steam\steam.exe`.

If the launcher, the hook or Steam is missing, the app list is not rewritten and the seat logs a warning that its Steam entries start without isolation. That differs from an explicit launch through the API, which refuses, because a seat that lost its Steam tile would be a worse surprise than a logged gap. To change the app list a seat uses, change the source Apollo app list and provision the seat again. An edit made inside a seat lasts only until the next provision. Invoking the native launcher directly is itself an opt in and works independently of the service setting.

Steam launched manually from a shortcut, Explorer, a Startup entry, or another program does not pass through this feature. Close Steam in the target seat before starting the isolated instance, and use the wrapper for subsequent Steam launches. Separate Windows users already isolate user files; native hooks address the Steam-specific machine/global state and require multi-session runtime validation. The feature does not grant game licenses or change Steam account access rules.

## Building and checking

Build the managed service using the normal MultiSeat build process. Building the native component requires Visual Studio 2022 C++ tools and CMake:

```powershell
.\src\MultiSeat.SteamIsolation\build.ps1
```

The four release binaries are written to `src\MultiSeat.SteamIsolation\artifacts`. MinHook v1.3.4 is vendored with its license in `third_party\minhook`. To build safe test fixtures and run the launcher smoke checks without launching installed Steam:

```powershell
.\src\MultiSeat.SteamIsolation\build.ps1 -BuildFixture
.\src\MultiSeat.SteamIsolation\tests\LauncherSmoke.ps1 -Artifacts .\src\MultiSeat.SteamIsolation\artifacts
.\src\MultiSeat.SteamIsolation\tests\GameLauncherSmoke.ps1 -Artifacts .\src\MultiSeat.SteamIsolation\artifacts
dotnet test src\MultiSeat.Tests\MultiSeat.Tests.csproj --filter FullyQualifiedName~SteamIsolation
```

## Isolation behavior and limits

The hook creates an IPC name from the Windows session and user SID, replaces Steam's `-master_ipc_name_override` argument and `steam_master_ipc_name_override` environment entry, and separates browser `htmlcache` and `cef_log` paths. It redirects `TempAppPath` and `TempAppCmdLine` registry operations to the seat user's `HKCU\Software\Valve\Steam`, and returns zero for successful DWORD `SteamPID` reads.

Child command lines and explicit environments are adjusted before process creation. Same-architecture replacement `steam.exe` launches receive the hook before their main thread runs. The hook sits on `CreateProcessInternalW`, where every Win32 creation API ends (`CreateProcessW` and `A`, `CreateProcessAsUser`, and a non-elevated `ShellExecute`), and it recognizes an unquoted program path that contains spaces the way `CreateProcess` does, by trying the text up to each space until a file exists. Two ways of starting a process cannot be seen from inside Steam: `CreateProcessWithTokenW` and `CreateProcessWithLogonW`, which the secondary logon service carries out, and an elevated `ShellExecute`, which the AppInfo service carries out. A Steam restarted that way is not isolated and is not refused, because nothing in the caller can tell. Steam is not known to restart itself through either. Browser and game processes receive no injected hook DLL. An architecture-changing Steam update requires relaunching through the supervisor; the current hook cannot inject a DLL of the other architecture, so such a restart is refused rather than let through unisolated.

The hook writes one line to the same `launcher.log` when it becomes active in `steam.exe`, and one for every restart it isolated or refused (with the reason). After a real session, `launcher.log` in the seat user's profile shows whether Steam ever restarted itself and whether each restart was isolated.

The feature defaults to disabled. Fixture checks cover both architectures, restart propagation, quoting, registry types, explicit child environments, readiness, and job cleanup. Automated tests do not validate actual Steam login, updates, games, anti-cheat behavior, or simultaneous real Windows seat sessions. Limited manual reports are described below. This source does not establish compatibility with every Steam release.

## Launching a game directly

A game launched from Explorer does not inherit the isolated Steam client's IPC environment. Launch it from that Steam client's library, or use `Launch-Game.cmd` and `Launch-IsolatedGame.ps1` together from this component's source folder. The command opens an EXE picker and launches the selected game with the same session/user-derived Steam connection name and its own installation folder as the working directory. Run it as the normal seat account, after starting Steam with the native supervisor.

The picker changes only the new game's environment. It does not inject a DLL into the game, modify saves, or create a second copy. This directly launched game is outside the Steam supervisor's job, so stopping the supervisor does not guarantee that the selected game exits. Close the game separately. The helper checks for a Steam process in the session; it does not verify that process was launched with isolation.

Manual user testing reported simultaneous Steam clients on the host and one Windows seat and successful Valheim launches via this helper. The game installation included a third-party Steamworks compatibility component; unmodified retail Steamworks games have not been validated. These reports supplement the fixture checks rather than establishing general game compatibility.

The IPC override technique predates this implementation; see [steam-multibox-easysetup](https://github.com/dreamware-media/steam-multibox-easysetup). This implementation adds targeted hooks, startup readiness, restart propagation, and process-tree supervision. No claim is made that those additions outperform simpler launch scripts across all games.
