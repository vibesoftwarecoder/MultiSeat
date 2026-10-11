param(
    [string]$Artifacts = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$TestRoot = (Join-Path $PSScriptRoot '..\build-game-smoke')
)
$ErrorActionPreference = 'Stop'
$taskSource = [IO.Path]::GetFullPath($Artifacts)
$taskRun = Join-Path ([IO.Path]::GetFullPath($TestRoot)) ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRun -Force | Out-Null
$taskFixture = Join-Path $taskSource 'SteamIsolationFixture.exe'
foreach ($taskName in @('MultiSeat.SteamLauncher.exe', 'MultiSeat.SteamHook.dll')) {
    Copy-Item -LiteralPath (Join-Path $taskSource $taskName) -Destination $taskRun
}
Copy-Item -LiteralPath $taskFixture -Destination (Join-Path $taskRun 'steam.exe')
Copy-Item -LiteralPath $taskFixture -Destination (Join-Path $taskRun 'game.exe')
$taskLauncher = $null
try {
    $taskSteamReport = Join-Path $taskRun 'steam-report.txt'
    $taskSteamArgs = '--steam "' + (Join-Path $taskRun 'steam.exe') + '" -- --report "' + $taskSteamReport + '" --hold-ms 30000'
    $taskLauncher = Start-Process -FilePath (Join-Path $taskRun 'MultiSeat.SteamLauncher.exe') -ArgumentList $taskSteamArgs -WorkingDirectory $taskRun -WindowStyle Hidden -PassThru
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $taskSteamReport) -and [DateTime]::UtcNow -lt $taskDeadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $taskSteamReport)) { throw 'Isolated Steam fixture did not start.' }
    $taskOriginal = $env:steam_master_ipc_name_override
    & (Join-Path $PSScriptRoot '..\Launch-IsolatedGame.ps1') -Executable (Join-Path $taskRun 'game.exe') -Arguments '--report game-report.txt'
    if ($env:steam_master_ipc_name_override -cne $taskOriginal) { throw 'Game launcher changed the caller environment.' }
    $taskGameReport = Join-Path $taskRun 'game-report.txt'
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $taskGameReport) -and [DateTime]::UtcNow -lt $taskDeadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $taskGameReport)) { throw 'Game did not write its report in its installation folder.' }
    $taskSteamIPC = @(Get-Content -LiteralPath $taskSteamReport | Where-Object { $_ -like 'steam_master_ipc_name_override=*' })
    $taskGameIPC = @(Get-Content -LiteralPath $taskGameReport | Where-Object { $_ -like 'steam_master_ipc_name_override=*' })
    if ($taskSteamIPC.Count -ne 1 -or $taskGameIPC.Count -ne 1 -or $taskSteamIPC[0] -cne $taskGameIPC[0]) { throw 'Game and isolated Steam used different IPC names.' }
    if (-not (Select-String -LiteralPath $taskGameReport -Pattern '^SteamPID=123456$' -Quiet)) { throw 'Direct game received an unexpected registry hook.' }
    Write-Host 'Game launcher environment and working-directory checks passed.'
} finally {
    if ($taskLauncher -and -not $taskLauncher.HasExited) {
        Stop-Process -Id $taskLauncher.Id
        [void]$taskLauncher.WaitForExit(5000)
    }
    if ($taskLauncher) { $taskLauncher.Dispose() }
}
