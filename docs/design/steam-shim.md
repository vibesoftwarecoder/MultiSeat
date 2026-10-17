# Steam shim: every way of starting Steam goes through isolation

Status: implemented in PR #114. The measurements below were taken on the reference host on
2026-10-16 and 2026-10-17 with Process Monitor and a stand-in Steam.

## The problem

Steam isolation (`docs/steam-isolation.md`) gives each account its own Steam instance. That only
helps if the Steam is started through the launcher. People start Steam in other ways: the desktop
shortcut, the Start menu, a `steam://` link, the Run entry Steam writes for itself. Each of those ran
a plain `steam.exe`. On one host the plain Steam on the console and the isolated Steam on a seat
could not both stay up:

| Order | Result |
|---|---|
| seat Steam (isolated), then a plain console Steam | the console Steam exited after about 10 s |
| plain seat Steam, then a console Steam | the seat's Steam was shut down within 1 s |
| console Steam started through the launcher, then the seat's, and the reverse | both stayed up |

So the console has to be isolated too, and every ordinary start has to go through the launcher.

## What a second `steam.exe` does

Found with Process Monitor on a running isolated Steam:

1. A `steam.exe` with **no** `-master_ipc_name_override` reads `SteamPID` from
   `HKLM\SOFTWARE\WOW6432Node\Valve\Steam`. That value is machine-wide. If the process it names is
   alive, `steam.exe` writes its command line to `TempAppCmdLine` in the same key, signals the
   running Steam, and exits. This is how `steam://` links and `-shutdown` reach a running Steam.
2. A `steam.exe` **with** `-master_ipc_name_override` skips that check and starts a master of its own.
   It logs in again and replaces the first client's session ("Session Replaced").

Consequences for isolation:

- Reading `SteamPID` from `HKLM`, a seat would hand its launch to the console's Steam. The hook first
  returned zero for `SteamPID`. That stopped the cross-account hand-off but also stopped two launches
  in one account finding each other, so a second launch always started a full client.
- The hook always added `-master_ipc_name_override`, so a second launch could never be a hand-off.
- A plain `steam.exe` wrote its command to `HKLM`, which an isolated Steam does not read.

## The fix

1. **`SteamPID` per user.** The hook moves `SteamPID` (on Steam's machine-wide key only) to the user's
   own `HKCU\Software\Valve\Steam`, like `TempAppPath` and `TempAppCmdLine` already were. Accounts stay
   apart, and two launches in one account find each other.
2. **`--forward` mode.** `MultiSeat.SteamLauncher.exe --steam <steam.exe> --forward -- <args>` starts
   a `steam.exe` that keeps the registry redirect and the instance name in the environment, but has no
   `-master_ipc_name_override` on its command line. It hands the arguments to the running Steam and
   exits. Measured: a forwarded `-shutdown` ended the console Steam in 2 s and left a seat's Steam
   running.
3. **The shim**, which decides between starting an isolated Steam and forwarding to one.

## The shim

`MultiSeat.SteamShim.exe --steam <absolute steam.exe> -- <the arguments steam.exe was given>`

A Windows-subsystem program (no console window). It never starts a plain Steam:

1. It checks that the path is an absolute `steam.exe` and that the launcher and the matching hook are
   beside it. If not, it logs an Error and shows a message (not for `-silent`, so a logon never waits
   on a box). It does not fall back to a plain Steam.
2. It takes a per-account named mutex, so two quick clicks cannot both start a master.
3. It looks for the isolated Steam of the account: `SteamPID` in the user's key, a live `steam.exe`
   in this session, with the hook DLL loaded. A plain Steam does not count.
4. If there is one it runs `launcher --forward -- <args>`. If there is none it runs
   `launcher -- <args>` and holds the mutex until the new Steam has registered (up to 25 s), so a
   second click forwards to it.

## What the service changes

Settings: `SteamIsolationEnabled` (existing, off by default), `SteamShimEnabled` (on) and
`SteamShimIncludeConsole` (on). The shim is only used when isolation is enabled.

| Entry point | Edit |
|---|---|
| Public Desktop and All Users Start Menu `Steam.lnk` | target becomes the shim, arguments `--steam "<old target>" -- <old arguments>`, icon pinned to Steam's |
| `HKCU\Software\Classes\steam\shell\open\command` | the shim, for each seat account and the console user. It outranks the machine-wide handler, so other accounts are untouched. |
| `HKCU\...\Run\Steam` | the shim, only where the account already had one |
| each account's own `Steam.lnk` files | as for the shared ones |

When it runs: shared shortcuts and the console user at service start, a seat's account at each
provision, before its session starts. Steam does not rewrite these entries when it starts or exits
(checked with marked values on 2026-10-17).

**Undo.** Each edit is recorded first in `C:\ProgramData\MultiSeat\steam-shim-backup.json` with what
was there before. The first original for a location is kept for good. `MultiSeat.Service.exe
--steam-shim-undo` restores everything, and the uninstall script runs it before removing the files,
because a shortcut left pointing at a removed shim would stop Steam from starting. Turning isolation
or the shim off in `appsettings.local.json` undoes the edits at the next service start.

**Nothing is edited if a shim file is missing.** The shim, the launcher and both hooks must exist, or
the service logs an Error and leaves every shortcut and registry value alone.

## What it does not cover

Anything that runs `steam.exe` by its path. `UnisolatedSteamWatch` logs a Warning, once per process,
for a `steam.exe` without the hook (judged after 8 s, so a Steam still being started is not
reported). It only reports; it never ends a process.

## Not decided here

- The console and the seat still share one `config\` folder in the Steam install
  (`loginusers.vdf`, `config.vdf`). The console's Steam did not sign in in two runs while the seat's
  Steam was up, and signed in at once in two runs without it. Not yet explained.
- A second copy of Steam in another folder keeps working through the shim, because the shim uses the
  `steam.exe` the shortcut named. The service only edits the shortcuts it finds in the shared and
  account locations, so a shortcut to that copy elsewhere is not changed.
- The Steam Client Service (SYSTEM) is shared by every session and exits right after it starts here.
  Seats, as standard users, log "Failed to create Service pipe (GLE 5)". Not caused by this change.
