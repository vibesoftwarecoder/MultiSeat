#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the MultiSeat Service as a Windows Service.
.DESCRIPTION
    Publishes the MultiSeat.Service project, copies the InputHook DLL,
    creates the required data directories, and registers the Windows service.
.PARAMETER Uninstall
    Remove the service and clean up.
#>
param(
    [switch]$Uninstall,

    # Install from a release asset instead of building. Point this at
    # multiseat-windows-x64.zip from a MultiSeat release:
    #
    #     .\scripts\install-service.ps1 -FromZip .\multiseat-windows-x64.zip
    #
    # The asset is self-contained, so this path needs NO .NET SDK, NO .NET runtime and NO
    # Node on the target - which is the entire point of issue #32. Everything after the
    # deploy step (RDP setup, certificates, service registration) is identical either way.
    [string]$FromZip
)

$ErrorActionPreference = "Stop"
$ServiceName = "MultiSeatService"
$DisplayName = "MultiSeat Multi-Seat Streaming Service"
$Description = "Manages multi-seat headless game streaming sessions with isolated input, audio, and display."
$InstallDir = "C:\Program Files\MultiSeat"
$DataDir = "C:\ProgramData\MultiSeat"
$ProjectDir = Join-Path $PSScriptRoot "..\src\MultiSeat.Service"
$InputHookBuild = Join-Path $PSScriptRoot "..\src\MultiSeat.InputHook\build\Release\MultiSeatInputHook.dll"

function Write-Step($msg) { Write-Host "[MultiSeat] $msg" -ForegroundColor Cyan }

# -- Uninstall -------------------------------------------------------
if ($Uninstall) {
    Write-Step "Stopping service..."
    $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -eq 'Running') {
            Stop-Service $ServiceName -Force
            Write-Step "Service stopped"
        }
        sc.exe delete $ServiceName | Out-Null
        Write-Step "Service removed"
    } else {
        Write-Step "Service not found -- nothing to remove"
    }
    Write-Host "`nTo fully clean up, manually delete:" -ForegroundColor Yellow
    Write-Host "  $InstallDir"
    Write-Host "  $DataDir"
    return
}

# -- Prerequisites check ---------------------------------------------
Write-Step "Checking prerequisites..."
$missing = @()
# -FromZip installs a self-contained build, so the SDK is only a prerequisite when this run
# is actually going to compile something. Demanding it either way would leave the barrier
# issue #32 exists to remove.
if (-not $FromZip -and !(Get-Command dotnet -ErrorAction SilentlyContinue)) { $missing += ".NET SDK" }
if ($FromZip -and !(Test-Path $FromZip)) { $missing += "the zip at $FromZip (file not found)" }
# Detect HidHide the same way the service does — via its driver service or the CLI on disk.
# The old check keyed off an "HKLM:\SOFTWARE\Nefarius Software Solutions\HidHide" registry key
# that HidHide 1.5.x doesn't reliably create, so it warned "not detected" even when HidHide was
# fully installed (issue #9).
$hidHideCli = "C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe"
if (!(Get-Service -Name "HidHide" -ErrorAction SilentlyContinue) -and !(Test-Path $hidHideCli)) {
    Write-Warning "HidHide not detected -- controller hiding will be unavailable"
}
if ($missing.Count -gt 0) {
    throw "Missing prerequisites: $($missing -join ', ')"
}

# -- RDP configuration -----------------------------------------------
# These settings are also applied by prerequisites\install-prerequisites.ps1.
# Re-applying here ensures a fresh service deploy always has the correct RDP
# configuration, even if the prereq script was run before these settings existed
# or was skipped entirely.

Write-Step "Verifying RDP configuration..."

# Enable Remote Desktop (fDenyTSConnections = 0)
#
# Worth knowing: MultiSeat's own connections are to 127.0.0.2 and loopback is not filtered, so the
# firewall rule enabled below is NOT needed for seats to work. It is turned on together with
# fDenyTSConnections so that "Remote Desktop enabled" means what an operator would expect it to.
# If this host should not accept RDP from the network — a good default, given NLA is turned off
# further down — disable the "Remote Desktop" firewall group again and MultiSeat keeps working.
$tsKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server"
if ((Get-ItemProperty $tsKey -Name "fDenyTSConnections" -ErrorAction SilentlyContinue).fDenyTSConnections -ne 0) {
    Set-ItemProperty $tsKey -Name "fDenyTSConnections" -Value 0
    Enable-NetFirewallRule -DisplayGroup "Remote Desktop" -ErrorAction SilentlyContinue
    Write-Host "  Applied: Remote Desktop enabled" -ForegroundColor Green
} else {
    Write-Host "  OK: Remote Desktop enabled" -ForegroundColor DarkGray
}

# Disable NLA (UserAuthentication=0) and enable TLS (SecurityLayer=2) on the RDP listener.
# SecurityLayer=2 makes TermService generate a self-signed TLS cert (SSLCertificateSHA1Hash),
# which TrustRdpLoopbackServer reads and writes to the console user's HKCU trust store so
# mstsc never shows "Do you trust this remote connection?" for 127.0.0.2.
#
# NLA cannot be left on: it is what makes the loopback logon MultiSeat depends on prompt, and
# there is no listener-scoped way to keep it for real clients and drop it for 127.0.0.2 — the
# setting belongs to the RDP-Tcp listener, which serves the network too. So this weakens RDP for
# the WHOLE machine: without NLA, anyone who can reach port 3389 gets to the logon stage before
# authenticating, which is exactly the pre-auth exposure NLA exists to remove.
#
# The mitigation is not in this script because it is a decision about the host, not about
# MultiSeat: keep 3389 off the network. See the note printed below and docs/security-posture.md.
$rdpTcpKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp"
$rdpTcpProps = Get-ItemProperty $rdpTcpKey -ErrorAction SilentlyContinue
$rdpChanged = $false
if ($rdpTcpProps.UserAuthentication -ne 0) {
    Set-ItemProperty $rdpTcpKey -Name "UserAuthentication" -Value 0
    $rdpChanged = $true
}
if ($rdpTcpProps.SecurityLayer -ne 2) {
    Set-ItemProperty $rdpTcpKey -Name "SecurityLayer" -Value 2
    $rdpChanged = $true
}
if ($rdpChanged) {
    Restart-Service -Name "TermService" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Write-Host "  Applied: NLA disabled, SecurityLayer=2 (TLS cert trust enabled)" -ForegroundColor Green
} else {
    Write-Host "  OK: NLA disabled, SecurityLayer=2" -ForegroundColor DarkGray
}

# Pre-trust 127.0.0.2 in the current user's HKCU so mstsc never shows "Do you trust
# this remote connection?" for loopback provisioning connections.
# TermService stores its self-signed TLS cert in Cert:\LocalMachine\Remote Desktop.
$rdpCert = Get-ChildItem 'Cert:\LocalMachine\Remote Desktop' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($rdpCert) {
    $trustKey = "HKCU:\Software\Microsoft\Terminal Server Client\Servers\127.0.0.2"
    if (-not (Test-Path $trustKey)) { New-Item $trustKey -Force | Out-Null }
    Set-ItemProperty $trustKey -Name "CertHash" -Value $rdpCert.GetCertHash() -Type Binary
    Set-ItemProperty $trustKey -Name "UsernameHint" -Value "" -Type String
    Write-Host "  Applied: 127.0.0.2 trusted in HKCU (thumbprint: $($rdpCert.Thumbprint))" -ForegroundColor Green
} else {
    Write-Host "  WARNING: No RDP TLS cert found in Cert:\LocalMachine\Remote Desktop -- mstsc trust dialog may appear" -ForegroundColor Yellow
}

# Suppress the RDP client certificate trust dialog (AuthenticationLevel = 0 machine policy).
# MultiSeat launches mstsc via CreateProcessAsUser with no interactive user to click dialogs.
#
# This used to say it was "safe because MultiSeat only ever connects to 127.0.0.2". That reasoning
# does not hold: the setting is a MACHINE POLICY, so it applies to every user on this host and to
# every server they connect to, not only to the loopback connections MultiSeat makes. What it
# costs is the warning you would otherwise get when a remote server's certificate does not match
# — the one that would tell you a connection was being intercepted.
#
# It is applied anyway because there is no per-target form of it and nobody is present to click
# the dialog, but the trade-off is real and is summarised at the end of this section.
$rdpClientKey = "HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services"
if (-not (Test-Path $rdpClientKey)) { New-Item $rdpClientKey -Force | Out-Null }
if ((Get-ItemProperty $rdpClientKey -Name "AuthenticationLevel" -ErrorAction SilentlyContinue).AuthenticationLevel -ne 0) {
    Set-ItemProperty $rdpClientKey -Name "AuthenticationLevel" -Value 0 -Type DWord
    Write-Host "  Applied: RDP client cert dialog suppressed" -ForegroundColor Green
} else {
    Write-Host "  OK: RDP client cert dialog suppressed" -ForegroundColor DarkGray
}

# Allow unsigned .rdp files without showing "publisher cannot be identified" warning.
# MultiSeat uses a connection.rdp in C:\ProgramData\MultiSeat\ which is not digitally signed.
if ((Get-ItemProperty $rdpClientKey -Name "AllowUnsignedFiles" -ErrorAction SilentlyContinue).AllowUnsignedFiles -ne 1) {
    Set-ItemProperty $rdpClientKey -Name "AllowUnsignedFiles" -Value 1 -Type DWord
    Write-Host "  Applied: unsigned .rdp file warning suppressed" -ForegroundColor Green
} else {
    Write-Host "  OK: unsigned .rdp file warning suppressed" -ForegroundColor DarkGray
}

# Allow audio capture redirection (audiocapturemode:i:1) without showing a consent dialog.
# Without this, mstsc shows a device redirection trust prompt that blocks headless provisioning.
if ((Get-ItemProperty $rdpClientKey -Name "fDisableAudioCapture" -ErrorAction SilentlyContinue).fDisableAudioCapture -ne 0) {
    Set-ItemProperty $rdpClientKey -Name "fDisableAudioCapture" -Value 0 -Type DWord
    Write-Host "  Applied: audio capture redirection allowed (no consent dialog)" -ForegroundColor Green
} else {
    Write-Host "  OK: audio capture redirection allowed" -ForegroundColor DarkGray
}

# Pre-authorize mstsc's device-redirection consent for 127.0.0.2.
#
# LocalDevices is a SUBKEY holding one REG_DWORD per server name — not a value on the
# parent key. This used to write "LocalDevices" as a DWORD directly under Terminal Server
# Client, which Windows never reads, so the pre-authorization this step reports as applied
# had no effect at all; the consent list appeared on every connection regardless.
#
# The bitmask 0x7FFFFFFF covers every redirection class (audio, drives, printers, smart
# cards, clipboard, ...).
$mstscLocalDevices = "HKCU:\Software\Microsoft\Terminal Server Client\LocalDevices"
if (-not (Test-Path $mstscLocalDevices)) { New-Item $mstscLocalDevices -Force | Out-Null }
if ((Get-ItemProperty $mstscLocalDevices -Name "127.0.0.2" -ErrorAction SilentlyContinue)."127.0.0.2" -ne 0x7FFFFFFF) {
    Set-ItemProperty $mstscLocalDevices -Name "127.0.0.2" -Value 0x7FFFFFFF -Type DWord
    Write-Host "  Applied: mstsc device redirection pre-authorized for 127.0.0.2" -ForegroundColor Green
} else {
    Write-Host "  OK: mstsc device redirection pre-authorized for 127.0.0.2" -ForegroundColor DarkGray
}

# Remove the ineffective value the old code wrote, so it stops looking like protection
# that is in place. Nothing reads it.
$mstscClientKey = "HKCU:\Software\Microsoft\Terminal Server Client"
if ($null -ne (Get-ItemProperty $mstscClientKey -Name "LocalDevices" -ErrorAction SilentlyContinue)) {
    Remove-ItemProperty $mstscClientKey -Name "LocalDevices" -ErrorAction SilentlyContinue
    Write-Host "  Cleaned: removed the old no-op LocalDevices value" -ForegroundColor DarkGray
}

# -- State the RDP trade-off ------------------------------------------
# These changes are machine-wide and outlive an uninstall, so they are worth one visible summary
# rather than a row of green "Applied" lines that read like routine setup. An operator who never
# reads the source should still learn that their host's RDP posture changed.
$rdpExposed = $false
try {
    $rdpRules = Get-NetFirewallRule -DisplayGroup 'Remote Desktop' -ErrorAction SilentlyContinue |
                Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' }
    $rdpExposed = [bool]$rdpRules
} catch { }

Write-Host ""
Write-Host "  NOTE: MultiSeat changed this machine's RDP settings, not just its own:" -ForegroundColor Yellow
Write-Host "    - Network Level Authentication is OFF for the RDP listener (all clients, not just" -ForegroundColor Yellow
Write-Host "      loopback). The seat logon needs it off, and the setting is not per-target." -ForegroundColor Yellow
Write-Host "    - mstsc no longer warns about server certificates or unsigned .rdp files, for" -ForegroundColor Yellow
Write-Host "      every user on this host and every server they connect to." -ForegroundColor Yellow
if ($rdpExposed) {
    Write-Host "    - Inbound Remote Desktop firewall rules are ENABLED, so port 3389 is reachable" -ForegroundColor Yellow
    Write-Host "      from the network. With NLA off, restrict it to loopback or trusted hosts." -ForegroundColor Yellow
} else {
    Write-Host "    - No inbound Remote Desktop firewall rule is enabled, so 3389 is not reachable" -ForegroundColor DarkGray
    Write-Host "      from the network. That is the posture this design wants; keep it that way." -ForegroundColor DarkGray
}
Write-Host "    See docs/security-posture.md to undo any of it." -ForegroundColor Yellow
Write-Host ""

# NOTE: none of the settings above suppress mstsc's "Unknown remote connection / we could
# not verify the publisher" security warning, which is a separate dialog. AllowUnsignedFiles
# above does not stop it on Windows 11 either — verified 2026-08-07, the warning still
# appeared with that policy set to 1. That is why SessionLauncher falls back to dismissing
# the dialog with SendKeys, and why it still reaches the user when the dismisser mistimes it.
# The real fix is signing the generated .rdp with rdpsign.exe and trusting the thumbprint
# via the TrustedCertThumbprints policy. Not done yet.

# -- RDP Wrapper check ------------------------------------------------
# Every seat IS an RDP session, so without multi-session RDP no seat can ever start.
# This script used to gate on SudoVDA and the audio cables but say nothing about the one
# dependency nothing works without: it would register and start the service on a host where
# seats were impossible, and the first sign the user got was a seat timing out (issue #33).
#
# Before 0.6.0 that was masked -- installing meant cloning the repo, so prerequisites\ was
# in front of you. The zip install removed the clone and with it the reminder.
#
# Test BEHAVIOUR, not presence: an installed wrapper whose ini has no section for this
# host's termsrv.dll is inactive, and looks identical to a correct install from the outside.
# This mirrors RdpWrapper.EnsureMultiSession() in the service so the two cannot disagree.
Write-Step "Checking RDP Wrapper (multi-session support)..."

# termsrv.dll's StringFileInfo and its VS_FIXEDFILEINFO DISAGREE -- measured 2026-09-01, the
# string said 10.0.26100.8115 while the raw fixed-info said .8972, and the raw one is what
# RDPWrap keys on. Read FileVersionRaw and never fall back to the string.
function Get-TermSrvVersionForGate {
    $termsrv = Join-Path $env:SystemRoot "System32\termsrv.dll"
    if (-not (Test-Path $termsrv)) { return $null }
    $raw = (Get-Item $termsrv).VersionInfo.FileVersionRaw
    if (-not $raw) { return $null }
    return ("{0}.{1}.{2}.{3}" -f $raw.Major, $raw.Minor, $raw.Build, $raw.Revision)
}

# BOTH sections are required. RDPWrap patches with whatever it finds, so a half-present
# pair is worse than none.
function Test-RdpWrapCoverage($iniPath, $version) {
    if (-not $version -or -not $iniPath -or -not (Test-Path $iniPath)) { return $false }
    $ini = Get-Content $iniPath
    $main   = [bool]($ini | Select-String -SimpleMatch -Pattern "[$version]"        -Quiet)
    $slInit = [bool]($ini | Select-String -SimpleMatch -Pattern "[$version-SLInit]" -Quiet)
    return ($main -and $slInit)
}

$script:RdpWrapProblem = $null
$rdpWrapDll = $null
$rdpWrapIni = $null

$sys32Dll = Join-Path $env:SystemRoot "System32\rdpwrap.dll"
if (Test-Path $sys32Dll) {
    # Classic install: rdpwrap.dll dropped into System32.
    $rdpWrapDll = $sys32Dll
    $classicIni = Join-Path ${env:ProgramFiles} "RDP Wrapper\rdpwrap.ini"
    if (Test-Path $classicIni) { $rdpWrapIni = $classicIni }
} else {
    # ServiceDll install (RDPWrap 1.6.2+, and TermWrap): TermService redirected away from
    # the stock termsrv.dll. Do NOT require the name "rdpwrap" -- TermWrap is a different
    # patch that works the same way, and demanding the name reported a working host as
    # broken in issue #15. What matters is that the redirect exists.
    try {
        $svcDll = (Get-ItemProperty `
            'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters' `
            -Name ServiceDll -ErrorAction Stop).ServiceDll
        if ($svcDll) {
            $expanded = [Environment]::ExpandEnvironmentVariables($svcDll)
            if ((Split-Path $expanded -Leaf) -ne 'termsrv.dll' -and (Test-Path $expanded)) {
                $rdpWrapDll = $expanded
                # Only RDPWrap is keyed by an offsets ini. No sibling ini means the patch
                # finds its offsets another way, and there is nothing to validate.
                $sibling = Join-Path (Split-Path $expanded -Parent) "rdpwrap.ini"
                if (Test-Path $sibling) { $rdpWrapIni = $sibling }
            }
        }
    } catch {
        # No ServiceDll value at all -- stock TermService. Handled as "not found" below.
    }
}

$termSrvVersion = Get-TermSrvVersionForGate

if (-not $rdpWrapDll) {
    $script:RdpWrapProblem = "not installed"
    Write-Host ""
    Write-Host "  *** WARNING: no multi-session patch found. ***" -ForegroundColor Red
    Write-Host "  No rdpwrap.dll in System32, and TermService still points at the stock" -ForegroundColor Yellow
    Write-Host "  termsrv.dll. Every seat is an RDP session, so NO SEAT CAN START." -ForegroundColor Yellow
    Write-Host "  Run prerequisites\install-prerequisites.ps1 to install RDP Wrapper." -ForegroundColor Yellow
    Write-Host ""
} elseif (-not $rdpWrapIni) {
    Write-Host "  OK: multi-session patch at $rdpWrapDll (not ini-keyed, nothing to verify)" -ForegroundColor DarkGray
} elseif (Test-RdpWrapCoverage $rdpWrapIni $termSrvVersion) {
    Write-Host "  OK: RDP Wrapper covers termsrv $termSrvVersion" -ForegroundColor DarkGray
} elseif (-not $termSrvVersion) {
    Write-Host "  WARNING: could not read termsrv.dll's version -- coverage unverified." -ForegroundColor Yellow
} else {
    $script:RdpWrapProblem = "installed but does not cover termsrv $termSrvVersion"
    Write-Host ""
    Write-Host "  *** WARNING: RDP Wrapper is installed but INACTIVE. ***" -ForegroundColor Red
    Write-Host "  $rdpWrapIni has no section pair for termsrv $termSrvVersion," -ForegroundColor Yellow
    Write-Host "  which a Windows update almost certainly replaced. Every seat is an RDP" -ForegroundColor Yellow
    Write-Host "  session, so NO SEAT CAN START until this build is covered." -ForegroundColor Yellow
    Write-Host "  Fix: prerequisites\install-prerequisites.ps1 refreshes the ini, and" -ForegroundColor Yellow
    Write-Host "       scripts\check-rdpwrap-offsets.ps1 -Generate computes the offsets" -ForegroundColor Yellow
    Write-Host "       locally if the community ini has not caught up yet." -ForegroundColor Yellow
    Write-Host ""
}

# -- SudoVDA check ---------------------------------------------------
# SudoVDA is required for per-seat virtual display isolation.
# Without it, Apollo captures the primary physical display and all seats
# share the same view. Warn loudly here; the prereq script installs it.
Write-Step "Checking SudoVDA (virtual display driver)..."

# Detect SudoVDA the same way the running service does
# (VirtualDisplayManager.IsSudoVdaAdapterPresent): a ROOT\DISPLAY device whose
# DeviceDesc or HardwareID actually names SudoMaker/SudoVDA.
#
# The previous check matched FriendlyName against "VDD|Virtual Display|SudoVDA|MTT",
# which matches ANY virtual display driver -- "USB Mobile Monitor Virtual Display",
# the MTT "Virtual Display Driver", etc. It therefore reported "SudoVDA detected"
# on machines with no SudoVDA at all, naming whichever unrelated adapter it hit,
# and contradicted the check immediately below it (issue #14).
function Test-SudoVdaPresent {
    $root = 'HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY'
    if (-not (Test-Path $root)) { return $null }
    foreach ($k in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
        $props = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
        $desc  = [string]$props.DeviceDesc
        $hwIds = (@($props.HardwareID) -join ';')
        if ($desc -match 'SudoMaker|SudoVDA' -or $hwIds -match 'SudoMaker|SudoVDA') {
            return [PSCustomObject]@{ Key = $k.PSChildName; Desc = $desc }
        }
    }
    return $null
}

$sudovdaDevice = Test-SudoVdaPresent
if ($sudovdaDevice) {
    Write-Host "  OK: SudoVDA detected (ROOT\DISPLAY\$($sudovdaDevice.Key))" -ForegroundColor DarkGray
} else {
    Write-Host ""
    Write-Host "  *** WARNING: SudoVDA virtual display driver is NOT installed. ***" -ForegroundColor Yellow
    Write-Host "  Seats will launch in degraded mode — Apollo will capture the" -ForegroundColor Yellow
    Write-Host "  physical display instead of an isolated virtual display." -ForegroundColor Yellow
    Write-Host "  Run prerequisites\install-prerequisites.ps1 to install SudoVDA." -ForegroundColor Yellow
    Write-Host ""
}

# -- Persistent VDD service start (OPTIONAL, not SudoVDA) -----------------------
# "VirtualDisplayDriver" is the service belonging to the OPTIONAL MttVDD persistent
# virtual display driver (VirtualDrivers/Virtual-Display-Driver), used only so a
# headless machine has a console-session display at boot. SudoVDA does NOT register
# this service, so its absence says nothing about SudoVDA.
#
# The old message here told the user to install SudoVDA when this service was
# missing, which directly contradicted the SudoVDA result printed above and sent
# people chasing a driver that was already fine (issue #14). Start it if present;
# otherwise say what it actually is.
$vddSvc = Get-Service -Name "VirtualDisplayDriver" -ErrorAction SilentlyContinue
if ($vddSvc) {
    if ($vddSvc.Status -eq 'Running') {
        Write-Host "  OK: VirtualDisplayDriver running" -ForegroundColor DarkGray
    } else {
        Write-Step "Starting VirtualDisplayDriver service (virtual displays)..."
        try {
            Start-Service "VirtualDisplayDriver" -ErrorAction Stop
            Write-Host "  OK: VirtualDisplayDriver started" -ForegroundColor Green
        } catch {
            Write-Host "  WARNING: Could not start VirtualDisplayDriver: $_" -ForegroundColor Yellow
            Write-Host "  Virtual displays may be unavailable. Reboot may be required." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  Note: optional persistent VDD (MttVDD) not installed — only needed so a" -ForegroundColor DarkGray
    Write-Host "        headless machine has a display at boot. Unrelated to SudoVDA." -ForegroundColor DarkGray
}

# -- VoiceMeeter start ---------------------------------------------------------
# VoiceMeeter Potato must be running for its virtual audio devices (VoiceMeeter
# Input, Aux Input, VAIO3) to route audio. Nothing auto-starts it: it is NOT in
# HKLM\Run on the reference host, despite what this comment used to claim, so
# between a boot and the first seat provision there is no VoiceMeeter process.
# Start it here (from the admin session, NOT Session 0) before the MultiSeat
# service begins — the service's AudioRouter will also try to start it, but
# launching a GUI app from SYSTEM/Session 0 is unreliable.
# Editions in preference order, 64-bit variant of each first. This used to look for
# voicemeeterpro.exe only - which is BANANA - while every message here said "Potato",
# so a host with Potato installed got Banana started and was told otherwise. Seat 3 is
# assigned the VAIO3 device and only Potato provides it, so Potato has to win.
# Same order as AudioRouter.VoiceMeeterExeNames; keep the two in step.
$vmNames = @(
    "voicemeeter8x64.exe",      # Potato, 64-bit
    "voicemeeter8.exe",         # Potato
    "voicemeeterpro_x64.exe",   # Banana, 64-bit
    "voicemeeterpro.exe",       # Banana
    "voicemeeter_x64.exe",      # basic, 64-bit
    "voicemeeter.exe"           # basic
)
# The installer uses the 32-bit tree, so look there first.
$vmRoots = @("C:\Program Files (x86)\VB\Voicemeeter", "C:\Program Files\VB\Voicemeeter")

$vmExe = $null
foreach ($root in $vmRoots) {
    foreach ($name in $vmNames) {
        $candidate = Join-Path $root $name
        if (Test-Path $candidate) { $vmExe = $candidate; break }
    }
    if ($vmExe) { break }
}

function Get-RunningVoiceMeeter {
    param([string[]]$Names)
    foreach ($n in $Names) {
        $proc = Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($n)) -ErrorAction SilentlyContinue
        if ($proc) { return $proc[0].ProcessName }
    }
    return $null
}

if ($vmExe) {
    $vmRunning = Get-RunningVoiceMeeter -Names $vmNames
    if ($vmRunning) {
        Write-Host "  OK: VoiceMeeter already running ($vmRunning)" -ForegroundColor DarkGray
    } else {
        Write-Step "Starting VoiceMeeter (audio routing)..."
        Start-Process $vmExe -WindowStyle Minimized
        Start-Sleep -Seconds 3
        $vmRunning = Get-RunningVoiceMeeter -Names $vmNames
        if ($vmRunning) {
            # Name what actually started - claiming Potato while running Banana is how
            # seat 3's VAIO3 device could look present and still carry no audio.
            Write-Host "  OK: VoiceMeeter started ($(Split-Path $vmExe -Leaf))" -ForegroundColor Green
        } else {
            Write-Host "  WARNING: VoiceMeeter may not have started. Check manually." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  NOTE: VoiceMeeter not found under VB\Voicemeeter in either Program Files tree -" -ForegroundColor Yellow
    Write-Host "        seats using its devices will have no audio. Run prerequisites\install-prerequisites.ps1." -ForegroundColor Yellow
}

# -- Standalone Apollo coexistence --------------------------------------------
# MultiSeat installs and manages its OWN Apollo (ApolloVibe at C:\Program Files\ApolloVibe)
# on a non-overlapping port block (PortBase 48100+), so it coexists with a standalone Apollo
# the user may run for their main console. We intentionally leave any default ApolloService
# alone — it is NOT stopped or disabled.
Write-Host "  OK: leaving any standalone ApolloService untouched (MultiSeat uses its own Apollo + ports)" -ForegroundColor DarkGray

# -- Stop service before publish so its DLLs are not locked ----------
$svcBeforePublish = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svcBeforePublish -and $svcBeforePublish.Status -eq 'Running') {
    Write-Step "Stopping service before publish..."
    Stop-Service $ServiceName -Force
    try { $svcBeforePublish.WaitForStatus('Stopped', (New-TimeSpan -Seconds 15)) }
    catch { Write-Warning "SCM stop timed out -- will force-kill the process" }
}

# Kill any surviving process running the service exe by path
# (handles cases where the process outlives the SCM status change)
$serviceExe = Join-Path $InstallDir "MultiSeat.Service.exe"
$lingering = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $serviceExe }
foreach ($proc in $lingering) {
    Write-Step "Force-killing lingering process PID $($proc.Id)..."
    $proc | Stop-Process -Force
    $proc.WaitForExit(10000)
}

# Brief pause to let the OS release all file handles before publish
Start-Sleep -Milliseconds 500
Write-Step "Service stopped and file handles released"

# -- Deploy: extract a release zip, or build from source --------------
#
# -FromZip installs the published asset from a MultiSeat release instead of building. That
# is the whole point of issue #32: a release zip is self-contained, so this path needs no
# .NET SDK, no .NET runtime and no Node on the target. The build path below is unchanged and
# is still what a developer gets by default.
if ($FromZip) {
    $srcFull = (Resolve-Path $FromZip -ErrorAction Stop).Path
    Write-Step "Installing from $srcFull"

    # Accept EITHER the .zip or an already-extracted folder. The asset now carries scripts\ , so
    # the natural flow is to extract it and run the installer from inside - at which point asking
    # the user to point back at the .zip would be circular, and would extract it a second time.
    $isDir = (Test-Path $srcFull -PathType Container)

    # Verify the payload BEFORE clearing the install directory. Extracting a bad zip over a
    # working install would leave the host with neither.
    $stage = Join-Path ([IO.Path]::GetTempPath()) ("multiseat-stage-" + [guid]::NewGuid().ToString("N").Substring(0,8))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        if ($isDir) {
            $stage = $srcFull          # already extracted; read it in place
        } else {
            Expand-Archive -Path $srcFull -DestinationPath $stage -Force
        }

        $required = @("MultiSeat.Service.exe", "appsettings.json", "wwwroot\index.html")
        $absent = @($required | Where-Object { -not (Test-Path (Join-Path $stage $_)) })
        if ($absent.Count -gt 0) {
            throw "This zip is missing $($absent -join ', '). It is not a MultiSeat release asset -- nothing was installed."
        }
        # A framework-dependent zip would run only where the ASP.NET Core runtime happens to
        # be installed, and would fail confusingly where it is not. Release assets are
        # self-contained; refuse anything else rather than half-install it.
        if (-not (Test-Path (Join-Path $stage "hostfxr.dll"))) {
            throw "This zip is not self-contained (no hostfxr.dll), so it would need a .NET runtime on this host. Refusing to install it."
        }

        # ⛔ Preserve host-local configuration BEFORE the wipe below.
        #
        # That wipe deletes everything in the install directory, so an upgrade used to destroy
        # appsettings.json AND appsettings.local.json and then drop the shipped defaults in their
        # place. Nothing was backed up and nothing said so. The API survived only by accident,
        # because ResolveApiKey falls back to a file in ProgramData that this never touches;
        # every setting without such a fallback was simply gone. See issue #45.
        #
        # ⚠️ appsettings.local.json is the documented place for host-local settings precisely
        # because a deploy cannot overwrite it — true of `dotnet publish`, and NOT true here
        # until now. Read that advice as conditional on this block existing.
        # ⭐ Preserve the FILES, byte for byte — never their text.
        #
        # Round-tripping through Get-Content/Set-Content rewrites the file: Set-Content appends a
        # trailing newline and can change the encoding, so a "preserved" file came back two bytes
        # different from the one the user wrote. Harmless for JSON today, wrong in principle for a
        # step whose entire job is to leave the user's file alone, and a trap the moment anything
        # here is not JSON.
        $preserveNames = @('appsettings.json', 'appsettings.local.json')
        $preserved = @{}
        $backupDir = $null
        foreach ($name in $preserveNames) {
            $existing = Join-Path $InstallDir $name
            if (Test-Path $existing) {
                if (-not $backupDir) {
                    $backupDir = Join-Path $env:ProgramData ("MultiSeat\config-backups\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
                    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
                }
                # The backup doubles as the staging copy: one byte-exact copy, used for both.
                # It lives outside the install directory because the wipe below is the very thing
                # being protected against.
                $kept = Join-Path $backupDir $name
                Copy-Item $existing $kept -Force
                $preserved[$name] = $kept
            }
        }
        if ($preserved.Count -gt 0) {
            Write-Host "  Backed up $($preserved.Count) config file(s) to $backupDir" -ForegroundColor DarkGray
        }

        if (Test-Path $InstallDir) {
            Get-ChildItem $InstallDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        } else {
            New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        }

        # The asset carries scripts\ , prerequisites\ and README.md so it can install itself
        # without a clone. They are NOT part of the deployed service - copying them would put a
        # stale copy of the installer inside Program Files, which is exactly the kind of thing
        # someone later runs by mistake.
        $skip = @('scripts', 'prerequisites', 'README.md')
        Get-ChildItem $stage -Force |
            Where-Object { $skip -notcontains $_.Name } |
            ForEach-Object { Copy-Item $_.FullName $InstallDir -Recurse -Force }

        # -- Put host configuration back over the shipped defaults --------------
        #
        # An upgrade must not silently change how this host is configured. The shipped
        # appsettings.json is what a FIRST install needs; on an upgrade the host's own copy wins.
        if ($preserved.ContainsKey('appsettings.local.json')) {
            Copy-Item $preserved['appsettings.local.json'] `
                      (Join-Path $InstallDir 'appsettings.local.json') -Force
            Write-Host "  Restored appsettings.local.json" -ForegroundColor DarkGray
        }

        if ($preserved.ContainsKey('appsettings.json')) {
            $shippedPath = Join-Path $InstallDir 'appsettings.json'

            # Report settings this release added that the host's file does not carry. Keeping the
            # host's file is right, but doing it silently would hide a new option forever — the
            # one real cost of preserving over replacing, so it is surfaced rather than ignored.
            try {
                $shippedKeys  = ((Get-Content $shippedPath -Raw | ConvertFrom-Json).MultiSeat |
                                 Get-Member -MemberType NoteProperty).Name
                $hostKeys     = ((Get-Content $preserved['appsettings.json'] -Raw | ConvertFrom-Json).MultiSeat |
                                 Get-Member -MemberType NoteProperty).Name
                $newKeys      = @($shippedKeys | Where-Object { $hostKeys -notcontains $_ })
                if ($newKeys.Count -gt 0) {
                    Write-Host ""
                    Write-Host "  This release adds $($newKeys.Count) setting(s) your appsettings.json does not have:" -ForegroundColor Yellow
                    $newKeys | ForEach-Object { Write-Host "      MultiSeat:$_" -ForegroundColor Yellow }
                    Write-Host "  Your file was kept as-is, so these run at their built-in defaults." -ForegroundColor Yellow
                    Write-Host ""
                }
            } catch {
                Write-Host "  NOTE: could not compare settings against the shipped file ($_)" -ForegroundColor Yellow
            }

            Copy-Item $preserved['appsettings.json'] $shippedPath -Force
            Write-Host "  Kept your existing appsettings.json (shipped defaults not applied)" -ForegroundColor DarkGray
        }

        $ver = "unknown"
        try {
            $ver = (& "$InstallDir\MultiSeat.Service.exe" --config 2>&1 |
                    Select-String "version\s*:" | Select-Object -First 1) -replace ".*:\s*", ""
        } catch { }
        Write-Step "Extracted release $ver -- no SDK, runtime or Node needed"
    }
    finally {
        # ONLY remove a staging dir we created. When -FromZip was given an already-extracted
        # folder, $stage IS the user's own directory and deleting it would destroy the thing they
        # just downloaded - along with the installer they are currently running from.
        if (-not $isDir) {
            Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
else {
    # -- Publish ----------------------------------------------------------
    Write-Step "Publishing MultiSeat.Service..."
    dotnet publish $ProjectDir -c Release -o "$InstallDir" --no-self-contained 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed"
    }

    # -- Build InputHook DLL ----------------------------------------------
    # OPTIONAL component. MSYS2 is a developer dependency, not a MultiSeat prerequisite, and
    # EnableKeyboardMouseIsolation is OFF by default (the hook is a no-op as architected — see
    # MultiSeatOptions.EnableKeyboardMouseIsolation). A missing DLL changes nothing about how
    # MultiSeat runs, so these are informational notes, NOT warnings: emitting WARNING here made
    # a normal install look broken and got reported as a bug (issue #14).
    $InputHookSrc = Join-Path $PSScriptRoot "..\src\MultiSeat.InputHook"
    $Bash = "C:\msys64\usr\bin\bash.exe"
    if (Test-Path $Bash) {
        Write-Step "Building MultiSeatInputHook.dll..."

        # Windows path -> MSYS path, without a scriptblock -replace: scriptblock substitution is
        # PowerShell 7 only and does NOT fail loudly on 5.1 - it stringifies the block into the
        # result, producing a path like ' "/$(([string]C:/...[0]).ToLower())" /Users/...' and a
        # CMake error about a directory that does not exist. Resolve-Path also drops the '..'.
        $srcFull = (Resolve-Path $InputHookSrc).Path
        $srcUnix = '/' + $srcFull.Substring(0, 1).ToLower() + ($srcFull.Substring(2) -replace '\\', '/')

        $buildScript = "export PATH='/ucrt64/bin:`$PATH'; cmake -B '$srcUnix/build/Release' -S '$srcUnix' -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_CXX_COMPILER=/ucrt64/bin/g++.exe -DCMAKE_MAKE_PROGRAM=/ucrt64/bin/ninja.exe && cmake --build '$srcUnix/build/Release'"

        # This step is optional and must never abort the install. With $ErrorActionPreference =
        # 'Stop' at the top of the file, anything the compiler writes to stderr surfaces as a
        # NativeCommandError and kills the whole script - which on 5.1 left the service STOPPED
        # mid-deploy, publish done and nothing restarted. Contain it here.
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $Bash -lc $buildScript 2>&1 | Out-Null
        }
        catch {
            $global:LASTEXITCODE = 1
        }
        finally {
            $ErrorActionPreference = $prevEap
        }

        if ($LASTEXITCODE -ne 0) {
            Write-Host "  Note: InputHook build failed -- skipping (optional, off by default)" -ForegroundColor DarkGray
        }
    } else {
        Write-Host "  Note: MSYS2 not present at C:\msys64 -- skipping optional InputHook build" -ForegroundColor DarkGray
    }

    # -- Copy InputHook DLL -----------------------------------------------
    if (Test-Path $InputHookBuild) {
        Copy-Item $InputHookBuild "$InstallDir\MultiSeatInputHook.dll" -Force
        Write-Step "Copied MultiSeatInputHook.dll"
    } else {
        Write-Host "  Note: MultiSeatInputHook.dll not built -- keyboard/mouse isolation stays unavailable." -ForegroundColor DarkGray
        Write-Host "        This is expected and safe: the feature is off by default and currently inert." -ForegroundColor DarkGray
    }

    # -- Build and deploy Dashboard ---------------------------------------
    $DashboardDir = Join-Path $PSScriptRoot "..\src\MultiSeat.Dashboard"
    if (Test-Path (Join-Path $DashboardDir "package.json")) {
        Write-Step "Building dashboard..."
        Push-Location $DashboardDir
        try {
            # Install npm dependencies if node_modules is missing or incomplete
            $nodeModules = Join-Path $DashboardDir "node_modules"
            $viteMarker  = Join-Path $nodeModules "vite\bin\vite.js"
            if (-not (Test-Path $viteMarker)) {
                Write-Step "Installing dashboard npm dependencies..."
                # Not (Get-Command ...)?.Source - the null-conditional operator is PowerShell 7 only,
                # and a parse error is fatal for the WHOLE file, so this one line made the documented
                # ".\scripts\install-service.ps1" fail on Windows PowerShell 5.1 before it ran a
                # single step. Keep this script 5.1-clean; that is the shell the docs imply.
                $nodeCmd = Get-Command node -ErrorAction SilentlyContinue
                $nodeExe = if ($nodeCmd) { $nodeCmd.Source } else { $null }
                if (-not $nodeExe) { $nodeExe = "C:\Program Files\nodejs\node.exe" }
                if (-not (Test-Path $nodeExe)) { throw "node.exe not found. Install Node.js first." }
                $result = Start-Process $nodeExe -ArgumentList "install.cjs" `
                    -Wait -NoNewWindow -PassThru -WorkingDirectory $DashboardDir
                if ($result.ExitCode -ne 0) { throw "npm install failed (exit $($result.ExitCode))" }
            }

            & cmd /c "$DashboardDir\build.bat"
            if ($LASTEXITCODE -ne 0) { throw "Dashboard build failed" }
            $distDir = Join-Path $DashboardDir "dist"
            if (Test-Path $distDir) {
                $wwwroot = Join-Path $InstallDir "wwwroot"
                if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
                Copy-Item $distDir $wwwroot -Recurse
                Write-Step "Dashboard deployed to $wwwroot"
            } else {
                Write-Warning "Dashboard dist/ not found after build"
            }
        } finally {
            Pop-Location
        }
    } else {
        Write-Warning "Dashboard not found -- skipping"
    }
}

# -- Create data directories ------------------------------------------
@("$DataDir", "$DataDir\apollo", "$DataDir\logs") | ForEach-Object {
    if (!(Test-Path $_)) {
        New-Item -ItemType Directory -Path $_ -Force | Out-Null
        Write-Step "Created $_"
    }
}

# -- Register Windows service ----------------------------------------
$exePath = Join-Path $InstallDir "MultiSeat.Service.exe"

$existing = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Step "Service already exists -- stopping and updating..."
    if ($existing.Status -eq 'Running') {
        Stop-Service $ServiceName -Force
    }
    sc.exe config $ServiceName binPath= "`"$exePath`"" start= auto | Out-Null
} else {
    Write-Step "Creating Windows service..."
    sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null
    sc.exe description $ServiceName "`"$Description`"" | Out-Null
}

# Configure service recovery (restart on failure)
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Write-Step "Configured automatic restart on failure"

# -- Start ------------------------------------------------------------
Write-Step "Starting service..."
Start-Service $ServiceName
$svc = Get-Service $ServiceName
Write-Host "`n[MultiSeat] Service installed and $($svc.Status)!" -ForegroundColor Green
Write-Host "  Dashboard: http://localhost:9550"
Write-Host "  Logs:      Windows Event Log (Application / MultiSeat.Service) -- run scripts\show-logs.ps1"
Write-Host "  Config:    $InstallDir\appsettings.json"

# Repeat the RDP Wrapper verdict LAST. It was already printed above, but a successful
# "Service installed" is what the user reads, and a warning several screens up is a
# warning nobody sees -- which is how issue #33 arrived as a seat timeout instead.
if ($script:RdpWrapProblem) {
    Write-Host ""
    Write-Host "  ================================================================" -ForegroundColor Red
    Write-Host "  NO SEAT WILL START YET: RDP Wrapper is $($script:RdpWrapProblem)." -ForegroundColor Red
    Write-Host "  Every seat is an RDP session. The service is installed and running," -ForegroundColor Yellow
    Write-Host "  but seats will fail with a session timeout until you run:" -ForegroundColor Yellow
    Write-Host "      prerequisites\install-prerequisites.ps1" -ForegroundColor White
    Write-Host "  Then re-check with: scripts\check-rdpwrap-offsets.ps1" -ForegroundColor Yellow
    Write-Host "  ================================================================" -ForegroundColor Red
}
