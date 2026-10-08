<#
.SYNOPSIS
    Issue #96: does a fresh seat session's input desktop ever become accessible on its own, and
    does that depend on KeepaliveOnSeparateDesktop (the issue #18 fix)?

.DESCRIPTION
    The reporter found that Apollo's first OpenInputDesktop/DuplicateOutput call can fail with
    ACCESS_DENIED on a freshly-created seat session, and that only a disconnect/reconnect clears
    it  -  with KeepaliveOnSeparateDesktop (issue #18's fix) on. Their own testing tried fixed delays
    up to 10s, all failed, and never tried the keepalive OFF on a fresh session as a control, so it
    is not yet known whether this is a slow race (would resolve on its own given longer) or a true
    deadlock (only an external reconnect clears it), nor whether it depends on the separate-desktop
    keepalive at all.

    This runs real provision/teardown cycles on THIS host, alternating
    MultiSeat:KeepaliveOnSeparateDesktop true/false, and for each one launches the
    --input-desktop-probe helper (MultiSeat:DiagnoseInputDesktopReadiness) inside the fresh seat
    session RIGHT AFTER it exists  -  in parallel with Apollo's own startup, not ahead of it, so the
    probe observes the real race instead of adding a delay in front of it. The probe samples
    OpenInputDesktop and the active RDP display identity every 250ms for the whole window and
    writes a timestamped JSONL timeline, which this script reads back and aggregates.

    WHAT THIS CANNOT RULE OUT ON ITS OWN
    SessionHealthCheck reconnects a session that goes Disconnected (RescueSessionUnderGateAsync)  - 
    a different trigger from issue #96's symptom (the reporter's session stays Active throughout).
    It should not fire here, but this script also samples the seat's own status every 2s during the
    wait, and that timeline is in the report, so a reader can check for a status change that might
    explain a resolution the probe recorded as "on its own."

    SAFETY
      - Refuses to run if a seat already exists for ANY account  -  same posture as smoke-seat.ps1.
      - Changes MultiSeat:KeepaliveOnSeparateDesktop and the two new diagnostic settings in
        appsettings.local.json, MERGING rather than replacing (anything else already in that file
        is preserved). Restores the file's ORIGINAL content when the script exits, including on
        Ctrl-C or an error, and restarts the service once more to put the setting back.
      - Every seat it creates, it tears down itself before moving to the next trial.
      - If -Account has a saved AutoStart preset, it is disabled for the run and restored at the
        end (also on Ctrl-C or an error) - otherwise the service restart inside every condition
        switch would silently reprovision a seat for it mid-run and break the next trial's own
        POST with "account already has a seat." Found by self-test, not by reasoning about it.

.PARAMETER Account
    The Windows account to provision each trial's seat under. It must already exist, and it is
    reused across every trial  -  issue #96 is about a fresh SESSION, not a fresh account.

.PARAMETER TrialsPerCondition
    How many fresh seats to provision under each value of KeepaliveOnSeparateDesktop. Default 5.
    The reporter's own PreApolloSeparateDesktopReconnect experiment showed non-deterministic
    results (session 15 worked, session 16 did not) from a single trial each  -  this is why a real
    sample size matters here rather than one run per condition.

.PARAMETER ProbeSeconds
    How long each trial's probe samples for. Default 90  -  well past the reporter's longest tried
    delay (10s), to tell a slow race apart from a true deadlock.

.EXAMPLE
    .\diagnose-issue96.ps1 -Account Gaming
    .\diagnose-issue96.ps1 -Account Gaming -TrialsPerCondition 8 -ProbeSeconds 120
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Account,
    [int]$TrialsPerCondition = 5,
    [int]$ProbeSeconds = 90,
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$Fps = 60
)

$ErrorActionPreference = 'Stop'

$exe = 'C:\Program Files\MultiSeat\MultiSeat.Service.exe'
$localConfigPath = 'C:\Program Files\MultiSeat\appsettings.local.json'

function Refuse {
    param([string]$Message)
    Write-Host ''
    Write-Host "REFUSED: $Message" -ForegroundColor Yellow
    Write-Host 'Nothing was provisioned and nothing was changed.' -ForegroundColor Yellow
    exit 2
}

# ---------------------------------------------------------------- stage 0
Write-Host ''
Write-Host '== issue #96 readiness diagnosis ==' -ForegroundColor Cyan
Write-Host '-- stage 0: deployed binary --' -ForegroundColor Cyan

if (-not (Test-Path $exe)) { Refuse "$exe does not exist - is the service installed?" }

$cfgRaw = & $exe --config 2>&1 | Out-String
if ($cfgRaw -notmatch 'effective MultiSeat settings') {
    Refuse ("the deployed binary does not understand --config, so it predates this diagnostic. " +
            "Run .\scripts\install-service.ps1 after pulling, then re-run this script.")
}
if ($cfgRaw -notmatch 'DiagnoseInputDesktopReadiness') {
    Refuse ("the deployed binary's --config output does not mention DiagnoseInputDesktopReadiness, " +
            "so it predates this diagnostic even though it understands --config. " +
            "Run .\scripts\install-service.ps1, then re-run this script.")
}
Write-Host '  deployed binary understands --config and DiagnoseInputDesktopReadiness.' -ForegroundColor Green

# ---------------------------------------------------------------- API
$keyPath = 'C:\ProgramData\MultiSeat\api-key.txt'
if (-not (Test-Path $keyPath)) { Refuse "no API key at $keyPath - is the service installed?" }
$Headers = @{ 'X-MultiSeat-Key' = (Get-Content $keyPath -Raw).Trim() }
$Api = 'http://127.0.0.1:9550/api'

function Get-Seats {
    # See smoke-seat.ps1's Get-Seats for why a naive @(...) count is unsafe on an empty host.
    @(Invoke-RestMethod "$Api/seats" -Headers $Headers -TimeoutSec 30 | Where-Object { $_.id })
}

$svc = Get-Service MultiSeatService -ErrorAction SilentlyContinue
if (-not $svc -or $svc.Status -ne 'Running') { Refuse 'MultiSeatService is not running.' }

try { $existing = Get-Seats } catch { Refuse "the API did not answer: $($_.Exception.Message)" }
if ($existing.Count -gt 0) {
    Refuse ("$($existing.Count) seat(s) already exist. This script provisions and tears down " +
            "repeatedly and refuses to run where that might disturb a seat somebody is using.")
}

try { $null = Get-LocalUser -Name $Account -ErrorAction Stop }
catch { Refuse "no local account named '$Account'. Create it first, or pass -Account with one that exists." }

# ---------------------------------------------------------------- autostart guard
# Found by self-test on 2026-10-06: every Set-DiagnosticConfig restart below recreates ANY seat
# with a saved AutoStart preset for -Account - and a seat account used for daily streaming (which
# this one almost certainly is) very likely has one. The preset survives a seat teardown, so the
# "no seat exists" check above does not catch it; it only fires on the NEXT service restart, which
# silently reprovisions a seat mid-run and makes every later trial's own POST fail with "account
# already has a seat". Clear it for the run and put it back exactly as found.
function Get-AccountAutoStart {
    @(Invoke-RestMethod "$Api/seats/presets" -Headers $Headers -TimeoutSec 30) |
        Where-Object { $_.accountName -eq $Account } |
        Select-Object -First 1 -ExpandProperty autoStart
}

function Set-AccountAutoStart {
    param([bool]$Enabled)
    # No REST endpoint sets this without a live seat, so provision a throwaway one just to flip
    # the preset, then tear it straight back down - only the preset is kept.
    $tmp = Invoke-RestMethod "$Api/seats" -Method Post -Headers $Headers -TimeoutSec 180 `
        -Body (@{ accountName = $Account; width = $Width; height = $Height; fps = $Fps } | ConvertTo-Json) `
        -ContentType 'application/json'
    Invoke-RestMethod "$Api/seats/$($tmp.id)/autostart" -Method Put -Headers $Headers -TimeoutSec 30 `
        -Body (@{ enabled = $Enabled } | ConvertTo-Json) -ContentType 'application/json' | Out-Null
    Invoke-RestMethod "$Api/seats/$($tmp.id)" -Method Delete -Headers $Headers -TimeoutSec 180 | Out-Null
    Start-Sleep -Seconds 5
}

$originalAutoStart = [bool](Get-AccountAutoStart)
if ($originalAutoStart) {
    Write-Host "  '$Account' has AutoStart enabled - disabling it for this run (restored at the end)." -ForegroundColor Yellow
    Set-AccountAutoStart -Enabled $false
}

# ---------------------------------------------------------------- config merge/restore
$originalConfigText = if (Test-Path $localConfigPath) { Get-Content $localConfigPath -Raw } else { $null }

function Set-DiagnosticConfig {
    param([bool]$KeepaliveOnSeparateDesktop)

    # -AsHashtable is PowerShell 6+ only, and this repo ships scripts for Windows PowerShell 5.1
    # too (lint-scripts.ps1 exists because of exactly this kind of version gap) - so this builds
    # and edits a plain PSCustomObject instead, which ConvertFrom-Json/ConvertTo-Json both support
    # on either engine.
    $cfg = if ($originalConfigText) {
        try { $originalConfigText | ConvertFrom-Json } catch { [PSCustomObject]@{} }
    } else { [PSCustomObject]@{} }
    if (-not $cfg.PSObject.Properties['MultiSeat']) {
        $cfg | Add-Member -NotePropertyName 'MultiSeat' -NotePropertyValue ([PSCustomObject]@{})
    }

    $cfg.MultiSeat | Add-Member -Force -NotePropertyName 'KeepaliveOnSeparateDesktop' -NotePropertyValue $KeepaliveOnSeparateDesktop
    $cfg.MultiSeat | Add-Member -Force -NotePropertyName 'DiagnoseInputDesktopReadiness' -NotePropertyValue $true
    $cfg.MultiSeat | Add-Member -Force -NotePropertyName 'InputDesktopReadinessProbeSeconds' -NotePropertyValue $ProbeSeconds

    ($cfg | ConvertTo-Json -Depth 10) | Set-Content $localConfigPath -Encoding utf8

    Restart-Service MultiSeatService
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service MultiSeatService).Status -ne 'Running' -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2   # let the API finish binding

    $check = & $exe --config 2>&1 | Out-String
    $resolved = $null
    if ($check -match 'KeepaliveOnSeparateDesktop\s*=\s*(\w+)') { $resolved = $matches[1] }
    if ($resolved -ne $KeepaliveOnSeparateDesktop.ToString()) {
        Refuse ("asked for KeepaliveOnSeparateDesktop=$KeepaliveOnSeparateDesktop but the deployed " +
                "binary resolved '$resolved' after restart. Aborting rather than run trials against " +
                "the wrong condition.")
    }

    $diagResolved = $null
    if ($check -match 'DiagnoseInputDesktopReadiness\s*=\s*(\w+)') { $diagResolved = $matches[1] }
    if ($diagResolved -ne 'True') {
        Refuse ("DiagnoseInputDesktopReadiness resolved to '$diagResolved' after restart, not True. " +
                "Every trial below would run with no probe at all, and silently report every seat " +
                "as NeverSucceeded with SampleCount 0. Check appsettings.local.json by hand.")
    }

    Write-Host "  confirmed: KeepaliveOnSeparateDesktop = $resolved, DiagnoseInputDesktopReadiness = $diagResolved" -ForegroundColor Green
}

function Restore-OriginalConfig {
    Write-Host ''
    Write-Host '-- restoring original configuration --' -ForegroundColor Cyan
    try {
        if ($originalConfigText) { $originalConfigText | Set-Content $localConfigPath -Encoding utf8 -NoNewline }
        else { Remove-Item $localConfigPath -ErrorAction SilentlyContinue }
        Restart-Service MultiSeatService -ErrorAction SilentlyContinue
        Write-Host '  restored and restarted.' -ForegroundColor Green
    } catch {
        Write-Host "  COULD NOT RESTORE appsettings.local.json automatically: $($_.Exception.Message)" -ForegroundColor Red
        if ($originalConfigText) {
            Write-Host '  original content was:' -ForegroundColor Yellow
            Write-Host $originalConfigText
        } else {
            Write-Host "  the file did not exist before this run - delete $localConfigPath by hand." -ForegroundColor Yellow
        }
    }
}

# ---------------------------------------------------------------- one trial
function Invoke-Trial {
    param([bool]$KeepaliveOnSeparateDesktop, [int]$TrialNumber)

    $statusTimeline = New-Object System.Collections.Generic.List[string]
    $trialStart = Get-Date
    $seat = $null
    try {
        $seat = Invoke-RestMethod "$Api/seats" -Method Post -Headers $Headers -TimeoutSec 180 `
            -Body (@{ accountName = $Account; width = $Width; height = $Height; fps = $Fps } | ConvertTo-Json) `
            -ContentType 'application/json'
    } catch {
        return [pscustomobject]@{
            Trial = $TrialNumber; Keepalive = $KeepaliveOnSeparateDesktop
            SeatId = $null; SessionId = $null; SeatStatus = 'ProvisionThrew'
            FirstSuccessMs = $null; NeverSucceeded = $true; SampleCount = 0
            DisplayAtStart = $null; DisplayAtSuccess = $null; DisplayChanged = $null
            StatusTimeline = $_.Exception.Message
        }
    }

    $seatId = $seat.id
    $probeFile = Join-Path 'C:\ProgramData\MultiSeat' "ms_inputdesktop_readiness_$($seatId -replace '-','').jsonl"

    # Poll both the seat's own status and the probe file growing, for the probe's whole window
    # plus a buffer  -  the probe keeps sampling regardless of what the seat's status does, so this
    # is strictly a wait, not a wait-for-Ready (reaching Error IS a valid, expected outcome here).
    $deadline = (Get-Date).AddSeconds($ProbeSeconds + 30)
    $lastStatus = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $live = Get-Seats | Where-Object { $_.id -eq $seatId }
            $nowStatus = if ($live) { $live.status } else { 'Gone' }
        } catch { $nowStatus = 'ApiError' }
        if ($nowStatus -ne $lastStatus) {
            $elapsedSeconds = [int]((Get-Date) - $trialStart).TotalSeconds
            $statusTimeline.Add("${elapsedSeconds}s:$nowStatus")
            $lastStatus = $nowStatus
        }

        if ((Test-Path $probeFile) -and ((Get-Content $probeFile -Raw) -match '"Kind":"summary"')) { break }
        Start-Sleep -Seconds 2
    }

    $samples = @()
    $switchEvents = @()
    $hookLine = $null
    $summaryLine = $null
    if (Test-Path $probeFile) {
        foreach ($line in Get-Content $probeFile) {
            if ($line -match '"Kind":"summary"') { $summaryLine = $line; continue }
            if (-not $line.Trim()) { continue }
            # The probe also writes one "hook" line (did the desktop-switch hook install) and one
            # "desktop-switch" line per EVENT_SYSTEM_DESKTOPSWITCH; neither is a sample.
            if ($line -match '"Kind":"hook"') { $hookLine = $line | ConvertFrom-Json; continue }
            if ($line -match '"Kind":"desktop-switch"') { $switchEvents += ($line | ConvertFrom-Json); continue }
            $samples += ($line | ConvertFrom-Json)
        }
    }

    $firstSuccess = $samples | Where-Object { $_.OpenInputDesktopSucceeded } | Select-Object -First 1
    $displayKey = { param($s) ($s.ActiveDisplays | ForEach-Object { "$($_.GdiName)/$($_.AdapterLow)/$($_.AdapterHigh)/$($_.TargetId)" }) -join ',' }
    $startDisplay = if ($samples.Count -gt 0) { & $displayKey $samples[0] } else { $null }
    $successDisplay = if ($firstSuccess) { & $displayKey $firstSuccess } else { $null }

    $result = [pscustomobject]@{
        Trial            = $TrialNumber
        Keepalive        = $KeepaliveOnSeparateDesktop
        SeatId           = $seatId
        SessionId        = $seat.sessionId
        SeatStatus       = $lastStatus
        FirstSuccessMs   = if ($firstSuccess) { [math]::Round($firstSuccess.ElapsedMs) } else { $null }
        NeverSucceeded   = ($null -eq $firstSuccess)
        SampleCount      = $samples.Count
        HookInstalled    = if ($hookLine) { $hookLine.Installed } else { $null }
        SwitchEvents     = $switchEvents.Count
        DisplayAtStart   = $startDisplay
        DisplayAtSuccess = $successDisplay
        DisplayChanged   = if ($firstSuccess) { $startDisplay -ne $successDisplay } else { $null }
        StatusTimeline   = ($statusTimeline -join ' -> ')
    }

    try {
        Invoke-RestMethod "$Api/seats/$seatId" -Method Delete -Headers $Headers -TimeoutSec 180 | Out-Null
        Start-Sleep -Seconds 5
    } catch {
        Write-Host "  WARNING: teardown of seat $seatId failed: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host '  check the dashboard before the next trial.' -ForegroundColor Red
    }
    try { Remove-Item $probeFile -ErrorAction SilentlyContinue } catch {}

    return $result
}

# ---------------------------------------------------------------- run both conditions
$allResults = @()
try {
    foreach ($keepalive in @($true, $false)) {
        Write-Host ''
        Write-Host "-- condition: KeepaliveOnSeparateDesktop = $keepalive --" -ForegroundColor Cyan
        Set-DiagnosticConfig -KeepaliveOnSeparateDesktop $keepalive

        for ($t = 1; $t -le $TrialsPerCondition; $t++) {
            Write-Host "  trial $t/$TrialsPerCondition ..." -NoNewline
            $r = Invoke-Trial -KeepaliveOnSeparateDesktop $keepalive -TrialNumber $t
            $allResults += $r
            $verdict = if ($r.NeverSucceeded) { "NEVER (status=$($r.SeatStatus))" } else { "$($r.FirstSuccessMs) ms" }
            Write-Host " $verdict" -ForegroundColor $(if ($r.NeverSucceeded) { 'Red' } else { 'Green' })
        }
    }
}
finally {
    Restore-OriginalConfig
    if ($originalAutoStart) {
        Write-Host ''
        Write-Host '-- restoring AutoStart --' -ForegroundColor Cyan
        try {
            Set-AccountAutoStart -Enabled $true
            Write-Host "  '$Account' AutoStart restored." -ForegroundColor Green
        } catch {
            Write-Host "  COULD NOT RESTORE AutoStart for '$Account': $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "  It started this run ENABLED - re-enable it by hand from the dashboard." -ForegroundColor Yellow
        }
    }
}

# ---------------------------------------------------------------- report
Write-Host ''
Write-Host '== per-trial results ==' -ForegroundColor Cyan
$allResults | Format-Table Keepalive, Trial, SeatStatus, FirstSuccessMs, NeverSucceeded, DisplayChanged, SampleCount, HookInstalled, SwitchEvents -AutoSize

Write-Host ''
Write-Host '== aggregate, per condition ==' -ForegroundColor Cyan
foreach ($keepalive in @($true, $false)) {
    $group = $allResults | Where-Object { $_.Keepalive -eq $keepalive }
    $succeeded = @($group | Where-Object { -not $_.NeverSucceeded })
    $never = @($group | Where-Object { $_.NeverSucceeded })
    $changed = @($succeeded | Where-Object { $_.DisplayChanged })

    Write-Host ("KeepaliveOnSeparateDesktop = {0}: {1} trials, {2} never succeeded within {3}s, {4} succeeded" -f `
        $keepalive, $group.Count, $never.Count, $ProbeSeconds, $succeeded.Count)
    if ($succeeded.Count -gt 0) {
        $times = $succeeded.FirstSuccessMs | Sort-Object
        $median = $times[[math]::Floor(($times.Count - 1) / 2)]
        Write-Host ("    time to first success (ms): min={0} median={1} max={2}" -f $times[0], $median, $times[-1])
        Write-Host ("    display identity changed between start and first success: {0}/{1}" -f $changed.Count, $succeeded.Count)
    }
}

Write-Host ''
Write-Host '== paste everything below into issue #96 ==' -ForegroundColor Cyan
Write-Host '```'
$allResults | Format-Table Keepalive, Trial, SeatStatus, FirstSuccessMs, NeverSucceeded, DisplayChanged, SampleCount, StatusTimeline -AutoSize | Out-String -Width 200
Write-Host '```'
