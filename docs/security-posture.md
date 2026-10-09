# Security posture

What MultiSeat changes about a host, what it deliberately does not protect against, and how to undo
the parts you would rather not keep.

Everything here is a statement of fact about the shipped code, not a recommendation. Where a
setting is a genuine weakening, it says so plainly — a security note that only lists reassurances
is not much use.

---

## Machine-wide changes made by the install scripts

Both `prerequisites\install-prerequisites.ps1` and `scripts\install-service.ps1` apply these. They
are **machine-wide**, they affect users and connections that have nothing to do with MultiSeat, and
**they survive uninstalling the service** — nothing removes them for you.

| Setting | Where | Effect beyond MultiSeat |
|---|---|---|
| `fDenyTSConnections = 0` + "Remote Desktop" firewall group enabled | `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server` | Remote Desktop is on, and port 3389 is reachable from the network |
| `UserAuthentication = 0` (**NLA off**) | `…\WinStations\RDP-Tcp` | Every RDP client, not just loopback, reaches the logon stage before authenticating |
| `SecurityLayer = 2` | `…\WinStations\RDP-Tcp` | TLS for the listener. Not a weakening — this one is an improvement |
| `AuthenticationLevel = 0` | `HKLM\SOFTWARE\Policies\…\Terminal Services` | mstsc stops warning about server certificate mismatches, for **every user on the host and every server they connect to** |
| `AllowUnsignedFiles = 1` | same policy key | mstsc stops warning about unsigned `.rdp` files, machine-wide |
| `127.0.0.2` cert trust + device redirection | `HKCU\…\Terminal Server Client\…` | Scoped to loopback only — no wider effect |

### Why NLA has to go

The seat logon is an mstsc connection to `127.0.0.2` with a stored credential, driven by a service
with no one present to answer a prompt. NLA makes that prompt. The setting lives on the `RDP-Tcp`
listener, which serves the network as well as loopback, and Windows offers no per-target form of
it — so there is no way to keep NLA for real clients and drop it only for the loopback connection.

**The mitigation is to keep 3389 off the network**, which is a decision about the host rather than
about MultiSeat, so the scripts do not make it for you. MultiSeat needs no inbound firewall rule at
all: loopback traffic is not filtered.

```powershell
Disable-NetFirewallRule -DisplayGroup 'Remote Desktop'
```

**Verified, not assumed**: with all inbound 3389 rules disabled, a seat still provisions normally —
session created, Apollo up and answering, seat `Ready` in 12.7 s. Seats connect to `127.0.0.2`, and
Windows Firewall does not filter loopback.

### ⚠️ RDPWrap reopens it, and the group command does not close it

`RDPWInst.exe -i` — the RDP Wrapper installer, which `install-prerequisites.ps1` runs — creates its
own firewall rule as part of installing:

```
netsh advfirewall firewall add rule name="Remote Desktop" dir=in protocol=tcp localport=3389 profile=any action=allow
```

That string is in the RDPWInst binary, and the rule it produces is **ungrouped**, so
`Disable-NetFirewallRule -DisplayGroup 'Remote Desktop'` leaves it enabled and the port open.

This matters because refreshing RDPWrap is routine: a Windows update to `termsrv.dll` breaks
`rdpwrap.ini`, the documented remedy is to re-run the prerequisites script, and doing so **silently
reopens 3389** on a host that had deliberately closed it. The prerequisites script now says so when
it happens; it does not close the rule for you, because some hosts want RDP reachable.

Check afterwards that nothing *else* still admits 3389. Besides the RDPWrap rule, a host can carry
any other ungrouped rule the group command does not touch, and one enabled rule is enough to leave
the port open:

```powershell
Get-NetFirewallRule -Enabled True -Direction Inbound -Action Allow | ForEach-Object {
    $pf = $_ | Get-NetFirewallPortFilter
    if ($pf.LocalPort -contains '3389') { "$($_.DisplayName)  [$($_.Name)]" }
}
```

Leave `fDenyTSConnections = 0` and TermService running — the firewall rules are what expose RDP to
the network; the service and the setting are what make loopback seats possible.

### Undoing the rest

```powershell
# Restore NLA (breaks seat provisioning while set)
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' `
  -Name UserAuthentication -Value 1
Restart-Service TermService -Force

# Restore mstsc's certificate and unsigned-file warnings
$p = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
Remove-ItemProperty $p -Name AuthenticationLevel  -ErrorAction SilentlyContinue
Remove-ItemProperty $p -Name AllowUnsignedFiles   -ErrorAction SilentlyContinue

# Turn Remote Desktop off entirely (seats stop working)
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' `
  -Name fDenyTSConnections -Value 1
```

Restoring NLA or disabling Remote Desktop stops seats from being created. The two mstsc warning
settings can be restored without breaking anything, at the cost of a dialog appearing during
provisioning that nobody is there to dismiss — which usually shows up as a seat that times out.

---

## What protects the API

- **Plaintext HTTP.** There is no HTTPS and no setting that claims otherwise. An option named
  `RequireHttps` used to exist, defaulted to true, and was never read by anything; it was removed
  rather than implemented.
- **API key**, sent as `X-MultiSeat-Key` or `?key=` (the query form exists because a browser cannot
  set headers on a WebSocket handshake). Bound beyond loopback, that key crosses the network in
  clear — the service logs a warning at startup saying so.
- `MultiSeat:ApiBindLoopbackOnly` binds to loopback only. Worth turning on for any host whose
  dashboard is only ever opened locally, including over a remote-desktop tool.
- **`/ws` is authenticated too.** It was not, and `/ws/seats` broadcasts whole `SeatInfo` objects —
  account names, session ids, ports, PIDs, audio device ids.
- **CORS defaults to loopback origins.** It used to default to `AllowAnyOrigin` whenever
  `CorsOrigins` was empty, which is the shipped configuration.
- **One request bypasses the key**: `GET /api/system/auth`, so the dashboard can show the auth
  toggle before it holds a key. `POST` on that path — the call that turns authentication off — is
  gated, and a test holds that distinction in place.

## Outbound connections

MultiSeat makes **no** connection to the internet unless you turn on update checks. With `MultiSeat:UpdateCheckEnabled` left at its default of `false`, the service only talks to loopback addresses (Apollo on this host).

With `UpdateCheckEnabled` set to `true`, the service sends an unauthenticated HTTPS GET to `api.github.com` for the release lists of `vibesoftwarecoder/MultiSeat`, `vibesoftwarecoder/ApolloVibe` and `vibesoftwarecoder/MoonlightVibe`, about every 12 hours (`UpdateCheckIntervalHours`, 1 to 168). The request carries the User-Agent `MultiSeat-update-check`, the usual `Accept` and `X-GitHub-Api-Version` headers, and an `If-None-Match` header after the first answer. It carries no version, host name, user name, seat data or API key, no cookies and no credentials. GitHub, and anyone who can see your network traffic, can see that this host's IP address asked for those pages. The three repository names and the API host are fixed in the program; no setting can point the check at another address, and redirects are followed only within `https://api.github.com`.

The check only reads. It never downloads, installs or runs anything. The installed ApolloVibe `sunshine.exe` is hashed on this host and compared on this host; the hash is not sent anywhere. Text from GitHub (release notes, `html_url`) is never forwarded to the dashboard: the release links it shows are built from the repository name and the version tag.

The result is shown on the dashboard (`GET /api/system/updates`). That endpoint needs the API key like the rest of `/api` whenever authentication is on, because the installed versions it reports would help someone choose an attack. `POST /api/system/updates/check` (check now, at most once a minute) and `POST /api/system/updates/settings` (the on/off switch) are gated the same way. The switch writes only `MultiSeat:UpdateCheckEnabled`, into `appsettings.local.json`. The file is replaced in one atomic step that keeps its existing permissions (it can hold the API key), so using the switch never widens who can read it.

To turn it off again, set `UpdateCheckEnabled` to `false` in `appsettings.local.json`, or use the System page. Both take effect without a service restart. The cache file `C:\ProgramData\MultiSeat\update-check.json` (restricted to SYSTEM and Administrators) can be deleted at any time.

## What protects the secrets on disk

- `accounts.json` (seat passwords) and `api-key.txt` carry an explicit DACL of SYSTEM +
  Administrators with inheritance disabled. They previously inherited `ProgramData`'s ACL, which
  grants `BUILTIN\Users` read.
- The installer's backups (`config-backups\` and `install-backups\` under `ProgramData\MultiSeat`) hold
  copies of `appsettings.json` and `appsettings.local.json`, which can carry the API key. Both folders
  get the same SYSTEM + Administrators DACL (plus the installing account), inheritance disabled.
  `config-backups\` existed before `install-backups\` and inherited `ProgramData`'s `BUILTIN\Users`
  read until this change.
- Seat passwords are DPAPI-protected at **CurrentUser** scope, which for this service is SYSTEM.
  The previous `LocalMachine` scope could be decrypted by any process on the machine regardless of
  which user it ran as.
- Seat accounts are **standard users** (`Users` + `Remote Desktop Users`), not administrators. They
  were administrators, on the incorrect belief that SudoVDA IPC required it.
  `MultiSeat:GrantSeatAdministrator` turns that back on for a setup that genuinely needs it, and
  hands the seat the ability to reach SYSTEM and therefore the credential store.
- The RDP credential used during provisioning is written through the credential API, not by passing
  the password on `cmdkey`'s command line.

## What a seat account can write, and what it deliberately cannot

A seat runs as a standard user, so it only gets read-and-execute on files **the service** created
under `ProgramData`. Windows grants it more than that on its own creations - the inherited ACL lets
a user create files and makes them the owner - so the split is: files Apollo makes for itself are
fine, files MultiSeat makes for Apollo are not.

That distinction was invisible for a fortnight. Making seats standard users (S4, `949eeb0`) meant a
seat could no longer write `sunshine_state.json`, which Apollo rewrites on every pairing. The write
failed silently, the reload restored the old contents, and every client a seat paired was forgotten
the moment it reconnected - reported from the field in PR #21, not caught here.

| file | who writes it | seat access |
|---|---|---|
| `<seat>/config/sunshine_state.json` | service creates, Apollo rewrites on every pairing | **Modify, granted explicitly** |
| `<seat>/config/apps.json` | service seeds, Apollo rewrites from its web UI | **Modify, granted explicitly** |
| `<seat>/config/credentials/*.pem` | Apollo creates them itself | writable as creator-owner, no grant needed |
| `shared_credentials.json` | service seeds; Apollo would rewrite it | **read-only, deliberately - see below** |

Each grant is a single explicit ACE for that seat's account SID on that one file. Inheritance is left
alone and nothing is removed. The account is resolved **machine-qualified** to a SID before the ACE
is written, so a domain account that happens to share a seat's name cannot be granted instead.

### Why the shared credentials file stays read-only

`shared_credentials.json` holds the Apollo web UI login shared by **every** seat. Granting one seat
write would let a standard user on that seat change the credentials for all the others, so it is left
read-only on purpose. The cost is that changing the web UI password from inside a seat does not
persist; do it as an administrator. This is a trade-off, not an oversight.

### The seat credentials directory is locked down - but the key it holds is shared

`<seat>/config/credentials/` is given an explicit DACL when the seat is provisioned: SYSTEM and
Administrators in full, that seat's account Modify, inheritance switched off, nothing else. Without
it the directory inherits ProgramData's `BUILTIN\Users:(RX)` and every standard user on the host -
including every other seat - can read the TLS private key.

    before   NT AUTHORITY\SYSTEM:(I)(F)  BUILTIN\Administrators:(I)(F)  BUILTIN\Users:(I)(RX)
    after    NT AUTHORITY\SYSTEM:(F)     BUILTIN\Administrators:(F)     <HOST>\<seat>:(M)

Seats provisioned before this shipped are fixed on their next provision. A seat whose account no
longer resolves is deliberately left alone: protecting the directory without granting the seat would
lock Apollo out of its own key and break the seat, which is worse than the exposure it closes.

### Each seat now generates its own TLS identity

A seat used to be seeded with a **copy of the console Apollo's `cakey.pem`**, on the reasoning that
Moonlight identifies a seat by its `uniqueid` rather than its certificate. Half of that is wrong:
pairing hands the client the **server certificate** (`root.plaincert`, `nvhttp.cpp getservercert`),
so the certificate is pinned per host entry. And one key shared by every seat and the console means
any of them can impersonate the others - while the source copy under the Apollo install stays
readable by every local user, which no permission work here can change.

MultiSeat now seeds nothing. Apollo generates its own 2048-bit credentials when either file is
missing, into the per-seat `credentials` directory that is already locked to SYSTEM, Administrators
and that seat. Verified on the reference host: with the directory emptied, a provisioned seat came
up serving TLS on its web UI with a key **different from the console Apollo's**.

### Seats provisioned before this still hold the shared key

Nothing removes it automatically, because **replacing a seat's certificate un-pairs every client
paired to it**. `MultiSeat:RotateSharedSeatTls` (default **off**) deletes the seeded pair on the
next provision so Apollo can generate a fresh one:

```jsonc
{ "MultiSeat": { "RotateSharedSeatTls": true } }
```

It only deletes a key that is **byte-identical to the console Apollo's** - a seat that already owns
its key is never touched - and it logs a warning naming the consequence when it fires. Turn it off
again afterwards; it is a migration, not a mode.

To check a seat by hand:

```powershell
$seat = "C:\ProgramData\MultiSeat\apollo\<seat>\config\credentials\cakey.pem"
$src  = "C:\Program Files\Apollo\config\credentials\cakey.pem"
(Get-FileHash $seat).Hash -eq (Get-FileHash $src).Hash   # True = still the shared key
```

## What is *not* protected

Stated so nobody has to discover it:

- **An administrator on this host can read everything.** Admins can become SYSTEM, and SYSTEM can
  decrypt the credential store. The file ACLs and DPAPI scope stop non-admin local users, backups,
  and copies taken off the machine — not a determined local administrator.
- **The API has no user model.** One key, all-or-nothing; anyone holding it can provision, tear
  down, and read every seat's details.
- **Seat account passwords are recoverable by design.** The service has to log seats in
  unattended, so it must be able to retrieve them in plaintext. They are protected in storage, not
  hashed.
- **Turning authentication off is a supported action.** `POST /api/system/auth` disables it, and
  the service warns loudly at startup when it is off.
