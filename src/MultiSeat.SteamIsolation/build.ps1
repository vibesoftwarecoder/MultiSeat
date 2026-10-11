param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [switch]$BuildFixture
)
$ErrorActionPreference = 'Stop'
$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio 2022 C++ build tools are required.' }
$installation = & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio 2022 C++ build tools are required.' }
$cmake = if ($cmakeCommand) { $cmakeCommand.Source } else { Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' }
if (-not (Test-Path -LiteralPath $cmake)) { throw 'CMake is required (install the Visual Studio CMake tools component).' }
$fixtureFlag = if ($BuildFixture) { 'ON' } else { 'OFF' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($architecture in @('x64', 'Win32')) {
    $buildDirectory = Join-Path $PSScriptRoot "build-$architecture"
    & $cmake -S $PSScriptRoot -B $buildDirectory -G 'Visual Studio 17 2022' -A $architecture "-DMULTISEAT_BUILD_STEAM_FIXTURE=$fixtureFlag"
    if ($LASTEXITCODE -ne 0) { throw "CMake configuration failed for $architecture" }
    & $cmake --build $buildDirectory --config Release --parallel
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $architecture" }
    $suffix = if ($architecture -eq 'Win32') { '32' } else { '' }
    foreach ($filename in @("MultiSeat.SteamLauncher$suffix.exe", "MultiSeat.SteamHook$suffix.dll")) {
        Copy-Item -LiteralPath (Join-Path $buildDirectory "Release\$filename") -Destination $destination -Force
    }
    if ($BuildFixture) {
        Copy-Item -LiteralPath (Join-Path $buildDirectory 'Release\SteamIsolationFixture.exe') -Destination (Join-Path $destination "SteamIsolationFixture$suffix.exe") -Force
    }
}
Write-Host "Steam isolation binaries written to $destination"
