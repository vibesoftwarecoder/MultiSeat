param(
    [string]$Executable = '',
    [string]$Arguments = ''
)
$ErrorActionPreference = 'Stop'
$taskInteractive = [string]::IsNullOrWhiteSpace($Executable)
try {
if ($taskInteractive) {
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.Application]::EnableVisualStyles()
    $taskPicker = New-Object Windows.Forms.OpenFileDialog
    try {
        $taskPicker.Title = 'Select a game to launch with isolated Steam'
        $taskPicker.Filter = 'Game executable (*.exe)|*.exe'
        $taskPicker.CheckFileExists = $true
        $taskPicker.Multiselect = $false
        $taskPicker.RestoreDirectory = $true
        if ($taskPicker.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { return }
        $Executable = $taskPicker.FileName
    } finally { $taskPicker.Dispose() }
}
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Game executable not found: $Executable"
}
if ([IO.Path]::GetExtension($Executable) -ine '.exe') {
    throw 'Select an EXE file.'
}
$taskSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
if (-not @(Get-Process steam -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $taskSession }).Count) {
    throw 'Start Steam through MultiSeat.SteamLauncher in this Windows session first.'
}
# Match the session/SID-derived name used by MultiSeat.SteamHook.cpp.
if (-not ('MultiSeatGameIPC' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
public static class MultiSeatGameIPC {
    public static string Name(int session, string sid) {
        ulong hash = 14695981039346656037UL;
        unchecked {
            foreach (char character in sid) { hash ^= (ulong)character; hash *= 1099511628211UL; }
        }
        return "MultiSeat_" + session + "_" + hash;
    }
}
'@
}
$taskIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $taskSid = $taskIdentity.User.Value } finally { $taskIdentity.Dispose() }
$taskIPC = [MultiSeatGameIPC]::Name($taskSession, $taskSid)
$taskStart = New-Object Diagnostics.ProcessStartInfo
$taskStart.FileName = [IO.Path]::GetFullPath($Executable)
$taskStart.WorkingDirectory = [IO.Path]::GetDirectoryName($taskStart.FileName)
$taskStart.Arguments = $Arguments
$taskStart.UseShellExecute = $false
$taskStart.EnvironmentVariables['steam_master_ipc_name_override'] = $taskIPC
Write-Host "Launching game for isolated Steam connection: $taskIPC"
$taskGame = [Diagnostics.Process]::Start($taskStart)
try { Write-Host "Game process started: $($taskGame.Id)" } finally { $taskGame.Dispose() }
} catch {
    if ($taskInteractive) {
        Add-Type -AssemblyName System.Windows.Forms
        [void][Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Steam game launcher',
            [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Error)
        exit 1
    }
    throw
}
