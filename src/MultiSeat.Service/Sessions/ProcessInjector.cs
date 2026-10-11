using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Interop;

namespace MultiSeat.Service.Sessions;

/// <summary>
/// Launches arbitrary executables inside a target Windows session.
///
/// Flow:
///   1. Acquire a primary token for the target session
///      (via WTSQueryUserToken or LogonUser + SetTokenInformation)
///   2. CreateEnvironmentBlock for the user's environment
///   3. CreateProcessAsUser targeting the session's desktop (WinSta0\Default)
///   4. Return the process ID and optionally wait for startup
///
/// Requirements:
///   - MultiSeat service must run as SYSTEM (for WTSQueryUserToken + SeTcbPrivilege)
///   - Target session must already exist (created by SessionLauncher)
/// </summary>
public sealed class ProcessInjector
{
    private readonly ILogger<ProcessInjector> _logger;
    private readonly MultiSeatOptions _options;
    private readonly SessionLauncher _sessionLauncher;

    public ProcessInjector(
        ILogger<ProcessInjector> logger,
        IOptions<MultiSeatOptions> options,
        SessionLauncher sessionLauncher)
    {
        _logger = logger;
        _options = options.Value;
        _sessionLauncher = sessionLauncher;
    }

    /// <summary>
    /// Launch a process inside the specified Windows session.
    /// Returns the PID of the launched process.
    /// </summary>
    public async Task<int> LaunchInSessionAsync(
        int sessionId,
        string accountName,
        string exePath,
        string? arguments = null,
        string? workingDir = null,
        CancellationToken ct = default,
        bool allowConsoleSession = false)
    {
        _logger.LogInformation(
            "Launching '{Exe}' in session {Sid} (account: {Account})",
            exePath, sessionId, accountName);

        // Validate the executable exists and is accessible
        if (!File.Exists(exePath))
        {
            // It might be in PATH — only warn, don't block
            _logger.LogDebug("Executable not found at absolute path: {Exe} — " +
                "assuming it's in PATH or will resolve at runtime", exePath);
        }

        // A seat never lives in the console session, so refuse before doing anything else.
        var consoleSessionId = (int)Kernel32.WTSGetActiveConsoleSessionId();
        EnsureNotConsoleSession(sessionId, consoleSessionId, exePath, allowConsoleSession);

        var launchPlan = SteamIsolationLaunchPlan.Create(exePath, arguments, _options);
        if (launchPlan.Supervised)
            _logger.LogInformation("Launching isolated Steam through '{Launcher}' in session {Sid}",
                launchPlan.ExecutablePath, sessionId);

        // Step 1: Get a primary token for the target session
        using var token = _sessionLauncher.GetSessionToken(sessionId, accountName);

        // Verify the token is stamped with the correct session ID
        if (AdvApi.GetTokenInformation(
                token, AdvApi.TokenInformationClass.TokenSessionId,
                out var tokenSid, sizeof(int), out _))
        {
            if (tokenSid != sessionId)
            {
                _logger.LogWarning(
                    "Token session ID mismatch: token has {TokenSid}, expected {Expected}. " +
                    "Re-stamping token.", tokenSid, sessionId);

                var targetSid = sessionId;
                if (!AdvApi.SetTokenInformation(
                        token, AdvApi.TokenInformationClass.TokenSessionId,
                        ref targetSid, sizeof(int)))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"SetTokenInformation(TokenSessionId={sessionId}) failed");
                }
            }
        }
        else
        {
            // This used to fall through in silence, so a token that was never checked looked
            // exactly like a token that passed. Say so — the post-launch check below is what
            // actually catches the consequence.
            _logger.LogWarning(
                "Could not read the token's session id ({Err}), so it was NOT verified against " +
                "session {Sid} before launching '{Exe}'.",
                Marshal.GetLastWin32Error(), sessionId, exePath);
        }

        using var tokenIdentity = launchPlan.Supervised ? new WindowsIdentity(token.DangerousGetHandle()) : null;
        using var readiness = launchPlan.Supervised
            ? new SteamIsolationReadiness(tokenIdentity!.User ?? throw new InvalidOperationException("Seat token has no user SID."))
            : null;
        if (readiness is not null) launchPlan = launchPlan.WithReadyEvent(readiness.Name);

        // Step 2: Create the environment block for the user
        if (!UserEnv.CreateEnvironmentBlock(out var envBlock, token, false))
        {
            _logger.LogWarning("CreateEnvironmentBlock failed: {Err}, using NULL env",
                Marshal.GetLastWin32Error());
            envBlock = IntPtr.Zero;
        }

        try
        {
            // Step 3: Build the command line
            var cmdLine = FormatCommandLine(launchPlan.ExecutablePath, launchPlan.Arguments);

            var si = new Kernel32.StartupInfo
            {
                cb = Marshal.SizeOf<Kernel32.StartupInfo>(),
                lpDesktop = @"WinSta0\Default",
                dwFlags = Kernel32.STARTF_USESHOWWINDOW,
                wShowWindow = Kernel32.SW_SHOW  // games should be visible
            };

            var creationFlags =
                Kernel32.CREATE_UNICODE_ENVIRONMENT |
                Kernel32.CREATE_NEW_CONSOLE |
                Kernel32.NORMAL_PRIORITY_CLASS;

            // Step 4: Launch the process
            if (!AdvApi.CreateProcessAsUserW(
                    token,
                    null,          // use cmdLine for both app + args
                    cmdLine,
                    IntPtr.Zero,   // default process security
                    IntPtr.Zero,   // default thread security
                    false,         // don't inherit handles
                    creationFlags,
                    envBlock,
                    workingDir,    // working directory (null = inherit)
                    ref si,
                    out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"CreateProcessAsUser failed for '{exePath}' in session {sessionId}: " +
                    $"Win32 error {err}. Ensure the service runs as SYSTEM.");
            }

            // Close handles we don't need
            Kernel32.CloseHandle(pi.hThread);

            // ⚠️ Ask the OS where the process actually landed. This log line used to report the
            // session we ASKED for, which is a claim about our intent rather than about the world —
            // so a process running in the wrong session produced a perfectly reassuring log. Seven
            // rounds of issue #18 went past exactly that line.
            try
            {
                VerifyLandedInSession(pi, exePath, sessionId, consoleSessionId);
                // Step 5: Wait briefly for the process to initialize
                if (readiness is not null)
                    await readiness.WaitAsync(pi.hProcess, TimeSpan.FromSeconds(180), ct);
                else
                    await WaitForProcessStartAsync(pi, ct);
            }
            catch
            {
                // The supervisor owns a kill-on-close job containing Steam and its children.
                // Terminating it also cleans up an incomplete or cancelled isolated launch.
                if (readiness is not null &&
                    Kernel32.WaitForSingleObject(pi.hProcess, 0) == Kernel32.WAIT_TIMEOUT &&
                    !Kernel32.TerminateProcess(pi.hProcess, 1))
                    _logger.LogError("Failed to terminate Steam isolation supervisor PID {Pid}: {Error}",
                        pi.dwProcessId, Marshal.GetLastWin32Error());
                throw;
            }
            finally
            {
                // Close the process handle even if the wait is cancelled, so it never leaks.
                Kernel32.CloseHandle(pi.hProcess);
            }
            return pi.dwProcessId;
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
                UserEnv.DestroyEnvironmentBlock(envBlock);
        }
    }

    /// <summary>
    /// Launch Apollo streaming server inside a target session.
    /// Special handling: Apollo needs specific env vars and the config path.
    /// </summary>
    public Task<int> LaunchApolloInSessionAsync(
        int sessionId,
        string accountName,
        string apolloExePath,
        string configPath,
        CancellationToken ct)
    {
        var arguments = $"\"{configPath}\"";

        // Working dir = per-seat config dir (not the Apollo install dir).
        // Apollo resolves sunshine_state.json from {workingDir}/config/sunshine_state.json.
        // Each seat must have its own working dir so they get distinct UUIDs and
        // Moonlight can list them as separate servers.
        return LaunchInSessionAsync(
            sessionId, accountName,
            apolloExePath, arguments,
            Path.GetDirectoryName(configPath),
            ct);
    }

    /// <summary>
    /// Refuse to launch a seat's process into the console session.
    ///
    /// This is the cheapest of the session guards and the one that would have caught issue #18 at
    /// provisioning time. A seat's process in the console session still runs, still serves the
    /// seat's ports, and still writes a healthy-looking log — while its input lands on the console
    /// desktop. There is no configuration in which that is what someone wanted, so it is an error
    /// rather than a warning.
    ///
    /// Deliberate console launches exist and go through <c>LaunchInConsoleSessionAsync</c>;
    /// <paramref name="allowConsoleSession"/> is the explicit opt-out for anything else.
    /// </summary>
    internal static void EnsureNotConsoleSession(
        int sessionId, int consoleSessionId, string exePath, bool allowConsoleSession)
    {
        if (allowConsoleSession || sessionId != consoleSessionId) return;

        throw new InvalidOperationException(
            $"Refusing to launch '{exePath}' into session {sessionId}, which is the CONSOLE " +
            "session. A seat's process belongs in the seat's own session; launching it here would " +
            "put its input on the console desktop. The seat's recorded session id is stale or " +
            "wrong — re-provision the seat. (Console launches go through LaunchInConsoleSessionAsync.)");
    }

    /// <summary>
    /// Confirm the process is in the session we aimed at, and kill it if not.
    ///
    /// A process in the wrong session is worse than no process: it runs, it looks healthy, it
    /// serves clients on the seat's ports, and it injects its input into somebody else's desktop.
    /// Killing it turns a silent seven-round mystery into one loud line at provisioning time.
    ///
    /// <see cref="Kernel32.ProcessIdToSessionId"/> was declared in this codebase and never called
    /// once — the capability to notice this existed the whole time.
    /// </summary>
    private void VerifyLandedInSession(
        Kernel32.ProcessInformation pi, string exePath, int expectedSessionId, int consoleSessionId)
    {
        if (!Kernel32.ProcessIdToSessionId((uint)pi.dwProcessId, out var actualSessionId))
        {
            _logger.LogWarning(
                "Process launched: '{Exe}' PID {Pid}, but its session could NOT be read ({Err}), " +
                "so it is unverified. Expected session {Sid}.",
                exePath, pi.dwProcessId, Marshal.GetLastWin32Error(), expectedSessionId);
            return;
        }

        if ((int)actualSessionId == expectedSessionId)
        {
            _logger.LogInformation(
                "Process launched: '{Exe}' PID {Pid} in session {Sid} (verified)",
                exePath, pi.dwProcessId, actualSessionId);
            return;
        }

        var landedOnConsole = (int)actualSessionId == consoleSessionId;
        _logger.LogError(
            "Process '{Exe}' PID {Pid} landed in session {Actual}, NOT the requested {Expected}{Console}. " +
            "Killing it: left running it would serve the seat's ports while injecting input into " +
            "another session's desktop, and every log it wrote would look correct.",
            exePath, pi.dwProcessId, actualSessionId, expectedSessionId,
            landedOnConsole ? " — that is the CONSOLE session" : "");

        try { Kernel32.TerminateProcess(pi.hProcess, 1); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not kill the mis-targeted process"); }

        throw new InvalidOperationException(
            $"'{exePath}' launched into session {actualSessionId} instead of {expectedSessionId}" +
            (landedOnConsole ? " (the console session)" : "") +
            ". The process was killed. The seat's recorded session id is stale or wrong.");
    }

    /// <summary>
    /// Launch any executable in the active console session using the current
    /// console user's token. For GUI apps (e.g. VoiceMeeter) that must run in
    /// an interactive session — Process.Start from SYSTEM targets Session 0,
    /// which is non-interactive and prevents GUI apps from functioning.
    /// </summary>
    public async Task<int> LaunchInConsoleSessionAsync(
        string exePath,
        string? arguments,
        CancellationToken ct)
    {
        var consoleSessionId = Kernel32.WTSGetActiveConsoleSessionId();
        _logger.LogInformation(
            "Launching '{Exe}' in console session {Sid}", exePath, consoleSessionId);

        if (!WtsApi.WTSQueryUserToken(consoleSessionId, out var userToken))
        {
            var err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err,
                $"WTSQueryUserToken failed for console session {consoleSessionId}");
        }

        using var token = new SafeTokenHandle(userToken);

        if (!UserEnv.CreateEnvironmentBlock(out var envBlock, token, false))
            envBlock = IntPtr.Zero;

        try
        {
            var cmdLine = FormatCommandLine(exePath, arguments);
            var workingDir = Path.GetDirectoryName(exePath);

            var si = new Kernel32.StartupInfo
            {
                cb = Marshal.SizeOf<Kernel32.StartupInfo>(),
                lpDesktop = @"WinSta0\Default",
                dwFlags = Kernel32.STARTF_USESHOWWINDOW,
                wShowWindow = Kernel32.SW_SHOWMINNOACTIVE,
            };

            var creationFlags =
                Kernel32.CREATE_UNICODE_ENVIRONMENT |
                Kernel32.CREATE_NEW_CONSOLE |
                Kernel32.NORMAL_PRIORITY_CLASS;

            if (!AdvApi.CreateProcessAsUserW(
                    token,
                    null,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    creationFlags,
                    envBlock,
                    workingDir,
                    ref si,
                    out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"CreateProcessAsUser failed for '{exePath}' in console session: Win32 error {err}");
            }

            Kernel32.CloseHandle(pi.hThread);

            _logger.LogInformation(
                "Process '{Exe}' launched in console session {Sid}: PID {Pid}",
                exePath, consoleSessionId, pi.dwProcessId);

            try
            {
                await WaitForProcessStartAsync(pi, ct);
            }
            finally
            {
                Kernel32.CloseHandle(pi.hProcess);
            }
            return pi.dwProcessId;
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
                UserEnv.DestroyEnvironmentBlock(envBlock);
        }
    }

    /// <summary>
    /// Launch an executable in the console session and wait for it to exit.
    /// Returns the process exit code, or -1 on timeout.
    /// Used for short-lived helper processes that must run in the console session
    /// (e.g. --enum-displays, --set-display-hz) where the service needs the result.
    /// </summary>
    public async Task<int> RunInConsoleSessionAsync(
        string exePath,
        string? arguments,
        int timeoutMs,
        CancellationToken ct)
    {
        var consoleSessionId = Kernel32.WTSGetActiveConsoleSessionId();
        _logger.LogDebug(
            "Running '{Exe}' in console session {Sid} (timeout {Ms}ms)",
            exePath, consoleSessionId, timeoutMs);

        if (!WtsApi.WTSQueryUserToken(consoleSessionId, out var userToken))
        {
            var err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err,
                $"WTSQueryUserToken failed for console session {consoleSessionId}");
        }

        using var token = new SafeTokenHandle(userToken);

        if (!UserEnv.CreateEnvironmentBlock(out var envBlock, token, false))
            envBlock = IntPtr.Zero;

        try
        {
            var cmdLine = FormatCommandLine(exePath, arguments);
            var workingDir = Path.GetDirectoryName(exePath);

            var si = new Kernel32.StartupInfo
            {
                cb = Marshal.SizeOf<Kernel32.StartupInfo>(),
                lpDesktop = @"WinSta0\Default",
                dwFlags = Kernel32.STARTF_USESHOWWINDOW,
                wShowWindow = Kernel32.SW_HIDE,
            };

            var creationFlags =
                Kernel32.CREATE_UNICODE_ENVIRONMENT |
                Kernel32.CREATE_NO_WINDOW |
                Kernel32.NORMAL_PRIORITY_CLASS;

            if (!AdvApi.CreateProcessAsUserW(
                    token, null, cmdLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    creationFlags, envBlock, workingDir,
                    ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"CreateProcessAsUser failed for '{exePath}' in console session: Win32 error {err}");
            }

            Kernel32.CloseHandle(pi.hThread);

            try
            {
                // Wait for the process to finish
                var waitResult = await Task.Run(
                    () => Kernel32.WaitForSingleObject(pi.hProcess, (uint)timeoutMs), ct);

                int exitCode = -1;
                if (waitResult == Kernel32.WAIT_OBJECT_0)
                {
                    Kernel32.GetExitCodeProcess(pi.hProcess, out var raw);
                    exitCode = (int)raw;
                }
                else
                {
                    _logger.LogWarning(
                        "Helper '{Exe}' did not exit within {Ms}ms — killing it", exePath, timeoutMs);
                }

                return exitCode;
            }
            finally
            {
                Kernel32.CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
                UserEnv.DestroyEnvironmentBlock(envBlock);
        }
    }

    /// <summary>
    /// Launch Apollo in the active console session (where the GPU display pipeline lives).
    /// RDP sessions cannot access DXGI — QueryDisplayConfig returns ACCESS_DENIED.
    /// The console session has full GPU access, so Apollo can create and capture
    /// SudoVDA virtual displays from there.
    /// </summary>
    public async Task<int> LaunchApolloInConsoleSessionAsync(
        string apolloExePath,
        string configPath,
        CancellationToken ct)
    {
        var consoleSessionId = Kernel32.WTSGetActiveConsoleSessionId();
        _logger.LogInformation(
            "Launching Apollo in console session {Sid} with config: {Config}",
            consoleSessionId, configPath);

        // Get a token for the console session — this gives us GPU/DXGI access.
        // WTSQueryUserToken returns the ELEVATED token for admin users.
        // SudoVDA IPC requires a medium-integrity (non-elevated) token — the same
        // as a normal shell-launched process. We use the linked filtered token instead.
        if (!WtsApi.WTSQueryUserToken(consoleSessionId, out var userToken))
        {
            var err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err,
                $"WTSQueryUserToken failed for console session {consoleSessionId}");
        }

        // Try to get the linked filtered token (medium integrity).
        // For non-admin users GetTokenInformationHandle will fail — fall back to userToken.
        IntPtr primaryToken;
        if (AdvApi.GetTokenInformationHandle(
                userToken,
                AdvApi.TokenInformationClass.TokenLinkedToken,
                out var linkedToken) && linkedToken != IntPtr.Zero)
        {
            _logger.LogDebug(
                "Console session {Sid}: using filtered (medium-integrity) linked token for Apollo",
                consoleSessionId);
            Kernel32.CloseHandle(userToken);
            primaryToken = linkedToken;
        }
        else
        {
            _logger.LogDebug(
                "Console session {Sid}: no linked token found, using WTSQueryUserToken result directly",
                consoleSessionId);
            primaryToken = userToken;
        }

        using var token = new SafeTokenHandle(primaryToken);

        if (!UserEnv.CreateEnvironmentBlock(out var envBlock, token, false))
            envBlock = IntPtr.Zero;

        try
        {
            var arguments = $"\"{configPath}\"";
            var cmdLine = FormatCommandLine(apolloExePath, arguments);
            var workingDir = Path.GetDirectoryName(configPath);

            var si = new Kernel32.StartupInfo
            {
                cb = Marshal.SizeOf<Kernel32.StartupInfo>(),
                lpDesktop = @"WinSta0\Default",
                dwFlags = Kernel32.STARTF_USESHOWWINDOW,
                wShowWindow = Kernel32.SW_SHOW,
            };

            var creationFlags =
                Kernel32.CREATE_UNICODE_ENVIRONMENT |
                Kernel32.CREATE_NEW_CONSOLE |
                Kernel32.NORMAL_PRIORITY_CLASS;

            if (!AdvApi.CreateProcessAsUserW(
                    token,
                    null,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    creationFlags,
                    envBlock,
                    workingDir,
                    ref si,
                    out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"CreateProcessAsUser failed for Apollo in console session: Win32 error {err}");
            }

            Kernel32.CloseHandle(pi.hThread);

            _logger.LogInformation(
                "Apollo launched in console session {Sid}: PID {Pid}",
                consoleSessionId, pi.dwProcessId);

            try
            {
                await WaitForProcessStartAsync(pi, ct);
            }
            finally
            {
                Kernel32.CloseHandle(pi.hProcess);
            }
            return pi.dwProcessId;
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
                UserEnv.DestroyEnvironmentBlock(envBlock);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRIVATE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Format the command line string for CreateProcessAsUser.
    /// The executable must be quoted if it contains spaces.
    /// </summary>
    private static string FormatCommandLine(string exePath, string? arguments)
    {
        // Always quote the executable path
        var quoted = exePath.Contains(' ') || !exePath.StartsWith('"')
            ? $"\"{exePath}\""
            : exePath;

        return string.IsNullOrWhiteSpace(arguments)
            ? quoted
            : $"{quoted} {arguments}";
    }

    /// <summary>
    /// Brief wait to confirm the process didn't crash on startup.
    /// Checks that the process is still alive after a short delay.
    /// </summary>
    private async Task WaitForProcessStartAsync(
        Kernel32.ProcessInformation pi, CancellationToken ct)
    {
        // Wait up to 2 seconds for the process to either stabilize or crash
        var waitResult = await Task.Run(
            () => Kernel32.WaitForSingleObject(pi.hProcess, 2000), ct);

        if (waitResult == Kernel32.WAIT_OBJECT_0)
        {
            // Process exited already — get exit code
            Kernel32.GetExitCodeProcess(pi.hProcess, out var exitCode);
            _logger.LogWarning(
                "Process PID {Pid} exited immediately with code {Code}",
                pi.dwProcessId, exitCode);
        }
        else if (waitResult == Kernel32.WAIT_TIMEOUT)
        {
            // Still running — good
            _logger.LogDebug("Process PID {Pid} is running after 2s startup check",
                pi.dwProcessId);
        }
    }
}
