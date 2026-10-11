param(
    [string]$Artifacts = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$TestRoot = (Join-Path $PSScriptRoot '..\build-smoke')
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($Artifacts)
$runRoot = Join-Path ([IO.Path]::GetFullPath($TestRoot)) ([guid]::NewGuid().ToString('N'))
function Assert-True($condition, [string]$message) { if (-not $condition) { throw $message } }
function Invoke-Fixture([string]$directory, [string]$tail, [bool]$expectReady, [int]$expectExit) {
    $name = 'Global\MultiSeatSteamReady-' + [guid]::NewGuid().ToString('N')
    $ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $name)
    $launcher = $null
    try {
        $args = '--steam "' + (Join-Path $directory 'steam.exe') + '" --ready-event "' + $name + '" -- ' + $tail
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
    Assert-True ($report -match '(?m)^steamPid=0\r?$') 'SteamPID DWORD was not isolated'
    Assert-True ($report -match '(?m)^steamPidNullType=0\r?$') 'SteamPID DWORD with null type was not isolated'
    Assert-True ($report -match '(?m)^steamPidString=fixture-nonDWORD\r?$') 'SteamPID string was corrupted'
    Assert-True ($report -match '(?m)^arg5=two words\r?$') 'Quoted argument was not preserved'
    Invoke-Fixture $directory '--report first.txt --restart-report restarted.txt --child-report browser.txt --child-hold-ms 200' $true 0
    $replacement = Get-Content -LiteralPath (Join-Path $directory 'restarted.txt') -Raw
    $browser = Get-Content -LiteralPath (Join-Path $directory 'browser.txt') -Raw
    Assert-True ($replacement -match '(?m)^steamPid=0\r?$') 'Replacement Steam was not hooked'
    Assert-True ($browser -match 'htmlcache\.MultiSeat_') 'Replacement browser cache was not isolated'
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
Invoke-Fixture $failureDirectory '--report unexpected.txt' $false 1
Assert-True (-not (Test-Path -LiteralPath (Join-Path $failureDirectory 'unexpected.txt'))) 'Unisolated child ran after a hook failure'
Write-Host "Native launcher smoke tests passed. Fixture reports: $runRoot"
