param(
    [string]$Artifacts = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$TestRoot = (Join-Path $PSScriptRoot '..\build-smoke')
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($Artifacts)
$runRoot = Join-Path ([IO.Path]::GetFullPath($TestRoot)) ([guid]::NewGuid().ToString('N'))
function Assert-True($condition, [string]$message) { if (-not $condition) { throw $message } }
# OptionalHeader.Subsystem sits at the same offset (68) in PE32 and PE32+ images; 2 = Windows GUI, 3 = console.
function Get-PeSubsystem([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    [int][BitConverter]::ToUInt16($bytes, $peOffset + 24 + 68)
}
# The service starts this launcher inside a seat; as a console program it opened a window there.
foreach ($launcherName in @('MultiSeat.SteamLauncher.exe', 'MultiSeat.SteamLauncher32.exe')) {
    $subsystem = Get-PeSubsystem (Join-Path $source $launcherName)
    Assert-True ($subsystem -eq 2) "$launcherName is subsystem $subsystem, expected 2 (Windows GUI) so it opens no console window"
}
# Prove the probe can tell the two apart: the fixture is a console program.
Assert-True ((Get-PeSubsystem (Join-Path $source 'SteamIsolationFixture.exe')) -eq 3) 'Subsystem probe did not read the console fixture as subsystem 3'
function Invoke-Fixture([string]$directory, [string]$tail, [bool]$expectReady, [int]$expectExit, [string]$steamDirectory = '', [string]$launcherFlags = '') {
    # The launcher and its hook live in $directory; the stand-in steam.exe may live elsewhere
    # (a folder with a space in its name, to test an unquoted program path).
    $steamDir = if ($steamDirectory) { $steamDirectory } else { $directory }
    $name = 'Global\MultiSeatSteamReady-' + [guid]::NewGuid().ToString('N')
    $ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $name)
    $launcher = $null
    try {
        $args = '--steam "' + (Join-Path $steamDir 'steam.exe') + '" ' + $launcherFlags + ' --ready-event "' + $name + '" -- ' + $tail
        $launcher = Start-Process -FilePath (Join-Path $directory 'MultiSeat.SteamLauncher.exe') -ArgumentList $args `
            -WorkingDirectory $directory -WindowStyle Hidden -RedirectStandardError (Join-Path $directory 'stderr.txt') -PassThru
        $signaled = $ready.WaitOne(5000)
        Assert-True ($signaled -eq $expectReady) "Readiness was $signaled, expected $expectReady in $directory"
        Assert-True ($launcher.WaitForExit(5000)) "Fixture process tree did not finish in $directory"
        Assert-True ($launcher.ExitCode -eq $expectExit) "Unexpected launcher exit $($launcher.ExitCode) in $directory"
    } finally {
        if ($launcher -and -not $launcher.HasExited) { Stop-Process -Id $launcher.Id }
        $ready.Dispose()
    }
}
foreach ($suffix in @('', '32')) {
    $directory = Join-Path $runRoot $(if ($suffix) { 'x86' } else { 'x64' })
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    foreach ($name in @('MultiSeat.SteamLauncher.exe', 'MultiSeat.SteamLauncher32.exe', "MultiSeat.SteamHook$suffix.dll")) {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination $directory
    }
    # Only the compiled harmless fixture is named steam.exe; installed Steam is never used.
    Copy-Item -LiteralPath (Join-Path $source "SteamIsolationFixture$suffix.exe") -Destination (Join-Path $directory 'steam.exe')
    Copy-Item -LiteralPath (Join-Path $source "SteamIsolationFixture$suffix.exe") -Destination (Join-Path $directory 'steamwebhelper.exe')
    Invoke-Fixture $directory '--report ready.txt --hold-ms 100 "two words" "quote\"inside"' $true 0
    $report = Get-Content -LiteralPath (Join-Path $directory 'ready.txt') -Raw
    # SteamPID is redirected only on Steam's machine-wide key. The fixture keeps it under a key of its
    # own, which must come back untouched: no zeroing, and no write that reaches real Steam settings.
    Assert-True ($report -match '(?m)^steamPid=123456\r?$') 'SteamPID DWORD on a key that is not Steam''s was changed'
    Assert-True ($report -match '(?m)^steamPidNullType=123456\r?$') 'SteamPID DWORD with null type on a key that is not Steam''s was changed'
    Assert-True ($report -match '(?m)^steamPidString=fixture-nonDWORD\r?$') 'SteamPID string was corrupted'
    Assert-True ($report -match '(?m)^arg5=two words\r?$') 'Quoted argument was not preserved'
    Assert-True ($report -match '(?m)^command=.*-master_ipc_name_override"\s+"MultiSeat_') 'A normal launch did not get the IPC option'
    # --forward: Steam is only handing a command to the instance already running. An explicit
    # -master_ipc_name_override would make it start a master of its own, so the command line must come
    # through untouched; the name still has to reach it, and the environment is where it goes.
    Invoke-Fixture $directory '--report forward.txt --hold-ms 100 steam://open/friends' $true 0 '' '--forward'
    $forwardReport = Get-Content -LiteralPath (Join-Path $directory 'forward.txt') -Raw
    Assert-True ($forwardReport -notmatch '(?m)^command=.*master_ipc_name_override') 'Forwarding launch got the IPC option on its command line'
    Assert-True ($forwardReport -match '(?m)^arg5=steam://open/friends\r?$') 'Forwarding launch changed the command it hands over'
    Assert-True ($forwardReport -match '(?m)^steam_master_ipc_name_override=MultiSeat_') 'Forwarding launch did not carry the instance name in its environment'
    Assert-True ($forwardReport -match '(?m)^steamPid=123456\r?$') 'Forwarding launch lost its registry behaviour'
    Invoke-Fixture $directory '--report after-forward.txt --hold-ms 100' $true 0
    Assert-True ((Get-Content -LiteralPath (Join-Path $directory 'after-forward.txt') -Raw) -match '(?m)^command=.*-master_ipc_name_override"\s+"MultiSeat_') 'A launch after a forwarding one lost its IPC option'
    # The shim: it starts an isolated Steam when the account has none, hands over when it has one, and
    # takes a dead pid for no Steam at all. MULTISEAT_STEAM_SHIM_KEY points its pid lookup at a key of
    # this test's own, so a real Steam on this machine is never taken for the stand-in.
    Copy-Item -LiteralPath (Join-Path $source 'MultiSeat.SteamShim.exe') -Destination $directory
    $shim = Join-Path $directory 'MultiSeat.SteamShim.exe'
    $fixtureSteam = Join-Path $directory 'steam.exe'
    $shimKeyPath = 'Software\MultiSeat\SteamShimTests\' + [guid]::NewGuid().ToString('N')
    $env:MULTISEAT_STEAM_SHIM_KEY = $shimKeyPath
    $shimKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($shimKeyPath)
    function Start-Shim([string]$tailText) {
        Start-Process -FilePath $shim -ArgumentList ('--steam "' + $fixtureSteam + '" -- ' + $tailText) `
            -WorkingDirectory $directory -WindowStyle Hidden -PassThru
    }
    try {
        # 1. No Steam in the account: it starts one, and keeps its lock until that one has registered.
        $firstShim = Start-Shim '--report shim-first.txt --hold-ms 8000'
        $firstSteam = $null
        for ($i = 0; $i -lt 60 -and -not $firstSteam; $i++) {
            Start-Sleep -Milliseconds 100
            $firstSteam = Get-Process steam -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $fixtureSteam } | Select-Object -First 1
        }
        Assert-True ($null -ne $firstSteam) 'The shim did not start a Steam in an account that had none'
        Assert-True (-not $firstShim.HasExited) 'The shim returned before the Steam it started had registered'
        $shimKey.SetValue('SteamPID', [int]$firstSteam.Id, [Microsoft.Win32.RegistryValueKind]::DWord)
        Assert-True ($firstShim.WaitForExit(10000)) 'The shim did not return once its Steam had registered'
        $firstReport = Get-Content -LiteralPath (Join-Path $directory 'shim-first.txt') -Raw
        Assert-True ($firstReport -match '(?m)^command=.*-master_ipc_name_override"\s+"MultiSeat_') 'The shim started a Steam without isolating it'
        # 2. A live isolated Steam: it hands the command over and starts nothing of its own.
        $secondShim = Start-Shim '--report shim-second.txt --hold-ms 100 steam://open/friends'
        Assert-True ($secondShim.WaitForExit(15000)) 'The shim did not return after handing a command over'
        $secondReport = Get-Content -LiteralPath (Join-Path $directory 'shim-second.txt') -Raw
        Assert-True ($secondReport -notmatch '(?m)^command=.*master_ipc_name_override') 'The shim handed over with an IPC option on the command line'
        Assert-True ($secondReport -match '(?m)^arg5=steam://open/friends\r?$') 'The shim changed the command it handed over'
        Assert-True ($secondReport -match '(?m)^steam_master_ipc_name_override=MultiSeat_') 'The shim handed over without the instance name'
        Assert-True (-not $firstSteam.HasExited) 'Handing a command over ended the running Steam'
        # 3. Control: a pid with no process behind it is no Steam, so the shim launches instead of forwarding.
        Assert-True ($firstSteam.WaitForExit(15000)) 'The first stand-in Steam did not finish'
        $thirdShim = Start-Shim '--report shim-third.txt --hold-ms 100'
        Assert-True ($thirdShim.WaitForExit(15000)) 'The shim did not return after launching beside a dead pid'
        $thirdReport = Get-Content -LiteralPath (Join-Path $directory 'shim-third.txt') -Raw
        Assert-True ($thirdReport -match '(?m)^command=.*-master_ipc_name_override"\s+"MultiSeat_') 'The shim forwarded to a pid that is not running'
    } finally {
        Remove-Item Env:\MULTISEAT_STEAM_SHIM_KEY -ErrorAction SilentlyContinue
        $shimKey.Dispose()
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($shimKeyPath, $false)
        Get-Process steam -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $fixtureSteam } | Stop-Process -Force
    }
    # An input the shim must refuse instead of passing on: no Steam is started and the code says so.
    $refused = Start-Process -FilePath $shim -ArgumentList '--steam "relative\steam.exe" -- -silent' -WorkingDirectory $directory -WindowStyle Hidden -PassThru
    Assert-True ($refused.WaitForExit(10000) -and $refused.ExitCode -eq 2) 'The shim accepted a relative Steam path'
    # A Steam that restarts itself can do it through any creation API, and with an unquoted program
    # path that contains spaces. Each way has to come back hooked, with its browser cache separated.
    # The hook also says so in the launcher log, which is what a real-Steam test would read.
    $restartLog = Join-Path $env:LOCALAPPDATA 'MultiSeat\SteamIsolation\launcher.log'
    $restartLogBefore = if (Test-Path -LiteralPath $restartLog) { (Get-Item -LiteralPath $restartLog).Length } else { 0 }
    $spaced = Join-Path $directory 'with space'
    New-Item -ItemType Directory -Force -Path $spaced | Out-Null
    foreach ($exe in @('steam.exe', 'steamwebhelper.exe')) {
        Copy-Item -LiteralPath (Join-Path $source "SteamIsolationFixture$suffix.exe") -Destination (Join-Path $spaced $exe)
    }
    foreach ($via in @('w', 'a', 'user', 'unquoted')) {
        $steamIn = if ($via -eq 'unquoted') { $spaced } else { '' }
        Invoke-Fixture $directory "--report first-$via.txt --restart-via $via --restart-report restarted-$via.txt --child-report browser-$via.txt --child-hold-ms 200" $true 0 $steamIn
        $replacement = Get-Content -LiteralPath (Join-Path $directory "restarted-$via.txt") -Raw
        $browser = Get-Content -LiteralPath (Join-Path $directory "browser-$via.txt") -Raw
        Assert-True ($replacement -match '(?m)^steamPid=123456\r?$') "Replacement Steam did not run its registry checks (restart via $via)"
        Assert-True ($replacement -match '(?m)^steam_master_ipc_name_override=MultiSeat_') "Replacement Steam was not hooked (restart via $via)"
        Assert-True ($browser -match 'htmlcache\.MultiSeat_') "Replacement browser cache was not isolated (restart via $via)"
    }
    $restartLogText = [IO.File]::ReadAllText($restartLog)
    $restartLogNew = $restartLogText.Substring([Math]::Min($restartLogBefore, $restartLogText.Length))
    $isolatedRestarts = ([regex]::Matches($restartLogNew, 'Steam restart isolated')).Count
    Assert-True ($isolatedRestarts -ge 4) "The hook logged $isolatedRestarts isolated restarts, expected one per way of restarting (4)"
    $supervisor = $null
    try {
        $args = '--steam "' + (Join-Path $directory 'steam.exe') + '" -- --restart-report live-restarted.txt --child-report live-browser.txt --child-hold-ms 30000'
        $supervisor = Start-Process -FilePath (Join-Path $directory 'MultiSeat.SteamLauncher.exe') -ArgumentList $args `
            -WorkingDirectory $directory -WindowStyle Hidden -PassThru
        $childInfo = $null
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            $childInfo = Get-CimInstance Win32_Process -Filter "Name='steamwebhelper.exe'" |
                Where-Object ExecutablePath -eq (Join-Path $directory 'steamwebhelper.exe')
            if ($childInfo -and (Test-Path -LiteralPath (Join-Path $directory 'live-browser.txt'))) { break }
            Start-Sleep -Milliseconds 100
        }
        Assert-True ([bool]$childInfo) 'Replacement browser fixture did not start'
        $childProcess = Get-Process -Id $childInfo.ProcessId
        Start-Sleep -Milliseconds 300
        Assert-True (-not $supervisor.HasExited) 'Supervisor stopped while replacement descendants were running'
        Stop-Process -Id $supervisor.Id
        Assert-True ($childProcess.WaitForExit(3000)) 'Supervisor termination left a browser descendant running'
    } finally {
        if ($supervisor -and -not $supervisor.HasExited) { Stop-Process -Id $supervisor.Id }
    }
}
$failureDirectory = Join-Path $runRoot 'missing-hook'
New-Item -ItemType Directory -Force -Path $failureDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'MultiSeat.SteamLauncher.exe') -Destination $failureDirectory
Copy-Item -LiteralPath (Join-Path $source 'SteamIsolationFixture.exe') -Destination (Join-Path $failureDirectory 'steam.exe')
$launcherLog = Join-Path $env:LOCALAPPDATA 'MultiSeat\SteamIsolation\launcher.log'
$logBefore = if (Test-Path -LiteralPath $launcherLog) { (Get-Item -LiteralPath $launcherLog).Length } else { 0 }
Invoke-Fixture $failureDirectory '--report unexpected.txt' $false 1
Assert-True (-not (Test-Path -LiteralPath (Join-Path $failureDirectory 'unexpected.txt'))) 'Unisolated child ran after a hook failure'
# With no console, a failure has to be findable afterwards: in the log file, and on stderr when it was redirected.
Assert-True (Test-Path -LiteralPath $launcherLog) "The launcher wrote no log file at $launcherLog"
$logText = [IO.File]::ReadAllText($launcherLog)
Assert-True ($logText.Substring([Math]::Min($logBefore, $logText.Length)) -match 'Steam isolation failed') 'The hook failure was not written to the launcher log'
$stderrText = Get-Content -LiteralPath (Join-Path $failureDirectory 'stderr.txt') -Raw
Assert-True ($stderrText -match 'Steam isolation failed') 'The hook failure was not written to the redirected stderr'
Write-Host "Native launcher smoke tests passed. Fixture reports: $runRoot"
