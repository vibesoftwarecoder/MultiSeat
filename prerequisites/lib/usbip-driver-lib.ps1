# usbip-driver-lib.ps1 - the logic behind prerequisites\install-usbip-driver.ps1.
#
# Dot-source this file; it defines functions and runs nothing. It is separate from the
# wrapper so tests can load it and replace every side effect (download, signature check,
# installer, driver enumeration, boot-state reads) with a fake.
#
# USER RULE (stated 2026-10-23): use usbip-win2 ONLY while its kernel drivers are signed by
# Microsoft. If they are not, STOP. The fallback is that we build and sign our own driver.
# Test-signing mode is never an option on the streaming host. Nothing in this file tries to
# work around a failed signature check, and nothing in it restarts the machine.
#
# Keep this file PowerShell 5.1 compatible (no ?. ?? or ternary) and ASCII only.

$script:UsbipPin = @{
    Version         = '0.9.8.1'
    Tag             = 'v.0.9.8.1'
    Asset           = 'USBip-0.9.8.1-x64.exe'
    Url             = 'https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe'
    Sha256          = '38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A'
    # Verified 2026-10-23: Authenticode Valid, issuer GlobalSign GCC R45 EV CodeSigning CA 2020.
    # Compared with the certificate's SimpleName (X509Certificate2.GetNameInfo), never a parsed subject string.
    InstallerSigner = 'Cloudyne Systems (Scheibling Consulting AB)'
    # The kernel drivers (usbip2_ude.sys, usbip2_filter.sys) and their catalogs.
    DriverSigner    = 'Microsoft Windows Hardware Compatibility Publisher'
    # Driver signatures must also chain to Microsoft Root Certificate Authority 2010 (thumbprint read from
    # the real chain of a Microsoft-signed driver on a Windows 11 host, 2026-10-24) ...
    DriverRootThumbprint = '3B1EFD3A66EA28B16697394703A72CA340A05BD5'
    # ... and the leaf must carry a Windows hardware driver EKU: 10.3.5 WHQL, 10.3.5.1 attested, 10.3.39 extended verification.
    DriverEkus      = @('1.3.6.1.4.1.311.10.3.5', '1.3.6.1.4.1.311.10.3.5.1', '1.3.6.1.4.1.311.10.3.39')
}

# Inno Setup flags. The installer has AlwaysRestart=yes; /NORESTART is what keeps it from
# rebooting the machine, so it must always be here. /LOG is appended per run.
$script:UsbipInstallerArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')

# Exit codes the wrapper returns.
#   0 = installed or already present, drivers verified Microsoft-signed
#   1 = refused or failed before/while installing (hash, signature, test-signing, ...)
#   2 = the user rule fired: an installed kernel driver is NOT verified Microsoft-signed
$script:UsbipExitOk = 0
$script:UsbipExitRefused = 1
$script:UsbipExitRule = 2

# ---------------------------------------------------------------------------------------
# Certificates. Never parse a subject string: an attacker controls what is inside a quoted
# RDN value. Use the certificate object.
# ---------------------------------------------------------------------------------------

function Get-UsbipCertFacts {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Cert)
    $f = @{ SimpleName = $null; Thumbprint = $null; Ekus = @(); RootThumbprint = $null; ChainOk = $false; ChainStatus = @() }
    if (-not $Cert) { return $f }
    $f.SimpleName = $Cert.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    $f.Thumbprint = $Cert.Thumbprint
    $ekus = @()
    foreach ($ext in $Cert.Extensions) {
        if ($ext -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($oid in $ext.EnhancedKeyUsages) { $ekus += $oid.Value }
        }
    }
    $f.Ekus = $ekus

    # Revocation: ONLINE, whole chain. An unreachable revocation server (OfflineRevocation /
    # RevocationStatusUnknown) is tolerated so an offline host can still install, but a certificate
    # that IS reported revoked fails. NotTimeValid is tolerated because driver certificates are short
    # lived and Windows has already validated the signature against its timestamp (Status = Valid);
    # the root pin below is what carries the trust decision here. Anything else (UntrustedRoot,
    # PartialChain, NotSignatureValid, ...) fails.
    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    try {
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(10)
        $null = $chain.Build($Cert)
        $allowed = @('NoError', 'NotTimeValid', 'OfflineRevocation', 'RevocationStatusUnknown')
        $statuses = @($chain.ChainStatus | ForEach-Object { [string]$_.Status })
        $f.ChainStatus = $statuses
        $bad = @($statuses | Where-Object { $allowed -notcontains $_ })
        $n = $chain.ChainElements.Count
        if ($n -gt 0) { $f.RootThumbprint = $chain.ChainElements[$n - 1].Certificate.Thumbprint }
        $f.ChainOk = ($bad.Count -eq 0 -and $n -ge 2)
    }
    catch { $f.ChainOk = $false }
    finally { $chain.Reset() }
    return $f
}

function Get-UsbipSignatureFacts {
    param([string]$Path)
    $s = Get-AuthenticodeSignature -LiteralPath $Path
    $facts = Get-UsbipCertFacts $s.SignerCertificate
    $facts.Status = [string]$s.Status
    return $facts
}

function Test-UsbipSignedBy {
    # $true only for status Valid + the exact signer name. Kind 'Driver' also requires the pinned
    # Microsoft root and a hardware-driver EKU.
    param([string]$Path, [hashtable]$Pin, [string]$Kind, [hashtable]$Seams, [ref]$Why)
    $facts = & $Seams.GetSignature $Path
    if (-not $facts -or $facts.Status -ne 'Valid') {
        $st = 'no signature info'
        if ($facts) { $st = $facts.Status }
        $Why.Value = "signature status is '$st', not Valid"
        return $false
    }
    $expected = $Pin.InstallerSigner
    if ($Kind -eq 'Driver') { $expected = $Pin.DriverSigner }
    if (-not $facts.SimpleName -or ([string]$facts.SimpleName) -cne $expected) {
        $Why.Value = "signer is '$($facts.SimpleName)', expected exactly '$expected'"
        return $false
    }
    if ($Kind -eq 'Driver') {
        if (-not $facts.ChainOk) {
            $Why.Value = "certificate chain is not acceptable ($(@($facts.ChainStatus) -join ','))"
            return $false
        }
        if (([string]$facts.RootThumbprint).ToUpperInvariant() -ne $Pin.DriverRootThumbprint.ToUpperInvariant()) {
            $Why.Value = "chain root is '$($facts.RootThumbprint)', not Microsoft Root Certificate Authority 2010"
            return $false
        }
        $has = $false
        foreach ($e in @($facts.Ekus)) { if ($Pin.DriverEkus -contains $e) { $has = $true } }
        if (-not $has) {
            $Why.Value = 'certificate has no Windows hardware driver EKU'
            return $false
        }
    }
    return $true
}

# ---------------------------------------------------------------------------------------
# Staging directory: protected DACL, created BEFORE any file goes into it.
# ---------------------------------------------------------------------------------------

function Get-UsbipDirSecurity {
    param([string]$Path)
    $di = New-Object System.IO.DirectoryInfo $Path
    $t = [Type]::GetType('System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl')
    if ($t) { return [System.IO.FileSystemAclExtensions]::GetAccessControl($di) }
    return $di.GetAccessControl()
}

function Set-UsbipDirSecurity {
    param([string]$Path, $Security)
    $di = New-Object System.IO.DirectoryInfo $Path
    $t = [Type]::GetType('System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl')
    if ($t) { [System.IO.FileSystemAclExtensions]::SetAccessControl($di, $Security) }
    else { $di.SetAccessControl($Security) }
}

function Get-UsbipStagingAclProblems {
    # Empty list = the directory is only reachable by SYSTEM and Administrators.
    param([string]$Path)
    $problems = @()
    $sec = Get-UsbipDirSecurity $Path
    if (-not $sec.AreAccessRulesProtected) { $problems += 'the DACL still inherits from its parent' }
    $okSids = @('S-1-5-18', 'S-1-5-32-544')
    foreach ($r in $sec.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($r.AccessControlType -eq 'Allow' -and $okSids -notcontains $r.IdentityReference.Value) {
            $problems += "allow entry for $($r.IdentityReference.Value)"
        }
    }
    return $problems
}

function Get-UsbipStagingRoot {
    # Machine-wide, never the user profile or %TEMP% (both are writable by the user's own non-elevated processes).
    return $env:ProgramData
}

function New-UsbipStagingDir {
    # A new, unpredictably named directory directly under a machine-wide location (ProgramData by
    # default, never the user profile or %TEMP%), restricted to SYSTEM and Administrators with
    # inheritance removed, THEN verified empty and verified protected, before the caller creates any file.
    param([string]$Root)
    if (-not $Root) { $Root = Get-UsbipStagingRoot }
    if (-not $Root) { throw 'no machine-wide staging root (ProgramData) is available' }
    $dir = Join-Path $Root ('MultiSeat-usbip-' + [guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $dir) { throw 'staging directory already exists' }
    $null = New-Item -ItemType Directory -Path $dir
    $sec = New-Object System.Security.AccessControl.DirectorySecurity
    $sec.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $id = New-Object System.Security.Principal.SecurityIdentifier $sid
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($id, 'FullControl', $inherit, 'None', 'Allow')
        $sec.AddAccessRule($rule)
    }
    Set-UsbipDirSecurity $dir $sec
    $problems = @(Get-UsbipStagingAclProblems $dir)
    if ($problems.Count -gt 0) { throw ('staging directory is not private: ' + ($problems -join '; ')) }
    return $dir
}

function Open-UsbipHeldFile {
    # One handle that stays open from the hash check until the installer has finished: readers are
    # allowed, writers and deleters are not, so the verified bytes cannot be swapped underneath us.
    param([string]$Path)
    return [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
}

function Get-UsbipStreamSha256 {
    param([System.IO.Stream]$Stream)
    $null = $Stream.Seek(0, [System.IO.SeekOrigin]::Begin)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [System.BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-', '') }
    finally { $sha.Dispose() }
}

# ---------------------------------------------------------------------------------------
# Boot state: refuse when test-signing or integrity checks are off, or when we cannot tell.
# ---------------------------------------------------------------------------------------

function Get-UsbipBootVerdict {
    param([hashtable]$Seams)
    $opts = $null
    $bcd = $null
    try { $opts = & $Seams.ReadStartOptions } catch { $opts = $null }
    try { $bcd = & $Seams.ReadBcd } catch { $bcd = $null }
    if ($null -eq $opts -and $null -eq $bcd) {
        return @{ Refuse = $true; Reason = 'cannot determine the boot signing state (neither the running boot options nor the BCD could be read)' }
    }
    # The running boot's options, language independent.
    if ($null -ne $opts -and ([string]$opts) -match '(?i)(^|[\s,])(TESTSIGNING|DISABLE_INTEGRITY_CHECKS|NOINTEGRITYCHECKS)(?=$|[\s,=])') {
        return @{ Refuse = $true; Reason = "Windows is running with $($Matches[2]) (test-signing / integrity checks off)" }
    }
    # The stored (next boot) configuration. Element names are not localized, values are, so anything
    # that is not clearly "no" counts as on.
    if ($bcd) {
        foreach ($m in [regex]::Matches([string]$bcd, '(?im)^\s*(testsigning|nointegritychecks)\s+(\S+)')) {
            if ($m.Groups[2].Value -notmatch '^(?i:no|off|false|0)$') {
                return @{ Refuse = $true; Reason = "the boot configuration sets $($m.Groups[1].Value) = $($m.Groups[2].Value)" }
            }
        }
    }
    return @{ Refuse = $false; Reason = $null }
}

# ---------------------------------------------------------------------------------------
# Side effects, all replaceable.
# ---------------------------------------------------------------------------------------

function New-UsbipDefaultSeams {
    @{
        IsAdmin = {
            $id = [Security.Principal.WindowsIdentity]::GetCurrent()
            (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)
        }
        # The running boot's options. Present in every language. $null = could not read.
        ReadStartOptions = {
            try {
                $v = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name 'SystemStartOptions' -ErrorAction Stop
                return [string]$v.SystemStartOptions
            } catch { return $null }
        }
        # The stored boot configuration (next boot). $null = could not read.
        ReadBcd = {
            $out = & bcdedit.exe /enum '{current}' 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0 -or -not $out.Trim()) { return $null }
            return $out
        }
        # Returns @{ Version = <string or $null>; DriverFiles = <string[]> }. Two sources, unioned:
        # DISM (Get-WindowsDriver) and the driver store folders themselves. Throws only when DISM
        # failed AND the store showed nothing.
        GetInstalledState = {
            $version = $null
            $root = $env:ProgramW6432
            if (-not $root) { $root = $env:ProgramFiles }
            $exe = Join-Path $root 'USBip\usbip.exe'
            if (Test-Path -LiteralPath $exe) {
                $version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
            }
            $dirs = @()
            $dismError = $null
            try {
                $pkgs = @(Get-WindowsDriver -Online -All -ErrorAction Stop |
                    Where-Object { $_.OriginalFileName -match '(?i)usbip2' })
                foreach ($p in $pkgs) { $dirs += (Split-Path -Parent $p.OriginalFileName) }
            } catch { $dismError = $_.Exception.Message }
            $repo = Join-Path $env:windir 'System32\DriverStore\FileRepository'
            if (Test-Path -LiteralPath $repo) {
                $dirs += @(Get-ChildItem -LiteralPath $repo -Directory -Filter 'usbip2*' -ErrorAction SilentlyContinue |
                    ForEach-Object { $_.FullName })
            }
            $files = @()
            foreach ($d in @($dirs | Select-Object -Unique)) {
                $files += @(Get-ChildItem -LiteralPath $d -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Extension -in '.sys', '.cat' } | ForEach-Object { $_.FullName })
            }
            $files = @($files | Select-Object -Unique)
            if ($files.Count -eq 0 -and $dismError) { throw "driver enumeration failed: $dismError" }
            return @{ Version = $version; DriverFiles = $files }
        }
        Download = {
            param($Url, $Dest)
            try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
            $ProgressPreference = 'SilentlyContinue'
            # Certificate validation is left at the platform default: never disabled.
            Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing
        }
        # Returns facts: Status, SimpleName, Ekus, RootThumbprint, ChainOk, ChainStatus.
        GetSignature = {
            param($Path)
            return (Get-UsbipSignatureFacts $Path)
        }
        # Returns the installer's exit code, or $null when it did not finish within the timeout
        # (it is then left running, not killed: killing an installer half way is worse).
        RunInstaller = {
            param($Path, $ArgList, $TimeoutSec)
            $p = Start-Process -FilePath $Path -ArgumentList $ArgList -PassThru
            if (-not $p.WaitForExit([int]($TimeoutSec * 1000))) { return $null }
            return $p.ExitCode
        }
        NewStagingDir = { return (New-UsbipStagingDir) }
    }
}

function Test-UsbipInstalledDrivers {
    # Fail closed: no drivers found, any exception, or any file not verified counts as a failure.
    param([hashtable]$Pin, [hashtable]$Seams)
    $bad = @()
    $count = 0
    try {
        $state = & $Seams.GetInstalledState
        $files = @()
        if ($state -and $state.DriverFiles) { $files = @($state.DriverFiles) }
        $count = $files.Count
        if ($files.Count -eq 0) {
            $bad += 'no installed usbip2 driver files were found, so nothing could be verified'
        }
        foreach ($f in $files) {
            $why = ''
            if (-not (Test-UsbipSignedBy $f $Pin 'Driver' $Seams ([ref]$why))) {
                $bad += ('{0}: {1}' -f (Split-Path -Leaf $f), $why)
            }
        }
    }
    catch {
        $bad += "the driver check itself failed: $($_.Exception.Message)"
    }
    return @{ Ok = ($bad.Count -eq 0); Problems = $bad; Count = $count }
}

function Invoke-UsbipDriverInstall {
    <#
    .SYNOPSIS
        Opt-in install of the pinned usbip-win2 release. Returns a result object; never exits and
        never reboots. -DryRun prints every step and performs none of them.
    #>
    param(
        [hashtable]$Pin = $script:UsbipPin,
        [hashtable]$Seams,
        [switch]$DryRun,
        [int]$InstallerTimeoutSec = 600,
        [scriptblock]$Say = { param($m, $c) Write-Host $m -ForegroundColor $c }
    )

    $defaults = New-UsbipDefaultSeams
    if (-not $Seams) { $Seams = @{} }
    foreach ($k in @($defaults.Keys)) { if (-not $Seams.ContainsKey($k)) { $Seams[$k] = $defaults[$k] } }

    $log = New-Object System.Collections.Generic.List[string]
    $emit = {
        param($m, $c)
        if (-not $c) { $c = 'White' }
        $log.Add($m)
        & $Say $m $c
    }
    $done = {
        param($code, $status)
        [pscustomobject]@{ ExitCode = $code; Status = $status; Messages = $log.ToArray(); DoNotReboot = ($code -eq 2) }
    }
    # The driver package is on the machine (or may be) and cannot be trusted. Say so plainly.
    $unverified = {
        param($problems, $justInstalled)
        & $emit '  ##################################################################' 'Red'
        & $emit '  STOP: the usbip-win2 kernel driver is NOT verified as Microsoft-signed.' 'Red'
        foreach ($p in $problems) { & $emit "    - $p" 'Red' }
        if ($justInstalled) {
            & $emit '  The driver package IS ALREADY INSTALLED on this machine (a virtual host controller' 'Red'
            & $emit '  and a USB hub upper filter). It will LOAD at the next boot or restart, including' 'Red'
            & $emit '  an automatic Windows Update restart.' 'Red'
        } else {
            & $emit '  A usbip-win2 driver package is on this machine and will load at the next boot.' 'Red'
        }
        & $emit '  Do NOT reboot until it is removed or verified. Nothing was enabled by this script.' 'Red'
        & $emit '  Your rule: use usbip-win2 only while its drivers are Microsoft-signed; otherwise we' 'Red'
        & $emit '  build and sign our own. This script changed nothing to make the check pass and did' 'Red'
        & $emit '  not touch test-signing mode.' 'Red'
        & $emit '  To remove the driver (these commands are NOT run by this script):' 'Yellow'
        & $emit '    1. Run the uninstaller:  "%ProgramW6432%\USBip\unins000.exe"  (or Settings > Apps > USBip)' 'Yellow'
        & $emit '    2. Or by hand, in an elevated prompt:  pnputil /enum-drivers' 'Yellow'
        & $emit '       find the entry whose Original Name starts with usbip2; its Published Name is oemNN.inf; then' 'Yellow'
        & $emit '       pnputil /delete-driver oemNN.inf /uninstall /force' 'Yellow'
        & $emit '  ##################################################################' 'Red'
    }

    & $emit "[MultiSeat] USB forwarding driver: usbip-win2 $($Pin.Version) (opt-in, Microsoft-signed drivers only)" 'Cyan'

    if ($DryRun) {
        & $emit '  DRY RUN: nothing below is executed.' 'Yellow'
        & $emit '  1. Refuse if Windows is in test-signing mode or has integrity checks off (running boot options and BCD), or if that cannot be determined.' 'Yellow'
        & $emit '  2. Skip if usbip-win2 is already installed at the pinned version (drivers are still checked).' 'Yellow'
        & $emit '  3. Create a private staging folder (SYSTEM and Administrators only) under ProgramData.' 'Yellow'
        & $emit "  4. Download $($Pin.Url) into it over HTTPS and keep the file locked against changes." 'Yellow'
        & $emit "  5. Require SHA-256 $($Pin.Sha256); delete the file and stop on a mismatch." 'Yellow'
        & $emit "  6. Require a Valid Authenticode signature with signer name exactly '$($Pin.InstallerSigner)'." 'Yellow'
        & $emit ('  7. Run the installer silently: ' + ($script:UsbipInstallerArgs -join ' ') + ' (it cannot restart the machine; ' + $InstallerTimeoutSec + ' s time limit).') 'Yellow'
        & $emit "  8. Require every installed usbip2 kernel driver file to be signed by '$($Pin.DriverSigner)', chaining to Microsoft Root Certificate Authority 2010, with a hardware-driver EKU." 'Yellow'
        & $emit '     If one is not: stop, enable nothing, say the package is installed and must not be rebooted into, print removal steps, exit non-zero.' 'Yellow'
        & $emit '  9. Tell you a REBOOT is needed. The reboot is your decision; this script never reboots.' 'Yellow'
        return (& $done $script:UsbipExitOk 'DryRun')
    }

    if (-not (& $Seams.IsAdmin)) {
        & $emit '  REFUSED: this step needs an elevated (Administrator) shell.' 'Red'
        return (& $done $script:UsbipExitRefused 'NotAdmin')
    }

    # Step 1: test-signing / integrity checks. Unknown = stop.
    $verdict = Get-UsbipBootVerdict $Seams
    if ($verdict.Refuse) {
        & $emit "  REFUSED: $($verdict.Reason)." 'Red'
        & $emit '  usbip-win2 is used here only with Microsoft-signed drivers. Test-signing mode weakens the' 'Red'
        & $emit '  streaming host and is not an accepted workaround. Turn it off yourself and reboot, or use our own signed driver.' 'Red'
        return (& $done $script:UsbipExitRefused 'TestSigning')
    }

    # Step 2: already installed?
    $state = $null
    try { $state = & $Seams.GetInstalledState } catch {
        & $emit "  Could not read the installed driver state: $($_.Exception.Message)" 'Yellow'
    }
    $installedVersion = $null
    if ($state) { $installedVersion = $state.Version }
    $atPin = $false
    if ($installedVersion) {
        try { $atPin = ([version]([string]$installedVersion -replace '[^\d\.].*$', '') -eq [version]$Pin.Version) } catch { $atPin = $false }
    }
    if ($atPin -and $state.DriverFiles -and @($state.DriverFiles).Count -gt 0) {
        & $emit "  SKIP: usbip-win2 $installedVersion is already installed (pinned version). Checking its drivers." 'Yellow'
        $chk = Test-UsbipInstalledDrivers $Pin $Seams
        if (-not $chk.Ok) {
            & $unverified $chk.Problems $false
            return (& $done $script:UsbipExitRule 'InstalledDriverNotMicrosoftSigned')
        }
        & $emit "  OK: $($chk.Count) driver file(s) are signed by '$($Pin.DriverSigner)' (Microsoft root, hardware-driver EKU)." 'Green'
        return (& $done $script:UsbipExitOk 'AlreadyInstalled')
    }
    if ($installedVersion) {
        & $emit "  Found usbip-win2 $installedVersion; the pinned version is $($Pin.Version). Proceeding with the pinned installer." 'Yellow'
    }

    if ($Pin.Url -notlike 'https://*') {
        & $emit '  REFUSED: the pinned URL is not HTTPS.' 'Red'
        return (& $done $script:UsbipExitRefused 'NotHttps')
    }

    $stage = $null
    $held = $null
    try {
        # Steps 3-6: private staging folder, download, lock, hash, signature on the SAME locked file.
        try { $stage = & $Seams.NewStagingDir } catch {
            & $emit "  REFUSED: could not create a private staging folder: $($_.Exception.Message)" 'Red'
            return (& $done $script:UsbipExitRefused 'StagingFailed')
        }
        $file = Join-Path $stage $Pin.Asset
        & $emit "  Downloading $($Pin.Asset) ..." 'White'
        try { & $Seams.Download $Pin.Url $file } catch {
            & $emit "  REFUSED: download failed: $($_.Exception.Message)" 'Red'
            return (& $done $script:UsbipExitRefused 'DownloadFailed')
        }
        if (-not (Test-Path -LiteralPath $file)) {
            & $emit '  REFUSED: the download produced no file.' 'Red'
            return (& $done $script:UsbipExitRefused 'DownloadFailed')
        }
        try { $held = Open-UsbipHeldFile $file } catch {
            & $emit "  REFUSED: could not lock the downloaded file: $($_.Exception.Message)" 'Red'
            return (& $done $script:UsbipExitRefused 'CannotLockFile')
        }

        $actual = Get-UsbipStreamSha256 $held
        if ($actual -ne $Pin.Sha256.ToUpperInvariant()) {
            $held.Dispose(); $held = $null
            Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
            & $emit "  REFUSED: SHA-256 mismatch. Expected $($Pin.Sha256), got $actual. The file was deleted." 'Red'
            return (& $done $script:UsbipExitRefused 'HashMismatch')
        }
        & $emit '  OK: SHA-256 matches the pin.' 'Green'

        $why = ''
        if (-not (Test-UsbipSignedBy $file $Pin 'Installer' $Seams ([ref]$why))) {
            $held.Dispose(); $held = $null
            Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
            & $emit "  REFUSED: installer signature check failed: $why. The file was deleted." 'Red'
            return (& $done $script:UsbipExitRefused 'InstallerSignature')
        }
        & $emit "  OK: installer is signed by '$($Pin.InstallerSigner)'." 'Green'

        # Step 7: install, still holding the verified file. /NORESTART always.
        $setupLog = Join-Path $stage 'usbip-setup.log'
        $installerArgs = @($script:UsbipInstallerArgs) + ('/LOG="' + $setupLog + '"')
        & $emit ('  Running the installer silently (' + ($script:UsbipInstallerArgs -join ' ') + ', ' + $InstallerTimeoutSec + ' s limit) ...') 'White'
        $code = & $Seams.RunInstaller $file $installerArgs $InstallerTimeoutSec
        if ($null -eq $code) {
            & $emit "  FAILED: the installer did not finish within $InstallerTimeoutSec s. It was left running, not killed. Check it before doing anything else." 'Red'
            return (& $done $script:UsbipExitRefused 'InstallerTimeout')
        }
        if ($code -eq 7 -or $code -eq 8) {
            $extra = ''
            if ($code -eq 8) { $extra = ' and a restart is needed first to clear the cause (your decision; this script will not restart anything)' }
            & $emit "  FAILED: the installer could not proceed (exit code $code)$extra. Nothing was installed by it. Re-run this step afterwards." 'Red'
            return (& $done $script:UsbipExitRefused 'InstallerCannotProceed')
        }
        if ($code -ne 0) {
            & $emit "  FAILED: the installer exited with code $code. If it ran at all, the driver may be partly installed: check Settings > Apps > USBip and the setup log." 'Red'
            return (& $done $script:UsbipExitRefused 'InstallerFailed')
        }
    }
    finally {
        if ($held) { try { $held.Dispose() } catch { } }
        if ($stage -and (Test-Path -LiteralPath $stage)) {
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # Step 8: the user rule, on what actually got installed. Any failure here is exit 2.
    $chk = Test-UsbipInstalledDrivers $Pin $Seams
    if (-not $chk.Ok) {
        & $unverified $chk.Problems $true
        return (& $done $script:UsbipExitRule 'InstalledDriverNotMicrosoftSigned')
    }
    & $emit "  OK: $($chk.Count) installed driver file(s) are signed by '$($Pin.DriverSigner)' (Microsoft root, hardware-driver EKU)." 'Green'

    # Step 9: the reboot is the user's decision.
    & $emit '  A REBOOT is required before the USB/IP driver works.' 'Magenta'
    & $emit '  Rebooting is your decision. This script does not and will not restart the machine.' 'Magenta'
    & $emit '  Seats keep USB forwarding off; only the console ApolloVibe can use it, and only once you enable it there.' 'Cyan'
    return (& $done $script:UsbipExitOk 'Installed')
}
