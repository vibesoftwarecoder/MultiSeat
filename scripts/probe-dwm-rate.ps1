<#
.SYNOPSIS
  Measures the DWM composition rate of the session it runs in.

.DESCRIPTION
  A seat streams its RDP session surface, so the session's composition rate is the ceiling on how
  often that seat can produce a new frame. MultiSeat raises it with DWMFRAMEINTERVAL
  (MultiSeatOptions.DwmFrameIntervalMs); this is how you check that the setting did anything.

  Windows silently ignores an out-of-range interval, so the registry value alone proves nothing.
  Measure instead of reading it back.

  The number comes from pacing off DwmFlush, which blocks until the next composition frame. If
  DwmFlush does not block, the count is meaningless and the script reports PROBE INVALID rather
  than a tidy figure it did not earn.

.NOTES
  Display and composition APIs are session-scoped: run this INSIDE the session being measured.
  Session 0 sees nothing. To probe a seat, register it as a scheduled task for the seat account
  with -LogonType Interactive; no password is needed.

  Exit codes: 0 = PROBE VALID, 2 = PROBE INVALID.

.EXAMPLE
  .\probe-dwm-rate.ps1
  Measures the current session and prints the rate.

.EXAMPLE
  $p = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\Seat1" -LogonType Interactive
  $a = New-ScheduledTaskAction -Execute powershell.exe -Argument '-File C:\path\probe-dwm-rate.ps1'
  Register-ScheduledTask -TaskName DwmProbe -Action $a -Principal $p -Force; Start-ScheduledTask DwmProbe
  Measures a seat session; the result file lands under C:\ProgramData\MultiSeat\logs.
#>
param([int]$Flushes = 240, [string]$Label = "")

$ErrorActionPreference = 'Continue'
$sid = (Get-Process -Id $PID).SessionId
$dir = 'C:\ProgramData\MultiSeat\logs'
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$out = Join-Path $dir ("dwm-rate-s{0}{1}.txt" -f $sid, $(if ($Label) { "-$Label" } else { "" }))
$lines = New-Object System.Collections.Generic.List[string]
function W($m) { $lines.Add($m); Write-Output $m }

W "=== DWM probe ==="
W ("label          : {0}" -f $Label)
W ("session        : {0}   user: {1}\{2}" -f $sid, $env:USERDOMAIN, $env:USERNAME)
W ("time           : {0}" -f (Get-Date -Format o))

$src = @'
using System;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
public struct DWM_TIMING_INFO {
  public uint cbSize;
  public uint rateRefreshNum;   public uint rateRefreshDen;
  public ulong qpcRefreshPeriod;
  public uint rateComposeNum;   public uint rateComposeDen;
  public ulong qpcVBlank;
  public ulong cRefresh;        public uint cDXRefresh;
  public ulong qpcCompose;
  public ulong cFrame;          public uint cDXPresent;
  public ulong cRefreshFrame;
  public ulong cFrameSubmitted; public uint cDXPresentSubmitted;
  public ulong cFrameConfirmed; public uint cDXPresentConfirmed;
  public ulong cRefreshConfirmed; public uint cDXRefreshConfirmed;
  public ulong cFramesLate;     public uint cFramesOutstanding;
  public ulong cFrameDisplayed; public ulong qpcFrameDisplayed;
  public ulong cRefreshFrameDisplayed;
  public ulong cFrameComplete;  public ulong qpcFrameComplete;
  public ulong cFramePending;   public ulong qpcFramePending;
  public ulong cFramesDisplayed; public ulong cFramesComplete;
  public ulong cFramesPending;  public ulong cFramesAvailable;
  public ulong cFramesDropped;  public ulong cFramesMissed;
  public ulong cRefreshNextDisplayed; public ulong cRefreshNextPresented;
  public ulong cRefreshesDisplayed;   public ulong cRefreshesPresented;
  public ulong cRefreshStarted;
  public ulong cPixelsReceived; public ulong cPixelsDrawn;
  public ulong cBuffersEmpty;
}
public static class Dwm {
  [DllImport("dwmapi.dll")] public static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO ti);
  [DllImport("dwmapi.dll")] public static extern int DwmFlush();
  [DllImport("dwmapi.dll")] public static extern int DwmIsCompositionEnabled(out bool en);
  public static int Size() { return Marshal.SizeOf(typeof(DWM_TIMING_INFO)); }
}
public static class Disp {
  [DllImport("user32.dll", CharSet=CharSet.Auto)]
  public static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
}
[StructLayout(LayoutKind.Sequential, CharSet=CharSet.Auto)]
public struct DEVMODE {
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
  public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra;
  public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation;
  public int dmDisplayFixedOutput; public short dmColor; public short dmDuplex; public short dmYResolution;
  public short dmTTOption; public short dmCollate;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;
  public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;
  public int dmDisplayFlags; public int dmDisplayFrequency;
  public int dmICMMethod; public int dmICMIntent; public int dmMediaType; public int dmDitherType;
  public int dmReserved1; public int dmReserved2; public int dmPanningWidth; public int dmPanningHeight;
}
'@
try { Add-Type -TypeDefinition $src -ErrorAction Stop } catch { W ("Add-Type FAILED: {0}" -f $_.Exception.Message); W "PROBE INVALID"; $lines | Set-Content $out; exit 2 }

W ("struct size    : {0} bytes (expect 320)" -f [Dwm]::Size())

# --- advertised display mode (the '1000Hz' question) ---
$dm = New-Object DEVMODE
$dm.dmSize = [int16]([System.Runtime.InteropServices.Marshal]::SizeOf([type]'DEVMODE'))
if ([Disp]::EnumDisplaySettings($null, -1, [ref]$dm)) {
    W ("display mode   : {0}x{1} @ {2} Hz advertised ({3})" -f $dm.dmPelsWidth, $dm.dmPelsHeight, $dm.dmDisplayFrequency, $dm.dmDeviceName)
} else { W "display mode   : EnumDisplaySettings failed" }
try {
    Get-CimInstance Win32_VideoController -ErrorAction Stop | ForEach-Object {
        W ("video adapter  : {0} | {1}x{2} @ {3} Hz" -f $_.Name, $_.CurrentHorizontalResolution, $_.CurrentVerticalResolution, $_.CurrentRefreshRate)
    }
} catch { W ("video adapter  : CIM failed: {0}" -f $_.Exception.Message) }

# --- DWM composition state ---
$en = $false
$hr = [Dwm]::DwmIsCompositionEnabled([ref]$en)
W ("composition    : enabled={0} (hr=0x{1:X8})" -f $en, $hr)

# --- reported rates ---
$ti = New-Object DWM_TIMING_INFO
$ti.cbSize = [uint32][Dwm]::Size()
$hr = [Dwm]::DwmGetCompositionTimingInfo([IntPtr]::Zero, [ref]$ti)
if ($hr -eq 0) {
    $rr = if ($ti.rateRefreshDen -gt 0) { $ti.rateRefreshNum / $ti.rateRefreshDen } else { 0 }
    $rc = if ($ti.rateComposeDen -gt 0) { $ti.rateComposeNum / $ti.rateComposeDen } else { 0 }
    W ("rateRefresh    : {0:N2} Hz  ({1}/{2})" -f $rr, $ti.rateRefreshNum, $ti.rateRefreshDen)
    W ("rateCompose    : {0:N2} Hz  ({1}/{2})" -f $rc, $ti.rateComposeNum, $ti.rateComposeDen)
    W ("qpcRefreshPeriod: {0}" -f $ti.qpcRefreshPeriod)
} else {
    W ("DwmGetCompositionTimingInfo FAILED hr=0x{0:X8}" -f $hr)
}

# --- EMPIRICAL: how often does the compositor actually tick? ---
# DwmFlush blocks until the next composition frame. If it never blocks, the count is meaningless
# and the probe says so instead of reporting a number.
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$fails = 0
for ($i = 0; $i -lt $Flushes; $i++) { if ([Dwm]::DwmFlush() -ne 0) { $fails++ } }
$sw.Stop()
$ms = $sw.Elapsed.TotalMilliseconds
$rate = if ($ms -gt 0) { $Flushes / ($ms / 1000.0) } else { 0 }
W ("DwmFlush       : {0} calls, {1} failed, {2:N1} ms total" -f $Flushes, $fails, $ms)
W ("MEASURED RATE  : {0:N1} fps  ({1:N3} ms per composition frame)" -f $rate, ($ms / $Flushes))

$valid = $true
if ($fails -gt 0) { W "!! DwmFlush returned errors"; $valid = $false }
if ($ms -lt 5) { W "!! DwmFlush did not block - it is not pacing to the compositor"; $valid = $false }
if (-not $en) { W "!! composition reported disabled"; $valid = $false }
W $(if ($valid) { "PROBE VALID" } else { "PROBE INVALID" })

$lines | Set-Content $out -Encoding UTF8
exit $(if ($valid) { 0 } else { 2 })
