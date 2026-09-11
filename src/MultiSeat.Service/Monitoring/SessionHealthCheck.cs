using System.Diagnostics;
using MultiSeat.Service.Api;
using MultiSeat.Service.Sessions;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Monitoring;

/// <summary>
/// Periodic liveness probe for all active seats.
///
/// Checks that the Windows session, Apollo process, and virtual display
/// are all still alive. Degraded seats trigger auto-recovery:
///   - Apollo crash → auto-restart (up to ApolloManager.MaxRestartAttempts)
///   - Session death → seat enters Error state (requires manual teardown)
///
/// Runs on the MultiSeatWorker's health check timer (default: every 5 seconds).
/// State changes are broadcast to connected WebSocket clients.
/// </summary>
public sealed class SessionHealthCheck
{
    private readonly ILogger<SessionHealthCheck> _logger;
    private readonly SessionLauncher _sessionLauncher;
    private readonly ApolloManager _apolloManager;
    private readonly SeatManager _seatManager;
    private readonly OnConnectAppLauncher _onConnectApps;
    private readonly ClientResolutionFollower _resolutionFollower;
    private readonly SeatLifecycleGate _lifecycleGate;

    public SessionHealthCheck(
        ILogger<SessionHealthCheck> logger,
        SessionLauncher sessionLauncher,
        ApolloManager apolloManager,
        SeatManager seatManager,
        OnConnectAppLauncher onConnectApps,
        ClientResolutionFollower resolutionFollower,
        SeatLifecycleGate lifecycleGate)
    {
        _logger = logger;
        _sessionLauncher = sessionLauncher;
        _apolloManager = apolloManager;
        _seatManager = seatManager;
        _onConnectApps = onConnectApps;
        _resolutionFollower = resolutionFollower;
        _lifecycleGate = lifecycleGate;
    }

    /// <summary>
    /// Run health checks on all active seats.
    /// Returns a list of seats that changed state (for broadcasting).
    /// </summary>
    public async Task<IReadOnlyList<SeatInfo>> CheckAllSeatsAsync(
        SeatManager seatManager, CancellationToken ct)
    {
        var changedSeats = new List<SeatInfo>();

        foreach (var seat in seatManager.GetAllSeats())
        {
            if (!IsWorthChecking(seat.Status)) continue;

            var changed = await CheckSeatAsync(seat, ct);
            if (changed)
                changedSeats.Add(seat);
        }

        // Broadcast state changes to WebSocket clients
        foreach (var seat in changedSeats)
        {
            try
            {
                await WebSocketHub.BroadcastSeatUpdateAsync(seat);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast state change for seat {Id}", seat.Id);
            }
        }

        return changedSeats;
    }

    /// <summary>
    /// Check a single seat's health. Returns true if state changed.
    /// </summary>
    /// <summary>
    /// Which seats this check looks at.
    ///
    /// A seat mid-provision or mid-teardown is expected to be in flux, so watching it would fight
    /// whatever is moving it. Error is skipped for a blunter reason: a seat parked there has no
    /// live session to check.
    ///
    /// ⚠️ That last one has a consequence worth stating, because it caused a real bug (PR #22):
    /// nothing here ever takes a seat OUT of Error, and the Apollo-restart check below only runs
    /// for a seat this method admits. So a seat that lands in Error stays broken until something
    /// outside this class hands it back - which is what POST /api/seats/{id}/session-reconnect now
    /// does. Widening this set is not the fix; it would have the check fighting a teardown.
    /// </summary>
    internal static bool IsWorthChecking(SeatStatus status) =>
        status is not (SeatStatus.Idle or SeatStatus.Provisioning
                       or SeatStatus.TearingDown or SeatStatus.Error);

    private async Task<bool> CheckSeatAsync(SeatInfo seat, CancellationToken ct)
    {
        // ── Check 1: Is the Windows session still alive? ──────────
        var sessionAlive = _sessionLauncher.IsSessionAlive(seat.SessionId);

        if (!sessionAlive)
        {
            _logger.LogWarning(
                "Seat {Id}: Windows session {Sid} no longer active",
                seat.Id, seat.SessionId);
            // Release the seat's mstsc on the way to Error. Nothing else will: teardown is
            // what normally calls DisconnectSession, and a seat parked in Error may never be
            // torn down — leaving a hidden mstsc alive for the rest of the host's uptime.
            try { _sessionLauncher.DisconnectSession(seat.SessionId); } catch { /* best effort */ }
            seat.TransitionTo(SeatStatus.Error, _logger);
            seat.ErrorMessage = "Windows session terminated unexpectedly";
            return true;
        }

        // ── Check 1b: Is the session Active (not just Disconnected)? ──
        // Sessions go Disconnected when the PC sleeps (mstsc drops). A Disconnected
        // session breaks QueryDisplayConfig / DXGI, so Apollo cannot stream.
        // Reconnect via mstsc to restore Active state, then restart Apollo.
        if (!_sessionLauncher.IsSessionActive(seat.SessionId))
        {
            _logger.LogWarning(
                "Seat {Id}: session {Sid} is Disconnected (PC may have slept) — reconnecting",
                seat.Id, seat.SessionId);

            // Show the repair while it is happening. Recovery takes 15-30s (relaunch, up to 10s
            // waiting for ACTIVE, a 2s settle, an Apollo restart, display isolation) and the seat
            // used to read Ready/Streaming throughout - claiming health at the one moment it is
            // least true. Restored to whatever it was on success; Error on any failure below.
            var previousStatus = seat.Status;
            seat.TransitionTo(SeatStatus.Connecting, _logger);

            // Broadcast here rather than leaving it to CheckAllSeatsAsync: that only publishes
            // after CheckSeatAsync returns, by which point recovery has finished and Connecting
            // would never be seen by a client.
            try { await WebSocketHub.BroadcastSeatUpdateAsync(seat); }
            catch (Exception ex) { _logger.LogDebug(ex, "Seat {Id}: could not broadcast Connecting", seat.Id); }

            try
            {
                // This branch rewrites SessionId and restarts Apollo, so it needs the same gate
                // as every other lifecycle mutation — otherwise a resolution change or manual
                // reconnect arriving mid-recovery interleaves with it and strands the old
                // session's mstsc. Nothing it calls below takes the gate, so there is no
                // reentrancy: the semaphore is not recursive and a nested acquire would
                // deadlock the seat until the 30s timeout.
                using var lease = await _lifecycleGate.AcquireAsync(seat.Id, ct);

                // Kill the existing Apollo first — it survived sleep but with a broken
                // display pipeline (DXGI/QueryDisplayConfig fail on Disconnected sessions).
                // Without this, RestartAsync launches a second Apollo alongside the first,
                // causing a port conflict. KillForReconnect also resets RestartCount so
                // sleep cycles don't exhaust the crash-restart limit.
                _apolloManager.KillForReconnect(seat);

                // Pass the geometry: if the stale session has to be logged off and recreated,
                // the replacement must come back at the seat's own size rather than inheriting
                // the console desktop's.
                //
                // Keep the id it answers with: that path returns a NEW session, and the
                // Apollo restart and display isolation just below both act on SessionId.
                seat.SessionId = await _sessionLauncher.LaunchSessionAsync(
                    seat.AccountName, ct, RdpGeometry.ForClient(seat.Width, seat.Height));

                // Do not start Apollo against a session that is not ACTIVE yet. Apollo calls
                // QueryDisplayConfig at startup, and a Disconnected session answers
                // ERROR_ACCESS_DENIED — so it comes up without a display, dies, and the
                // health check restarts it into the same state. A fixed delay is not enough
                // on its own: it is a guess about how long the session takes, and losing that
                // race produces exactly this loop.
                if (!await WaitForSessionActiveAsync(
                        id => _sessionLauncher.IsSessionActive(id), seat.SessionId, ct))
                {
                    _logger.LogWarning(
                        "Seat {Id}: session {Sid} did not become ACTIVE within 10s after reconnect — aborting",
                        seat.Id, seat.SessionId);
                    try { _sessionLauncher.DisconnectSession(seat.SessionId); } catch { /* best effort */ }
                    seat.TransitionTo(SeatStatus.Error, _logger);
                    seat.ErrorMessage = "RDP session did not become active after reconnect";
                    return true;
                }

                // Give the display pipeline a moment to reinitialize after the session
                // transitions back to Active — SudoVDA and DXGI need a beat to be ready.
                await Task.Delay(2000, ct);

                _logger.LogInformation(
                    "Seat {Id}: session reconnected — restarting Apollo",
                    seat.Id);
                var newPid = await _apolloManager.RestartAsync(seat, ct);
                if (newPid > 0)
                {
                    seat.ApolloProcessId = newPid;
                    _logger.LogInformation(
                        "Seat {Id}: Apollo restarted after reconnect (PID {Pid})",
                        seat.Id, newPid);

                    // The session disconnect/reconnect wiped display-isolation state
                    // (SudoVDA is no longer primary; the RDP adapter has come back at
                    // its 1024×768 wake default). Without this, Apollo's mode change
                    // ends up on the wrong display and the stream stays at 1024×768.
                    await _seatManager.ApplyDisplayIsolationAsync(seat, ct);

                    // Back to whatever it was before the sleep - a Streaming seat returns to
                    // Streaming, not to Ready.
                    seat.TransitionTo(previousStatus, _logger);
                    return true;
                }

                // Apollo did not come back. Deliberately NOT an Error: Check 2 below picks this
                // up on the next tick (seat.ApolloProcessId still holds the now-dead pid, and
                // KillForReconnect reset RestartCount) and retries up to MaxRestartAttempts
                // before giving up. Erroring here would spend that budget on one bad attempt,
                // which after a wake - devices still settling - is the wrong call. What was
                // missing is that the failure was completely silent.
                _logger.LogWarning(
                    "Seat {Id}: Apollo did not restart after reconnect (pid {Pid}) — leaving it to "
                    + "the crash check, which retries up to {Max} times",
                    seat.Id, newPid, Streaming.ApolloManager.MaxRestartAttempts);
                seat.TransitionTo(previousStatus, _logger);
            }
            catch (OperationCanceledException)
            {
                // Shutdown. Put the status back rather than leaving the seat stuck in Connecting
                // for whatever a future run makes of it.
                _logger.LogInformation("Seat {Id}: session reconnect canceled", seat.Id);
                seat.TransitionTo(previousStatus, _logger);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Seat {Id}: failed to reconnect session after sleep", seat.Id);
                seat.TransitionTo(SeatStatus.Error, _logger);
                seat.ErrorMessage = "Session reconnect failed: " + ex.Message;
            }
            return true;
        }

        // ── Check 2: Is Apollo still running? ─────────────────────
        var apolloAlive = IsProcessAlive(seat.ApolloProcessId);

        if (!apolloAlive && seat.ApolloProcessId > 0 &&
            seat.Status is SeatStatus.Ready or SeatStatus.Streaming)
        {
            _logger.LogWarning(
                "Seat {Id}: Apollo (PID {Pid}) crashed — attempting restart",
                seat.Id, seat.ApolloProcessId);

            WarnIfApolloDiedOnStartup(seat);

            // Restarting mutates ApolloProcessId and the ApolloManager instance record, so it
            // must not interleave with a manual restart or a resolution change for the same
            // seat. The dead-session branch above stays outside the gate deliberately: it only
            // writes Status and never touches lifecycle state.
            using var lease = await _lifecycleGate.AcquireAsync(seat.Id, ct);

            // Try auto-restart
            var newPid = await _apolloManager.RestartAsync(seat, ct);

            if (newPid > 0)
            {
                seat.ApolloProcessId = newPid;
                _logger.LogInformation(
                    "Seat {Id}: Apollo restarted successfully (PID {Pid})",
                    seat.Id, newPid);

                // Apollo restart re-creates the SudoVDA monitor — the in-session
                // display-isolation state (SudoVDA-as-primary, RDP shrunk to 640×480)
                // doesn't survive that, so reapply it.
                await _seatManager.ApplyDisplayIsolationAsync(seat, ct);
                return true; // state metadata changed (PID)
            }
            else
            {
                // Restart failed — give up, and release the session's mstsc with it.
                WarnIfApolloDiedOnStartup(seat);
                try { _sessionLauncher.DisconnectSession(seat.SessionId); } catch { /* best effort */ }
                seat.TransitionTo(SeatStatus.Error, _logger);
                seat.ErrorMessage = "Apollo streaming server crashed and could not be restarted";
                return true;
            }
        }

        // ── Check 3: Is a launched app still running? ─────────────
        // If a game was launched and has exited, transition back to Ready
        if (seat.Status == SeatStatus.Streaming && !string.IsNullOrEmpty(seat.LaunchApp))
        {
            // We don't track the game PID separately (it could spawn children),
            // so we only flag if Apollo itself died (handled above).
            // In the future, we could track the game PID for auto-restart.
        }

        // ── Client connect/disconnect: tail Apollo's log for the edges ──────────
        // Moves the seat between Ready and Streaming, and launches (or kills) the configured
        // per-seat apps. Cheap: reads only the bytes appended since the previous tick.
        //
        // ⚠️ This DOES change seat state now. It used to be skipped entirely unless
        // MultiSeat:LaunchOnConnect was configured, which is empty by default — so on a normal
        // host nothing ever noticed a client connecting and a streaming seat reported Ready
        // forever (#43). The app launching is still gated; the observation is not.
        if (_onConnectApps.ProcessSeat(seat, ct))
            return true; // Ready <-> Streaming changed — worth broadcasting

        // ── Follow the client's requested resolution ──────────────
        // Apollo cannot apply it itself inside an RDP seat, so resize by reconnecting the
        // session. No-op unless MultiSeat:FollowClientResolution is on.
        if (await _resolutionFollower.ProcessSeatAsync(seat, ct))
            return true; // seat geometry changed — worth broadcasting

        // ── Late SudoVDA detection ────────────────────────────────
        // Apollo creates the seat's virtual display when a client connects, not at startup,
        // so provisioning's one-shot lookup always misses it and display isolation gets
        // skipped for the seat's whole life. Retry here until it appears. No-op once
        // DisplayDevicePath is set, and silent while there is still nothing to find.
        if (string.IsNullOrEmpty(seat.DisplayDevicePath))
        {
            try
            {
                if (await _seatManager.TryLateDisplayDetectionAsync(seat, ct))
                    return true; // DisplayDevicePath changed — worth broadcasting
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Seat {Id}: late display detection failed", seat.Id);
            }
        }

        return false; // no state change
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Point at the log setting that would explain an Apollo which died during startup.
    ///
    /// An Apollo that exits seconds after launch did not crash mid-stream; it failed to
    /// initialise, and a video encoder that will not open is the usual cause. That reason comes
    /// from FFmpeg — h264_amf, the QSV encoders and the software encoders are all FFmpeg encoders
    /// — and Apollo sets FFmpeg to AV_LOG_QUIET unless its level is exactly `verbose`. Our default
    /// is `info`, so the seat log shows "Creating encoder [...]" and then nothing at all.
    ///
    /// ⚠️ `debug` does NOT lift it. Apollo's test is `min_log_level >= 1`, and debug is 1.
    ///
    /// Without this hint the failure is unexplained and the log looks truncated rather than
    /// deliberately silenced (GitHub issue #24).
    /// </summary>
    private void WarnIfApolloDiedOnStartup(SeatInfo seat)
    {
        var uptime = _apolloManager.GetUptime(seat.Id);
        if (!IsStartupFailure(uptime)) return;

        _logger.LogWarning(
            "Seat {Id}: Apollo exited {Seconds:F1}s after starting, so it failed to initialise "
            + "rather than crashing. A video encoder that will not open is the usual cause, and "
            + "Apollo discards the FFmpeg error that would say why unless its log level is "
            + "verbose. Set MultiSeat:ApolloLogLevel to \"verbose\" in appsettings.local.json "
            + "(\"debug\" is NOT enough), restart the service, and re-provision to see it.",
            seat.Id, uptime.Value.TotalSeconds);
    }

    /// <summary>
    /// How soon after launch an Apollo exit counts as "failed to start" rather than "crashed".
    /// Generous: a seat that streams for a minute and then dies is a different problem.
    /// </summary>
    internal static readonly TimeSpan ApolloStartupWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether an Apollo that has exited died during startup rather than mid-stream.
    /// </summary>
    /// <param name="uptime">
    /// How long it ran, or null when there is no instance record. Null must NOT count as a startup
    /// failure: "we never launched it" would otherwise read as "it died instantly" and every seat
    /// the manager has no record of would emit the hint.
    /// </param>
    internal static bool IsStartupFailure(TimeSpan? uptime) =>
        uptime is not null && uptime <= ApolloStartupWindow;

    /// <summary>
    /// Poll until the session reports Active, or the timeout expires.
    /// </summary>
    /// <remarks>
    /// Takes the probe as a delegate rather than calling SessionLauncher directly so the timing
    /// can be tested without a Windows session.
    /// </remarks>
    /// <returns>true if the session became Active within the timeout.</returns>
    internal static async Task<bool> WaitForSessionActiveAsync(
        Func<int, bool> isSessionActive,
        int sessionId,
        CancellationToken ct,
        int pollMs = 500,
        int timeoutMs = 10_000)
    {
        // Deviation from the ported original, which only observed cancellation through the
        // Task.Delay inside the loop: an already-cancelled token skipped the loop entirely and
        // returned a plain false, which the caller cannot tell from "the session never came up"
        // and so parks the seat in Error during an ordinary shutdown. Cancelling always throws
        // here, matching both Task.Delay below and LaunchSessionAsync in the same try block.
        ct.ThrowIfCancellationRequested();

        var waited = 0;
        while (waited < timeoutMs && !ct.IsCancellationRequested)
        {
            if (isSessionActive(sessionId))
                return true;
            await Task.Delay(pollMs, ct);
            waited += pollMs;
        }

        // One last look: the final sleep may have covered the transition.
        return isSessionActive(sessionId);
    }
}
