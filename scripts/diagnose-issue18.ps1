<#
.SYNOPSIS
    One-command diagnosis for issue #18: does seat input still reach the console cursor, and if so
    what is carrying it?

.DESCRIPTION
    Run this ELEVATED, in the CONSOLE session, while a Moonlight client is streaming a seat and you
    can put your hand on that client's mouse.

    It replaces the check-keepalive.ps1 / watch-console-cursor.ps1 / suspend-mstsc-probe.ps1 dance
    with a single run that gates itself before it measures anything, and that refuses to report a
    verdict it has not earned.

    WHY THE STIMULUS IS YOUR HAND, NOT SetCursorPos
    The earlier seat-side probe drove the cursor with SetCursorPos and SendInput from inside the
    seat. On the reporter's host every one of those calls was refused, so the console's "no
    movement" was a reading that could not have come out any other way. Your hand on the Moonlight
    mouse exercises the real path the bug is about - Apollo injecting input into the seat session -
    and it needs no second shell, no SYSTEM token, and no scheduled task. If the console cursor
    tracks your client mouse, that IS the bug, reproduced, with nothing synthetic in between.

    THE FIVE STAGES, each of which can stop the run

      0. PROVENANCE. Which exe is actually deployed, what commit it was built from, and what value
         it resolved for KeepaliveOnSeparateDesktop. A 'git pull' does not rebuild the service, so
         the binary in C:\Program Files\MultiSeat can be older than the source you just fetched.
         That is what happened last time and it cost a whole round trip. If the deployed binary
         predates the fix, this stops here and says so, instead of measuring an old build.

      1. FRESHNESS. Restarting the service does not relaunch mstsc - an RDP session that stayed
         Active keeps the mstsc that created it. Any mstsc older than the deployment is a seat that
         predates the upgrade, so the new code never ran for it. Stops here too.

      2. PLACEMENT. Which desktop each mstsc is really on, read back from the desktop's own window
         list rather than from config or a log line.

      3. SYMPTOM. Measures a hands-off noise floor first, then asks you to move the client mouse,
         and compares. It self-tests the sampler before trusting any quiet reading, and it counts
         BUTTONS as well as position, because "position without clicks" and "position with clicks"
         are different mechanisms and issue #18 never established which one this is.

      4. MECHANISM. Only runs if stage 3 reproduced the symptom. Suspends the keepalive mstsc and
         repeats the identical stimulus. If the leak stops, mstsc is carrying it; if it continues,
         something else is and we go hunting. mstsc is suspended, never killed, because killing it
         drops the seat to Disconnected and ends the stream, so a stopped cursor would prove
         nothing. It is always resumed, including on Ctrl-C.

    Everything it prints is safe to paste into the issue. No paths outside Program Files, no user
    names, no addresses.

.PARAMETER Seconds
    Length of each measurement phase. Default 8.

.PARAMETER SkipMechanism
    Run stages 0-3 only. Use if you would rather not have the stream paused for a few seconds.

.NOTES
    GitHub issue #18. Supersedes check-keepalive.ps1 as the thing to run.
#>
[CmdletBinding()]
param(
    [int]$Seconds = 8,
    [switch]$SkipMechanism
)

$ErrorActionPreference = 'Stop'
$exe = 'C:\Program Files\MultiSeat\MultiSeat.Service.exe'

# ---------------------------------------------------------------- interop
if (-not ('Diag18' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public struct PT { public int X; public int Y; }

public static class Diag18 {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool GetCursorPos(out PT p);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    delegate bool EnumDesktopProc([MarshalAs(UnmanagedType.LPWStr)] string desktop, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumDesktopsW(IntPtr winsta, EnumDesktopProc cb, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenDesktopW(string desktop, int flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr h);
    delegate bool EnumWindowProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowProc cb, IntPtr param);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr h);

    public static int Err() { return Marshal.GetLastWin32Error(); }

    public static List<string> Desktops() {
        var found = new List<string>();
        EnumDesktopsW(GetProcessWindowStation(), delegate(string d, IntPtr p) { found.Add(d); return true; }, IntPtr.Zero);
        return found;
    }

    public static List<int> PidsOn(string desktop) {
        var pids = new List<int>();
        var h = OpenDesktopW(desktop, 0, false, 0x0100);
        if (h == IntPtr.Zero) return pids;
        try {
            EnumDesktopWindows(h, delegate(IntPtr hwnd, IntPtr p) {
                int pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pid != 0 && !pids.Contains(pid)) pids.Add(pid);
                return true;
            }, IntPtr.Zero);
        } finally { CloseDesktop(h); }
        return pids;
    }

    // PROCESS_SUSPEND_RESUME (0x0800) | PROCESS_QUERY_LIMITED_INFORMATION (0x1000)
    public static int Suspend(int pid, bool resume) {
        IntPtr h = OpenProcess(0x0800 | 0x1000, false, pid);
        if (h == IntPtr.Zero) return -1;
        try {
            if (resume) { return NtResumeProcess(h); }
            return NtSuspendProcess(h);
        } finally { CloseHandle(h); }
    }
}
'@
}

function Say([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Head([string]$t) { Write-Host ''; Write-Host $t -ForegroundColor Cyan }

$report = New-Object System.Collections.Generic.List[string]
function Rec([string]$line) { $report.Add($line) | Out-Null }

Head '== MultiSeat issue #18 diagnosis =='
Say ("run at   : {0}" -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Say ("shell    : session {0}, elevated={1}" -f (Get-Process -Id $PID).SessionId, $isAdmin)
Rec ("shell session={0} elevated={1}" -f (Get-Process -Id $PID).SessionId, $isAdmin)

# ---------------------------------------------------------------- stage 0
Head '-- stage 0: which binary is actually deployed --'

if (-not (Test-Path $exe)) {
    Say ("  ABORT: {0} does not exist. The service is not deployed here." -f $exe) 'Red'
    exit 1
}

$cfgRaw = ''
try { $cfgRaw = & $exe --config 2>&1 | Out-String } catch { $cfgRaw = '' }

if ($cfgRaw -notmatch 'effective MultiSeat settings') {
    Say '  ABORT: the deployed binary does not understand --config.' 'Red'
    Say '  That alone dates it before ddb2342, so it cannot contain the keepalive fix either.' 'Red'
    Say ''
    Say '  Deploy the build you fetched, then re-run this script:' 'Yellow'
    Say '      .\scripts\install-service.ps1' 'Yellow'
    exit 1
}

$deployedAt = $null
$version    = '(none)'
$keepaliveSetting = $null
foreach ($line in ($cfgRaw -split "`r?`n")) {
    if ($line -match 'built / copied:\s*(.+)$') {
        $t = $matches[1].Trim()
        try { $deployedAt = [datetime]::Parse($t) } catch { $deployedAt = $null }
    }
    if ($line -match 'version\s*:\s*(.+)$')                     { $version = $matches[1].Trim() }
    if ($line -match 'KeepaliveOnSeparateDesktop\s*=\s*(\w+)')  { $keepaliveSetting = $matches[1].Trim() }
}

$deployedText = 'UNREADABLE'
if ($deployedAt) { $deployedText = $deployedAt.ToString('yyyy-MM-dd HH:mm:ss') }
$keepaliveText = 'NOT REPORTED'
if ($keepaliveSetting) { $keepaliveText = $keepaliveSetting }

Say ("  deployed exe built/copied : {0}" -f $deployedText)
Say ("  version stamp             : {0}" -f $version)
Say ("  KeepaliveOnSeparateDesktop: {0}" -f $keepaliveText)
Rec ("deployed={0} version={1} keepalive={2}" -f $deployedText, $version, $keepaliveText)

if ($version -eq '(none)' -or $version -notmatch '\+') {
    Say '  ABORT: the binary carries no commit stamp, so it predates the build that records one.' 'Red'
    Say '  Run .\scripts\install-service.ps1 and try again.' 'Red'
    exit 1
}
if ($keepaliveSetting -ne 'True') {
    Say '  ABORT: KeepaliveOnSeparateDesktop did not resolve to True in the DEPLOYED binary.' 'Red'
    Say '  Nothing below would be testing the fix. Check appsettings.local.json.' 'Red'
    exit 1
}
Say '  stage 0 PASS - a stamped binary with the fix compiled in is deployed.' 'Green'

# ---------------------------------------------------------------- stage 1
Head '-- stage 1: is the seat newer than the deployment --'

$mstsc = @(Get-Process mstsc -ErrorAction SilentlyContinue)
if ($mstsc.Count -eq 0) {
    Say '  ABORT: no mstsc is running, so no seat is up. Provision one and re-run.' 'Yellow'
    exit 2
}

$stale = @()
foreach ($p in $mstsc) {
    $age = 'newer than deploy'
    if ($deployedAt -and $p.StartTime -lt $deployedAt) { $age = 'OLDER THAN DEPLOY'; $stale += $p }
    Say ("  pid {0,-7} session {1,-4} started {2}  {3}" -f $p.Id, $p.SessionId, $p.StartTime.ToString('HH:mm:ss'), $age)
    Rec ("mstsc pid={0} session={1} started={2} {3}" -f $p.Id, $p.SessionId, $p.StartTime.ToString('HH:mm:ss'), $age)
}
if ($stale.Count -gt 0) {
    Say '  ABORT: an mstsc predates the deployment, so the new code never ran for that seat.' 'Red'
    Say '  Restarting the service does NOT relaunch mstsc. Tear the seat down, provision a fresh' 'Red'
    Say '  one, then re-run this script.' 'Red'
    exit 1
}
Say '  stage 1 PASS - every mstsc was started after the deployment.' 'Green'

# ---------------------------------------------------------------- stage 2
Head '-- stage 2: which desktop is the keepalive on --'

$desktops = [Diag18]::Desktops()
Say ("  desktops in this window station: {0}" -f ($desktops -join ', '))
Rec ("desktops={0}" -f ($desktops -join ','))

$map = @{}
foreach ($d in $desktops) { $map[$d] = [Diag18]::PidsOn($d) }

$onConsole = @()
foreach ($p in $mstsc) {
    $where = @()
    foreach ($d in $desktops) { if ($map[$d] -contains $p.Id) { $where += $d } }
    $desk = 'unknown (no visible window)'
    if ($where.Count -gt 0) { $desk = ($where -join '+') }
    if ($where -contains 'Default') { $onConsole += $p }
    Say ("  pid {0,-7} desktop: {1}" -f $p.Id, $desk)
    Rec ("placement pid={0} desktop={1}" -f $p.Id, $desk)
}
if ($onConsole.Count -gt 0) {
    Say '  stage 2 WARN - an mstsc still has windows on the console desktop.' 'Yellow'
    Say '  Measuring anyway: whether that actually leaks input is the question, not an assumption.' 'Yellow'
} else {
    Say '  stage 2 PASS - no mstsc has windows on the console desktop.' 'Green'
}

# ---------------------------------------------------------------- sampler
function Sample-Console([int]$secs, [string]$label) {
    $p = New-Object PT
    [void][Diag18]::GetCursorPos([ref]$p)
    $last = @($p.X, $p.Y)
    $samples = 0; $changes = 0; $path = 0; $clicks = 0
    $lastBtn = $false
    $deadline = (Get-Date).AddSeconds($secs)
    while ((Get-Date) -lt $deadline) {
        [void][Diag18]::GetCursorPos([ref]$p)
        $samples++
        if ($p.X -ne $last[0] -or $p.Y -ne $last[1]) {
            $changes++
            $path += [math]::Abs($p.X - $last[0]) + [math]::Abs($p.Y - $last[1])
            $last = @($p.X, $p.Y)
        }
        $lb = (([Diag18]::GetAsyncKeyState(1)) -band 0x8000) -ne 0
        $rb = (([Diag18]::GetAsyncKeyState(2)) -band 0x8000) -ne 0
        $btn = ($lb -or $rb)
        if ($btn -and -not $lastBtn) { $clicks++ }
        $lastBtn = $btn
        Start-Sleep -Milliseconds 40
    }
    Say ("  {0,-22} {1,5} samples  {2,4} moves  {3,6} px travelled  {4,3} clicks" -f $label, $samples, $changes, $path, $clicks)
    Rec ("{0}: samples={1} moves={2} path={3} clicks={4}" -f $label, $samples, $changes, $path, $clicks)
    return [pscustomobject]@{ Samples=$samples; Changes=$changes; Path=$path; Clicks=$clicks }
}

# ---------------------------------------------------------------- stage 3
Head '-- stage 3: does client input reach the console cursor --'

# Self-test: a quiet reading is only evidence if this loop would have caught a move.
$sp = New-Object PT
[void][Diag18]::GetCursorPos([ref]$sp)
$origin = @($sp.X, $sp.Y)
$okSet = [Diag18]::SetCursorPos($origin[0] + 6, $origin[1] + 6)
Start-Sleep -Milliseconds 250
[void][Diag18]::GetCursorPos([ref]$sp)
$selftest = ($sp.X -ne $origin[0] -or $sp.Y -ne $origin[1])
[void][Diag18]::SetCursorPos($origin[0], $origin[1])
if ($selftest) {
    Say '  selftest PASS - this sampler does see console cursor movement.' 'Green'
    Rec 'selftest=PASS'
} else {
    Say ("  selftest FAIL - SetCursorPos ok={0} err={1}. Readings below are MEANINGLESS." -f $okSet, [Diag18]::Err()) 'Red'
    Rec 'selftest=FAIL'
}

Say ''
Say '  PHASE A - noise floor. Take your hands OFF the client mouse and the host mouse.' 'Yellow'
Read-Host '  Press Enter when your hands are off, then do not touch anything'
$idle = Sample-Console $Seconds 'A idle (hands off)'

Say ''
Say '  PHASE B - stimulus. Move the MOONLIGHT CLIENT mouse in big circles for the whole phase,' 'Yellow'
Say '            and left-click a few times. Do NOT touch the host mouse.' 'Yellow'
Read-Host '  Press Enter, then start moving the client mouse immediately'
$stim = Sample-Console $Seconds 'B client mouse moving'

# A leak has to clear the noise floor by a real margin, not by one stray sample.
$floor = [math]::Max(($idle.Path * 3), 60)
$leaked = ($stim.Path -gt $floor)

Say ''
if (-not $selftest) {
    Say '  stage 3 INVALID - the sampler failed its self-test, so nothing here counts.' 'Red'
    Rec 'stage3=INVALID'
    $leaked = $false
} elseif ($leaked) {
    Say ("  stage 3 REPRODUCED - console cursor travelled {0} px under client input, against a {1} px floor." -f $stim.Path, $floor) 'Red'
    Rec 'stage3=REPRODUCED'
    if ($stim.Clicks -gt 0) {
        Say ("  Clicks crossed too ({0} seen on the console). Different mechanism from position-only." -f $stim.Clicks) 'Red'
        Rec 'stage3-clicks=YES'
    } else {
        Say '  Position crossed but clicks did NOT. Matches the RDP pointer-position theory.' 'Yellow'
        Rec 'stage3-clicks=NO'
    }
} else {
    Say ("  stage 3 CLEAR - console cursor travelled {0} px, under the {1} px floor. No leak seen." -f $stim.Path, $floor) 'Green'
    Rec 'stage3=CLEAR'
}

# ---------------------------------------------------------------- stage 4
if ($leaked -and (-not $SkipMechanism)) {
    Head '-- stage 4: is the keepalive mstsc carrying it --'

    $target = $mstsc[0]
    if ($onConsole.Count -gt 0) { $target = $onConsole[0] }
    Say ("  suspending mstsc pid {0} (suspend, not kill - the stream stays up)" -f $target.Id)

    $suspended = $false
    try {
        $rc = [Diag18]::Suspend($target.Id, $false)
        if ($rc -ne 0) {
            Say ("  could not suspend (rc={0}). Skipping stage 4." -f $rc) 'Yellow'
            Rec 'stage4=SUSPEND-FAILED'
        } else {
            $suspended = $true
            Start-Sleep -Milliseconds 400
            Say ''
            Say '  PHASE C - move the CLIENT mouse in circles again, exactly as in phase B.' 'Yellow'
            Read-Host '  Press Enter, then start moving'
            $susp = Sample-Console $Seconds 'C mstsc suspended'

            if ($susp.Path -le $floor) {
                Say '  stage 4 RESULT: the leak STOPPED with mstsc suspended.' 'Green'
                Say '  The keepalive mstsc is what carries seat input to the console cursor.' 'Green'
                Rec 'stage4=MSTSC-IS-CAUSE'
            } else {
                Say ("  stage 4 RESULT: the leak CONTINUED ({0} px with mstsc suspended)." -f $susp.Path) 'Red'
                Say '  mstsc is NOT the mechanism. Something else is mirroring the pointer.' 'Red'
                Rec 'stage4=MSTSC-NOT-CAUSE'
            }
        }
    } finally {
        if ($suspended) {
            [void][Diag18]::Suspend($target.Id, $true)
            Say ("  mstsc pid {0} resumed." -f $target.Id) 'Green'
            Start-Sleep -Milliseconds 400
            # A stream that died mid-probe would also read as "no movement", so prove it came back.
            Say ''
            Say '  PHASE D - confirm the stream still works: move the client mouse once more.' 'Yellow'
            Read-Host '  Press Enter, then move the client mouse'
            $after = Sample-Console 4 'D after resume'
            if ($after.Path -le $floor) {
                Say '  WARNING: no movement after the resume either. The stream may have dropped' 'Red'
                Say '  during the probe, which would make stage 4 unreadable. Re-run it.' 'Red'
                Rec 'stage4-validity=SUSPECT-STREAM-DIED'
            } else {
                Say '  stream confirmed alive after the resume, so stage 4 is readable.' 'Green'
                Rec 'stage4-validity=OK'
            }
        }
    }
} elseif ($leaked -and $SkipMechanism) {
    Head '-- stage 4 skipped by request --'
    Rec 'stage4=SKIPPED'
}

# ---------------------------------------------------------------- report
Head '== paste everything between the fences into issue #18 =='
Write-Host '```'
foreach ($l in $report) { Write-Host $l }
Write-Host '```'
Write-Host ''
