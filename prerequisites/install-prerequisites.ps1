#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Downloads and installs all MultiSeat prerequisites automatically.
.DESCRIPTION
    Downloads each prerequisite from the internet if not already present in
    this folder, then installs it. Run from an elevated PowerShell prompt.
    Each step is skipped if the software is already installed.
.PARAMETER SkipReboot
    Suppress the reboot prompt at the end.
.PARAMETER SkipDownload
    Skip downloading missing files; only install what is already present.
.PARAMETER Seats
    Number of seats to provision audio cables for. Defaults to 4.
    Each seat needs one VB-CABLE device. Only meaningful under SharedHost audio.
.PARAMETER AudioMode
    Which audio mode this host will run, which decides whether the virtual audio
    cables are installed at all.

      PerSession  (default) Each seat uses its own RDP "Remote Audio" endpoint.
                  VB-CABLE and VoiceMeeter are NOT installed - they are not used,
                  and VoiceMeeter is the one prerequisite that forces a reboot.
      SharedHost  Seats render onto host virtual cables. Installs VB-CABLE and
                  VoiceMeeter Potato. This is the only mode with a microphone path.

    When omitted, the deployed service configuration decides
    (appsettings.local.json, then appsettings.json); failing that, PerSession -
    which is the service's own default.
#>
param(
    [switch]$SkipReboot,
    [switch]$SkipDownload,
    [int]$Seats = 4,
    [ValidateSet('PerSession', 'SharedHost')]
    [string]$AudioMode
)

$ErrorActionPreference = "Stop"
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$Installed   = @()
$Skipped     = @()
$NeedsReboot = $false

# ----------------------------------------------------------------
# Re-launch natively on a 64-bit OS
# ----------------------------------------------------------------
# Windows' PnP and DriverStore surfaces are not WOW64-friendly. Under a 32-bit PowerShell
# on a 64-bit OS the ViGEmBus step breaks in two ways at once (issue #9):
#   1. Get-PnpDevice does not enumerate kernel-created root device nodes, so
#      ROOT\VIGEMBUS\* comes back empty even immediately after the node is created.
#   2. C:\Windows\System32\DriverStore is file-system-redirected to SysWOW64, hiding
#      every staged driver INF, so the SetupAPI fallback has nothing to bind.
# Re-launching natively fixes both at the source instead of working around each symptom.
# SysNative is a virtual alias visible only to 32-bit processes; the child inherits this
# process's elevated token, so there is no second UAC prompt.
if (-not [Environment]::Is64BitProcess -and [Environment]::Is64BitOperatingSystem) {
    $NativeHost = Join-Path $env:WINDIR 'SysNative\WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path $NativeHost) {
        Write-Host "[MultiSeat] Running under 32-bit PowerShell -- re-launching in 64-bit..." -ForegroundColor Yellow
        $Fwd = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
        if ($SkipReboot)   { $Fwd += '-SkipReboot' }
        if ($SkipDownload) { $Fwd += '-SkipDownload' }
        if ($AudioMode)    { $Fwd += @('-AudioMode', $AudioMode) }
        $Fwd += @('-Seats', $Seats)
        & $NativeHost @Fwd
        if ($null -eq $LASTEXITCODE) { exit 0 } else { exit $LASTEXITCODE }
    }
    Write-Host "[MultiSeat] WARNING: 32-bit PowerShell and no 64-bit host found --" -ForegroundColor Yellow
    Write-Host "  driver detection and installation may fail. Re-run from a 64-bit PowerShell." -ForegroundColor Yellow
}

# ----------------------------------------------------------------
# Logging  --  transcript goes to prerequisites\prereq.log
# ----------------------------------------------------------------
$LogFile = Join-Path $ScriptDir "prereq.log"
Start-Transcript -Path $LogFile -Force | Out-Null

function Write-Step($msg) { Write-Host "`n[MultiSeat] $msg" -ForegroundColor Cyan }
function Write-OK($msg)   { Write-Host "  OK: $msg" -ForegroundColor Green }
function Write-Skip($msg) { Write-Host "  SKIP: $msg" -ForegroundColor Yellow; $script:Skipped += $msg }

# ----------------------------------------------------------------
# Diagnostic helpers  --  written to log for post-run analysis
# ----------------------------------------------------------------
function Write-AudioDiagnostics {
    Write-Host "`n  [DIAG] Audio device state:" -ForegroundColor DarkGray
    Write-Host "  [DIAG] Win32_SoundDevice matches:" -ForegroundColor DarkGray
    Get-CimInstance Win32_SoundDevice |
        Where-Object { $_.Name -match "CABLE|VB-Audio|Virtual Audio|Hi-Fi Cable|VoiceMeeter" } |
        ForEach-Object { Write-Host "    $($_.Name)" -ForegroundColor DarkGray }
    Write-Host "  [DIAG] PnpDevice MEDIA class (all):" -ForegroundColor DarkGray
    Get-PnpDevice -Class "MEDIA" -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "    [$($_.Status)] $($_.FriendlyName)" -ForegroundColor DarkGray }
    Write-Host "  [DIAG] VoiceMeeter processes:" -ForegroundColor DarkGray
    Get-Process | Where-Object { $_.Name -match "(?i)voice" } |
        ForEach-Object { Write-Host "    PID $($_.Id)  $($_.Name)  $($_.Path)" -ForegroundColor DarkGray }
    Write-Host "  [DIAG] VoiceMeeter install dir:" -ForegroundColor DarkGray
    $vmDir = $null
    foreach ($c in @("C:\Program Files\VB\Voicemeeter","C:\Program Files (x86)\VB\Voicemeeter")) {
        if (Test-Path $c) { $vmDir = $c; break }
    }
    if ($vmDir) {
        Write-Host "    Path: $vmDir" -ForegroundColor DarkGray
        Get-ChildItem $vmDir | Where-Object { $_.Name -match "VBVMAUX_Setup|VBVMVAIO3_Setup" } |
            ForEach-Object { Write-Host "    EXE: $($_.Name)" -ForegroundColor DarkGray }
    } else {
        Write-Host "    (directory not found)" -ForegroundColor DarkGray
    }
}

# ----------------------------------------------------------------
# Download helper  --  fetches a file only if not already on disk.
# Returns the full path on success, $null on failure.
# ----------------------------------------------------------------
function Get-Prerequisite {
    param(
        [string]$Filename,
        [string]$Url,
        [string]$Description
    )
    $dest = Join-Path $ScriptDir $Filename
    if (Test-Path $dest) {
        Write-Host "  Found locally: $Filename" -ForegroundColor DarkGray
        return $dest
    }
    if ($SkipDownload) {
        Write-Host "  Not found and -SkipDownload specified: $Filename" -ForegroundColor Yellow
        return $null
    }
    Write-Host "  Downloading $Description..." -ForegroundColor White
    Write-Host "    $Url" -ForegroundColor DarkGray
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Url -OutFile $dest -UseBasicParsing
        $ProgressPreference = 'Continue'
        return $dest
    } catch {
        Write-Host "  WARNING: Download failed  --  $_" -ForegroundColor Yellow
        Write-Host "  Download manually from: $Url" -ForegroundColor Yellow
        return $null
    }
}

# Helper: extract a zip and return the extraction directory
function Expand-ZipFile($ZipPath) {
    $dir = Join-Path $env:TEMP ("ms_prereq_" + [System.IO.Path]::GetFileNameWithoutExtension($ZipPath))
    Expand-Archive $ZipPath -DestinationPath $dir -Force
    return $dir
}

# Helper: count installed virtual audio devices (VB-CABLE + VoiceMeeter)
function Get-VacCount {
    return @(Get-CimInstance Win32_SoundDevice |
        Where-Object { $_.Name -match "\bCABLE\b|Virtual Audio|Hi-Fi Cable|VoiceMeeter" }).Count
}

# Helper: install VoiceMeeter Potato's extra VAIO drivers (AUX VAIO + VAIO3).
# The main Voicemeeter8Setup.exe only registers VAIO1.  The other two have their
# own setup executables that live in the VoiceMeeter install directory.
function Install-VoiceMeeterPotatoVAIOs($VmExePath) {
    $vmDir = Split-Path $VmExePath
    foreach ($setup in @("VBVMAUX_Setup_x64.exe","VBVMVAIO3_Setup_x64.exe")) {
        $exe = Join-Path $vmDir $setup
        if (Test-Path $exe) {
            Write-Host "  Installing $setup..." -ForegroundColor White
            Start-Process $exe -ArgumentList "-i" -Wait -NoNewWindow
            Write-Host "  Installed $setup" -ForegroundColor Green
        } else {
            Write-Host "  WARNING: $setup not found at $exe" -ForegroundColor Yellow
        }
    }
    $script:NeedsReboot = $true
}

# Helper: install one VB-CABLE pack from a zip file
function Install-VbCablePack($ZipPath, $Label) {
    $dir    = Expand-ZipFile $ZipPath
    $setup  = Get-ChildItem $dir -Recurse | Where-Object { $_.Name -match "^VBCABLE_Setup.*\.exe$" } |
              Select-Object -First 1
    if ($setup) {
        Write-Host "  Installing $Label..." -ForegroundColor White
        Start-Process $setup.FullName -ArgumentList "-i" -Wait -NoNewWindow
        Write-Host "  Installed $Label" -ForegroundColor Green
        $script:Installed += $Label
        $script:NeedsReboot = $true
    } else {
        # Fall back to pnputil with the INF file
        $inf = Get-ChildItem $dir -Recurse | Where-Object { $_.Extension -eq ".inf" } |
               Select-Object -First 1
        if ($inf) {
            Write-Host "  Installing $Label via pnputil..." -ForegroundColor White
            pnputil /add-driver $inf.FullName /install | Out-Null
            $script:Installed += $Label
            $script:NeedsReboot = $true
        } else {
            Write-Host "  WARNING: No installer found in $Label zip" -ForegroundColor Yellow
        }
    }
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
}

# ----------------------------------------------------------------
# 1. ViGEm Bus Driver
# ----------------------------------------------------------------
Write-Step "ViGEm Bus Driver (virtual Xbox 360 controllers)"

# Check for the ViGEmBus PnP device node — this is what Apollo actually connects to.
# A running service without a device node means the installation is broken (common after
# a failed upgrade or sc delete without reboot). Service-only check is insufficient.
#
# Match nodes by InstanceId (ROOT\VIGEMBUS\*) as well as FriendlyName. A node created by the
# SetupAPI fallback below has NO FriendlyName until its driver loads (which needs a reboot), so a
# FriendlyName-only check fails to see it on the next run and creates ANOTHER node — a user
# reported 20 accumulated "Nefarius Virtual Gamepad Emulation Bus" nodes from repeated re-runs
# without rebooting (issue #9). InstanceId is present the moment the node is registered.
#
# Query three sources and merge, because no single one is reliable everywhere: Get-PnpDevice
# returns nothing for root-enumerated nodes under a 32-bit host (see the re-launch guard above,
# and it still misses them on some builds), so WMI and the device Enum registry key act as
# backstops. Every source is normalised to InstanceId / FriendlyName / Status.
function Get-ViGEmNodes {
    $candidates = New-Object System.Collections.Generic.List[object]

    # Source 1: PnP cmdlets — richest data (real driver Status), least reliable availability.
    foreach ($d in @(Get-PnpDevice -PresentOnly:$false -ErrorAction SilentlyContinue)) {
        if ($d.InstanceId -like 'ROOT\VIGEMBUS\*' -or
            $d.FriendlyName -like '*ViGEm*' -or
            $d.FriendlyName -like '*Gamepad Emulation*') {
            $candidates.Add([PSCustomObject]@{
                InstanceId   = $d.InstanceId
                FriendlyName = $d.FriendlyName
                Status       = $d.Status
            })
        }
    }

    # Source 2: WMI — bitness-independent. Note Win32_PnPEntity names its fields PNPDeviceID
    # and Name (it has no InstanceId/FriendlyName), and reports health through
    # ConfigManagerErrorCode, where 0 means "working properly".
    foreach ($d in @(Get-CimInstance -ClassName Win32_PnPEntity -ErrorAction SilentlyContinue)) {
        if ($d.PNPDeviceID -like 'ROOT\VIGEMBUS\*' -or
            $d.Name -like '*ViGEm*' -or
            $d.Name -like '*Gamepad Emulation*') {
            $status = 'Unknown'
            if ($d.ConfigManagerErrorCode -eq 0) { $status = 'OK' }
            $candidates.Add([PSCustomObject]@{
                InstanceId   = $d.PNPDeviceID
                FriendlyName = $d.Name
                Status       = $status
            })
        }
    }

    # Source 3: the device Enum registry key — the node is written here the moment it is
    # registered, before any driver loads, and it is readable at any process bitness. This is
    # the backstop that stops a re-run from stacking up another duplicate node.
    foreach ($k in @(Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\VIGEMBUS' -ErrorAction SilentlyContinue)) {
        $candidates.Add([PSCustomObject]@{
            InstanceId   = "ROOT\VIGEMBUS\$($k.PSChildName)"
            FriendlyName = (Get-ItemProperty $k.PSPath -Name 'FriendlyName' -ErrorAction SilentlyContinue).FriendlyName
            Status       = 'Unknown'
        })
    }

    # Richer sources were added first, so the first entry seen for an InstanceId wins.
    $seen = @{}
    foreach ($c in $candidates) {
        if (-not $c.InstanceId) { continue }
        $key = $c.InstanceId.ToUpperInvariant()
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = $true
            $c
        }
    }
}

$vigemNodes = @(Get-ViGEmNodes)

# Remove duplicate root nodes left by earlier re-runs — keep one, remove the rest. Removing the
# extras lets the remaining node bind the driver cleanly (20 conflicting nodes never report OK).
if ($vigemNodes.Count -gt 1) {
    Write-Host "  Found $($vigemNodes.Count) ViGEmBus device nodes — removing duplicates (keeping one)..." -ForegroundColor Yellow
    foreach ($dup in ($vigemNodes | Select-Object -Skip 1)) {
        & pnputil /remove-device $dup.InstanceId 2>&1 | Out-Null
        Write-Host "  [DIAG] Removed duplicate node: $($dup.InstanceId)" -ForegroundColor DarkGray
    }
    $vigemNodes = @(Get-ViGEmNodes)
}

$vigemDevice = $vigemNodes | Select-Object -First 1

$vigem = Get-Service -Name "ViGEmBus" -ErrorAction SilentlyContinue
if ($vigem) {
    Write-Host "  [DIAG] ViGEmBus service status: $($vigem.Status)" -ForegroundColor DarkGray
}
if ($vigemDevice) {
    Write-Host "  [DIAG] ViGEmBus PnP device: $($vigemDevice.FriendlyName) [$($vigemDevice.Status)] $($vigemDevice.InstanceId)" -ForegroundColor DarkGray
}

# Only 'Error'/'Degraded' count as broken. 'Unknown' must NOT: sources 2 and 3 of Get-ViGEmNodes
# report it for a perfectly healthy node (the registry backstop cannot see driver state at all),
# and that backstop is the only source on hosts where Get-PnpDevice returns nothing for root nodes.
# Treating 'Unknown' as broken would delete a working node on exactly those hosts.
$vigemBadStatuses = @('Error', 'Degraded')

$vigemOk = $false
if ($vigemDevice -and $vigemDevice.Status -eq 'OK') {
    Write-OK "Already installed (service + PnP device OK)"
    $vigemOk = $true
} elseif ($vigemDevice -and $vigemBadStatuses -contains $vigemDevice.Status) {
    # A node that exists but failed to bind a driver — e.g. Code 28 CM_PROB_FAILED_INSTALL, seen on
    # the reference host 2026-09-02, where Apollo logged "ViGEmBus is not installed or running"
    # while the service showed Running and Add/Remove Programs showed 1.22.0.
    #
    # Reinstalling does NOT repair this. The WiX Burn bootstrapper sees its own version already
    # registered and exits 0 in about two seconds without touching the device; `msiexec /fa` repair
    # exits 0 and does nothing either. Both were measured. The node has to be removed so the
    # SetupAPI fallback further down can recreate it and bind the staged INF.
    Write-Host "  ViGEmBus PnP device present but broken (Status: $($vigemDevice.Status)) — removing the dead node..." -ForegroundColor Yellow
    & pnputil /remove-device $vigemDevice.InstanceId 2>&1 | Out-Null
    Write-Host "  [DIAG] Removed failed node: $($vigemDevice.InstanceId)" -ForegroundColor DarkGray
    $vigemDevice = $null
} elseif ($vigem -and $vigem.Status -eq 'Running' -and -not $vigemDevice) {
    Write-Host "  ViGEmBus service running but PnP device missing — reinstalling..." -ForegroundColor Yellow
} elseif ($vigem -and $vigem.Status -ne 'Running') {
    Write-Host "  ViGEmBus service found but not running (Status: $($vigem.Status)) — reinstalling..." -ForegroundColor Yellow
}

if (-not $vigemOk) {
    $exe = Get-ChildItem $ScriptDir -Filter "ViGEmBus*.exe" | Select-Object -First 1
    if (-not $exe) {
        $f = Get-Prerequisite "ViGEmBus_1.22.0_x64_x86_arm64.exe" `
            "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe" `
            "ViGEmBus v1.22.0"
        if ($f) { $exe = Get-Item $f }
    }
    if ($exe) {
        Write-Host "  Installing $($exe.Name) (WiX Burn silent)..."
        # ViGEmBus v1.22.0 ships a WiX Burn bootstrapper, NOT an NSIS installer.
        # Its silent switches are /quiet /norestart; the NSIS /S flag is rejected with an
        # "Invalid command line" dialog that blocks the run (see GitHub issue #6).
        $p = Start-Process $exe.FullName -ArgumentList "/quiet","/norestart" -Wait -PassThru
        Write-Host "  [DIAG] Installer exit code: $($p.ExitCode)" -ForegroundColor DarkGray
    }

    # Re-check device after installer attempt (or if no installer was found).
    # On some machines the WiX installer doesn't create the device node.
    # Fall back to creating the device node directly via SetupAPI ONLY if none exists.
    # Detection is by InstanceId (Get-ViGEmNodes) so a just-created node whose driver hasn't
    # loaded yet is recognised and we don't stack up duplicate nodes on re-runs (issue #9).
    $vigemDevice = @(Get-ViGEmNodes) | Select-Object -First 1

    # Apollo opens the device, not the service, so a node left in a broken state is worth exactly
    # as much as no node at all. Drop it and let the SetupAPI path below build a fresh one.
    if ($vigemDevice -and $vigemBadStatuses -contains $vigemDevice.Status) {
        Write-Host "  [DIAG] Node still $($vigemDevice.Status) after the installer — removing so it can be recreated" -ForegroundColor DarkGray
        & pnputil /remove-device $vigemDevice.InstanceId 2>&1 | Out-Null
        $vigemDevice = $null
    }

    if (-not $vigemDevice) {
        # Find a staged ViGEmBus INF in the DriverStore to use for device node creation.
        $stagedInf = Get-ChildItem 'C:\Windows\System32\DriverStore\FileRepository' -Filter 'vigembus.inf_*' |
            Get-ChildItem -Filter 'ViGEmBus.inf' -ErrorAction SilentlyContinue |
            Select-Object -First 1

        if ($stagedInf) {
            Write-Host "  Installer did not create device node — creating via SetupAPI..." -ForegroundColor Yellow
            Write-Host "  [DIAG] Using staged INF: $($stagedInf.FullName)" -ForegroundColor DarkGray

            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public class ViGEmInstaller {
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }
    [DllImport("setupapi.dll", SetLastError=true)]
    public static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwnd);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name, ref Guid classGuid,
        string desc, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data,
        uint prop, byte[] buf, uint bufSize);
    [DllImport("setupapi.dll", SetLastError=true)]
    public static extern bool SetupDiCallClassInstaller(uint func, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError=true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hwId,
        string inf, uint flags, ref bool reboot);
    public static readonly IntPtr INVALID = new IntPtr(-1);
}
'@ -ErrorAction SilentlyContinue

            $classGuid  = [Guid]'4D36E97D-E325-11CE-BFC1-08002BE10318'
            $hwId       = 'Nefarius\ViGEmBus\Gen1'
            $hwIdBytes  = [System.Text.Encoding]::Unicode.GetBytes($hwId + [char]0 + [char]0)
            $devInfoSet = [ViGEmInstaller]::SetupDiCreateDeviceInfoList([IntPtr]::Zero, [IntPtr]::Zero)
            $devInfo    = New-Object ViGEmInstaller+SP_DEVINFO_DATA
            $devInfo.cbSize = [uint32][System.Runtime.InteropServices.Marshal]::SizeOf($devInfo)

            $ok = [ViGEmInstaller]::SetupDiCreateDeviceInfoW($devInfoSet, 'ViGEmBus', [ref]$classGuid, 'ViGEm Bus Driver', [IntPtr]::Zero, 1, [ref]$devInfo)
            if ($ok) { [ViGEmInstaller]::SetupDiSetDeviceRegistryPropertyW($devInfoSet, [ref]$devInfo, 1, $hwIdBytes, $hwIdBytes.Length) | Out-Null }
            if ($ok) { [ViGEmInstaller]::SetupDiCallClassInstaller(0x19, $devInfoSet, [ref]$devInfo) | Out-Null }
            $reboot = $false
            $ok = [ViGEmInstaller]::UpdateDriverForPlugAndPlayDevicesW([IntPtr]::Zero, $hwId, $stagedInf.FullName, 1, [ref]$reboot)
            [ViGEmInstaller]::SetupDiDestroyDeviceInfoList($devInfoSet) | Out-Null

            # Bind the service to the device node so it survives reboots
            $devKey = 'HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\VIGEMBUS\0000'
            if (Test-Path $devKey) {
                Set-ItemProperty $devKey -Name 'Service' -Value 'ViGEmBus' -ErrorAction SilentlyContinue
            }

            Write-Host "  [DIAG] UpdateDriver result: $ok, reboot: $reboot, error: $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())" -ForegroundColor DarkGray
            Start-Sleep -Seconds 2
            $vigemDevice = @(Get-ViGEmNodes) | Select-Object -First 1
        } else {
            Write-Host "  WARNING: No staged ViGEmBus INF found — cannot create device node." -ForegroundColor Yellow
            Write-Host "  Download ViGEmBus from https://github.com/nefarius/ViGEmBus/releases and re-run." -ForegroundColor Yellow
        }
    }

    $vigem = Get-Service -Name "ViGEmBus" -ErrorAction SilentlyContinue
    if ($vigemDevice -and $vigemDevice.Status -eq 'OK') {
        Write-OK "Installed (PnP device OK, service: $($vigem.Status))"
        $Installed += "ViGEm"
    } elseif ($vigemDevice -and $vigemBadStatuses -contains $vigemDevice.Status) {
        # Recreated and still broken. A reboot will not help here and saying so sends people down
        # the wrong path — the driver package itself is the suspect. Do NOT count this as installed:
        # Apollo will log "ViGEmBus is not installed or running" and gamepads will not work.
        Write-Host "  WARNING: ViGEmBus PnP device is $($vigemDevice.Status) after reinstall — gamepads will NOT work." -ForegroundColor Red
        Write-Host "  Apollo will log 'ViGEmBus is not installed or running'. Check the node with:" -ForegroundColor Yellow
        Write-Host "    Get-PnpDevice -InstanceId '$($vigemDevice.InstanceId)' | Select-Object Status,Problem" -ForegroundColor Yellow
        $Skipped += "ViGEm (device $($vigemDevice.Status) after reinstall)"
    } elseif ($vigem) {
        Write-Host "  WARNING: Service present ($($vigem.Status)) but PnP device not yet visible — reboot may be required." -ForegroundColor Yellow
        $Installed += "ViGEm"
        $NeedsReboot = $true
    } else {
        Write-Host "  WARNING: ViGEmBus not found after install — reboot and re-run to verify." -ForegroundColor Yellow
        $Skipped += "ViGEm (not found post-install)"
    }
}

# ----------------------------------------------------------------
# 2. HidHide
# ----------------------------------------------------------------
Write-Step "HidHide (controller isolation)"

$hidhide = Get-Service -Name "HidHide" -ErrorAction SilentlyContinue
if ($hidhide) {
    # HidHide is a kernel filter driver. It may legitimately show as Stopped before the
    # first reboot after install (driver not yet loaded into the HID stack). Service
    # existence alone is sufficient proof of a successful install -- do not attempt to
    # start or reinstall based on status.
    Write-Host "  [DIAG] HidHide service status: $($hidhide.Status)" -ForegroundColor DarkGray
    Write-OK "Already installed (service status: $($hidhide.Status))"
    if ($hidhide.Status -ne 'Running') {
        Write-Host "  NOTE: Service not yet running -- this is normal before first reboot." -ForegroundColor DarkGray
    }
} else {
    $exe = Get-ChildItem $ScriptDir -Filter "HidHide*.exe" | Select-Object -First 1
    if (-not $exe) {
        $f = Get-Prerequisite "HidHide_1.5.230_x64.exe" `
            "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe" `
            "HidHide v1.5.230"
        if ($f) { $exe = Get-Item $f }
    }
    if ($exe) {
        # Helper: check whether HidHide is installed (service OR CLI on disk)
        function Test-HidHideInstalled {
            $svc = Get-Service -Name "HidHide" -ErrorAction SilentlyContinue
            $cli = "C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe"
            return ($null -ne $svc -or (Test-Path $cli))
        }

        # HidHide 1.5.x is NOT an Inno Setup installer -- it is a custom native installer
        # (no .wixburn section, no Inno/NSIS markers) whose silent switches are
        # /quiet /norestart. The Inno switches this used to pass
        # (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-) are rejected: the process dies
        # immediately with 0xE0000001 (-536870911) before it writes even a /LOG file, and
        # nothing is installed. Verified on 2026-08-06 -- /quiet /norestart installs cleanly
        # (exit 0, driver Running, no reboot needed) on the same machine that failed.
        # Same class of bug as ViGEmBus in issue #6: right installer, wrong switch dialect.
        Write-Host "  Trying silent install of $($exe.Name)..." -ForegroundColor White
        $p = Start-Process $exe.FullName -ArgumentList "/quiet", "/norestart" -Wait -PassThru
        Write-Host "  [DIAG] Silent installer exit code: $($p.ExitCode)" -ForegroundColor DarkGray

        if (Test-HidHideInstalled) {
            Write-OK "Installed (reboot required to activate kernel driver)"
            $Installed += "HidHide"
            $NeedsReboot = $true
        } else {
            Write-Host "  Silent install did not register HidHide — launching interactive installer..." -ForegroundColor Yellow
            Write-Host "  Click through the wizard and accept any driver-signing prompts, then the script will continue." -ForegroundColor Cyan
            # Launch with a visible window and wait for the user to complete the install.
            # -Wait blocks until the installer process exits — no Read-Host needed.
            Start-Process $exe.FullName -Wait
            if (Test-HidHideInstalled) {
                Write-OK "HidHide installed (reboot required to activate kernel driver)"
                $Installed += "HidHide"
                $NeedsReboot = $true
            } else {
                Write-Host "  HidHide not detected after install attempt." -ForegroundColor Yellow
                Write-Host "  It may activate after a reboot — re-run this script post-reboot to verify." -ForegroundColor Yellow
                $Skipped += "HidHide (install attempted — re-run after reboot to verify)"
                $NeedsReboot = $true
            }
        }
    } else {
        Write-Skip "HidHide  --  get from https://github.com/nefarius/HidHide/releases"
    }
}

# ----------------------------------------------------------------
# 3. Virtual audio cables  --  SharedHost audio only
# ----------------------------------------------------------------
# Under PerSession (the service default since 2026-08-19) every seat renders into its own
# RDP "Remote Audio" endpoint, so no host virtual cable is involved at any point. Installing
# them anyway costs the user a reboot -- VoiceMeeter's drivers only register after one -- and
# leaves devices behind that nothing reads. That is not hypothetical tidiness: on the
# reference host a leftover VoiceMeeter device had become the machine's default RECORDING
# device, which silently broke the microphone for an unrelated stream.
#
# Resolution order: the -AudioMode switch, then whatever the deployed service is configured
# for, then PerSession.
function Get-ConfiguredAudioMode {
    foreach ($f in @(
        (Join-Path $env:ProgramFiles 'MultiSeat\appsettings.local.json'),
        (Join-Path $env:ProgramFiles 'MultiSeat\appsettings.json')
    )) {
        if (-not (Test-Path $f)) { continue }
        # Regex rather than ConvertFrom-Json: appsettings.json carries // comments, which
        # ConvertFrom-Json rejects outright on Windows PowerShell 5.1.
        $m = [regex]::Match((Get-Content $f -Raw), '"AudioMode"\s*:\s*"([^"]+)"')
        if ($m.Success) { return @{ Mode = $m.Groups[1].Value; Source = $f } }
    }
    return $null
}

$audioModeSource = 'the built-in default'
if ($AudioMode) {
    $audioModeSource = 'the -AudioMode switch'
} else {
    $foundMode = Get-ConfiguredAudioMode
    if ($foundMode) {
        $AudioMode       = $foundMode.Mode
        $audioModeSource = $foundMode.Source
    } else {
        $AudioMode = 'PerSession'
    }
}

$installAudioCables = ($AudioMode -eq 'SharedHost')

if (-not $installAudioCables) {
    Write-Step "Virtual audio devices (AudioMode = $AudioMode, from $audioModeSource)"
    Write-Skip "VB-CABLE + VoiceMeeter  --  not used under PerSession audio"
    Write-Host "  Each seat gets its own RDP Remote Audio endpoint instead, so no host cable is" -ForegroundColor DarkGray
    Write-Host "  involved. Skipping these also avoids the reboot VoiceMeeter would demand." -ForegroundColor DarkGray
    Write-Host "  Re-run with -AudioMode SharedHost if you need the microphone path, which is the" -ForegroundColor DarkGray
    Write-Host "  one capability PerSession does not have." -ForegroundColor DarkGray
}

if ($installAudioCables) {

    # ----------------------------------------------------------------
    # 3a. VB-CABLE Basic  --  virtual audio device for seat 0
    # ----------------------------------------------------------------
    Write-Step "VB-CABLE Basic (virtual audio device for seat 0)"

    $cableInstalled = [bool]@(Get-CimInstance Win32_SoundDevice |
        Where-Object { $_.Name -match "\bCABLE\b" })

    if ($cableInstalled) {
        Write-OK "Already installed"
    } else {
        $zip = Get-ChildItem $ScriptDir -Filter "VBCABLE_Driver_Pack45.zip" | Select-Object -First 1
        if (-not $zip) {
            $f = Get-Prerequisite "VBCABLE_Driver_Pack45.zip" `
                "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip" `
                "VB-CABLE (basic)"
            if ($f) { $zip = Get-Item $f }
        }
        if ($zip) {
            Install-VbCablePack $zip.FullName "VB-CABLE (basic)"
        } else {
            Write-Skip "VB-CABLE  --  get from https://vb-audio.com/Cable/"
        }
    }

    # ----------------------------------------------------------------
    # 3b. VoiceMeeter Potato  --  3 virtual audio devices for seats 1-3
    # ----------------------------------------------------------------
    Write-Step "VoiceMeeter Potato (virtual audio devices for seats 1-3)"

    # VoiceMeeter editions, Potato first. This section exists for Potato's THREE VAIO devices
    # (VoiceMeeter Input, AUX Input, VAIO3) which seats 1-3 use; Banana provides only two and
    # basic only one.
    #
    # This used to detect VoiceMeeter by looking for voicemeeterpro.exe, which is BANANA, and
    # that was wrong in both directions: on a host with only Banana it reported Potato present
    # and skipped the install, and on a host with Potato it launched Banana to "activate" the
    # devices -- including VAIO3, which Banana does not have -- so the 3-device check below
    # could fail and re-run the installer for nothing. Keep this list in step with
    # AudioRouter.VoiceMeeterExeNames and the copy in scripts\install-service.ps1.
    $vmNames = @(
        "voicemeeter8x64.exe",      # Potato, 64-bit
        "voicemeeter8.exe",         # Potato
        "voicemeeterpro_x64.exe",   # Banana, 64-bit
        "voicemeeterpro.exe",       # Banana
        "voicemeeter_x64.exe",      # basic, 64-bit
        "voicemeeter.exe"           # basic
    )

    function Find-VoiceMeeterExe {
        param([string[]]$Names)

        $roots = @()
        foreach ($rp in @(
            "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter\",
            "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter\"
        )) {
            if (Test-Path $rp) {
                $loc = (Get-ItemProperty $rp -ErrorAction SilentlyContinue).InstallLocation
                if ($loc) { $roots += $loc }
            }
        }
        # The installer uses the 32-bit tree; check it first, and keep the 64-bit one as a fallback.
        $roots += "C:\Program Files (x86)\VB\Voicemeeter"
        $roots += "C:\Program Files\VB\Voicemeeter"

        foreach ($root in $roots) {
            foreach ($name in $Names) {
                $candidate = Join-Path $root $name
                if (Test-Path $candidate) { return $candidate }
            }
        }
        return $null
    }

    function Get-RunningVoiceMeeterName {
        param([string[]]$Names)
        foreach ($n in $Names) {
            $proc = Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($n)) -ErrorAction SilentlyContinue
            if ($proc) { return $proc[0].ProcessName }
        }
        return $null
    }

    $vmExe = Find-VoiceMeeterExe -Names $vmNames
    # Potato is what this section is actually for -- anything else counts as "not installed yet".
    $vmIsPotato = $vmExe -and ((Split-Path $vmExe -Leaf) -match '^voicemeeter8')
    if ($vmExe -and -not $vmIsPotato) {
        Write-Host "  Found $(Split-Path $vmExe -Leaf), which is not Potato -- seats 1-3 need Potato's" -ForegroundColor Yellow
        Write-Host "  three VAIO devices, so Potato will be installed alongside it." -ForegroundColor Yellow
    }

    # Helper: re-run the VoiceMeeter installer to repair/register its audio drivers.
    # Downloading the zip if it is no longer in the script directory.
    # The drivers only appear as audio devices after a subsequent reboot.
    function Register-VoiceMeeterDrivers {
        Write-Host "  Re-running VoiceMeeter installer to register audio drivers..." -ForegroundColor White
        $zip = Get-ChildItem $ScriptDir -Filter "Voicemeeter8Setup*.zip" | Select-Object -First 1
        if (-not $zip) {
            $f = Get-Prerequisite "Voicemeeter8Setup_v3122.zip" `
                "https://download.vb-audio.com/Download_CABLE/Voicemeeter8Setup_v3122.zip" `
                "VoiceMeeter Potato v3.1.2.2"
            if ($f) { $zip = Get-Item $f }
        }
        if (-not $zip) {
            Write-Host "  WARNING: Cannot find VoiceMeeter installer zip to repair drivers." -ForegroundColor Yellow
            Write-Host "  Download from https://vb-audio.com/Voicemeeter/potato.htm and re-run." -ForegroundColor Yellow
            $script:Skipped += "VoiceMeeter audio drivers (installer not found)"
            return
        }
        $dir   = Expand-ZipFile $zip.FullName
        $setup = Get-ChildItem $dir -Recurse |
                 Where-Object { $_.Name -match "Voicemeeter.*Setup.*\.exe$|VoicemeeterPro.*\.exe$" } |
                 Select-Object -First 1
        if ($setup) {
            Start-Process $setup.FullName -ArgumentList "/S" -Wait -NoNewWindow
            Write-OK "Installer re-run -- reboot required for audio devices to appear"
            # The main installer only registers VAIO1.  Explicitly install AUX VAIO + VAIO3.
            # Re-resolve: $vmExe was computed BEFORE this install, so it may be stale or null.
            $vmExe = Find-VoiceMeeterExe -Names $vmNames
            if ($vmExe) {
                Install-VoiceMeeterPotatoVAIOs $vmExe
            }
        } else {
            Write-Host "  WARNING: No installer exe found inside VoiceMeeter zip." -ForegroundColor Yellow
            $script:Skipped += "VoiceMeeter audio drivers (exe not found in zip)"
        }
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        $script:NeedsReboot = $true
    }

    if ($vmIsPotato) {
        # VoiceMeeter's VAIO2 and VAIO3 devices only appear after VoiceMeeter has been
        # launched at least once.  Start it now (idempotent -- second instance exits) and
        # wait a moment so the WDM driver entries settle before we count.
        # Launch the POTATO executable specifically: VAIO3 is a Potato device, so starting
        # Banana here would never register it, and the count below would stay at 2.
        $vmRunning = Get-RunningVoiceMeeterName -Names $vmNames
        if (-not $vmRunning) {
            Write-Host "  Starting VoiceMeeter Potato ($(Split-Path $vmExe -Leaf)) to activate virtual audio devices..." -ForegroundColor White
            Start-Process $vmExe -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 8
        } elseif ($vmRunning -notmatch '^voicemeeter8') {
            # A different edition is already running. Do not start Potato alongside it -- VB's
            # editions share one audio engine and are not meant to run at once.
            #
            # Note what this does and does not mean, because it is easy to overstate: the VAIO
            # devices are registered by the INSTALLERS, so they are present whichever edition is
            # running (measured: 3/3 registered with Banana running). What needs Potato running is
            # the mixing itself -- seat audio routed through its VAIO devices has no engine behind
            # it while a lesser edition holds the driver.
            Write-Host "  NOTE: $vmRunning is running, which is not Potato. The VAIO devices stay" -ForegroundColor Yellow
            Write-Host "  registered, but seat audio through them needs Potato to be the running" -ForegroundColor Yellow
            Write-Host "  mixer -- close it and start $(Split-Path $vmExe -Leaf)." -ForegroundColor Yellow
        } else {
            Write-Host "  VoiceMeeter Potato already running ($vmRunning)" -ForegroundColor DarkGray
        }

        Write-AudioDiagnostics

        $vmDevices = @(Get-CimInstance Win32_SoundDevice |
            Where-Object { $_.Name -match "VoiceMeeter" }).Count
        if ($vmDevices -ge 3) {
            Write-OK "Already installed and audio devices registered ($vmDevices/3)"
        } else {
            Write-Host "  Installed but audio devices not yet registered ($vmDevices/3 found) -- registering..." -ForegroundColor Yellow
            Register-VoiceMeeterDrivers
            Write-Host "  [DIAG] Audio state after installer re-run:" -ForegroundColor DarkGray
            Write-AudioDiagnostics
            Write-OK "VoiceMeeter drivers registered -- reboot required"
            $Installed += "VoiceMeeter audio drivers"
        }
    } else {
        $zip = Get-ChildItem $ScriptDir -Filter "Voicemeeter8Setup*.zip" | Select-Object -First 1
        if (-not $zip) {
            $f = Get-Prerequisite "Voicemeeter8Setup_v3122.zip" `
                "https://download.vb-audio.com/Download_CABLE/Voicemeeter8Setup_v3122.zip" `
                "VoiceMeeter Potato v3.1.2.2"
            if ($f) { $zip = Get-Item $f }
        }
        if ($zip) {
            $dir = Expand-ZipFile $zip.FullName
            $setup = Get-ChildItem $dir -Recurse |
                     Where-Object { $_.Name -match "Voicemeeter.*Setup.*\.exe$|VoicemeeterPro.*\.exe$" } |
                     Select-Object -First 1
            if ($setup) {
                Write-Host "  Installing VoiceMeeter Potato..."
                Start-Process $setup.FullName -ArgumentList "/S" -Wait -NoNewWindow
                $vmExe = Find-VoiceMeeterExe -Names $vmNames
                if ($vmExe) {
                    # Register auto-start at Windows boot (system-wide)
                    $runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
                    Set-ItemProperty -Path $runKey -Name "VoiceMeeter" -Value "`"$vmExe`""
                    Write-OK "Installed and registered to auto-start at boot"
                    $Installed += "VoiceMeeter Potato"
                    # Install AUX VAIO + VAIO3 (not included in the main installer)
                    Install-VoiceMeeterPotatoVAIOs $vmExe
                } else {
                    Write-Host "  Silent install may not have worked -- launching interactive installer..." -ForegroundColor Yellow
                    Start-Process $setup.FullName -Wait
                    $vmExe = Find-VoiceMeeterExe -Names $vmNames
                    if ($vmExe) {
                        $runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
                        Set-ItemProperty -Path $runKey -Name "VoiceMeeter" -Value "`"$vmExe`""
                        Write-OK "Installed and registered to auto-start at boot"
                        $Installed += "VoiceMeeter Potato"
                        Install-VoiceMeeterPotatoVAIOs $vmExe
                    } else {
                        Write-Host "  WARNING: VoiceMeeter not found after install. Check manually." -ForegroundColor Yellow
                        $Skipped += "VoiceMeeter Potato"
                    }
                }
            } else {
                Write-Host "  WARNING: No installer found in VoiceMeeter zip" -ForegroundColor Yellow
                $Skipped += "VoiceMeeter Potato"
            }
            Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        } else {
            Write-Skip "VoiceMeeter Potato  --  get from https://vb-audio.com/Voicemeeter/potato.htm"
        }
    }

    # Verify combined virtual audio device count
    $totalVac = Get-VacCount
    Write-Host "  Total virtual audio devices installed: $totalVac (need $Seats for $Seats seat(s))" -ForegroundColor DarkGray
    if ($totalVac -lt $Seats) {
        Write-Host "  WARNING: Only $totalVac virtual audio device(s) detected — $Seats needed for $Seats seat(s)." -ForegroundColor Yellow
        Write-Host "  Re-run the script after reboot to recheck." -ForegroundColor Yellow
        $Skipped += "Audio devices (need $Seats, have $totalVac)"
    } else {
        Write-OK "Enough virtual audio devices ($totalVac >= $Seats)"
    }

}  # end: if ($installAudioCables)

# ----------------------------------------------------------------
# 4. RDP Wrapper Library
# ----------------------------------------------------------------
Write-Step "RDP Wrapper Library (concurrent RDP sessions)"

# Helper: get the resolved path that TermService's ServiceDll registry value points to.
# RDP Wrapper v1.6.2+ registers the DLL via the TermService ServiceDll key rather than
# copying it to System32.  Detect EITHER installation method.
function Get-RdpWrapperDllPath {
    # Method 1: classic install — dll lives in System32
    $sys32dll = "$env:SystemRoot\System32\rdpwrap.dll"
    if (Test-Path $sys32dll) { return $sys32dll }

    # Method 2: ServiceDll install — dll is registered as the TermService implementation
    $svcDll = (Get-ItemProperty `
        "HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters" `
        -Name "ServiceDll" -ErrorAction SilentlyContinue).ServiceDll
    if ($svcDll) {
        $resolved = [System.Environment]::ExpandEnvironmentVariables($svcDll)
        if ($resolved -match "rdpwrap" -and (Test-Path $resolved)) { return $resolved }
    }

    return $null
}

# Helper: the termsrv.dll version RDPWrap actually keys on.
#
# ⚠️ termsrv.dll's StringFileInfo and its VS_FIXEDFILEINFO DISAGREE. Measured on the reference
# host 2026-09-01: the string said 10.0.26100.8115 while the raw fixed-info said 10.0.26100.8972,
# and 8972 is the section RDPWrap uses. Checking the string can therefore report "covered" off a
# section that is not the one in play — a right answer for the wrong reason, which stays invisible
# until a build where only one of the two exists. Always read FileVersionRaw, and never fall back
# to the string.
function Get-TermSrvVersion {
    $termsrv = Join-Path $env:SystemRoot "System32\termsrv.dll"
    if (-not (Test-Path $termsrv)) { return $null }
    $raw = (Get-Item $termsrv).VersionInfo.FileVersionRaw
    if (-not $raw) { return $null }
    return ("{0}.{1}.{2}.{3}" -f $raw.Major, $raw.Minor, $raw.Build, $raw.Revision)
}

# Helper: does this ini cover that build?
# BOTH sections are required — RDPWrap patches with whatever it finds, so a half-present pair is
# worse than none.
function Test-RdpWrapIniCoverage($iniPath, $version) {
    if (-not $version -or -not (Test-Path $iniPath)) { return $false }
    $ini = Get-Content $iniPath
    $main   = [bool]($ini | Select-String -SimpleMatch -Pattern "[$version]"        -Quiet)
    $slInit = [bool]($ini | Select-String -SimpleMatch -Pattern "[$version-SLInit]" -Quiet)
    return ($main -and $slInit)
}

# Helper: compute offsets for the running termsrv.dll ourselves and append them to the ini.
#
# The community ini (sebaxakerhtc) is maintained by someone else on their own cadence, so between
# a Windows update landing and their next commit there is a window where multi-session RDP is
# dead — and every seat IS an RDP session, so MultiSeat is dead with it. That window is what this
# closes. It only runs when the ini does not already cover the running build, so on a normal host
# it costs nothing.
#
# llccd/RDPWrapOffsetFinder (MIT) was validated against a known-good answer on 2026-09-01: run on
# a build the community ini already covered and multi-session demonstrably worked on, both the
# symbol and _nosymbol builds produced all 20 keys IDENTICAL to that section. The _nosymbol one
# matters most here — it pattern-matches instead of fetching PDBs from Microsoft's symbol server,
# so it still works on a host with no route to those symbols, which is a normal state for a
# machine whose remote access just broke.
function Add-GeneratedRdpWrapOffsets($iniPath, $version) {
    $zipPath = Get-Prerequisite "RDPWrapOffsetFinder-1.0.zip" `
        "https://github.com/llccd/RDPWrapOffsetFinder/releases/download/v1.0/RDPWrapOffsetFinder-1.0.zip" `
        "RDPWrapOffsetFinder v1.0 (generates offsets for an unlisted Windows build)"
    if (-not $zipPath) {
        Write-Host "  Could not obtain RDPWrapOffsetFinder -- cannot generate offsets." -ForegroundColor Yellow
        return $false
    }

    $work = Join-Path $env:TEMP "rdpwrap-offsetfinder"
    try {
        if (!(Test-Path $work)) { New-Item -ItemType Directory -Path $work -Force | Out-Null }
        Expand-Archive $zipPath -DestinationPath $work -Force
    } catch {
        Write-Host "  WARNING: Could not unpack RDPWrapOffsetFinder ($_)" -ForegroundColor Yellow
        return $false
    }

    # x64 explicitly. Running the 32-bit build against a 64-bit termsrv.dll would produce
    # confident, wrong offsets — and wrong offsets patch the wrong bytes.
    $exeDir = Join-Path $work "64bit"
    $exe    = Join-Path $exeDir "RDPWrapOffsetFinder.exe"
    $exeNs  = Join-Path $exeDir "RDPWrapOffsetFinder_nosymbol.exe"
    if (-not (Test-Path $exe)) {
        Write-Host "  RDPWrapOffsetFinder x64 binary not found in the archive." -ForegroundColor Yellow
        return $false
    }

    $termsrv = Join-Path $env:SystemRoot "System32\termsrv.dll"
    Push-Location $exeDir
    try {
        $out = & $exe $termsrv 2>&1 | ForEach-Object { "$_".Trim() }
        if (-not ($out | Where-Object { $_ -match "^SingleUserOffset" })) {
            Write-Host "  Symbol lookup gave nothing usable -- trying the nosymbol build..." -ForegroundColor DarkGray
            if (Test-Path $exeNs) {
                $out = & $exeNs $termsrv 2>&1 | ForEach-Object { "$_".Trim() }
            }
        }
    } catch {
        Write-Host "  WARNING: RDPWrapOffsetFinder failed to run ($_)" -ForegroundColor Yellow
        Pop-Location
        return $false
    }
    Pop-Location

    # 20 keys is the full set (12 patch + 8 SLInit). Anything short means the run half-failed, and
    # appending a partial section is the one outcome worse than appending nothing.
    $keys = @($out | Where-Object { $_ -match "=" })
    if ($keys.Count -lt 20) {
        Write-Host ("  Only {0} offsets produced (expected 20) -- refusing to write a partial section." -f $keys.Count) -ForegroundColor Yellow
        return $false
    }

    # Sanity-check that the tool read the same build we are patching for. If it disagrees, the
    # offsets belong to some other file and must not be written.
    if (-not ($out | Where-Object { $_ -eq "[$version]" })) {
        Write-Host "  Generated section is not for $version -- refusing to write it." -ForegroundColor Yellow
        return $false
    }

    $backup = "$iniPath.$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
    Copy-Item $iniPath $backup -Force

    Add-Content -Path $iniPath -Value "" -Encoding ASCII
    Add-Content -Path $iniPath -Value $out -Encoding ASCII

    Write-Host "  Generated offsets for $version locally and appended them." -ForegroundColor Green
    Write-Host "    backup: $backup" -ForegroundColor DarkGray
    return $true
}

# Helper: copy the ini, make sure it actually covers this build, and restart TermService so the
# new ini takes effect immediately.
function Update-RdpWrapIni($rdpDir) {
    $iniFile = Get-ChildItem $ScriptDir -Filter "rdpwrap.ini" | Select-Object -First 1
    if (-not $iniFile) {
        $f = Get-Prerequisite "rdpwrap.ini" `
            "https://raw.githubusercontent.com/sebaxakerhtc/rdpwrap.ini/master/rdpwrap.ini" `
            "rdpwrap.ini (Windows 11 26100+ patch)"
        if ($f) { $iniFile = Get-Item $f }
    }
    if ($iniFile -and (Test-Path $rdpDir)) {
        $installedIni = Join-Path $rdpDir "rdpwrap.ini"
        Copy-Item $iniFile.FullName $installedIni -Force
        Write-Host "  Updated rdpwrap.ini for current Windows build" -ForegroundColor DarkGray

        # Verify rather than assume. A copied ini is not a covered build: the whole failure mode
        # here is a Windows update the ini has not caught up with.
        $version = Get-TermSrvVersion
        if (-not $version) {
            Write-Host "  WARNING: Could not read termsrv.dll's version -- cannot verify ini coverage." -ForegroundColor Yellow
        } elseif (Test-RdpWrapIniCoverage $installedIni $version) {
            Write-Host "  ini covers termsrv $version" -ForegroundColor DarkGray
        } else {
            Write-Host "  ini does NOT cover termsrv $version -- multi-session RDP would not work." -ForegroundColor Yellow

            # Try a FRESH community ini before generating our own.
            #
            # Get-Prerequisite caches by filename and returns the cached copy forever, which is
            # right for an installer but wrong for this file: rdpwrap.ini's whole job is to track
            # Windows builds. Measured here 2026-09-01 — the cached copy was from April, covered
            # 10.0.26100.8115 and not the running 10.0.26100.8972, and copying it over the
            # installed ini DOWNGRADED a host that had been fine. That is the exact opposite of
            # what "re-run the prereq script to refresh rdpwrap.ini" is supposed to do.
            #
            # Community offsets are preferred over generated ones when both are available: they
            # have been used by many more hosts than this one.
            if (-not $SkipDownload) {
                Write-Host "  Re-downloading the community ini (the cached copy may be stale)..." -ForegroundColor Yellow
                $fresh = Join-Path $env:TEMP "rdpwrap-fresh.ini"
                try {
                    $ProgressPreference = 'SilentlyContinue'
                    Invoke-WebRequest -UseBasicParsing -OutFile $fresh `
                        -Uri "https://raw.githubusercontent.com/sebaxakerhtc/rdpwrap.ini/master/rdpwrap.ini"
                    $ProgressPreference = 'Continue'
                    if (Test-RdpWrapIniCoverage $fresh $version) {
                        Copy-Item $fresh $installedIni -Force
                        # Refresh the cache too, so the next run does not repeat this.
                        Copy-Item $fresh (Join-Path $ScriptDir "rdpwrap.ini") -Force
                        Write-Host "  Fresh community ini covers termsrv $version -- using it." -ForegroundColor Green
                    } else {
                        Write-Host "  The community ini does not cover this build yet either." -ForegroundColor Yellow
                    }
                } catch {
                    Write-Host "  Could not re-download the community ini ($_)" -ForegroundColor Yellow
                }
            }
        }

        # Re-test: the fresh download may have resolved it. Only generate as a last resort.
        if ($version -and -not (Test-RdpWrapIniCoverage $installedIni $version)) {
            Write-Host "  Generating the offsets from this machine's termsrv.dll instead..." -ForegroundColor Yellow
            if (Add-GeneratedRdpWrapOffsets $installedIni $version) {
                if (-not (Test-RdpWrapIniCoverage $installedIni $version)) {
                    Write-Host "  WARNING: still not covered after generating -- seats will not start." -ForegroundColor Yellow
                    $script:Skipped += "RDP Wrapper (no offsets for termsrv $version)"
                }
            } else {
                Write-Host "  Could not generate offsets. Seats will not start until the ini covers" -ForegroundColor Yellow
                Write-Host "  termsrv $version. Run scripts\check-rdpwrap-offsets.ps1 for detail." -ForegroundColor Yellow
                $script:Skipped += "RDP Wrapper (no offsets for termsrv $version)"
            }
        }

        # Restart TermService so rdpwrap.dll re-reads the ini — after any generated section has
        # been appended, or the restart would load the ini we already know is insufficient.
        Write-Host "  Restarting TermService to apply new ini..." -ForegroundColor DarkGray
        try {
            Stop-Service -Name "TermService" -Force -ErrorAction Stop
            Start-Service -Name "TermService" -ErrorAction Stop
            Write-Host "  TermService restarted OK" -ForegroundColor DarkGray
        } catch {
            Write-Host "  WARNING: Could not restart TermService ($_)" -ForegroundColor Yellow
            Write-Host "  Reboot the machine to fully apply the updated rdpwrap.ini." -ForegroundColor Yellow
            $script:NeedsReboot = $true
        }
    }
}

$existingDll = Get-RdpWrapperDllPath
if ($existingDll) {
    Write-OK "Already installed (dll: $existingDll)"
    $rdpDir = Split-Path $existingDll
    Update-RdpWrapIni $rdpDir
} else {
    $zip = Get-ChildItem $ScriptDir -Filter "RDPWrap*.zip" | Select-Object -First 1
    if (-not $zip) {
        $f = Get-Prerequisite "RDPWrap-v1.6.2.zip" `
            "https://github.com/stascorp/rdpwrap/releases/download/v1.6.2/RDPWrap-v1.6.2.zip" `
            "RDPWrap v1.6.2"
        if ($f) { $zip = Get-Item $f }
    }
    if ($zip) {
        $rdpDir = "C:\Program Files\RDP Wrapper"
        if (!(Test-Path $rdpDir)) { New-Item -ItemType Directory -Path $rdpDir -Force | Out-Null }
        Expand-Archive $zip.FullName -DestinationPath $rdpDir -Force

        # Copy ini BEFORE running the installer so RDPWInst picks up the correct offsets.
        Update-RdpWrapIni $rdpDir

        # Use RDPWInst.exe directly -- install.bat ends with `pause` which blocks
        # in non-interactive (scripted) contexts.
        $rdpInst = Get-ChildItem $rdpDir -Filter "RDPWInst.exe" | Select-Object -First 1
        if ($rdpInst) {
            Start-Process $rdpInst.FullName -ArgumentList "-i -o" -Wait -NoNewWindow

            # RDPWInst opens 3389 to the network as part of installing, by running:
            #   netsh advfirewall firewall add rule name="Remote Desktop" dir=in
            #                     protocol=tcp localport=3389 profile=any action=allow
            # (that string is in the RDPWInst binary). The rule it creates is ungrouped, so
            # `Disable-NetFirewallRule -DisplayGroup 'Remote Desktop'` does NOT turn it off and a
            # host that deliberately closed RDP gets it silently reopened by a routine RDPWrap
            # refresh — which is the normal remedy after a Windows update breaks rdpwrap.ini.
            #
            # Not disabled here: some hosts genuinely want RDP reachable, and this script should not
            # quietly decide otherwise. It is said instead, because being reopened without being
            # told is the part that actually costs someone.
            $rdpRule = Get-NetFirewallRule -Enabled True -Direction Inbound -Action Allow -ErrorAction SilentlyContinue |
                       Where-Object { ($_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue).LocalPort -contains '3389' }
            if ($rdpRule) {
                Write-Host "  NOTE: RDPWInst opened inbound TCP/3389 to the network (rule 'Remote Desktop')." -ForegroundColor Yellow
                Write-Host "        MultiSeat does NOT need it — seats connect over loopback, which is not" -ForegroundColor Yellow
                Write-Host "        filtered, and this is verified. With NLA off (see docs/security-posture.md)" -ForegroundColor Yellow
                Write-Host "        an open 3389 is worth closing deliberately:" -ForegroundColor Yellow
                foreach ($r in $rdpRule) {
                    Write-Host ("          Disable-NetFirewallRule -Name '{0}'   # {1}" -f $r.Name, $r.DisplayName) -ForegroundColor Yellow
                }
            }

            $installedDll = Get-RdpWrapperDllPath
            if ($installedDll) {
                Write-OK "Installed (dll: $installedDll)"
                $Installed += "RDP Wrapper"
                $NeedsReboot = $true
            } else {
                Write-Host "  WARNING: RDPWInst ran but rdpwrap.dll not found -- reboot and re-run to verify." -ForegroundColor Yellow
                $Skipped += "RDP Wrapper (dll not found post-install)"
            }
        } else {
            Write-Skip "No RDPWInst.exe found in RDPWrap zip"
        }
    } else {
        Write-Skip "RDPWrap  --  get from https://github.com/stascorp/rdpwrap/releases"
    }
}

# ----------------------------------------------------------------
# 5. Apollo (vibesoftwarecoder fork — mic passthrough + latest Apollo HEAD)
# ----------------------------------------------------------------
Write-Step "Apollo (game streaming server)"

# MultiSeat installs its OWN Apollo (ApolloVibe) in a dedicated directory so it never
# touches a standalone Apollo a user may already run at C:\Program Files\Apollo. The two
# installs stay independent and MultiSeat coexists with the user's Apollo out of the box.
$apolloInstallDir = "C:\Program Files\ApolloVibe"
$apolloPath = "$apolloInstallDir\sunshine.exe"
# Apollo seeds config\apps.json from assets\apps.json on first run; without it Apollo
# exits at startup. A complete install needs BOTH files. Some earlier release zips
# shipped sunshine.exe only (missing assets\), so checking sunshine.exe alone would
# wrongly treat a broken install as complete and skip the fix. See MultiSeat issue #5.
$apolloAssetsSeed = "$apolloInstallDir\assets\apps.json"
# Deliberately version-free, and pointed at /releases/latest/ rather than a tag.
# This used to name an exact tag and filename, so every ApolloVibe release needed a
# matching edit here. That coupling is why installs sat on the June 2026 build until
# 2026-09-05, three source fixes behind. Leave this URL alone and it stays current.
$apolloZipName = "apollovibe-windows-x64.zip"
$apolloDownloadUrl = "https://github.com/vibesoftwarecoder/ApolloVibe/releases/latest/download/$apolloZipName"

$apolloComplete = (Test-Path $apolloPath) -and (Test-Path $apolloAssetsSeed)

if ($apolloComplete) {
    Write-OK "Already installed at $apolloPath"
} else {
    if (Test-Path $apolloPath) {
        Write-Host "  Existing Apollo install is incomplete (missing assets\apps.json) -- re-extracting." -ForegroundColor Yellow
        Write-Host "  If a stale zip in '$ScriptDir' is also incomplete, delete it so the fixed release is re-downloaded." -ForegroundColor Yellow
    }
    $zip = Get-ChildItem $ScriptDir | Where-Object { $_.Name -eq $apolloZipName } | Select-Object -First 1
    if (-not $zip) {
        $f = Get-Prerequisite $apolloZipName $apolloDownloadUrl "ApolloVibe, latest release (vibesoftwarecoder fork)"
        if ($f) { $zip = Get-Item $f }
    }
    if ($zip) {
        Write-Host "  Extracting $($zip.Name) to $apolloInstallDir ..."
        New-Item -ItemType Directory -Path $apolloInstallDir -Force | Out-Null
        Expand-Archive -Path $zip.FullName -DestinationPath $apolloInstallDir -Force
        if ((Test-Path $apolloPath) -and (Test-Path $apolloAssetsSeed)) {
            Write-OK "Installed"
            $Installed += "Apollo"
        } elseif (Test-Path $apolloPath) {
            Write-Host "  WARNING: Apollo extracted but assets\apps.json is missing -- the release zip is incomplete." -ForegroundColor Yellow
            Write-Host "  Apollo will fail to start. Re-download the latest release asset and try again." -ForegroundColor Yellow
        } else {
            Write-Host "  WARNING: Apollo not found at $apolloPath after extraction." -ForegroundColor Yellow
            Write-Host "  Check the zip structure and set ApolloExePath in appsettings.json." -ForegroundColor Yellow
        }
    } else {
        Write-Skip "Apollo  --  download from https://github.com/vibesoftwarecoder/ApolloVibe/releases"
    }
}

# ----------------------------------------------------------------
# 6. Persistent Virtual Display Driver (VirtualDrivers/Virtual-Display-Driver,
#    aka MttVDD / itsmikethetech VDD)
#
# This is NOT Apollo's SudoVDA — Apollo ships its own SudoVDA driver
# inside the Apollo package (step 5) and creates one virtual monitor per
# Apollo instance on demand. This driver is a SEPARATE always-on IddCx
# display that appears at boot, so headless hosts have a desktop for
# RustDesk / AnyDesk / RDP to attach to before any seat is provisioned.
#
# Without this driver, a headless machine (no physical monitor) has no
# console-session display at boot — remote desktop tools won't see
# anything until Apollo + a seat have started. With it, you get a
# persistent display at boot, and Apollo's SudoVDA still handles
# per-seat isolation.
#
# Installed device shows up as "Root\MttVDD" in Device Manager.
# Display count is configured via vdd_settings.xml (see below).
# ----------------------------------------------------------------
Write-Step "Persistent Virtual Display Driver (MttVDD — for headless boot)"

function Test-PersistentVddInstalled {
    return [bool](Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -match "VDD|Virtual Display|MTT" } |
        Where-Object { $_.FriendlyName -notmatch "SudoMaker" })
}

if (Test-PersistentVddInstalled) {
    $dev = (Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -match "VDD|Virtual Display|MTT" } |
        Where-Object { $_.FriendlyName -notmatch "SudoMaker" } |
        Select-Object -First 1).FriendlyName
    Write-OK "Already installed ($dev)"
} else {
    $setup = Get-ChildItem $ScriptDir | Where-Object { $_.Name -match "Virtual\.Display\.Driver.*\.exe|^VDD.*\.exe$" } |
             Select-Object -First 1
    if (-not $setup) {
        $f = Get-Prerequisite "Virtual.Display.Driver-setup-x64.exe" `
            "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.5.2/Virtual.Display.Driver-v25.05.03-setup-x64.exe" `
            "Persistent VDD (VirtualDrivers/Virtual-Display-Driver v25.5.2)"
        if ($f) { $setup = Get-Item $f }
    }
    if ($setup) {
        Write-Host "  Installing $($setup.Name) (silent)..." -ForegroundColor White
        Start-Process $setup.FullName -ArgumentList "/S" -Wait -NoNewWindow

        if (Test-PersistentVddInstalled) {
            Write-OK "Installed (reboot required to activate virtual display)"
            $Installed += "VirtualDisplayDriver"
            $NeedsReboot = $true
        } else {
            # Silent install (/S) is not always supported — fall back to interactive
            Write-Host "  Silent install did not register the device — launching interactive installer..." -ForegroundColor Yellow
            Write-Host "  Click through the installer, accept any driver signing prompts, then press Enter here." -ForegroundColor Cyan
            Start-Process $setup.FullName -Wait

            if (Test-PersistentVddInstalled) {
                Write-OK "Installed (reboot required to activate virtual display)"
                $Installed += "VirtualDisplayDriver"
                $NeedsReboot = $true
            } else {
                Write-Host ""
                Write-Host "  ╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Yellow
                Write-Host "  ║  Persistent VDD not installed — headless boot will have no  ║" -ForegroundColor Yellow
                Write-Host "  ║  display until a seat is provisioned. Apollo's own SudoVDA  ║" -ForegroundColor Yellow
                Write-Host "  ║  still handles per-seat virtual displays.                   ║" -ForegroundColor Yellow
                Write-Host "  ╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Yellow
                Write-Host "  Try installing manually: $($setup.FullName)" -ForegroundColor Yellow
                Write-Host "  Then re-run this script." -ForegroundColor Yellow
                $Skipped += "VirtualDisplayDriver (install failed — needed for headless boot)"
                $NeedsReboot = $true
            }
        }
    } else {
        Write-Host ""
        Write-Host "  ╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Yellow
        Write-Host "  ║  Persistent VDD installer not found — needed for headless  ║" -ForegroundColor Yellow
        Write-Host "  ║  boot. Apollo's SudoVDA still handles per-seat displays.    ║" -ForegroundColor Yellow
        Write-Host "  ╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Yellow
        Write-Host "  Download from: https://github.com/VirtualDrivers/Virtual-Display-Driver/releases" -ForegroundColor Yellow
        Write-Host "  Place the installer in: $ScriptDir" -ForegroundColor Yellow
        Write-Host "  Then re-run this script." -ForegroundColor Yellow
        $Skipped += "VirtualDisplayDriver (installer not found — needed for headless boot)"
    }
}

# ── Configure virtual display count ─────────────────────────────────
# vdd_settings.xml tells the driver how many virtual monitors to create
# at startup. Without it the driver defaults to 1, so seats 1-3 would
# have no isolated display. We write it to request exactly $Seats
# monitors. Re-running the script is safe — it only overwrites if the
# count is wrong. A reboot is required for count changes to take effect.
#
# Config location: C:\VirtualDisplayDriver\vdd_settings.xml
# (installed alongside the driver by the setup exe)
# ────────────────────────────────────────────────────────────────────
Write-Step "Persistent VDD display count  --  configuring $Seats virtual display(s)"

# The installer places config under one of these paths depending on version
$vddConfigPaths = @(
    "C:\VirtualDisplayDriver",
    "C:\Program Files\VirtualDisplayDriver",
    "C:\Program Files (x86)\VirtualDisplayDriver"
)
$vddConfigDir = $vddConfigPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

# If not installed yet (pre-reboot), create the expected dir so the
# config is ready when the driver first loads after reboot.
if (-not $vddConfigDir) {
    $vddConfigDir = "C:\VirtualDisplayDriver"
    New-Item -ItemType Directory -Path $vddConfigDir -Force | Out-Null
    Write-Host "  Created $vddConfigDir (driver will read config here after reboot)" -ForegroundColor DarkGray
}

$vddConfigPath = Join-Path $vddConfigDir "vdd_settings.xml"

# Check whether the current file already has the right count.
$currentCount = 0
if (Test-Path $vddConfigPath) {
    try {
        [xml]$existingXml = Get-Content $vddConfigPath -ErrorAction Stop
        $currentCount = [int]($existingXml.vdd.monitors.count)
    } catch {
        $currentCount = 0
    }
}

if ($currentCount -eq $Seats) {
    Write-OK "vdd_settings.xml already configured for $Seats display(s)"
} else {
    # Build XML that requests $Seats monitors at common resolutions.
    # The driver uses this list to populate its supported modes EDID;
    # the service's ResolutionNegotiator picks the actual session resolution
    # at seat-creation time via ChangeDisplaySettingsEx.
    $xmlContent = @"
<vdd>
  <monitors>
    <count>$Seats</count>
  </monitors>
  <resolutions>
    <resolution width="1920" height="1080" refreshRate="60"/>
    <resolution width="1920" height="1080" refreshRate="120"/>
    <resolution width="2560" height="1440" refreshRate="60"/>
    <resolution width="2560" height="1440" refreshRate="120"/>
    <resolution width="1280" height="720"  refreshRate="60"/>
  </resolutions>
  <options>
    <HardwareCursor>true</HardwareCursor>
  </options>
</vdd>
"@

    Set-Content -Path $vddConfigPath -Value $xmlContent -Encoding UTF8
    Write-OK "vdd_settings.xml written: $Seats virtual display(s) at $vddConfigPath"

    # Attempt a live reload by restarting the VDD driver service if it's running.
    # If the service isn't running yet (pre-first-reboot), this is a no-op.
    $vddService = Get-Service -Name "VirtualDisplayDriver" -ErrorAction SilentlyContinue
    if ($vddService -and $vddService.Status -eq 'Running') {
        try {
            Restart-Service -Name "VirtualDisplayDriver" -Force -ErrorAction Stop
            Write-Host "  Restarted VirtualDisplayDriver service to apply new display count" -ForegroundColor DarkGray
        } catch {
            Write-Host "  Could not restart VirtualDisplayDriver service ($_)" -ForegroundColor DarkGray
            Write-Host "  Reboot required to apply new display count." -ForegroundColor Yellow
            $NeedsReboot = $true
        }
    } else {
        $NeedsReboot = $true
    }
    $Installed += "VirtualDisplayDriver config ($Seats displays)"
}

# ----------------------------------------------------------------
# 7. .NET 9 SDK  (includes runtime + dotnet publish for service build)
# ----------------------------------------------------------------
Write-Step ".NET 9 SDK"

$sdkOk = if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    dotnet --list-sdks 2>$null | Where-Object { $_ -match "^9\." }
} else { $null }
if ($sdkOk) {
    Write-OK "Already installed ($($sdkOk | Select-Object -First 1))"
} else {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host "  Installing via winget..."
        winget install --id Microsoft.DotNet.SDK.9 --silent --accept-source-agreements --accept-package-agreements
        Write-OK "Installed via winget"
        $Installed += ".NET 9 SDK"
    } else {
        $installer = Get-ChildItem $ScriptDir | Where-Object { $_.Name -match "dotnet-sdk.*win-x64|dotnet-hosting" } | Select-Object -First 1
        if ($installer) {
            Start-Process $installer.FullName -ArgumentList "/install /quiet /norestart" -Wait -NoNewWindow
            Write-OK "Installed"
            $Installed += ".NET 9 SDK"
        } else {
            Write-Host "  WARNING: winget not available. Install .NET 9 SDK from:" -ForegroundColor Yellow
            Write-Host "    https://dotnet.microsoft.com/download/dotnet/9.0" -ForegroundColor Yellow
            $Skipped += ".NET 9 SDK"
        }
    }
}

# ----------------------------------------------------------------
# 8. Node.js  (required to build the dashboard)
# ----------------------------------------------------------------
Write-Step "Node.js (dashboard build)"

$nodeOk = Get-Command node -ErrorAction SilentlyContinue
if ($nodeOk) {
    Write-OK "Already installed ($(node --version))"
} else {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host "  Installing via winget..."
        winget install --id OpenJS.NodeJS.LTS --silent --accept-source-agreements --accept-package-agreements
        Write-OK "Installed via winget"
        $Installed += "Node.js"
    } else {
        Write-Host "  WARNING: winget not available. Install Node.js 20+ from https://nodejs.org/" -ForegroundColor Yellow
        $Skipped += "Node.js"
    }
}

# ----------------------------------------------------------------
# 9. Windows configuration (Remote Desktop + firewall)
# ----------------------------------------------------------------
Write-Step "Windows configuration (Remote Desktop + firewall)"

$rdpReg = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server" `
    -Name "fDenyTSConnections" -ErrorAction SilentlyContinue
if ($rdpReg.fDenyTSConnections -eq 1) {
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server" `
        -Name "fDenyTSConnections" -Value 0
    Enable-NetFirewallRule -DisplayGroup "Remote Desktop" -ErrorAction SilentlyContinue
    Write-OK "Remote Desktop enabled"
} else {
    Write-OK "Remote Desktop already enabled"
}

# Disable NLA (Network Level Authentication) requirement for the RDP listener.
# NLA requires a trusted certificate for the loopback connection (127.0.0.2).
# When NLA is required, mstsc shows a pre-connection certificate warning dialog
# which blocks the session from being created (the dialog has no one to click it).
# Setting UserAuthentication=0 lets mstsc connect without pre-authentication,
# which is safe for loopback-only connections like MultiSeat uses.
$rdpTcpKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp"
$rdpTcpReg = Get-ItemProperty $rdpTcpKey -ErrorAction SilentlyContinue
if ($rdpTcpReg.UserAuthentication -ne 0) {
    Set-ItemProperty $rdpTcpKey -Name "UserAuthentication" -Value 0
    Set-ItemProperty $rdpTcpKey -Name "SecurityLayer"      -Value 1  # TLS only (no NLA)
    Write-OK "NLA disabled for RDP listener (required for loopback session creation)"
} else {
    Write-OK "NLA already disabled"
}

# Suppress the RDP client certificate trust dialog for loopback connections.
# MultiSeat launches mstsc via CreateProcessAsUser (no interactive user to click dialogs).
# Setting AuthenticationLevel = 0 via machine policy is equivalent to
# authentication level:i:0 in a .rdp file — mstsc connects without verifying the
# server certificate. Safe here because 127.0.0.2 is always the local machine.
$rdpClientPolicyKey = "HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services"
if (-not (Test-Path $rdpClientPolicyKey)) {
    New-Item -Path $rdpClientPolicyKey -Force | Out-Null
}
$current = Get-ItemProperty $rdpClientPolicyKey -Name "AuthenticationLevel" -ErrorAction SilentlyContinue
if ($current.AuthenticationLevel -ne 0) {
    Set-ItemProperty $rdpClientPolicyKey -Name "AuthenticationLevel" -Value 0 -Type DWord
    Write-OK "RDP client cert dialog suppressed (AuthenticationLevel=0 machine policy)"
} else {
    Write-OK "RDP client cert dialog already suppressed"
}

if (-not (Get-NetFirewallRule -DisplayName "MultiSeat API" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "MultiSeat API" -Direction Inbound `
        -Protocol TCP -LocalPort 9550 -Action Allow | Out-Null
    Write-OK "Firewall: port 9550 opened (dashboard)"
} else {
    Write-OK "Firewall: port 9550 already open"
}

if (-not (Get-NetFirewallRule -DisplayName "MultiSeat Streaming" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "MultiSeat Streaming" -Direction Inbound `
        -Protocol TCP -LocalPort 47984-48063 -Action Allow | Out-Null
    New-NetFirewallRule -DisplayName "MultiSeat Streaming UDP" -Direction Inbound `
        -Protocol UDP -LocalPort 47984-48063 -Action Allow | Out-Null
    Write-OK "Firewall: ports 47984-48063 opened (streaming)"
} else {
    Write-OK "Firewall: streaming ports already open"
}

# ----------------------------------------------------------------
# Summary
# ----------------------------------------------------------------
Write-Host ""
Write-Host ("=" * 60) -ForegroundColor White
Write-Host "[MultiSeat] Prerequisites complete" -ForegroundColor Cyan
Write-Host ("=" * 60) -ForegroundColor White

if ($Installed.Count -gt 0) {
    Write-Host "`n  Installed:" -ForegroundColor Green
    $Installed | ForEach-Object { Write-Host "    - $_" -ForegroundColor Green }
}
if ($Skipped.Count -gt 0) {
    Write-Host "`n  Needs attention:" -ForegroundColor Yellow
    $Skipped | ForEach-Object { Write-Host "    - $_" -ForegroundColor Yellow }
}

if ($NeedsReboot -and -not $SkipReboot) {
    Write-Host "`n  A REBOOT is required before continuing." -ForegroundColor Magenta
    # Read-Host THROWS under -NonInteractive, which failed the whole script at the very last step
    # even when every install before it succeeded — so a scripted or CI run reported failure for a
    # prompt it was never going to answer. Ask if we can; otherwise say what to do and exit clean.
    $reboot = $null
    try {
        $reboot = Read-Host "  Reboot now? (y/N)"
    } catch {
        Write-Host "  (non-interactive session — not prompting)" -ForegroundColor DarkGray
        Write-Host "  Reboot when convenient, then re-run this script to confirm clean." -ForegroundColor Magenta
    }
    if ($reboot -eq 'y') { Restart-Computer -Force }
} elseif ($NeedsReboot) {
    Write-Host "`n  A REBOOT is required before continuing." -ForegroundColor Magenta
}

Write-Host "`n  Next step: Run scripts\install-service.ps1 to build and deploy MultiSeat." -ForegroundColor Cyan
Write-Host "  Log written to: $LogFile" -ForegroundColor DarkGray
Write-Host ""

Stop-Transcript | Out-Null
