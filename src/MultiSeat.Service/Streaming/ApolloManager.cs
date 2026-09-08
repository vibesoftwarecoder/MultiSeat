using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Streaming;

/// <summary>
/// Manages per-seat Apollo (Sunshine fork) process lifecycle.
///
/// Each seat runs its own Apollo instance with isolated config,
/// port range, display target, and audio device.
///
/// Architecture:
///   - Config is generated per-seat by ApolloConfigBuilder
///   - Apollo is launched inside the seat's Windows session via ProcessInjector
///     (so it sees the session's virtual display + audio device)
///   - Health is monitored by SessionHealthCheck; crashed instances are auto-restarted
///   - On teardown, the entire process tree is killed (Apollo spawns child encoders)
///
/// Apollo (Sunshine) uses these port offsets within a seat's block
/// (see Shared/Constants for the authoritative values):
///   -5  GFE HTTPS (Moonlight serverinfo/pair/launch)
///    0  GFE HTTP  (same, plaintext — the value written to the 'port' config key)
///    1  Web UI    (Apollo HTTPS web UI)
///    9  Video   (RTP)
///   10  Control (ENet)
///   11  Audio   (RTP)
///   12  Mic     (RTP)
///   26  RTSP    (session setup)
///
/// Requires Apollo (Sunshine fork) installed:
///   https://github.com/ClassicOldSong/Apollo
/// </summary>
public sealed class ApolloManager
{
    private readonly ILogger<ApolloManager> _logger;
    private readonly MultiSeatOptions _options;
    private readonly ApolloConfigBuilder _configBuilder;
    private readonly ProcessInjector _processInjector;

    // Seat → Apollo instance tracking
    private readonly ConcurrentDictionary<Guid, ApolloInstance> _instances = new();

    public ApolloManager(
        ILogger<ApolloManager> logger,
        IOptions<MultiSeatOptions> options,
        ApolloConfigBuilder configBuilder,
        ProcessInjector processInjector)
    {
        _logger = logger;
        _options = options.Value;
        _configBuilder = configBuilder;
        _processInjector = processInjector;
    }

    /// <summary>
    /// True if the Apollo executable exists at the configured path.
    /// </summary>
    public bool IsApolloInstalled => File.Exists(_options.ApolloExePath);

    /// <summary>
    /// Get the number of running Apollo instances.
    /// </summary>
    public int RunningInstanceCount => _instances.Count(i => i.Value.IsAlive);

    /// <summary>
    /// Start an Apollo instance for the given seat.
    /// Generates per-seat config and launches Apollo inside the seat's Windows session.
    /// Returns the Apollo process ID.
    /// </summary>
    public async Task<int> StartAsync(SeatInfo seat, CancellationToken ct)
    {
        if (seat.SessionId < 0)
            throw new InvalidOperationException(
                $"Seat {seat.Id} has no active session (SessionId={seat.SessionId}). " +
                "Launch a Windows session before starting Apollo.");

        if (!IsApolloInstalled)
        {
            _logger.LogWarning(
                "Apollo not found at {Path} — streaming will not work. " +
                "Install Apollo from https://github.com/ClassicOldSong/Apollo",
                _options.ApolloExePath);
            return -1;
        }

        // Generate per-seat configuration file
        var configPath = _configBuilder.BuildConfig(seat, _options.ApolloConfigDir);
        _logger.LogInformation(
            "Seat {Id}: Apollo config generated at {Config}", seat.Id, configPath);

        // Launch Apollo inside the seat's own Windows session.
        // Apollo (SudoMaker fork) connects to the SudoVDA IddCx driver via session-scoped
        // IPC — the SudoVDA watchdog aborts if the connection fails. The IPC works when
        // Apollo runs in the same session as the virtual display, not in the console session.
        // The seat's session was created by SessionLauncher and already has the virtual display.
        var pid = await _processInjector.LaunchApolloInSessionAsync(
            seat.SessionId, seat.AccountName,
            _options.ApolloExePath, configPath, ct);

        if (pid <= 0)
        {
            _logger.LogError("Seat {Id}: Apollo failed to start (PID={Pid})", seat.Id, pid);
            return pid;
        }

        var instance = new ApolloInstance(
            SeatId: seat.Id,
            ProcessId: pid,
            ConfigPath: configPath,
            SessionId: seat.SessionId,
            AccountName: seat.AccountName,
            StartedAt: DateTimeOffset.UtcNow,
            RestartCount: 0);

        _instances[seat.Id] = instance;

        _logger.LogInformation(
            "Seat {Id}: Apollo started (PID {Pid}) — Moonlight can connect on port {Port}",
            seat.Id, pid, seat.PortBase + 1);

        return pid;
    }

    /// <summary>
    /// Kill the Apollo process if running, but preserve the instance record
    /// (config path, seat ID) so <see cref="RestartAsync"/> can reuse it.
    /// Resets RestartCount to 0 — a sleep/reconnect is not a crash.
    /// </summary>
    public void KillForReconnect(SeatInfo seat)
    {
        if (!_instances.TryGetValue(seat.Id, out var instance))
            return;

        if (instance.ProcessId > 0)
        {
            try
            {
                var proc = Process.GetProcessById(instance.ProcessId);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
                _logger.LogInformation(
                    "Seat {Id}: Apollo killed before reconnect (PID {Pid})",
                    seat.Id, instance.ProcessId);
            }
            catch (ArgumentException) { } // already exited
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Seat {Id}: error killing Apollo before reconnect", seat.Id);
            }
        }

        // Reset restart count — a sleep reconnect is not a crash
        _instances[seat.Id] = instance with { ProcessId = 0, RestartCount = 0 };
    }

    /// <summary>
    /// Stop the Apollo instance for a seat. Kills the entire process tree
    /// (Apollo spawns encoder sub-processes).
    /// </summary>
    public void Stop(SeatInfo seat)
    {
        _instances.TryRemove(seat.Id, out _);

        if (seat.ApolloProcessId <= 0) return;

        try
        {
            var proc = Process.GetProcessById(seat.ApolloProcessId);
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            _logger.LogInformation("Seat {Id}: Apollo stopped (PID {Pid})",
                seat.Id, seat.ApolloProcessId);
        }
        catch (ArgumentException)
        {
            // Process already exited
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Seat {Id}: error stopping Apollo", seat.Id);
        }
    }

    /// <summary>
    /// Restart a crashed Apollo instance for a seat.
    /// Called by SessionHealthCheck when it detects Apollo is no longer running.
    /// </summary>
    public async Task<int> RestartAsync(SeatInfo seat, CancellationToken ct)
    {
        if (!_instances.TryGetValue(seat.Id, out var prev))
        {
            _logger.LogWarning(
                "Seat {Id}: no previous Apollo instance to restart", seat.Id);
            return await StartAsync(seat, ct);
        }

        if (prev.RestartCount >= MaxRestartAttempts)
        {
            _logger.LogError(
                "Seat {Id}: Apollo has crashed {Count} times — giving up. " +
                "Check {LogPath} for errors.",
                seat.Id, prev.RestartCount,
                ResolveLogPath(Path.GetDirectoryName(prev.ConfigPath)!));
            return -1;
        }

        _logger.LogWarning(
            "Seat {Id}: restarting Apollo (attempt {N}/{Max})",
            seat.Id, prev.RestartCount + 1, MaxRestartAttempts);

        // Re-use existing config — restart in the seat's own session (same as initial start)
        var pid = await _processInjector.LaunchApolloInSessionAsync(
            seat.SessionId, seat.AccountName,
            _options.ApolloExePath, prev.ConfigPath, ct);

        if (pid > 0)
        {
            _instances[seat.Id] = prev with
            {
                ProcessId = pid,
                StartedAt = DateTimeOffset.UtcNow,
                RestartCount = prev.RestartCount + 1,
                SessionId = seat.SessionId,
                AccountName = seat.AccountName
            };

            seat.ApolloProcessId = pid;
            _logger.LogInformation(
                "Seat {Id}: Apollo restarted (PID {Pid})", seat.Id, pid);
        }

        return pid;
    }

    /// <summary>
    /// Check if Apollo is running for a seat.
    /// </summary>
    public bool IsAlive(Guid seatId)
    {
        if (!_instances.TryGetValue(seatId, out var instance))
            return false;

        return instance.IsAlive;
    }

    /// <summary>
    /// Get the Apollo web UI URL for a seat (HTTPS).
    /// Used by the dashboard for seat management links.
    /// </summary>
    public string? GetWebUiUrl(SeatInfo seat)
    {
        if (seat.ApolloProcessId <= 0 || seat.PortBase <= 0)
            return null;

        // Apollo's 'port' config key = HTTP; HTTPS web UI = port + 1
        var httpsPort = seat.PortBase + 1;
        return $"https://localhost:{httpsPort}";
    }

    /// <summary>
    /// Get the config path for a seat's Apollo instance.
    /// </summary>
    public string? GetConfigPath(Guid seatId)
    {
        return _instances.TryGetValue(seatId, out var instance)
            ? instance.ConfigPath
            : null;
    }

    /// <summary>
    /// How long this seat's Apollo has been running, or null if we have no record of it.
    /// </summary>
    /// <remarks>
    /// Used to tell a crash apart from a failure to start. An Apollo that dies seconds after launch
    /// did not crash mid-stream — it failed to initialise, and the encoder is the usual reason.
    /// </remarks>
    public TimeSpan? GetUptime(Guid seatId)
    {
        return _instances.TryGetValue(seatId, out var instance)
            ? DateTimeOffset.UtcNow - instance.StartedAt
            : null;
    }

    /// <summary>
    /// Get the restart count for a seat's Apollo instance.
    /// </summary>
    public int GetRestartCount(Guid seatId)
    {
        return _instances.TryGetValue(seatId, out var instance)
            ? instance.RestartCount
            : 0;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSTANTS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Maximum number of automatic restart attempts before giving up.
    /// After this many crashes, the seat enters Error state and requires
    /// manual intervention (check Apollo logs).
    /// </summary>
    public const int MaxRestartAttempts = 3;

    /// <summary>
    /// Get the log file path for a seat's Apollo instance.
    /// </summary>
    public string GetLogPath(string accountName, string configDir)
    {
        var seatDir = Path.Combine(configDir, accountName);
        return ResolveLogPath(seatDir);
    }

    /// <summary>
    /// Resolve the log file a seat's streaming binary is actually writing.
    ///
    /// We ask for <c>&lt;seatDir&gt;/apollo.log</c> via the <c>log_path</c> config key
    /// (see ApolloConfigBuilder), but not every build honours it: Vibepollo ignores
    /// <c>log_path</c> and writes timestamped files to <c>&lt;seatDir&gt;\logs\apollo-&lt;stamp&gt;.log</c>
    /// instead. Hardcoding the requested name meant we read a file that never existed —
    /// which silently disabled SudoVDA display detection (so display isolation was always
    /// skipped) and launch-on-connect.
    ///
    /// So resolve by inspection rather than by assumption: take the newest non-empty
    /// <c>apollo*.log</c> from the seat root or its <c>logs\</c> subdirectory. That covers
    /// both layouts, and follows the current file across restarts and log rotation.
    /// Empty files are skipped deliberately — Vibepollo leaves a 0-byte file in the seat
    /// root while writing the real log under <c>logs\</c>.
    ///
    /// "Non-empty" is decided by <see cref="HasContent"/>, not by the directory entry —
    /// see there for why. Ordering still uses the entry's timestamp, which is safe: a log
    /// created later always sorts above one a previous run finished writing.
    ///
    /// Falls back to the requested path when nothing matches, so callers keep their
    /// existing "log not there yet" behaviour.
    /// </summary>
    public static string ResolveLogPath(string seatDir)
    {
        var requested = Path.Combine(seatDir, "apollo.log");

        try
        {
            // The seat directory is named for the account, so the owner we expect is derivable
            // without threading it through both call sites.
            var seatSid = ResolveAccountSid(Path.GetFileName(seatDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

            FileInfo? newest = null;
            foreach (var dir in new[] { seatDir, Path.Combine(seatDir, "logs") })
            {
                if (!Directory.Exists(dir)) continue;

                foreach (var candidate in new DirectoryInfo(dir).EnumerateFiles("apollo*.log"))
                {
                    if (!HasContent(candidate)) continue;
                    if (!IsTrustedLogOwner(OwnerOf(candidate), seatSid)) continue;
                    if (newest is null || candidate.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                        newest = candidate;
                }
            }

            if (newest is not null) return newest.FullName;
        }
        catch (IOException) { /* fall through to the requested path */ }
        catch (UnauthorizedAccessException) { /* fall through to the requested path */ }

        return requested;
    }

    /// <summary>
    /// True when a file genuinely holds bytes.
    ///
    /// <see cref="FileInfo.Length"/> reports the cached directory entry, and Windows does
    /// not refresh that on every write to an open file — a log being actively written can
    /// read 0 while holding thousands of bytes. Seen on the host: a live seat log read 0 at
    /// the exact moment the seat was provisioned and 21,144 two minutes later. Trusting it
    /// made <see cref="ResolveLogPath"/> skip the live log and hand callers a stale one from
    /// a previous run, so display detection and launch-on-connect read the wrong file.
    ///
    /// Ask the handle instead — it reports the true size. Share ReadWrite (and Delete)
    /// because the streaming binary holds the log open for writing the whole time.
    /// A file we cannot open is one we could not read later either, so it counts as empty.
    /// </summary>
    /// <summary>
    /// Whether a candidate log may be believed, judged by who owns it (GH #28).
    ///
    /// <see cref="ResolveLogPath"/> picks a log by filename pattern and write time, and
    /// <c>OnConnectAppLauncher</c> then acts on its "CLIENT CONNECTED" lines. Both of those are
    /// forgeable by anyone who can create a file in the seat directory. Ownership is not: a planted
    /// file is owned by whoever planted it.
    ///
    /// Only two accounts legitimately author these. Apollo runs as the seat, so the seat owns its
    /// own log; MultiSeat runs as SYSTEM, so SYSTEM owns anything the service creates.
    ///
    /// ⚠️ Fails OPEN, deliberately, on BOTH unknowns - an owner that cannot be read and a seat
    /// account that does not resolve. Excluding on doubt would hand back the requested path and
    /// silently blind display detection and launch-on-connect, which is the regression the pattern
    /// search exists to avoid in the first place. ⛔ Getting this wrong is not theoretical: an
    /// earlier draft filtered when only the owner was unknown, which excluded every candidate any
    /// time the account failed to resolve. Four ResolveLogPath tests caught it.
    ///
    /// The directory ACL applied in ApolloConfigBuilder is the control that actually closes GH #28.
    /// This is defence in depth, and it covers the window on an existing host before its seats next
    /// provision.
    /// </summary>
    internal static bool IsTrustedLogOwner(SecurityIdentifier? owner, SecurityIdentifier? seatSid)
    {
        // Both unknowns fail open, and for the same reason: filtering on a comparison we cannot
        // make would exclude every candidate and hand back the requested path, blinding display
        // detection and launch-on-connect on a host that has done nothing wrong.
        if (owner is null) return true;    // owner unreadable
        if (seatSid is null) return true;  // seat account did not resolve, so there is nothing to compare against

        if (owner.IsWellKnown(WellKnownSidType.LocalSystemSid)) return true;
        if (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) return true;

        return owner.Equals(seatSid);
    }

    /// <summary>Owner SID of a file, or null when it cannot be read.</summary>
    private static SecurityIdentifier? OwnerOf(FileInfo file)
    {
        try
        {
            return file.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Local account name to SID, or null when it does not resolve.</summary>
    private static SecurityIdentifier? ResolveAccountSid(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return null;
        try
        {
            return (SecurityIdentifier)new NTAccount(Environment.MachineName, accountName)
                .Translate(typeof(SecurityIdentifier));
        }
        catch (Exception) { return null; }
    }

    private static bool HasContent(FileInfo file)
    {
        try
        {
            using var fs = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return fs.Length > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Parse Apollo's startup log to find the SudoVDA virtual display device UUID.
    ///
    /// Apollo enumerates all displays at startup and writes a JSON block to the log:
    ///   "Currently available display devices:\n[{...}, {...}]"
    ///
    /// Each entry has a "device_id" (UUID like {f0cfefd7-...}) and
    /// "friendly_name". SudoVDA shows up as "VDD by MTT".
    ///
    /// We return device_id (UUID) for output_name. The UUID works reliably at
    /// both startup and stream LAUNCH time in the console session.
    /// The GDI display_name (\\.\DISPLAY37) does NOT work — Apollo falls back
    /// to the primary monitor when given a GDI path as output_name.
    ///
    /// Returns the UUID (e.g. "{f0cfefd7-be89-5733-a759-8fe046803517}") or null if not found.
    ///
    /// The 1000Hz fallback below is deliberately narrow. Inside an RDP-loopback seat the
    /// Microsoft RDP indirect display (RdpIdd) ALSO reports 1000Hz with edid=null and
    /// friendly_name="" — it is indistinguishable from SudoVDA on those fields alone. A
    /// naive "first 1000Hz display" match therefore returns the RDP surface and we hand it
    /// to Apollo as output_name, so the seat streams the host/RDP desktop at its own size
    /// (e.g. 3440x1440) while reporting success. Worse, on a host with no SudoVDA at all
    /// the fallback still "finds" something, masking the real fault.
    ///
    /// What separates them: SudoVDA is an ADDITIONAL display attached alongside the
    /// session's existing desktop, so at parse time it is never the only display and never
    /// the primary one (MultiSeat's display isolation makes it primary later, after this
    /// runs). The RDP surface is the session's primary. So the fallback requires a
    /// non-primary 1000Hz display AND more than one display present; otherwise we return
    /// null and let the caller's "no virtual display" path report the truth.
    /// </summary>
    public string? ParseSudoVdaDisplayId(string logPath)
    {
        if (!File.Exists(logPath)) return null;

        try
        {
            string text;
            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                text = sr.ReadToEnd();

            var result = ParseSudoVdaDisplayIdFromLogText(text);

            if (result.DeviceId != null)
            {
                if (result.FriendlyName != null)
                    _logger.LogInformation(
                        "Found SudoVDA display in Apollo log: {DeviceId} ({Name})",
                        result.DeviceId, result.FriendlyName);
                else
                    _logger.LogInformation(
                        "Found SudoVDA display by 1000Hz refresh rate (friendly_name was empty): {DeviceId}",
                        result.DeviceId);
                return result.DeviceId;
            }

            if (result.RejectedPrimaryOnly)
                _logger.LogWarning(
                    "No SudoVDA display in Apollo log: {Count} display(s) enumerated and the only " +
                    "1000Hz match was the session's primary display — that is the RDP surface, not " +
                    "a virtual display. Apollo created no virtual display for this seat.",
                    result.DisplayCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing Apollo log for SudoVDA display at {Path}", logPath);
        }

        return null;
    }

    /// <summary>Outcome of parsing Apollo's display-list JSON.</summary>
    /// <param name="DeviceId">The SudoVDA device UUID, or null when none was identified.</param>
    /// <param name="FriendlyName">Set when matched by name; null when matched by the 1000Hz fallback.</param>
    /// <param name="DisplayCount">How many displays Apollo enumerated.</param>
    /// <param name="RejectedPrimaryOnly">
    /// True when the only 1000Hz display was the session primary (the RDP surface) or was the
    /// lone display — i.e. we deliberately declined a match the old code would have accepted.
    /// </param>
    public readonly record struct SudoVdaParseResult(
        string? DeviceId,
        string? FriendlyName,
        int DisplayCount,
        bool RejectedPrimaryOnly);

    /// <summary>
    /// Pure parse of Apollo's "Currently available display devices:" JSON block.
    /// Public and static so the RdpIdd-vs-SudoVDA discrimination can be tested directly —
    /// same rationale as <see cref="ResolveLogPath"/>.
    /// </summary>
    public static SudoVdaParseResult ParseSudoVdaDisplayIdFromLogText(string text)
    {
        var none = new SudoVdaParseResult(null, null, 0, false);
        {
            var marker = "Currently available display devices:";
            var start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return none;

            var jsonStart = text.IndexOf('[', start);
            if (jsonStart < 0) return none;

            // Apollo writes the closing "]" on its own line
            var jsonEnd = text.IndexOf("\n]", jsonStart);
            if (jsonEnd < 0) return none;

            var json = text[jsonStart..(jsonEnd + 2)];

            // Walk through each display entry and find the SudoVDA one.
            // JSON field order: device_id → display_name → edid → friendly_name →
            //                   info { hdr_state, origin_point, primary, refresh_rate,
            //                          resolution, resolution_scale }
            //
            // Primary match: friendly_name contains "VDD", "SudoVDA", or "SudoMaker".
            // Fallback (see the remarks above): a NON-PRIMARY display at 1000Hz, and only
            // when more than one display is present.
            string? currentDeviceId = null;
            int currentRefreshNumerator = 0;
            bool currentIsPrimary = false;
            // "numerator" appears under both refresh_rate and resolution_scale; only the
            // one immediately following a "refresh_rate" key is the refresh rate.
            bool expectRefreshNumerator = false;

            var displayCount = 0;
            string? hz1000Candidate = null;
            var sawPrimaryHz1000 = false;

            // Close out the display entry we just finished parsing.
            void FinalizeEntry()
            {
                if (currentDeviceId == null) return;
                displayCount++;
                if (currentRefreshNumerator != 1000) return;
                if (currentIsPrimary)
                    sawPrimaryHz1000 = true;      // the RDP surface — never SudoVDA
                else
                    hz1000Candidate ??= currentDeviceId;
            }

            foreach (var line in json.Split('\n'))
            {
                var trimmed = line.Trim().TrimEnd(',');

                // New display object — finalize the previous entry first
                var deviceIdMatch = Regex.Match(trimmed,
                    @"""device_id""\s*:\s*""([^""]+)""");
                if (deviceIdMatch.Success)
                {
                    FinalizeEntry();

                    currentDeviceId = deviceIdMatch.Groups[1].Value;
                    currentRefreshNumerator = 0;
                    currentIsPrimary = false;
                    expectRefreshNumerator = false;
                    continue;
                }

                if (currentDeviceId == null) continue;

                // Check friendly_name — allow empty string ([^"]* not [^"]+)
                var nameMatch = Regex.Match(trimmed,
                    @"""friendly_name""\s*:\s*""([^""]*)""");
                if (nameMatch.Success)
                {
                    var friendlyName = nameMatch.Groups[1].Value;
                    if (IsSudoVdaFriendlyName(friendlyName))
                        return new SudoVdaParseResult(
                            currentDeviceId, friendlyName, displayCount + 1, false);
                    continue;
                }

                if (Regex.IsMatch(trimmed, @"""primary""\s*:\s*true"))
                {
                    currentIsPrimary = true;
                    continue;
                }

                if (trimmed.Contains("\"refresh_rate\"", StringComparison.Ordinal))
                {
                    expectRefreshNumerator = true;
                    continue;
                }

                var numeratorMatch = Regex.Match(trimmed, @"""numerator""\s*:\s*(\d+)");
                if (numeratorMatch.Success && expectRefreshNumerator &&
                    int.TryParse(numeratorMatch.Groups[1].Value, out var num))
                {
                    currentRefreshNumerator = num;
                    expectRefreshNumerator = false;
                }
            }

            FinalizeEntry();

            // Require a second display: SudoVDA is attached ALONGSIDE the session desktop,
            // so a lone display can only be that desktop.
            if (hz1000Candidate != null && displayCount > 1)
                return new SudoVdaParseResult(hz1000Candidate, null, displayCount, false);

            // Nothing usable. Flag the case where the old code would have returned the
            // RDP surface, so the caller can say so instead of silently finding nothing.
            return new SudoVdaParseResult(
                null, null, displayCount,
                RejectedPrimaryOnly: sawPrimaryHz1000 || hz1000Candidate != null);
        }
    }

    private static bool IsSudoVdaFriendlyName(string name) =>
        name.Contains("VDD", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("SudoVDA", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("SudoMaker", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Tracks a running Apollo instance for a seat.
/// </summary>
internal sealed record ApolloInstance(
    Guid SeatId,
    int ProcessId,
    string ConfigPath,
    int SessionId,
    string AccountName,
    DateTimeOffset StartedAt,
    int RestartCount)
{
    /// <summary>
    /// Check if the Apollo process is still running.
    /// </summary>
    public bool IsAlive
    {
        get
        {
            if (ProcessId <= 0) return false;
            try
            {
                using var proc = Process.GetProcessById(ProcessId);
                return !proc.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
