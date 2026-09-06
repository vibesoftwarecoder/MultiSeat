using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Accounts;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Interop;

namespace MultiSeat.Service.Sessions;

/// <summary>
/// Launches background Windows sessions for seat accounts.
///
/// Strategy:
///   Creates sessions via RDP loopback — connecting to the local RDP
///   listener (127.0.0.2) triggers termsrv.dll to allocate a real
///   interactive session. This is the only reliable way to create new
///   sessions from Session 0 (Windows Service context).
///
///   CreateProcessAsUser alone does NOT create new sessions from
///   Session 0 — it always launches processes in Session 0 itself.
///   The RDP protocol is required to trigger session creation.
///
///   On Windows 11 24H2 (build 26100+), termsrv.dll must be patched via
///   RDP Wrapper for concurrent sessions. Without it, the existing console
///   session's user gets disconnected.
///
/// Flow:
///   1. Store seat account credentials in the console user's credential store
///   2. Launch mstsc.exe /v:127.0.0.2 in the console session
///   3. RDP connection triggers termsrv to create a new session
///   4. Poll WTS until the account's session appears
///   5. Disconnect the mstsc client — session stays alive as "Disconnected"
///   6. Clean up stored credentials
///   7. Launch the session anchor process in the new session to hold it open
/// </summary>
public sealed class SessionLauncher
{
    private readonly ILogger<SessionLauncher> _logger;
    private readonly MultiSeatOptions _options;
    private readonly AccountManager _accounts;

    // Track session-anchor process handles per session to prevent premature cleanup.
    // Concurrent because provisioning, teardown, and the health-check loop can all
    // touch it from different threads.
    private readonly ConcurrentDictionary<int, SessionAnchorInfo> _sessionAnchors = new();

    // Track the mstsc process that is keeping a session Active.
    // DisconnectSession() kills mstsc, sending the session back to Disconnected.
    private readonly ConcurrentDictionary<int, Process?> _pendingMstsc = new();

    /// <summary>
    /// Serializes "write Default.rdp, then launch mstsc against it".
    ///
    /// There is exactly one Default.rdp and it must stay that way — an explicit .rdp argument
    /// triggers the unsuppressible security warning (see <see cref="RdpFileBuilder"/>) — but its
    /// contents are now per-seat, since it carries that seat's geometry. Two seats provisioning
    /// at once could otherwise interleave write/launch and hand a seat the other's resolution,
    /// which would look exactly like the bug this geometry work exists to fix.
    ///
    /// Held until the session exists, i.e. until mstsc has certainly read the file. Seat
    /// provisioning is sequential today, so in practice this costs nothing.
    /// </summary>
    private readonly SemaphoreSlim _rdpFileGate = new(1, 1);

    /// <summary>
    /// Writes the transient RDP credential mstsc authenticates with. Was cmdkey.exe, which put the
    /// seat password on a command line; see RdpCredentialStore.
    /// </summary>
    private readonly RdpCredentialStore _credentials;

    // RDP loopback address — use 127.0.0.2 to avoid conflicts with 127.0.0.1/localhost
    private const string RdpLoopbackAddress = "127.0.0.2";

    public SessionLauncher(
        ILogger<SessionLauncher> logger,
        IOptions<MultiSeatOptions> options,
        AccountManager accounts)
    {
        _logger = logger;
        _options = options.Value;
        _accounts = accounts;
        _credentials = new RdpCredentialStore(logger);
    }

    /// <summary>
    /// Launch a new background interactive session for the given account.
    /// Returns the Windows Session ID.
    /// </summary>
    /// <summary>
    /// Launch (or reconnect) a background interactive session for the given account
    /// and leave it in ACTIVE state so that processes launched into it have full
    /// display-pipeline access (QueryDisplayConfig, DXGI, SudoVDA IPC).
    ///
    /// The caller MUST call <see cref="DisconnectSession"/> once the launched
    /// processes have finished initializing. Until then an mstsc window is
    /// minimized on the console desktop.
    /// </summary>
    /// <param name="geometry">
    /// Desktop size for the session, or null to let mstsc choose (which means inheriting the
    /// console desktop's size). Only honoured when a session is CREATED — reconnecting to an
    /// existing session cannot resize it, so a caller changing resolution must log the session
    /// off first. See <see cref="RdpGeometry"/>.
    /// </param>
    public async Task<int> LaunchSessionAsync(
        string accountName, CancellationToken ct, RdpGeometry? geometry = null)
    {
        var password = _accounts.GetCredential(accountName)
            ?? throw new InvalidOperationException(
                $"No stored credential for '{accountName}'. " +
                "Account was likely discovered at startup without a password. " +
                "Delete and recreate the account, or update the credential.");

        // Check if this user already has a session (Active or Disconnected)
        var existingSessionId = FindExistingSession(accountName);
        if (existingSessionId >= 0)
        {
            // Never reuse the physical console session — that's the main user's active
            // desktop. We always need a dedicated RDP loopback session so Apollo runs
            // isolated in the seat account's session, not the console session.
            // This happens when the seat account is the same as the logged-in console
            // user (e.g. the same account is used for both the desktop login and a seat).
            var consoleSessionId = (int)Kernel32.WTSGetActiveConsoleSessionId();
            if (existingSessionId == consoleSessionId)
            {
                _logger.LogInformation(
                    "Account {Account} is logged into the console session ({Sid}) — " +
                    "ignoring it and creating a dedicated RDP loopback session instead",
                    accountName, existingSessionId);
                // Fall through to CreateSessionViaRdpLoopbackAsync
            }
            else
            {
                var state = FindSessionState(existingSessionId);
                if (state == WtsApi.WtsConnectState.Active)
                {
                    // Already active — nothing to do (mstsc may already be tracked)
                    _logger.LogInformation(
                        "Account {Account} already has ACTIVE session {Sid}",
                        accountName, existingSessionId);
                    return existingSessionId;
                }

                // Session exists but is Disconnected — reconnect via mstsc so it
                // becomes Active and display APIs are available.
                _logger.LogInformation(
                    "Account {Account} has DISCONNECTED session {Sid} — reconnecting via mstsc",
                    accountName, existingSessionId);

                await ReconnectSessionAsync(existingSessionId, accountName, password, geometry, ct);

                // ReconnectSessionAsync logs a warning but doesn't throw when the session
                // stays Disconnected (e.g. on cold boot where SYSTEM running mstsc can't
                // bring the old session to Active). If it's still not Active, log off the
                // stale session and create a fresh one so Apollo gets a live display pipeline.
                if (FindSessionState(existingSessionId) != WtsApi.WtsConnectState.Active)
                {
                    _logger.LogWarning(
                        "Session {Sid} still Disconnected after reconnect — " +
                        "logging off stale session and creating a fresh one",
                        existingSessionId);

                    // Kill the mstsc started in ReconnectSessionAsync, then log off
                    DisconnectSession(existingSessionId);
                    WtsApi.WTSLogoffSession(WtsApi.WTS_CURRENT_SERVER_HANDLE, existingSessionId, bWait: false);

                    // Wait for the session to disappear so WaitForNewSessionAsync
                    // (inside CreateSessionViaRdpLoopbackAsync) finds the new session,
                    // not the stale one that's still being torn down.
                    var logoffWait = Stopwatch.StartNew();
                    while (logoffWait.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (FindExistingSession(accountName) < 0) break;
                        await Task.Delay(500, ct);
                    }

                    _logger.LogInformation(
                        "Stale session {Sid} gone — proceeding to create fresh session for {Account}",
                        existingSessionId, accountName);
                    // Fall through to CreateSessionViaRdpLoopbackAsync below
                }
                else
                {
                    return existingSessionId;
                }
            }
        }

        // No session yet (or stale session was logged off) — create one via RDP loopback.
        _logger.LogInformation("Creating background session for {Account}", accountName);

        var sessionId = await CreateSessionViaRdpLoopbackAsync(accountName, password, geometry, ct);

        if (sessionId < 0)
            throw new InvalidOperationException(
                $"Failed to create session for account '{accountName}'. " +
                "Ensure RDP Wrapper is installed and the termsrv.dll patch is active.");

        return sessionId;
    }

    /// <summary>
    /// Disconnect the session for the given session ID by killing the tracked
    /// mstsc process. The session goes from Active → Disconnected; processes
    /// already running inside it (Apollo, the session anchor) continue unaffected.
    /// Safe to call multiple times or when no mstsc is tracked.
    /// </summary>
    public void DisconnectSession(int sessionId)
    {
        if (_pendingMstsc.TryRemove(sessionId, out var mstsc))
        {
            _logger.LogInformation("Disconnecting session {Sid} (killing mstsc)", sessionId);
            KillMstsc(mstsc);
        }
    }

    /// <summary>
    /// Enumerate active WTS sessions and find one belonging to the given username.
    /// </summary>
    public int FindExistingSession(string accountName)
    {
        if (!WtsApi.WTSEnumerateSessionsW(
                WtsApi.WTS_CURRENT_SERVER_HANDLE, 0, 1,
                out var pSessionInfo, out var count))
        {
            _logger.LogWarning("WTSEnumerateSessions failed: {Err}", Marshal.GetLastWin32Error());
            return -1;
        }

        try
        {
            var structSize = Marshal.SizeOf<WtsApi.WtsSessionInfo>();
            for (int i = 0; i < count; i++)
            {
                var current = Marshal.PtrToStructure<WtsApi.WtsSessionInfo>(
                    pSessionInfo + i * structSize);

                if (current.State is not WtsApi.WtsConnectState.Active
                    and not WtsApi.WtsConnectState.Disconnected)
                    continue;

                if (WtsApi.WTSQuerySessionInformationW(
                        WtsApi.WTS_CURRENT_SERVER_HANDLE,
                        current.SessionId,
                        WtsApi.WtsInfoClass.WTSUserName,
                        out var pUser, out _))
                {
                    var user = Marshal.PtrToStringUni(pUser);
                    WtsApi.WTSFreeMemory(pUser);

                    if (string.Equals(user, accountName, StringComparison.OrdinalIgnoreCase))
                        return current.SessionId;
                }
            }
        }
        finally
        {
            WtsApi.WTSFreeMemory(pSessionInfo);
        }

        return -1;
    }

    /// <summary>
    /// Get a usable primary token for the given session ID.
    /// Tries WTSQueryUserToken first (requires SYSTEM), then falls back
    /// to LogonUser with stored credentials.
    /// </summary>
    public SafeTokenHandle GetSessionToken(int sessionId, string accountName)
    {
        // Path 1: WTSQueryUserToken — fastest, uses existing session token.
        // For admin users, WTSQueryUserToken returns a FILTERED (medium-integrity) token, so we
        // ask for the linked elevated one; for standard users there is no linked token and the
        // filtered token is used as-is. Both paths work.
        //
        // This used to say the elevated token was REQUIRED for SudoVDA IPC, and that claim is what
        // justified making every seat account a local administrator. It is wrong. Apollo's SudoVDA
        // client opens the device with a plain CreateFileA for GENERIC_READ | GENERIC_WRITE and
        // checks nothing, and the driver's INF ships the SDDL
        // D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;WD) — which grants Everyone exactly that access.
        // Verified rather than reasoned: an account in Users only opened the interface with last
        // error 0. Seats are no longer administrators; see MultiSeatOptions.GrantSeatAdministrator.
        if (WtsApi.WTSQueryUserToken((uint)sessionId, out var rawToken))
        {
            _logger.LogDebug("Got session token via WTSQueryUserToken for session {Sid}", sessionId);

            // ⚠️ WTSQueryUserToken takes a SESSION, not an account. It returns whoever occupies that
            // session, and nothing downstream notices: the token's session id equals the one we
            // asked for, so the session check in ProcessInjector passes and the process launches
            // happily as the wrong user in the wrong session.
            //
            // That is not hypothetical. If sessionId is ever the console session, this hands back
            // the CONSOLE user's token, and a seat's Apollo then runs in the console session with
            // the seat's config, ports and log file — looking healthy from every angle while its
            // SendInput lands on the console desktop. Which is exactly what issue #18 reports.
            //
            // So verify the token belongs to the account we were asked for, and refuse if not.
            // Guessing here is worse than failing: the caller can retry, a mis-targeted seat cannot.
            EnsureTokenBelongsTo(rawToken, accountName, sessionId);

            // Duplicate to a primary token first
            if (AdvApi.DuplicateTokenEx(
                    rawToken,
                    AdvApi.MAXIMUM_ALLOWED,
                    IntPtr.Zero,
                    AdvApi.SecurityImpersonationLevel.SecurityImpersonation,
                    AdvApi.TokenType.TokenPrimary,
                    out var dupToken))
            {
                Kernel32.CloseHandle(rawToken);
                using var filteredToken = new SafeTokenHandle(dupToken);

                // Try to get the elevated linked token (for admin accounts). Harmless when there
                // isn't one — a seat account is a standard user now and takes the path below.
                var elevatedToken = TryGetLinkedElevatedToken(filteredToken);
                if (elevatedToken != null)
                {
                    _logger.LogDebug(
                        "Session {Sid}: using elevated linked token for SudoVDA IPC access",
                        sessionId);
                    return elevatedToken;
                }

                // Non-admin user or no linked token — use filtered token as-is.
                // We need to return a new handle since filteredToken will be disposed.
                if (AdvApi.DuplicateTokenEx(
                        filteredToken,
                        AdvApi.MAXIMUM_ALLOWED,
                        IntPtr.Zero,
                        AdvApi.SecurityImpersonationLevel.SecurityImpersonation,
                        AdvApi.TokenType.TokenPrimary,
                        out var dupToken2))
                {
                    return new SafeTokenHandle(dupToken2);
                }
            }

            _logger.LogWarning("DuplicateTokenEx failed after WTSQueryUserToken: {Err}",
                Marshal.GetLastWin32Error());
            // Fall through to LogonUser path
            Kernel32.CloseHandle(rawToken);
        }
        else
        {
            _logger.LogDebug("WTSQueryUserToken failed for session {Sid}: {Err}",
                sessionId, Marshal.GetLastWin32Error());
        }

        // Path 2: LogonUser with stored credential
        var password = _accounts.GetCredential(accountName);
        if (password is null)
            throw new InvalidOperationException($"No credential available for '{accountName}'");

        return LogonAndCreatePrimaryToken(accountName, password, sessionId);
    }

    /// <summary>
    /// Attempt to retrieve the linked elevated (unfiltered) token from a
    /// UAC-filtered token. Returns null if the token isn't filtered or if
    /// the operation fails. The returned token is a primary token suitable
    /// for CreateProcessAsUser.
    /// </summary>
    private SafeTokenHandle? TryGetLinkedElevatedToken(SafeTokenHandle filteredToken)
    {
        // Check if this token is elevated already (TokenElevation)
        if (AdvApi.GetTokenInformation(
                filteredToken, AdvApi.TokenInformationClass.TokenElevation,
                out var isElevated, sizeof(int), out _))
        {
            if (isElevated != 0)
            {
                _logger.LogDebug("Token is already elevated, no linked token needed");
                return null; // Already elevated, use as-is
            }
        }

        // Get the linked token (the elevated counterpart of the filtered token)
        // TOKEN_LINKED_TOKEN is a struct with a single HANDLE field
        if (!AdvApi.GetTokenInformationHandle(
                filteredToken, AdvApi.TokenInformationClass.TokenLinkedToken,
                out var linkedTokenHandle))
        {
            _logger.LogDebug("No linked token available (error {Err})",
                Marshal.GetLastWin32Error());
            return null;
        }

        if (linkedTokenHandle == IntPtr.Zero)
            return null;

        // The linked token is an impersonation token — duplicate to primary
        if (!AdvApi.DuplicateTokenEx(
                linkedTokenHandle,
                AdvApi.MAXIMUM_ALLOWED,
                IntPtr.Zero,
                AdvApi.SecurityImpersonationLevel.SecurityImpersonation,
                AdvApi.TokenType.TokenPrimary,
                out var primaryToken))
        {
            _logger.LogWarning("DuplicateTokenEx failed for linked token: {Err}",
                Marshal.GetLastWin32Error());
            Kernel32.CloseHandle(linkedTokenHandle);
            return null;
        }

        Kernel32.CloseHandle(linkedTokenHandle);
        return new SafeTokenHandle(primaryToken);
    }

    /// <summary>
    /// Log off a background session by session ID.
    /// Also cleans up the session anchor process.
    /// </summary>
    public void LogoffSession(int sessionId)
    {
        if (sessionId < 0) return;

        // Kill any pending mstsc first (session might still be Active)
        DisconnectSession(sessionId);

        // Kill the session anchor first
        if (_sessionAnchors.TryRemove(sessionId, out var anchor))
        {
            try
            {
                if (!anchor.Process.HasExited)
                {
                    anchor.Process.Kill();
                    anchor.Process.WaitForExit(3000);
                }
            }
            catch { /* best effort */ }
            finally
            {
                anchor.Process.Dispose();
            }
        }

        if (!WtsApi.WTSLogoffSession(WtsApi.WTS_CURRENT_SERVER_HANDLE, sessionId, true))
        {
            _logger.LogWarning("WTSLogoffSession({Sid}) failed: {Err}",
                sessionId, Marshal.GetLastWin32Error());
        }
        else
        {
            _logger.LogInformation("Session {Sid} logged off", sessionId);
        }
    }

    /// <summary>
    /// Check if the Windows session is still active.
    /// Uses WTS state as the authoritative source.
    /// </summary>
    public bool IsSessionAlive(int sessionId)
    {
        // Always check WTS state as the authoritative source
        var wtsAlive = FindSessionState(sessionId) is WtsApi.WtsConnectState.Active
            or WtsApi.WtsConnectState.Disconnected;

        if (!wtsAlive)
            return false;

        // If the session anchor process died but the WTS session is still active,
        // the session is alive — log a warning but don't kill the seat.
        if (_sessionAnchors.TryGetValue(sessionId, out var anchor) && anchor.Process.HasExited)
        {
            _logger.LogWarning(
                "Session anchor for session {Sid} exited, but WTS session is still active",
                sessionId);
        }

        return true;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRIVATE — RDP loopback session creation
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a new interactive session via RDP loopback connection.
    ///
    /// Flow:
    ///   1. Get the active console session (where a user is logged in)
    ///   2. Store the RDP credential as the console user (CredWrite, impersonated)
    ///   3. Launch mstsc.exe in the console session targeting 127.0.0.2
    ///   4. Poll WTS until the seat account's session appears
    ///   5. Launch a session anchor process in the new session
    ///   6. Save mstsc process in _pendingMstsc (caller will kill it via DisconnectSession)
    ///   7. Clean up stored credentials
    ///
    /// The session is returned in ACTIVE state. The caller must call
    /// DisconnectSession() once processes have finished initializing.
    /// </summary>
    private async Task<int> CreateSessionViaRdpLoopbackAsync(
        string accountName, string password, RdpGeometry? geometry, CancellationToken ct)
    {
        var consoleSessionId = Kernel32.WTSGetActiveConsoleSessionId();
        if (consoleSessionId == 0xFFFFFFFF)
            throw new InvalidOperationException(
                "No active console session found. Ensure a display is connected and Windows is running.");

        _logger.LogInformation(
            "Creating session for {Account} via RDP loopback (console session: {ConsoleSid})",
            accountName, consoleSessionId);

        using var primaryConsoleToken = GetConsoleSessionToken(consoleSessionId);

        var machineName = Environment.MachineName;
        var credTarget = $"TERMSRV/{RdpLoopbackAddress}";
        Process? mstscProcess = null;

        // Default.rdp is shared but its contents are per-seat — hold the gate across
        // write-then-launch so a concurrent provision cannot hand this seat another's geometry.
        await _rdpFileGate.WaitAsync(ct);

        try
        {
            // Step 0: Start the window hider first of all, so it is fully booted and polling
            // before mstsc exists. Spawning it immediately before mstsc is not enough — the
            // helper takes a few hundred ms to start, and mstsc shows its window ~300ms in, so
            // the first poll can land just after the window is already on screen (measured: one
            // provision in three still flashed for ~850ms). The steps below take a second or
            // more, which is free head start.
            StartWindowHideWatcher(primaryConsoleToken, consoleSessionId);

            // Step 1: Store the credential mstsc will use.
            _logger.LogDebug("Storing RDP credentials for {Account} (target: {Cred})...",
                accountName, credTarget);
            if (_credentials.Write(primaryConsoleToken, credTarget,
                                   $"{machineName}\\{accountName}", password))
            {
                _logger.LogDebug("Stored RDP credential for {Account}", accountName);
            }

            // Step 2: Write Default.rdp as the console user.
            // mstsc treats Default.rdp as a trusted user settings file — no "unknown
            // publisher" security warning. Passing an explicit unsigned .rdp file as an
            // argument is what triggers that dialog; using Default.rdp avoids it entirely.
            EnsureDefaultRdp(primaryConsoleToken, consoleSessionId, geometry);

            // Step 3: Launch mstsc.exe targeting loopback (no file argument)
            _logger.LogInformation("Launching mstsc to {Addr} for {Account}...",
                RdpLoopbackAddress, accountName);
            mstscProcess = LaunchMstscInConsoleSession(
                primaryConsoleToken, consoleSessionId);
            _logger.LogInformation("mstsc launched — PID {Pid}", mstscProcess.Id);

            // Fire-and-forget: auto-click "Connect" on the security trust dialog if
            // it appears. Runs concurrently with WaitForNewSessionAsync so it can
            // dismiss the dialog that would otherwise block the session from appearing.
            DismissMstscSecurityDialog(consoleSessionId, mstscProcess.Id);

            // Step 4: Wait for the new session to appear, monitoring mstsc liveness
            var sessionId = await WaitForNewSessionAsync(accountName, ct, mstscProcess);

            if (sessionId < 0)
            {
                throw new InvalidOperationException(
                    $"RDP loopback session for '{accountName}' did not appear within timeout. " +
                    "Check the service log for the mstsc exit code logged above. " +
                    "Steps to diagnose: " +
                    "(1) Re-run prerequisites\\install-prerequisites.ps1 to refresh rdpwrap.ini for your Windows build. " +
                    "(2) Open RDPConf.exe and verify Listener state is 'Listening [fully supported]'. " +
                    "(3) Manually run: mstsc /v:127.0.0.2 and log in as the seat account to verify RDP works. " +
                    "(4) Check HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\\WinStations\\RDP-Tcp: UserAuthentication must be 0.");
            }

            _logger.LogInformation(
                "RDP loopback created session {Sid} for {Account}", sessionId, accountName);

            // Step 5: Launch the session anchor BEFORE anything else to avoid a race
            // where Windows logs off the session because no processes are running.
            await LaunchSessionAnchorAsync(sessionId, accountName, password, ct);

            // Step 6: Keep mstsc alive so the session stays ACTIVE.
            // Hide the mstsc window — a minimized mstsc causes Windows to throttle
            // the RDP session which freezes Moonlight. Hidden = no throttling.
            await Task.Delay(500, ct); // brief wait for mstsc to create its window
            // Belt and braces: the watcher (started at launch) handles the RDP window; this
            // one-shot also clears anything else mstsc left on screen now that the connection
            // is up and no dialog is pending.
            HideProcessWindows(primaryConsoleToken, consoleSessionId, mstscProcess.Id);
            MuteMstscAudio(primaryConsoleToken, consoleSessionId, mstscProcess.Id);

            // Kill an mstsc left over for this same session id before overwriting the entry.
            // Assigning straight into _pendingMstsc drops the previous Process without killing
            // it, and nothing else holds a handle — so it survives as an orphan holding a
            // session open. Reachable when a session is recreated or reconnected without a
            // DisconnectSession in between.
            KillOrphanedMstsc(sessionId);

            _pendingMstsc[sessionId] = mstscProcess;
            mstscProcess = null; // Don't kill in finally block
            _logger.LogInformation(
                "Session {Sid} is ACTIVE — caller must call DisconnectSession when ready",
                sessionId);

            return sessionId;
        }
        finally
        {
            // Released here rather than right after the launch: mstsc reads Default.rdp at an
            // unobservable moment during startup, so the only safe release point is after the
            // session it produced exists.
            _rdpFileGate.Release();

            // Always clean up stored credentials
            try
            {
                _credentials.Delete(primaryConsoleToken, credTarget);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean up the RDP credential (non-critical)");
            }

            // Kill mstsc only if we didn't hand it off to _pendingMstsc
            if (mstscProcess != null)
                KillMstsc(mstscProcess);
        }
    }

    /// <summary>
    /// Reconnect an existing disconnected session via RDP loopback, bringing it
    /// to ACTIVE state. The session ID is unchanged — mstsc reconnects to it.
    /// Stores mstsc in _pendingMstsc; caller must call DisconnectSession().
    /// </summary>
    /// <remarks>
    /// <paramref name="geometry"/> is written into Default.rdp for consistency but does NOT
    /// resize the session: reconnecting attaches to a desktop that already exists at its
    /// original size. Changing a live seat's resolution means logging the session off and
    /// creating a new one.
    /// </remarks>
    private async Task ReconnectSessionAsync(
        int sessionId, string accountName, string password, RdpGeometry? geometry,
        CancellationToken ct)
    {
        var consoleSessionId = Kernel32.WTSGetActiveConsoleSessionId();
        if (consoleSessionId == 0xFFFFFFFF)
            throw new InvalidOperationException(
                "No active console session found for RDP reconnect.");

        using var primaryToken = GetConsoleSessionToken(consoleSessionId);

        var credTarget = $"TERMSRV/{RdpLoopbackAddress}";
        Process? mstscProcess = null;

        // Same shared-file gate as the create path — this also writes Default.rdp then launches.
        await _rdpFileGate.WaitAsync(ct);

        try
        {
            var machineName = Environment.MachineName;
            _credentials.Write(primaryToken, credTarget, $"{machineName}\\{accountName}", password);

            StartWindowHideWatcher(primaryToken, consoleSessionId);   // first — needs a head start
            EnsureDefaultRdp(primaryToken, consoleSessionId, geometry);
            mstscProcess = LaunchMstscInConsoleSession(primaryToken, consoleSessionId);
            DismissMstscSecurityDialog(consoleSessionId, mstscProcess.Id);

            // Wait for session to become Active
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var timeout = TimeSpan.FromMilliseconds(_options.SessionConnectTimeoutMs);
            while (sw.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();
                var state = FindSessionState(sessionId);
                if (state == WtsApi.WtsConnectState.Active)
                    break;
                await Task.Delay(500, ct);
            }

            var finalState = FindSessionState(sessionId);
            if (finalState != WtsApi.WtsConnectState.Active)
                _logger.LogWarning(
                    "Session {Sid} did not reach Active state after reconnect (state: {State})",
                    sessionId, finalState);
            else
                _logger.LogInformation(
                    "Session {Sid} is ACTIVE after reconnect", sessionId);

            await Task.Delay(500, ct);
            HideProcessWindows(primaryToken, consoleSessionId, mstscProcess.Id);
            MuteMstscAudio(primaryToken, consoleSessionId, mstscProcess.Id);

            // Same orphan risk as the fresh-session path above.
            KillOrphanedMstsc(sessionId);

            _pendingMstsc[sessionId] = mstscProcess;
            mstscProcess = null;
        }
        finally
        {
            _rdpFileGate.Release();

            try
            {
                _credentials.Delete(primaryToken, credTarget);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean up the RDP credential (non-critical)");
            }

            if (mstscProcess != null)
                KillMstsc(mstscProcess);
        }
    }

    /// <summary>
    /// Get a primary token suitable for launching processes in the console session.
    ///
    /// Tries WTSQueryUserToken first — works when a user is logged into the console.
    /// Falls back to the SYSTEM token when nobody is logged in (cold boot, lock screen).
    /// SYSTEM has full access to WinSta0\Default and its own credential store, so
    /// the credential write and mstsc both work correctly without a console user present.
    /// </summary>
    private SafeTokenHandle GetConsoleSessionToken(uint consoleSessionId)
    {
        // Happy path: a user is already logged into the console session
        if (WtsApi.WTSQueryUserToken(consoleSessionId, out var rawToken))
        {
            _logger.LogDebug(
                "Got console token via WTSQueryUserToken (session {Sid})", consoleSessionId);

            if (AdvApi.DuplicateTokenEx(
                    rawToken, AdvApi.MAXIMUM_ALLOWED, IntPtr.Zero,
                    AdvApi.SecurityImpersonationLevel.SecurityIdentification,
                    AdvApi.TokenType.TokenPrimary, out var primaryRaw))
            {
                Kernel32.CloseHandle(rawToken);
                return new SafeTokenHandle(primaryRaw);
            }

            var dupErr = Marshal.GetLastWin32Error();
            Kernel32.CloseHandle(rawToken);
            _logger.LogWarning(
                "DuplicateTokenEx failed for console user token (err {Err}) — falling back to SYSTEM",
                dupErr);
        }
        else
        {
            _logger.LogInformation(
                "No user logged into console session {Sid} — using SYSTEM token. " +
                "Seat provisioning works without a logged-in console user.",
                consoleSessionId);
        }

        // Fallback: use the SYSTEM token (the service's own process token).
        // SYSTEM can launch processes into WinSta0\Default and owns its own
        // credential store, so the generic credential and mstsc both work as expected.
        if (!AdvApi.OpenProcessToken(
                Kernel32.GetCurrentProcess(),
                AdvApi.TOKEN_ALL_ACCESS,
                out var systemTokenRaw))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Failed to open SYSTEM process token for console session launch");

        if (!AdvApi.DuplicateTokenEx(
                systemTokenRaw, AdvApi.MAXIMUM_ALLOWED, IntPtr.Zero,
                AdvApi.SecurityImpersonationLevel.SecurityIdentification,
                AdvApi.TokenType.TokenPrimary, out var systemPrimaryRaw))
        {
            Kernel32.CloseHandle(systemTokenRaw);
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "DuplicateTokenEx failed for SYSTEM token");
        }

        Kernel32.CloseHandle(systemTokenRaw);
        return new SafeTokenHandle(systemPrimaryRaw);
    }

    /// <summary>
    /// Run a command in the console session using CreateProcessAsUser
    /// with the console user's token.
    /// Returns the process exit code when <paramref name="waitForExit"/> is true,
    /// or -1 if not waiting.
    /// </summary>
    private int RunInConsoleSession(
        SafeTokenHandle consoleToken, uint consoleSessionId,
        string commandLine, bool waitForExit)
    {
        // Ensure the token targets the console session
        var targetSid = (int)consoleSessionId;
        AdvApi.SetTokenInformation(
            consoleToken, AdvApi.TokenInformationClass.TokenSessionId,
            ref targetSid, sizeof(int));

        if (!UserEnv.CreateEnvironmentBlock(out var envBlock, consoleToken, false))
            envBlock = IntPtr.Zero;

        try
        {
            var si = new Kernel32.StartupInfo
            {
                cb = Marshal.SizeOf<Kernel32.StartupInfo>(),
                lpDesktop = @"WinSta0\Default",
                dwFlags = Kernel32.STARTF_USESHOWWINDOW,
                wShowWindow = Kernel32.SW_HIDE
            };

            var flags = Kernel32.CREATE_UNICODE_ENVIRONMENT |
                        Kernel32.CREATE_NO_WINDOW |
                        Kernel32.NORMAL_PRIORITY_CLASS;

            if (!AdvApi.CreateProcessAsUserW(
                    consoleToken, null, commandLine,
                    IntPtr.Zero, IntPtr.Zero, false, flags, envBlock, null,
                    ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"CreateProcessAsUser failed for '{commandLine}': error {err}");
            }

            Kernel32.CloseHandle(pi.hThread);

            if (waitForExit)
            {
                Kernel32.WaitForSingleObject(pi.hProcess, 10_000);
                Kernel32.GetExitCodeProcess(pi.hProcess, out var exitCode);
                Kernel32.CloseHandle(pi.hProcess);
                return (int)exitCode;
            }

            Kernel32.CloseHandle(pi.hProcess);
            return -1;
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
                UserEnv.DestroyEnvironmentBlock(envBlock);
        }
    }

    /// <summary>
    /// Run a one-shot helper command inside the active console session (WinSta0\Default).
    /// Acquires the console session token internally. Waits up to 10 seconds.
    /// Use for operations that require the console session's window station context —
    /// e.g. ChangeDisplaySettingsEx for IddCx (SudoVDA) Console displays, whose
    /// display pipeline is only accessible from within the console session.
    /// </summary>
    public void RunHelperInConsoleSession(string commandLine)
    {
        var consoleSessionId = Kernel32.WTSGetActiveConsoleSessionId();
        if (consoleSessionId == 0xFFFFFFFF)
            throw new InvalidOperationException("No active console session");

        using var token = GetConsoleSessionToken(consoleSessionId);
        RunInConsoleSession(token, consoleSessionId, commandLine, waitForExit: true);
    }

    /// <summary>
    /// Run a one-shot helper command inside a seat's RDP session with no visible window.
    /// Waits up to 10 seconds for the command to complete.
    /// Used for operations that must run from within the session (e.g. setting display Hz),
    /// because some APIs (ChangeDisplaySettingsEx null, Core Audio) are session-scoped.
    /// </summary>
    public void RunHelperInSeatSession(int sessionId, string accountName, string commandLine)
    {
        using var token = GetSessionToken(sessionId, accountName);

        // The elevated linked token for admin accounts doesn't carry the seat's session ID —
        // it defaults to Session 0. Without this, CreateProcessAsUser launches the helper in
        // Session 0 and ChangeDisplaySettingsEx(null) changes the wrong display.
        var sid = sessionId;
        AdvApi.SetTokenInformation(
            token.DangerousGetHandle(),
            AdvApi.TokenInformationClass.TokenSessionId,
            ref sid, sizeof(int));

        if (!UserEnv.CreateEnvironmentBlock(out var envBlock, token, false))
            envBlock = IntPtr.Zero;

        try
        {
            var si = new Kernel32.StartupInfo
            {
                cb = Marshal.SizeOf<Kernel32.StartupInfo>(),
                lpDesktop = @"WinSta0\Default",
                dwFlags = Kernel32.STARTF_USESHOWWINDOW,
                wShowWindow = Kernel32.SW_HIDE
            };

            var flags = Kernel32.CREATE_UNICODE_ENVIRONMENT |
                        Kernel32.CREATE_NO_WINDOW |
                        Kernel32.NORMAL_PRIORITY_CLASS;

            if (!AdvApi.CreateProcessAsUserW(
                    token, null, commandLine,
                    IntPtr.Zero, IntPtr.Zero, false, flags, envBlock, null,
                    ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"CreateProcessAsUser failed for helper '{commandLine}' in session {sessionId}: error {err}");
            }

            Kernel32.CloseHandle(pi.hThread);
            Kernel32.WaitForSingleObject(pi.hProcess, 10_000);
            Kernel32.CloseHandle(pi.hProcess);
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
                UserEnv.DestroyEnvironmentBlock(envBlock);
        }
    }

    /// <summary>
    /// Start the keepalive mstsc on its own desktop, via a helper running inside the console
    /// session. Returns null if anything went wrong, so the caller can fall back rather than
    /// leave the seat without a keepalive at all.
    ///
    /// The work happens in a helper because window stations are per-session: this service is in
    /// session 0 and cannot create a desktop in the console session's window station. See
    /// <see cref="KeepaliveDesktopHelper"/> for why the desktop matters at all.
    /// </summary>
    private Process? LaunchMstscOnSeparateDesktop(
        SafeTokenHandle consoleToken, uint consoleSessionId)
    {
        // ProgramData, not %TEMP%: the helper runs as the console user and the service reads the
        // file back as SYSTEM, so it has to be somewhere both can reach. Same reasoning as the
        // staged Default.rdp.
        var pidFile = Path.Combine(
            @"C:\ProgramData\MultiSeat", $"keepalive-{Guid.NewGuid():N}.pid");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                _logger.LogWarning("Could not resolve this executable's path for the keepalive helper");
                return null;
            }

            var exit = RunInConsoleSession(
                consoleToken, consoleSessionId,
                $"\"{exe}\" --keepalive-mstsc {RdpLoopbackAddress} \"{pidFile}\"",
                waitForExit: true);

            if (exit != 0)
            {
                _logger.LogWarning(
                    "Keepalive helper exited {Code}; it could not create the desktop or start mstsc",
                    exit);
                return null;
            }

            if (!File.Exists(pidFile))
            {
                _logger.LogWarning(
                    "Keepalive helper reported success but wrote no PID, so there is nothing to track");
                return null;
            }

            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
            {
                _logger.LogWarning("Keepalive helper wrote a PID that does not parse");
                return null;
            }

            var process = Process.GetProcessById(pid);
            _logger.LogInformation(
                "Keepalive mstsc (PID {Pid}) started on {Desktop} - its pointer cannot reach the "
                + "console desktop",
                pid, KeepaliveDesktopHelper.QualifiedDesktop);

            return process;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start the keepalive on its own desktop");
            return null;
        }
        finally
        {
            try { if (File.Exists(pidFile)) File.Delete(pidFile); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Launch mstsc.exe in the console session targeting the RDP loopback address.
    /// Returns the mstsc Process for cleanup.
    /// </summary>
    private Process LaunchMstscInConsoleSession(
        SafeTokenHandle consoleToken, uint consoleSessionId)
    {
        if (_options.KeepaliveOnSeparateDesktop)
        {
            var onOwnDesktop = LaunchMstscOnSeparateDesktop(consoleToken, consoleSessionId);
            if (onOwnDesktop is not null) return onOwnDesktop;

            _logger.LogWarning(
                "Falling back to the console desktop for the keepalive. The seat will work, but "
                + "while a client streams it may mirror the seat's pointer onto the console "
                + "desktop (issue #18).");
        }

        var targetSid = (int)consoleSessionId;
        AdvApi.SetTokenInformation(
            consoleToken, AdvApi.TokenInformationClass.TokenSessionId,
            ref targetSid, sizeof(int));

        if (!UserEnv.CreateEnvironmentBlock(out var envBlock, consoleToken, false))
            envBlock = IntPtr.Zero;

        try
        {
            // Position mstsc on the virtual display (non-primary monitor) so it
            // stays in a fully "active" window state — mstsc throttles when minimized
            // or hidden, which freezes Moonlight. On the virtual display the user
            // can't see it (no physical screen there) but Windows treats it as active.
            var virtualBounds = FindNonPrimaryMonitorBounds();
            int startX, startY, startW, startH;
            uint showCmd;
            uint dwFlags = Kernel32.STARTF_USESHOWWINDOW;

            if (virtualBounds.HasValue)
            {
                var b = virtualBounds.Value;
                startX = b.Left;
                startY = b.Top;
                startW = b.Width;
                startH = b.Height;
                showCmd = (uint)Kernel32.SW_SHOW;
                dwFlags |= (uint)(Kernel32.STARTF_USEPOSITION | Kernel32.STARTF_USESIZE);
                _logger.LogInformation(
                    "Positioning mstsc on virtual display at ({X},{Y}) {W}x{H}",
                    startX, startY, startW, startH);
            }
            else
            {
                // No virtual display found (EnumDisplayMonitors is not accessible from
                // Session 0 / SYSTEM service), so there is nowhere off-screen to put the
                // window. Start it HIDDEN rather than minimized.
                //
                // Minimized is not good enough: measured, mstsc shows the window itself when
                // the connection comes up, roughly 440ms after launch, and it lands full-size
                // on the console user's display for about a second before anything can hide it
                // — the window-hide watcher is a separate process and cannot start that fast.
                // Closing that window (the natural response) disconnects the seat.
                //
                // Hidden is also the state this window ends up in anyway, and the one that does
                // NOT make Windows throttle the RDP session the way minimized does. Security
                // dialogs are separate top-level windows, so the dismisser still finds them.
                startX = 0; startY = 0; startW = 0; startH = 0;
                showCmd = (uint)Kernel32.SW_HIDE;
                _logger.LogInformation(
                    "No secondary monitor found — mstsc will start hidden.");
            }

            var si = new Kernel32.StartupInfo
            {
                cb = Marshal.SizeOf<Kernel32.StartupInfo>(),
                lpDesktop = @"WinSta0\Default",
                dwFlags = (int)dwFlags,
                wShowWindow = (short)(int)showCmd,
                dwX = startX,
                dwY = startY,
                dwXSize = startW,
                dwYSize = startH,
            };

            var flags = Kernel32.CREATE_UNICODE_ENVIRONMENT |
                        Kernel32.NORMAL_PRIORITY_CLASS;

            // No explicit .rdp file argument — mstsc reads Default.rdp from the user's
            // shell Documents folder (written by EnsureDefaultRdp before this call).
            // Default.rdp is always trusted; only .rdp files passed as arguments trigger
            // the "Caution: Unknown remote connection / Unknown publisher" security warning.
            var cmdLine = $"mstsc.exe /v:{RdpLoopbackAddress}";

            if (!AdvApi.CreateProcessAsUserW(
                    consoleToken, null, cmdLine,
                    IntPtr.Zero, IntPtr.Zero, false, flags, envBlock, null,
                    ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"Failed to launch mstsc.exe: error {err}");
            }

            Kernel32.CloseHandle(pi.hThread);
            Kernel32.CloseHandle(pi.hProcess);

            return Process.GetProcessById(pi.dwProcessId);
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
                UserEnv.DestroyEnvironmentBlock(envBlock);
        }
    }

    /// <summary>
    /// Poll WTS until a session for the given account appears.
    /// If <paramref name="mstsc"/> is provided, monitors it for early exit —
    /// if mstsc dies before the session appears, fail fast instead of waiting
    /// the full timeout (gives actionable diagnostics sooner).
    /// </summary>
    private async Task<int> WaitForNewSessionAsync(
        string accountName, CancellationToken ct, Process? mstsc = null)
    {
        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMilliseconds(_options.SessionConnectTimeoutMs);

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            // If mstsc already exited, the connection attempt failed (e.g. cert
            // dialog dismissed, credential rejected, or RDP Wrapper not active).
            // Fail fast instead of burning the full timeout.
            if (mstsc != null)
            {
                try
                {
                    if (mstsc.HasExited)
                    {
                        _logger.LogError(
                            "mstsc.exe (PID {Pid}) exited with code {Code} after {Ms}ms — " +
                            "the RDP connection attempt failed before session '{Account}' appeared. " +
                            "Common causes: (1) rdpwrap.ini is outdated for your Windows build " +
                            "(re-run prerequisites\\install-prerequisites.ps1), " +
                            "(2) a certificate or credential dialog appeared that no one clicked, " +
                            "(3) Remote Desktop is disabled or the RDP port is blocked.",
                            mstsc.Id, mstsc.ExitCode, sw.ElapsedMilliseconds, accountName);
                        return -1;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not check mstsc process liveness (non-critical)");
                }
            }

            var sessionId = FindExistingSession(accountName);
            if (sessionId >= 0)
            {
                _logger.LogDebug(
                    "Session for {Account} appeared as {Sid} after {Ms}ms",
                    accountName, sessionId, sw.ElapsedMilliseconds);
                return sessionId;
            }

            await Task.Delay(500, ct);
        }

        // Timeout — log whether mstsc is still alive to help distinguish
        // "mstsc still running but session never appeared" (RDP Wrapper multi-session
        // not working — termsrv disconnected the console user instead) vs.
        // "mstsc died silently" (which should have been caught above).
        if (mstsc != null)
        {
            try
            {
                var stillRunning = !mstsc.HasExited;
                _logger.LogWarning(
                    "Session for {Account} timed out after {Ms}ms — mstsc is {State}. " +
                    "{Hint}",
                    accountName, sw.ElapsedMilliseconds,
                    stillRunning ? "still running" : $"exited (code {mstsc.ExitCode})",
                    stillRunning
                        ? "RDP Wrapper multi-session patch may not be active — " +
                          "the connection may have reconnected an existing session instead of creating a new one. " +
                          "Check RDPConf.exe (Listener state should be 'Listening [fully supported]')."
                        : "mstsc exited silently at timeout — see earlier logs for exit code.");
            }
            catch { /* best effort */ }
        }

        return -1;
    }

    /// <summary>
    /// Launch the session anchor process inside the new session to hold it open.
    /// Without a running process, Windows may garbage-collect the session.
    /// </summary>
    private async Task LaunchSessionAnchorAsync(
        int sessionId, string accountName, string password, CancellationToken ct)
    {
        // Use WTSQueryUserToken to get the session's actual token, which has
        // proper access to the session's window station and desktop.
        // A LogonUser token does NOT have this access and causes
        // STATUS_DLL_INIT_FAILED (0xC0000142) when launching console processes.
        SafeTokenHandle token;
        if (WtsApi.WTSQueryUserToken((uint)sessionId, out var rawToken))
        {
            _logger.LogDebug("Got session token via WTSQueryUserToken for the session anchor in session {Sid}", sessionId);
            // Duplicate to a primary token
            if (AdvApi.DuplicateTokenEx(
                    rawToken,
                    AdvApi.MAXIMUM_ALLOWED,
                    IntPtr.Zero,
                    AdvApi.SecurityImpersonationLevel.SecurityImpersonation,
                    AdvApi.TokenType.TokenPrimary,
                    out var dupTokenRaw))
            {
                Kernel32.CloseHandle(rawToken);
                token = new SafeTokenHandle(dupTokenRaw);
            }
            else
            {
                _logger.LogWarning("DuplicateTokenEx failed for session token, falling back to LogonUser");
                Kernel32.CloseHandle(rawToken);
                token = LogonAndCreatePrimaryToken(accountName, password, sessionId);
            }
        }
        else
        {
            _logger.LogWarning("WTSQueryUserToken failed for session {Sid}: {Err}, falling back to LogonUser",
                sessionId, Marshal.GetLastWin32Error());
            token = LogonAndCreatePrimaryToken(accountName, password, sessionId);
        }

        if (!UserEnv.CreateEnvironmentBlock(out var envBlock, token, false))
            envBlock = IntPtr.Zero;

        try
        {
            var anchorCmd = BuildSessionAnchorCommand();

            var si = new Kernel32.StartupInfo
            {
                cb = Marshal.SizeOf<Kernel32.StartupInfo>(),
                lpDesktop = @"WinSta0\Default",
                dwFlags = Kernel32.STARTF_USESHOWWINDOW,
                wShowWindow = Kernel32.SW_HIDE
            };

            var flags = Kernel32.CREATE_UNICODE_ENVIRONMENT |
                        Kernel32.CREATE_NEW_CONSOLE |
                        Kernel32.CREATE_NEW_PROCESS_GROUP |
                        Kernel32.NORMAL_PRIORITY_CLASS;

            if (!AdvApi.CreateProcessAsUserW(
                    token, null, anchorCmd,
                    IntPtr.Zero, IntPtr.Zero, false, flags, envBlock, null,
                    ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning(
                    "Failed to launch the session anchor in session {Sid}: error {Err}. " +
                    "Session will rely on RDP session persistence.",
                    sessionId, err);
                return;
            }

            Kernel32.CloseHandle(pi.hThread);

            _logger.LogInformation(
                "Session anchor launched in session {Sid}: PID {Pid} (cmd: {Cmd}). This is the in-seat anchor that stops Windows reclaiming the session - NOT the console-session keepalive mstsc of issue #18, which logs separately",
                sessionId, pi.dwProcessId, anchorCmd);

            // Brief wait to verify the anchor didn't crash immediately
            var waitResult = Kernel32.WaitForSingleObject(pi.hProcess, 1000);
            if (waitResult == Kernel32.WAIT_OBJECT_0)
            {
                Kernel32.GetExitCodeProcess(pi.hProcess, out var exitCode);
                _logger.LogError(
                    "Session anchor PID {Pid} in session {Sid} exited immediately with code {Code}. " +
                    "Session may not persist.",
                    pi.dwProcessId, sessionId, exitCode);
                Kernel32.CloseHandle(pi.hProcess);
                return; // Don't try to track a dead process
            }

            _logger.LogInformation(
                "Session anchor PID {Pid} is running after 1s check", pi.dwProcessId);

            try
            {
                var process = Process.GetProcessById(pi.dwProcessId);
                _sessionAnchors[sessionId] = new SessionAnchorInfo(process, pi.hProcess);
            }
            catch (ArgumentException)
            {
                // Process exited between the wait check and GetProcessById
                _logger.LogWarning("Session anchor PID {Pid} exited before it could be tracked", pi.dwProcessId);
                Kernel32.CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
                UserEnv.DestroyEnvironmentBlock(envBlock);
            token.Dispose();
        }
    }

    /// <summary>
    /// Write Default.rdp to the console user's shell Documents folder.
    ///
    /// WHY THIS WORKS (vs passing an explicit .rdp file):
    ///   mstsc distinguishes between Default.rdp (the user's own settings file — always
    ///   trusted) and .rdp files passed as arguments (could come from anyone — subject to
    ///   the "Caution: Unknown remote connection / Unknown publisher" security warning).
    ///   By writing our settings into Default.rdp and launching mstsc with just /v:,
    ///   the publisher-trust dialog is never triggered — no file is being "opened from
    ///   an external source."
    ///
    /// We write the file AS the console user (via RunInConsoleSession PowerShell) so that
    /// shell-folder redirects such as OneDrive Known Folder Move resolve correctly.
    /// SYSTEM writing directly to the path would bypass the redirect and land in the wrong
    /// location; running as the user lets GetFolderPath('MyDocuments') return the right path.
    /// </summary>
    private void EnsureDefaultRdp(
        SafeTokenHandle consoleToken, uint consoleSessionId, RdpGeometry? geometry)
    {
        // Content (including why each key is there) lives in RdpFileBuilder — it is a pure
        // function of the audio mode and the geometry, so it can be asserted in tests rather
        // than only observed on a live host.
        var content = RdpFileBuilder.Build(_options.AudioMode, geometry);

        // Stage the file content in ProgramData where SYSTEM always has write access.
        const string stagingPath = @"C:\ProgramData\MultiSeat\default_rdp_staging.rdp";
        try
        {
            Directory.CreateDirectory(@"C:\ProgramData\MultiSeat");
            File.WriteAllText(stagingPath, content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write Default.rdp staging file — skipping");
            return;
        }

        // Copy the staging file to the user's shell Documents folder.
        // PowerShell's GetFolderPath('MyDocuments') uses the shell API and correctly
        // resolves OneDrive Known Folder Move redirects when running as the target user.
        var ps =
            $"Copy-Item -Force -Path '{stagingPath}'" +
            " -Destination ([IO.Path]::Combine([Environment]::GetFolderPath('MyDocuments'), 'Default.rdp'))";

        try
        {
            var exit = RunInConsoleSession(consoleToken, consoleSessionId,
                $"powershell.exe -NoProfile -NonInteractive -Command \"{ps}\"",
                waitForExit: true);

            if (exit == 0)
                _logger.LogInformation(
                    "Default.rdp written to console user's shell Documents folder " +
                    "(audio {Mode}, geometry {Geometry})",
                    _options.AudioMode,
                    geometry is null ? "unset — session inherits the console desktop size"
                                     : $"{geometry.Width}x{geometry.Height} @ {geometry.ScaleFactor}%");
            else
                _logger.LogWarning(
                    "PowerShell exited with code {Code} writing Default.rdp — " +
                    "mstsc may still show a dialog if the file wasn't updated", exit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write Default.rdp (non-critical)");
        }

        // Permanently trust 127.0.0.2 in the console user's HKCU so the
        // "Do you trust this remote connection?" dialog is suppressed on all
        // future connections. The dialog dismisser below covers this connection
        // in case the registry entry hasn't taken effect yet.
        TrustRdpLoopbackServer(consoleToken, consoleSessionId);
    }

    /// <summary>
    /// Fire-and-forget: auto-dismiss the mstsc security/trust dialog.
    ///
    /// Uses WScript.Shell.AppActivate + SendKeys("{ENTER}") from inside the console
    /// session. This works on all Windows versions including Windows 11 where the
    /// dialog is rendered in WinUI/XAML and EnumChildWindows cannot find buttons.
    /// AppActivate targets the dialog by its fixed title "Remote Desktop Connection".
    ///
    /// Runs only at mstsc launch and exits the moment it clicks, so it costs one
    /// short-lived process per seat rather than anything resident. It is a FALLBACK:
    /// <see cref="TrustRdpLoopbackServer"/> writes a permanent HKCU trust entry, which is
    /// what stops the dialog appearing at all. In practice this fires on the first
    /// connection after the .rdp settings change and then never again.
    ///
    /// It must outlive the connect attempt. Polling for a fixed 12 s while
    /// SessionConnectTimeoutMs was 15 s left a 3 s hole in which a late dialog went
    /// unclicked, provisioning timed out, and the log blamed "session did not appear" —
    /// pointing at RDP Wrapper rather than at a dialog nobody answered.
    /// </summary>
    private void DismissMstscSecurityDialog(uint consoleSessionId, int mstscPid)
    {
        // Outlast the connect attempt, with a margin for the dialog appearing right at the
        // deadline. Bounded either way: it stops as soon as it clicks.
        var pollMs = 200;
        var attempts = Math.Max(60, (_options.SessionConnectTimeoutMs + 5000) / pollMs);

        _ = Task.Run(() =>
        {
            try
            {
                using var token = GetConsoleSessionToken(consoleSessionId);
                // WScript.Shell approach: AppActivate finds the dialog by title and
                // SendKeys injects Enter regardless of Win32 vs WinUI rendering.
                // Single quotes used throughout to avoid nested-quote issues in
                // the powershell.exe -Command "..." wrapper.
                var ps =
                    "$w=New-Object -ComObject WScript.Shell;" +
                    $"for($i=0;$i-lt{attempts};$i++){{" +
                    "if($w.AppActivate('Remote Desktop Connection')){" +
                    "Start-Sleep -Milliseconds 300;" +
                    "$w.SendKeys('{ENTER}');exit 0};" +
                    $"Start-Sleep -Milliseconds {pollMs}}};exit 1";
                var result = RunInConsoleSession(token, consoleSessionId,
                    $"powershell.exe -NoProfile -NonInteractive -Command \"{ps}\"",
                    waitForExit: true);
                if (result == 0)
                    _logger.LogInformation(
                        "Security dialog dismisser sent Enter to mstsc PID {Pid}", mstscPid);
                else
                    _logger.LogDebug(
                        "Security dialog dismisser: dialog not found for mstsc PID {Pid}", mstscPid);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Security dialog dismisser failed (non-critical)");
            }
        });
    }

    /// <summary>
    /// Write a permanent trust entry for 127.0.0.2 into the console user's HKCU so
    /// mstsc never shows the "Do you trust this remote connection?" dialog again.
    /// With SecurityLayer=1 (RDP security, no TLS), the cert hash is 20 zero bytes.
    /// </summary>
    private void TrustRdpLoopbackServer(SafeTokenHandle consoleToken, uint consoleSessionId)
    {
        // Read the TLS cert hash that TermService generated for SecurityLayer=2.
        // TermService stores its self-signed cert in Cert:\LocalMachine\Remote Desktop.
        // SSLCertificateSHA1Hash under HKLM RDP-Tcp is only written after the first client
        // connection, so we always fall back to the cert store directly.
        const string ps =
            "$h=(Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\\WinStations\\RDP-Tcp'" +
            " -Name SSLCertificateSHA1Hash -EA SilentlyContinue).SSLCertificateSHA1Hash;" +
            "if(-not $h){$c=Get-ChildItem 'Cert:\\LocalMachine\\Remote Desktop' -EA SilentlyContinue|Select-Object -First 1;" +
            "if($c){$h=$c.GetCertHash()}};" +
            "if($h){$k='HKCU:\\Software\\Microsoft\\Terminal Server Client\\Servers\\127.0.0.2';" +
            "if(-not(Test-Path $k)){New-Item $k -Force|Out-Null};" +
            "Set-ItemProperty $k CertHash $h -Type Binary;" +
            "Set-ItemProperty $k UsernameHint '' -Type String;" +
            "Write-Output \"Trusted: $(($h|ForEach-Object{$_.ToString('X2')})-join'')\"}else{Write-Error 'No RDP TLS cert found'}";
        try
        {
            RunInConsoleSession(consoleToken, consoleSessionId,
                $"powershell.exe -NoProfile -NonInteractive -Command \"{ps}\"",
                waitForExit: true);
            _logger.LogDebug("Trusted RDP loopback 127.0.0.2 in console user HKCU");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write RDP trust registry entry (non-critical)");
        }
    }

    /// <summary>
    /// Find the bounds of the first non-primary monitor.
    /// This is where we position the mstsc window — on the virtual display that
    /// the user cannot physically see. The window stays in a normal shown state
    /// so mstsc doesn't throttle, but it doesn't appear on the user's main screen.
    /// Returns null if only one monitor is found (fall back to safe off-screen coords).
    /// </summary>
    private User32.Rect? FindNonPrimaryMonitorBounds()
    {
        User32.Rect? result = null;
        var gcHandle = GCHandle.Alloc(new List<User32.Rect>());

        try
        {
            User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                FindNonPrimaryMonitorCallback, GCHandle.ToIntPtr(gcHandle));

            var list = (List<User32.Rect>)gcHandle.Target!;
            if (list.Count > 0)
                result = list[0];
        }
        finally
        {
            gcHandle.Free();
        }

        return result;
    }

    private static bool FindNonPrimaryMonitorCallback(
        IntPtr hMonitor, IntPtr hdcMonitor, ref User32.Rect lprcMonitor, IntPtr dwData)
    {
        var info = new User32.MonitorInfo { cbSize = (uint)Marshal.SizeOf<User32.MonitorInfo>() };
        if (User32.GetMonitorInfo(hMonitor, ref info))
        {
            if ((info.dwFlags & User32.MonitorInfo.PRIMARY) == 0)
            {
                var gcHandle = GCHandle.FromIntPtr(dwData);
                ((List<User32.Rect>)gcHandle.Target!).Add(info.rcMonitor);
                return false; // stop — found one
            }
        }
        return true; // continue
    }

    /// <summary>
    /// Kill and forget any mstsc already recorded against <paramref name="sessionId"/>.
    ///
    /// Call this before writing a new entry into <see cref="_pendingMstsc"/>. The dictionary is
    /// the only thing holding these handles, so an indexer assignment silently discards the
    /// previous Process without killing it — leaving an mstsc nobody will ever clean up, still
    /// holding its session. DisconnectSession is what normally removes the entry; the orphan
    /// appears on the paths that recreate or reconnect a session without one.
    ///
    /// Logged at Warning because reaching it means an earlier cleanup was missed. Removing
    /// before killing keeps the dictionary consistent if the kill throws.
    /// </summary>
    private void KillOrphanedMstsc(int sessionId)
    {
        if (!_pendingMstsc.TryRemove(sessionId, out var previous) || previous is null) return;

        _logger.LogWarning(
            "Orphaned mstsc PID {Pid} still recorded for session {Sid} — killing it before replacing the entry",
            previous.Id, sessionId);
        KillMstsc(previous);
    }

    private static void KillMstsc(Process? mstsc)
    {
        if (mstsc == null) return;
        try
        {
            if (!mstsc.HasExited)
            {
                mstsc.Kill();
                mstsc.WaitForExit(3000);
            }
        }
        catch { /* best effort */ }
        finally
        {
            mstsc.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRIVATE — Token management
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// LogonUser + DuplicateTokenEx + SetTokenInformation to create a
    /// primary token targeted at a specific existing session.
    /// Used by ProcessInjector for launching apps in an already-running session.
    /// </summary>
    /// <summary>
    /// Throw unless the token really belongs to <paramref name="accountName"/>.
    ///
    /// The comparison is by SID, not by name: names are ambiguous across machine and domain
    /// scopes, get localised, and were already the cause of one silent failure in this project
    /// (the English-literal group names in account provisioning).
    ///
    /// If the account cannot be resolved to a SID at all, this does NOT block the launch — an
    /// unresolvable name is a different fault and blocking on it would turn a working host into a
    /// broken one. It logs loudly instead, which is the honest signal.
    /// </summary>
    private void EnsureTokenBelongsTo(IntPtr rawToken, string accountName, int sessionId)
    {
        var tokenSid = AdvApi.TryGetTokenUserSid(rawToken);
        if (tokenSid is null)
        {
            _logger.LogWarning(
                "Session {Sid}: could not read the token's user SID, so it was NOT verified to " +
                "belong to '{Account}'. Proceeding, but if this seat behaves as though its input " +
                "lands elsewhere, start here.",
                sessionId, accountName);
            return;
        }

        SecurityIdentifier expected;
        try
        {
            expected = (SecurityIdentifier)new NTAccount(accountName)
                .Translate(typeof(SecurityIdentifier));
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            _logger.LogWarning(ex,
                "Session {Sid}: '{Account}' could not be resolved to a SID, so the session token " +
                "was not verified. Proceeding.", sessionId, accountName);
            return;
        }

        if (tokenSid == expected) return;

        // Name the user we actually got — "wrong user" without saying who is a dead end for whoever
        // reads this in a bug report.
        string actual;
        try { actual = tokenSid.Translate(typeof(NTAccount)).Value; }
        catch (Exception) { actual = tokenSid.Value; }

        Kernel32.CloseHandle(rawToken);

        throw new InvalidOperationException(
            $"Session {sessionId} belongs to '{actual}', not to '{accountName}'. Refusing to launch, " +
            "because WTSQueryUserToken returns whoever occupies a session and the resulting token " +
            "would pass every downstream check while running the wrong user in the wrong session. " +
            "The seat's recorded session id is stale or wrong — re-provision the seat.");
    }

    private SafeTokenHandle LogonAndCreatePrimaryToken(
        string accountName, string password, int targetSessionId)
    {
        if (!AdvApi.LogonUserW(
                accountName, ".", password,
                AdvApi.LOGON32_LOGON_INTERACTIVE,
                AdvApi.LOGON32_PROVIDER_DEFAULT,
                out var logonTokenRaw))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"LogonUser failed for '{accountName}'");
        }

        var logonToken = new SafeTokenHandle(logonTokenRaw);

        if (!AdvApi.DuplicateTokenEx(
                logonToken,
                AdvApi.MAXIMUM_ALLOWED,
                IntPtr.Zero,
                AdvApi.SecurityImpersonationLevel.SecurityIdentification,
                AdvApi.TokenType.TokenPrimary,
                out var dupTokenRaw))
        {
            logonToken.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "DuplicateTokenEx failed");
        }

        logonToken.Dispose();
        var dupToken = new SafeTokenHandle(dupTokenRaw);

        // Stamp the target session ID onto the token
        var sid = targetSessionId;
        if (!AdvApi.SetTokenInformation(
                dupToken,
                AdvApi.TokenInformationClass.TokenSessionId,
                ref sid,
                sizeof(int)))
        {
            var err = Marshal.GetLastWin32Error();
            dupToken.Dispose();
            throw new Win32Exception(err,
                $"SetTokenInformation(TokenSessionId={targetSessionId}) failed. " +
                "The service must run as SYSTEM with SeTcbPrivilege.");
        }

        return dupToken;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRIVATE — Session state queries
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Poll until the session shows up in WTS enumeration and has
    /// a desktop ready, or until timeout.
    /// </summary>
    private async Task WaitForSessionReadyAsync(
        int sessionId, string accountName, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMilliseconds(_options.SessionConnectTimeoutMs);

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            var state = FindSessionState(sessionId);
            if (state is WtsApi.WtsConnectState.Active
                or WtsApi.WtsConnectState.Disconnected)
            {
                _logger.LogDebug("Session {Sid} ready in {Ms}ms (state: {State})",
                    sessionId, sw.ElapsedMilliseconds, state);
                return;
            }

            await Task.Delay(250, ct);
        }

        // Don't fail — the session might still be initializing but usable
        _logger.LogWarning(
            "Session {Sid} for {Account} did not reach Active/Disconnected state " +
            "within {Timeout}ms. Current state: {State}. Proceeding anyway.",
            sessionId, accountName, _options.SessionConnectTimeoutMs,
            FindSessionState(sessionId));
    }

    /// <summary>Returns true if the session exists and is in Active state (not Disconnected).</summary>
    public bool IsSessionActive(int sessionId) =>
        FindSessionState(sessionId) == WtsApi.WtsConnectState.Active;

    private WtsApi.WtsConnectState? FindSessionState(int sessionId)
    {
        if (WtsApi.WTSQuerySessionInformationW(
                WtsApi.WTS_CURRENT_SERVER_HANDLE,
                sessionId,
                WtsApi.WtsInfoClass.WTSConnectState,
                out var pState, out _))
        {
            var state = (WtsApi.WtsConnectState)Marshal.ReadInt32(pState);
            WtsApi.WTSFreeMemory(pState);
            return state;
        }

        return null;
    }

    /// <summary>
    /// Build the command line for the session anchor process.
    /// </summary>
    internal static string BuildSessionAnchorCommand()
    {
        // Use "waitfor" with a signal name that will never arrive.
        // Unlike "cmd.exe /c pause", waitfor does NOT depend on console input
        // (ReadConsoleInput), so it survives RDP disconnection reliably.
        // It uses a named event internally and blocks without any I/O.
        //
        // The /t 999999 sets a ~11.5 day timeout. If it ever expires,
        // the session health check will detect and handle it.
        //
        // Alternatives rejected:
        //   - "cmd.exe /c pause": pause uses ReadConsoleInput which fails
        //     when the console is detached after RDP disconnect
        //   - "cmd.exe /k": receives CTRL_CLOSE_EVENT on RDP disconnect
        //   - PowerShell: Node.js v24 null-byte env var bug
        //   - "ping -n 2147483647 127.0.0.1": works but generates network traffic
        // waitfor exits with code 1 in RDP sessions — use full-path timeout instead.
        // /nobreak prevents Ctrl+C from killing it.
        return @"C:\Windows\System32\timeout.exe /t 99999 /nobreak";
    }

    /// <summary>
    /// Hide all top-level windows belonging to the mstsc process so it isn't visible
    /// to whoever is using the host account. The process keeps running — the session
    /// stays Active — but the window is invisible.
    ///
    /// Runs a helper (<c>--hide-windows</c>) INSIDE the console session via
    /// CreateProcessAsUser. EnumWindows only enumerates windows on the caller's
    /// window station/desktop, so a SYSTEM service in Session 0 cannot see (or hide)
    /// the console user's mstsc window — doing it in-process was a silent no-op that
    /// left the RDP window on the host's screen (GitHub issue #8).
    /// </summary>
    private void HideProcessWindows(SafeTokenHandle consoleToken, uint consoleSessionId, int mstscPid)
    {
        try
        {
            var exePath = Path.Combine(AppContext.BaseDirectory, "MultiSeat.Service.exe");
            RunInConsoleSession(consoleToken, consoleSessionId,
                $"\"{exePath}\" --hide-windows {mstscPid}",
                waitForExit: true);
            _logger.LogInformation("Hid mstsc windows for PID {Pid} (console-session helper)", mstscPid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hide mstsc windows for PID {Pid} (non-critical)", mstscPid);
        }
    }

    /// <summary>
    /// Mute mstsc's audio session in the console session.
    ///
    /// SharedHost (audiomode:i:1): audio plays on the host, so mstsc has no audio session of its
    /// own and this is typically a no-op — kept as a safety net in case mstsc creates a
    /// session-scoped endpoint for control traffic. One blocking attempt, as before.
    ///
    /// PerSession (audiomode:i:0): this is LOAD-BEARING. The seat's audio is redirected to the
    /// RDP *client*, and that client is the hidden mstsc living in the console session — so
    /// without this mute the seat's game audio comes out of the host's speakers. It is also the
    /// step that lets the seat keep its own audio while the host stays quiet, which is the whole
    /// point of the mode.
    ///
    /// Under PerSession we do not block provisioning on it, and we do not bound it either: a
    /// bounded poll was measured failing outright, because mstsc creates no audio session until
    /// the seat first plays something, which can be long after provisioning. The helper therefore
    /// WATCHES for the life of the mstsc process (timeout -1) and exits with it. Verify with
    /// `MultiSeat.Service.exe --audio-peaks` in the console session — the mstsc APP line must
    /// read 0.000000 while a seat is playing audio.
    /// </summary>
    /// <summary>
    /// Start a resident watcher that keeps mstsc's RDP client window hidden for as long as the
    /// process lives.
    ///
    /// Hiding once after the session connects is not enough. mstsc re-shows that window later —
    /// on connect, on reconnect, and when the session's resolution changes — and because the
    /// window is now a real one (screen mode id:i:1 is required for the seat's geometry to
    /// apply at all), it lands on top of whatever the console user is doing. The natural
    /// response, closing it, disconnects the seat.
    ///
    /// Same shape as <see cref="MuteMstscAudio"/>'s watcher, and narrow by design: it only
    /// touches the RDP client window class, never dialogs, so it cannot hide the security prompt
    /// out from under the dismisser.
    /// </summary>
    private void StartWindowHideWatcher(SafeTokenHandle consoleToken, uint consoleSessionId)
    {
        try
        {
            var exePath = Path.Combine(AppContext.BaseDirectory, "MultiSeat.Service.exe");

            // Wait for mstsc long enough to cover a slow connect, then follow it for its lifetime.
            var adoptSeconds = Math.Max(30, (_options.SessionConnectTimeoutMs / 1000) + 15);

            // Stamp the cutoff HERE, before mstsc exists. The helper cannot sample it itself: by
            // the time its runtime is up, mstsc is already running and would look pre-existing.
            // A second of slack absorbs clock granularity between the two processes.
            var startedAfter = DateTime.UtcNow.AddSeconds(-1);

            RunInConsoleSession(consoleToken, consoleSessionId,
                $"\"{exePath}\" --hide-windows-new {startedAfter.Ticks} {adoptSeconds}",
                waitForExit: false);

            _logger.LogInformation(
                "Window-hide watcher started — it will adopt the mstsc launched next and keep its " +
                "RDP window hidden for that process's lifetime, so it cannot appear on the console " +
                "user's screen (closing that window would disconnect the seat)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to start window-hide watcher — mstsc's window may become visible on the " +
                "console desktop. Closing that window would disconnect the seat.");
        }
    }

    private void MuteMstscAudio(SafeTokenHandle consoleToken, uint consoleSessionId, int mstscPid)
    {
        var perSession = _options.AudioMode == AudioMode.PerSession;

        try
        {
            var exePath = Path.Combine(AppContext.BaseDirectory, "MultiSeat.Service.exe");
            // -1 = watch for the process's lifetime; 0 = the historical single attempt.
            var timeoutMs = perSession ? -1 : 0;

            RunInConsoleSession(consoleToken, consoleSessionId,
                $"\"{exePath}\" --mute-audio {mstscPid} {timeoutMs}",
                waitForExit: !perSession);

            if (perSession)
                _logger.LogInformation(
                    "MuteMstscAudio watcher started for PID {Pid} — it will mute mstsc as soon as " +
                    "the seat plays audio and stay resident until mstsc exits (per-session audio: " +
                    "this is what keeps seat audio off the host's speakers)", mstscPid);
            else
                _logger.LogInformation("MuteMstscAudio helper ran for PID {Pid}", mstscPid);
        }
        catch (Exception ex)
        {
            // Non-critical under SharedHost (no-op anyway); under PerSession this means seat
            // audio may be audible on the host, so say so rather than logging a bare warning.
            if (perSession)
                _logger.LogWarning(ex,
                    "Failed to start mute-audio watcher for PID {Pid} — seat audio may play on " +
                    "the host's speakers. Check with --audio-peaks in the console session.", mstscPid);
            else
                _logger.LogWarning(ex, "Failed to run mute-audio helper for PID {Pid} (non-critical)", mstscPid);
        }
    }

    // ═══════════════════════════════════════════════════════════════════

    private sealed record SessionAnchorInfo(Process Process, IntPtr NativeHandle) : IDisposable
    {
        public void Dispose()
        {
            Process.Dispose();
            if (NativeHandle != IntPtr.Zero)
                Kernel32.CloseHandle(NativeHandle);
        }
    }
}
