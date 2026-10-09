<#
.SYNOPSIS
    Pure helpers for scripts\install-service.ps1: payload checks, runtime checks, folder replace.

.DESCRIPTION
    Dot-sourced by install-service.ps1. Dot-sourcing this file defines functions and runs
    NOTHING else, so tests can load it without touching a service, the registry or any real
    install folder. Every path is a parameter; nothing here knows C:\Program Files\MultiSeat.

    Keep this file Windows PowerShell 5.1 clean (no ?? , ?. , ternary, && or ||) and pure ASCII:
    scripts\lint-scripts.ps1 parses it under both engines in CI.

    Why this exists (measured 2026-10-08): `dotnet publish` straight into a live install folder
    copies runtimeconfig.json only when the source is NEWER than the destination. Onto a
    self-contained folder that produced a hybrid twice: a framework-dependent runtimeconfig.json
    beside a leftover hostfxr.dll. The host then looks for a bundled runtime the config does not
    declare, and the service fails with "No frameworks were found". The fix is to build into a
    staging folder, verify it, and replace the install folder as a whole.
#>

# Files a self-contained publish puts beside the exe. A framework-dependent payload has none.
$script:BundledRuntimeFiles = @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')
$script:RuntimeConfigName = 'MultiSeat.Service.runtimeconfig.json'

function Get-PayloadRequiredFiles {
    # What every payload, from a zip or from a build, must contain to be a MultiSeat install.
    return @('MultiSeat.Service.exe', 'appsettings.json', 'wwwroot\index.html')
}

function Get-MissingPayloadFiles {
    param([Parameter(Mandatory = $true)][string]$Dir)
    return @(Get-PayloadRequiredFiles | Where-Object { -not (Test-Path (Join-Path $Dir $_)) })
}

function Read-RuntimeConfig {
    # Returns the runtimeOptions object, or throws a message that names the file.
    param([Parameter(Mandatory = $true)][string]$Path)
    $json = [System.IO.File]::ReadAllText($Path)
    $doc = $json | ConvertFrom-Json
    if ($null -eq $doc -or $null -eq $doc.PSObject.Properties['runtimeOptions']) {
        throw "$Path has no runtimeOptions section"
    }
    return $doc.runtimeOptions
}

function Test-PayloadConsistency {
    <#
    .SYNOPSIS
        Is this folder a coherent .NET payload? Used on the staged payload AND on the installed
        folder after copying.
    .DESCRIPTION
        Self-contained <=> runtimeconfig.json has includedFrameworks <=> hostfxr.dll is present.
        A self-contained payload must carry the whole bundled runtime. A framework-dependent
        payload must declare `frameworks` and carry no bundled runtime file at all.
        Returns an object: Ok, Mode (self-contained / framework-dependent / unknown), Problems.
    #>
    param([Parameter(Mandatory = $true)][string]$Dir)

    $problems = @()
    $mode = 'unknown'
    $cfgPath = Join-Path $Dir $script:RuntimeConfigName

    $present = @($script:BundledRuntimeFiles | Where-Object { Test-Path (Join-Path $Dir $_) })
    $hasHostfxr = $present -contains 'hostfxr.dll'

    if (-not (Test-Path $cfgPath)) {
        $problems += "$($script:RuntimeConfigName) is missing, so nothing says which runtime this payload needs."
    } else {
        $opts = $null
        try { $opts = Read-RuntimeConfig -Path $cfgPath }
        catch { $problems += "$($script:RuntimeConfigName) cannot be read: $($_.Exception.Message)" }

        if ($null -ne $opts) {
            $included = @()
            if ($null -ne $opts.PSObject.Properties['includedFrameworks']) { $included = @($opts.includedFrameworks) }
            $frameworks = @()
            if ($null -ne $opts.PSObject.Properties['frameworks']) { $frameworks = @($opts.frameworks) }
            if ($frameworks.Count -eq 0 -and $null -ne $opts.PSObject.Properties['framework']) { $frameworks = @($opts.framework) }

            if ($included.Count -gt 0 -and $frameworks.Count -gt 0) {
                $problems += "$($script:RuntimeConfigName) declares both includedFrameworks (self-contained) and frameworks (framework-dependent)."
            } elseif ($included.Count -gt 0) {
                $mode = 'self-contained'
                if (-not $hasHostfxr) {
                    $problems += "$($script:RuntimeConfigName) has includedFrameworks (self-contained) but hostfxr.dll is absent."
                } else {
                    $lacking = @($script:BundledRuntimeFiles | Where-Object { $present -notcontains $_ })
                    if ($lacking.Count -gt 0) {
                        $problems += "The bundled runtime is incomplete: $($lacking -join ', ') missing beside hostfxr.dll."
                    }
                }
            } elseif ($frameworks.Count -gt 0) {
                $mode = 'framework-dependent'
                if ($present.Count -gt 0) {
                    $problems += "$($script:RuntimeConfigName) declares frameworks (framework-dependent) and no includedFrameworks, but bundled runtime file(s) are present: $($present -join ', '). This is the hybrid that fails with 'No frameworks were found'."
                }
            } else {
                $problems += "$($script:RuntimeConfigName) declares neither includedFrameworks nor frameworks."
            }
        }
    }

    return [pscustomobject]@{
        Ok       = ($problems.Count -eq 0)
        Mode     = $mode
        Problems = $problems
    }
}

# -- Shared runtime requirements (framework-dependent source builds) ------------------

function Get-RuntimeRequirements {
    # The shared frameworks a framework-dependent runtimeconfig.json demands.
    param([Parameter(Mandatory = $true)][string]$RuntimeConfigPath)
    $opts = Read-RuntimeConfig -Path $RuntimeConfigPath
    $roll = 'Minor'
    if ($null -ne $opts.PSObject.Properties['rollForward'] -and $opts.rollForward) { $roll = [string]$opts.rollForward }
    $list = @()
    if ($null -ne $opts.PSObject.Properties['frameworks']) { $list = @($opts.frameworks) }
    elseif ($null -ne $opts.PSObject.Properties['framework']) { $list = @($opts.framework) }
    $out = @()
    foreach ($f in $list) {
        $out += [pscustomobject]@{ Name = [string]$f.name; Version = [string]$f.version; RollForward = $roll }
    }
    return $out
}

function ConvertTo-FrameworkVersion {
    # "9.0.20", "10.0.0-preview.1" -> [version] 9.0.20 (suffix ignored). $null when unparseable.
    param([string]$Text)
    if (-not $Text) { return $null }
    $core = ($Text -split '[-+]')[0]
    $v = $null
    if ([version]::TryParse($core, [ref]$v)) { return $v }
    return $null
}

function Test-RuntimeVersionSatisfies {
    <#
    .SYNOPSIS
        Would the host's runtime version `Installed` serve a request for `Required`?
    .DESCRIPTION
        Follows the .NET roll-forward policy: Disable = exact; LatestPatch = same major.minor,
        patch at least as new; Minor / LatestMinor (the default) = same major, at least as new;
        Major / LatestMajor = at least as new. Pre-release suffixes are ignored.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Required,
        [Parameter(Mandatory = $true)][string]$Installed,
        [string]$RollForward = 'Minor'
    )
    $r = ConvertTo-FrameworkVersion $Required
    $i = ConvertTo-FrameworkVersion $Installed
    if ($null -eq $r -or $null -eq $i) { return $false }
    # Compare three parts only; the runtime has no revision component.
    $rv = New-Object System.Version($r.Major, $r.Minor, [Math]::Max($r.Build, 0))
    $iv = New-Object System.Version($i.Major, $i.Minor, [Math]::Max($i.Build, 0))
    $policy = 'minor'
    if ($RollForward) { $policy = $RollForward.ToLowerInvariant() }
    switch ($policy) {
        'disable'     { return ($iv -eq $rv) }
        'latestpatch' { return ($iv.Major -eq $rv.Major -and $iv.Minor -eq $rv.Minor -and $iv -ge $rv) }
        'major'       { return ($iv -ge $rv) }
        'latestmajor' { return ($iv -ge $rv) }
        default       { return ($iv.Major -eq $rv.Major -and $iv -ge $rv) }
    }
}

function Get-HostDotnetRuntimes {
    <#
    .SYNOPSIS
        Lines of `dotnet --list-runtimes` for the install the service host (the apphost) will use.
    .DESCRIPTION
        The service runs as SYSTEM, so a dotnet that exists only on this user's PATH is no use to
        it. The apphost looks at the registered install location, then at Program Files\dotnet.
        Falls back to PATH only when neither exists, and says nothing is found by returning @().
    #>
    $candidates = @()
    try {
        $reg = Get-ItemProperty 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64' -Name InstallLocation -ErrorAction Stop
        if ($reg.InstallLocation) { $candidates += (Join-Path $reg.InstallLocation 'dotnet.exe') }
    } catch { }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    foreach ($exe in $candidates) {
        if ($exe -and (Test-Path $exe)) {
            $lines = @(& $exe --list-runtimes 2>$null)
            if ($LASTEXITCODE -eq 0 -and $lines.Count -gt 0) { return $lines }
        }
    }
    return @()
}

function Test-HostHasRuntimes {
    <#
    .SYNOPSIS
        Does the host have every shared framework the payload demands?
    .PARAMETER RuntimeLines
        Output lines of `dotnet --list-runtimes`, e.g. "Microsoft.NETCore.App 9.0.20 [C:\...]".
    #>
    param(
        [Parameter(Mandatory = $true)]$Requirements,
        [string[]]$RuntimeLines = @()
    )
    $installed = @()
    foreach ($line in $RuntimeLines) {
        if ($line -match '^\s*(?<n>\S+)\s+(?<v>\S+)\s+\[') {
            $installed += [pscustomobject]@{ Name = $Matches['n']; Version = $Matches['v'] }
        }
    }
    $missing = @()
    foreach ($req in @($Requirements)) {
        $mine = @($installed | Where-Object { $_.Name -eq $req.Name })
        $ok = $false
        foreach ($m in $mine) {
            if (Test-RuntimeVersionSatisfies -Required $req.Version -Installed $m.Version -RollForward $req.RollForward) { $ok = $true; break }
        }
        if (-not $ok) {
            $have = 'none installed'
            if ($mine.Count -gt 0) { $have = 'installed: ' + (($mine | ForEach-Object { $_.Version }) -join ', ') }
            $missing += "$($req.Name) $($req.Version) or a compatible newer one (roll-forward $($req.RollForward)); $have"
        }
    }
    return [pscustomobject]@{ Ok = ($missing.Count -eq 0); Missing = $missing }
}

# -- Replace the install folder -------------------------------------------------------

function Clear-DirectoryContents {
    param([Parameter(Mandatory = $true)][string]$Dir, [string]$OnError = 'Stop')
    Get-ChildItem -LiteralPath $Dir -Force | Remove-Item -Recurse -Force -ErrorAction $OnError
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$From,
        [Parameter(Mandatory = $true)][string]$To,
        [string[]]$SkipNames = @()
    )
    Get-ChildItem -LiteralPath $From -Force |
        Where-Object { $SkipNames -notcontains $_.Name } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $To -Recurse -Force -ErrorAction Stop }
}

function Read-JsonLoose {
    # appsettings.json carries full-line // comments. PowerShell 7 parses them; Windows PowerShell
    # 5.1 does not, so the "this release adds settings" report silently never worked there.
    param([Parameter(Mandatory = $true)][string]$Path)
    $text = [System.IO.File]::ReadAllText($Path)
    $text = [regex]::Replace($text, '(?m)^[ \t]*//[^\r\n]*', '')
    return ($text | ConvertFrom-Json)
}

function Get-Sha256 {
    # .NET directly: Get-FileHash lives in a module that is not always loadable when PSModulePath
    # is shared between Windows PowerShell and PowerShell 7.
    param([Parameter(Mandatory = $true)][string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fs = [System.IO.File]::OpenRead($Path)
        try { return [System.BitConverter]::ToString($sha.ComputeHash($fs)) }
        finally { $fs.Dispose() }
    } finally { $sha.Dispose() }
}

function Restore-DirectoryFromBackup {
    # Put a full-folder backup back. A file that is already in place and identical is left alone:
    # after a wipe that failed on a locked file, that file still holds the old bytes, and copying
    # over it would fail for no reason.
    param(
        [Parameter(Mandatory = $true)][string]$Backup,
        [Parameter(Mandatory = $true)][string]$To
    )
    $root = (Resolve-Path -LiteralPath $Backup).Path.TrimEnd([char]92)
    Get-ChildItem -LiteralPath $root -Recurse -Force -File | ForEach-Object {
        $dest = Join-Path $To $_.FullName.Substring($root.Length + 1)
        $dir = Split-Path $dest -Parent
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        if (Test-Path -LiteralPath $dest -PathType Leaf) {
            $have = Get-Item -LiteralPath $dest
            if ($have.Length -eq $_.Length -and (Get-Sha256 $dest) -eq (Get-Sha256 $_.FullName)) { return }
        }
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force -ErrorAction Stop
    }
}

function Protect-BackupDirectory {
    # The backups hold appsettings.local.json, which can carry the API key, and ProgramData's
    # inherited ACL lets every standard user (including seat accounts) read what lands there.
    # Limit the backup root to SYSTEM, Administrators and the account running the installer.
    param([Parameter(Mandatory = $true)][string]$Path)
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $out = & icacls.exe $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*${me}:(OI)(CI)F" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not restrict access to $Path (icacls: $out). The backups keep the inherited permissions."
    }
}

function Install-Payload {
    <#
    .SYNOPSIS
        Replace the install folder with a verified staged payload. Shared by -FromZip and the
        source build, so the two differ only in how the stage is produced.
    .DESCRIPTION
        1. Refuse an inconsistent stage before touching anything.
        2. Copy appsettings.json and appsettings.local.json out, byte for byte (ConfigBackupRoot\Stamp).
        3. Copy the whole current install folder to FolderBackupRoot\Stamp.
        4. Wipe the install folder, copy the stage in, put the host's config back, and check that
           the INSTALLED folder is consistent.
        5. If step 4 fails at any point, wipe again and copy the folder backup back, then throw
           an exception whose Data['Restored'] says whether the old folder is back.
        Only the newest KeepFolderBackups full-folder backups are kept.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [Parameter(Mandatory = $true)][string]$ConfigBackupRoot,
        [Parameter(Mandatory = $true)][string]$FolderBackupRoot,
        [string]$Stamp = (Get-Date -Format 'yyyyMMdd-HHmmss'),
        [string[]]$SkipNames = @('scripts', 'prerequisites', 'README.md'),
        [int]$KeepFolderBackups = 3
    )

    # 1. The stage must be good before anything is changed.
    $absent = @(Get-MissingPayloadFiles -Dir $Stage)
    if ($absent.Count -gt 0) {
        throw "The staged payload is missing $($absent -join ', ') -- nothing was installed."
    }
    $check = Test-PayloadConsistency -Dir $Stage
    if (-not $check.Ok) {
        throw "The staged payload is inconsistent -- nothing was installed. $($check.Problems -join ' ')"
    }

    # 2. Preserve host-local configuration BEFORE the wipe. See issue #45: this wipe once
    #    deleted both files with no backup. Preserve the FILES, byte for byte, never their text:
    #    a Get-Content / Set-Content round trip appends a newline and can change the encoding.
    $preserveNames = @('appsettings.json', 'appsettings.local.json')
    $preserved = @{}
    $backupDir = $null
    foreach ($name in $preserveNames) {
        $existing = Join-Path $InstallDir $name
        if (Test-Path -LiteralPath $existing) {
            if (-not $backupDir) {
                $backupDir = Join-Path $ConfigBackupRoot $Stamp
                New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
                Protect-BackupDirectory -Path $ConfigBackupRoot
            }
            # The backup doubles as the staging copy. It lives outside the install directory
            # because the wipe below is the very thing being protected against.
            $kept = Join-Path $backupDir $name
            Copy-Item -LiteralPath $existing -Destination $kept -Force
            $preserved[$name] = $kept
        }
    }
    if ($preserved.Count -gt 0) {
        Write-Host "  Backed up $($preserved.Count) config file(s) to $backupDir" -ForegroundColor DarkGray
    }

    # 3. Full copy of the folder being replaced, so a failure midway can be undone.
    $folderBackup = $null
    if ((Test-Path -LiteralPath $InstallDir) -and @(Get-ChildItem -LiteralPath $InstallDir -Force).Count -gt 0) {
        $folderBackup = Join-Path $FolderBackupRoot $Stamp
        New-Item -ItemType Directory -Path $folderBackup -Force | Out-Null
        Protect-BackupDirectory -Path $FolderBackupRoot
        Copy-DirectoryContents -From $InstallDir -To $folderBackup
        Write-Host "  Backed up the whole install folder to $folderBackup" -ForegroundColor DarkGray
    }

    # 4. Replace.
    try {
        if (Test-Path -LiteralPath $InstallDir) {
            Clear-DirectoryContents -Dir $InstallDir -OnError 'Stop'
        } else {
            New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        }

        # The asset carries scripts\ , prerequisites\ and README.md so it can install itself
        # without a clone. They are NOT part of the deployed service.
        Copy-DirectoryContents -From $Stage -To $InstallDir -SkipNames $SkipNames

        # Put host configuration back over the shipped defaults. An upgrade must not silently
        # change how this host is configured.
        if ($preserved.ContainsKey('appsettings.local.json')) {
            Copy-Item -LiteralPath $preserved['appsettings.local.json'] `
                      -Destination (Join-Path $InstallDir 'appsettings.local.json') -Force -ErrorAction Stop
            Write-Host "  Restored appsettings.local.json" -ForegroundColor DarkGray
        }

        if ($preserved.ContainsKey('appsettings.json')) {
            $shippedPath = Join-Path $InstallDir 'appsettings.json'

            # Report settings this build added that the host's file does not carry. Keeping the
            # host's file is right, but doing it silently would hide a new option forever.
            try {
                $shippedKeys = ((Read-JsonLoose $shippedPath).MultiSeat |
                                Get-Member -MemberType NoteProperty).Name
                $hostKeys    = ((Read-JsonLoose $preserved['appsettings.json']).MultiSeat |
                                Get-Member -MemberType NoteProperty).Name
                $newKeys     = @($shippedKeys | Where-Object { $hostKeys -notcontains $_ })
                if ($newKeys.Count -gt 0) {
                    Write-Host ""
                    Write-Host "  This release adds $($newKeys.Count) setting(s) your appsettings.json does not have:" -ForegroundColor Yellow
                    $newKeys | ForEach-Object { Write-Host "      MultiSeat:$_" -ForegroundColor Yellow }
                    Write-Host "  Your file was kept as-is, so these run at their built-in defaults." -ForegroundColor Yellow
                    Write-Host ""
                }
            } catch {
                $why = "$_"
                if ($why.Length -gt 160) { $why = $why.Substring(0, 160) + '...' }
                Write-Host "  NOTE: could not compare settings against the shipped file ($why)" -ForegroundColor Yellow
            }

            Copy-Item -LiteralPath $preserved['appsettings.json'] -Destination $shippedPath -Force -ErrorAction Stop
            Write-Host "  Kept your existing appsettings.json (shipped defaults not applied)" -ForegroundColor DarkGray
        }

        # The installed folder must itself be coherent, whatever it looked like before.
        $after = Test-PayloadConsistency -Dir $InstallDir
        if (-not $after.Ok) {
            throw "The installed folder is inconsistent after copying: $($after.Problems -join ' ')"
        }
        Write-Host "  Install folder is consistent ($($after.Mode))" -ForegroundColor DarkGray
    }
    catch {
        $origEx = $_.Exception
        $reason = $origEx.Message
        $restored = $false
        $restoreNote = ''
        try {
            if (Test-Path -LiteralPath $InstallDir) {
                Clear-DirectoryContents -Dir $InstallDir -OnError 'SilentlyContinue'
            } else {
                New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
            }
            if ($folderBackup) {
                Restore-DirectoryFromBackup -Backup $folderBackup -To $InstallDir
                $restored = $true
                $restoreNote = "The previous install folder was restored from $folderBackup."
            } else {
                $restored = $true
                $restoreNote = 'There was no previous install folder, so the partial copy was removed.'
            }
        } catch {
            $restoreNote = "RESTORE FAILED ($($_.Exception.Message)). The previous folder is intact at $folderBackup; copy it back by hand."
        }
        $ex = New-Object System.InvalidOperationException("Install failed: $reason $restoreNote", $origEx)
        $ex.Data['Restored'] = $restored
        throw $ex
    }

    # 5. Keep only the newest few full-folder backups; they are large.
    if (Test-Path -LiteralPath $FolderBackupRoot) {
        $old = @(Get-ChildItem -LiteralPath $FolderBackupRoot -Directory |
                 Sort-Object Name -Descending | Select-Object -Skip $KeepFolderBackups)
        foreach ($d in $old) { Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }

    return [pscustomobject]@{ ConfigBackup = $backupDir; FolderBackup = $folderBackup }
}
