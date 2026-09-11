using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Streaming;

/// <summary>
/// Launches configured apps into a seat's session when a Moonlight client connects,
/// and (optionally) kills them on disconnect.
///
/// Why this exists:
///   Apollo creates the virtual Xbox 360 controller (ViGEm) only while a client is
///   streaming. Game launchers like Steam Big Picture and EmulationStation enumerate
///   controllers at launch and do not reliably hot-plug-detect a pad that appears
///   afterwards. If such a launcher is autostarted at login it runs before any stream
///   and never sees the pad. Launching it AFTER the client connects (when the pad
///   exists) makes its startup scan pick the controller up.
///
/// Detection:
///   Apollo's per-seat log (apollo.log) emits "CLIENT CONNECTED" / "CLIENT DISCONNECTED"
///   lines. This watcher tails that log on each health-check tick, tracks the connected
///   state per seat, and fires on the transitions.
///
/// Configured via MultiSeat:LaunchOnConnect in appsettings.json. Empty = disabled.
/// </summary>
public sealed class OnConnectAppLauncher
{
    private const string ConnectedMarker = "CLIENT CONNECTED";
    private const string DisconnectedMarker = "CLIENT DISCONNECTED";

    // Longest marker we scan for; we retain this-many-minus-one chars between reads so a
    // marker straddling a tick boundary is reassembled on the next read and still detected.
    // internal so tests can compute split points from the real value instead of hardcoding it.
    internal static readonly int MaxMarkerLen =
        Math.Max(ConnectedMarker.Length, DisconnectedMarker.Length);

    private readonly ILogger<OnConnectAppLauncher> _logger;
    private readonly MultiSeatOptions _options;
    private readonly ApolloManager _apollo;
    private readonly ProcessInjector _injector;

    private readonly ConcurrentDictionary<Guid, SeatConnState> _states = new();

    public OnConnectAppLauncher(
        ILogger<OnConnectAppLauncher> logger,
        IOptions<MultiSeatOptions> options,
        ApolloManager apollo,
        ProcessInjector injector)
    {
        _logger = logger;
        _options = options.Value;
        _apollo = apollo;
        _injector = injector;
    }

    /// <summary>
    /// Inspect a seat's Apollo log for new connect/disconnect events and act on edges: move the
    /// seat between Ready and Streaming, and launch or kill the configured apps.
    ///
    /// Returns true when the seat's status changed, so the caller can broadcast it.
    ///
    /// ⭐ Detection is NOT gated on MultiSeat:LaunchOnConnect. It used to be — this method opened
    /// with <c>if (_options.LaunchOnConnect.Length == 0) return;</c>, which disabled the whole
    /// watcher including the connect/disconnect reading. Since that option is empty by default,
    /// nothing on a default host ever observed a client connecting, and a seat streaming happily
    /// still reported Ready: the dashboard showed it idle, and anything deciding whether a seat
    /// was safe to tear down got the wrong answer. See issue #43.
    ///
    /// Launching apps stays gated; only the observation is unconditional. Cheap either way — it
    /// reads just the bytes appended since the previous tick.
    /// </summary>
    public bool ProcessSeat(SeatInfo seat, CancellationToken ct)
    {
        if (seat.SessionId < 0) return false;

        var logPath = _apollo.GetLogPath(seat.AccountName, _options.ApolloConfigDir);
        if (!File.Exists(logPath)) return false;

        var state = _states.GetOrAdd(seat.Id, _ => SeedState(logPath));

        // The resolved log can change under us — Apollo restarting produces a new timestamped
        // file (see ApolloManager.ResolveLogPath). A byte offset into the old file means
        // nothing in the new one, so re-seed against the new file rather than reading from a
        // stale position. ReadLatestState's rotation guard only catches shrinkage, and a new
        // log is usually longer than the old offset, so it would not fire here.
        if (!string.Equals(state.LogPath, logPath, StringComparison.OrdinalIgnoreCase))
        {
            lock (state.Gate)
            {
                state.LogPath = logPath;
                state.Offset  = 0;
                state.Carry   = string.Empty;
            }
        }

        bool? connectedNow = ReadLatestState(logPath, state);
        if (connectedNow is null) return false; // no new connect/disconnect lines since last tick

        lock (state.Gate)
        {
            if (connectedNow.Value == state.Connected) return false; // no edge
            state.Connected = connectedNow.Value;

            // Status first, and independently of the app feature. A client is attached or it is
            // not; whether anyone configured an app to launch has nothing to do with it.
            var statusChanged = ApplyStreamingStatus(seat, connectedNow.Value);

            if (_options.LaunchOnConnect.Length > 0)
            {
                if (connectedNow.Value)
                    OnConnect(seat, state, ct);
                else
                    OnDisconnect(seat, state);
            }

            return statusChanged;
        }
    }

    /// <summary>
    /// Move the seat between Ready and Streaming to match whether a client is attached.
    ///
    /// ⚠️ Only those two statuses are touched. A seat that is Provisioning, TearingDown or Error
    /// is mid-something that matters more than a client edge, and stamping Streaming over it would
    /// both lose that information and trip the transition table.
    /// </summary>
    private bool ApplyStreamingStatus(SeatInfo seat, bool clientConnected)
    {
        var target = clientConnected ? SeatStatus.Streaming : SeatStatus.Ready;

        if (seat.Status == target) return false;
        if (seat.Status is not (SeatStatus.Ready or SeatStatus.Streaming))
        {
            _logger.LogDebug(
                "Seat {Id}: client {Edge} while {Status} — leaving the status alone",
                seat.Id, clientConnected ? "connected" : "disconnected", seat.Status);
            return false;
        }

        seat.TransitionTo(target, _logger);
        _logger.LogInformation(
            "Seat {Id}: client {Edge} — now {Status}",
            seat.Id, clientConnected ? "connected" : "disconnected", target);
        return true;
    }

    /// <summary>Drop tracked state for a seat that has been torn down.</summary>
    public void Forget(Guid seatId) => _states.TryRemove(seatId, out _);

    // ── Edge handlers (called under state.Gate) ──────────────────────────

    private void OnConnect(SeatInfo seat, SeatConnState state, CancellationToken ct)
    {
        // Avoid duplicate launches: skip if a launch is already in flight, or if the
        // apps we launched on a previous connect are still running (KillOnDisconnect=false).
        if (state.Launching)
        {
            _logger.LogDebug("Seat {Id}: launch-on-connect already in progress — skipping", seat.Id);
            return;
        }
        if (AnyTrackedAppAlive(state))
        {
            _logger.LogDebug(
                "Seat {Id}: launch-on-connect apps still running — reusing, not relaunching", seat.Id);
            return;
        }

        state.Launching = true;
        var sessionId = seat.SessionId;
        var account = seat.AccountName;
        var seatId = seat.Id;

        // Run detached so the per-launch settle delay never stalls the health-check loop.
        _ = Task.Run(async () =>
        {
            try
            {
                if (_options.LaunchOnConnectDelayMs > 0)
                    await Task.Delay(_options.LaunchOnConnectDelayMs, ct);

                foreach (var app in _options.LaunchOnConnect)
                {
                    if (string.IsNullOrWhiteSpace(app.Path)) continue;
                    try
                    {
                        var pid = await _injector.LaunchInSessionAsync(
                            sessionId, account, app.Path, app.Arguments, app.WorkingDirectory, ct);
                        if (pid > 0)
                        {
                            lock (state.Gate) state.LaunchedPids.Add(pid);
                            _logger.LogInformation(
                                "Seat {Id}: launched on-connect app '{Exe}' (PID {Pid})",
                                seatId, app.Path, pid);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Seat {Id}: failed to launch on-connect app '{Exe}'", seatId, app.Path);
                    }
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            finally
            {
                lock (state.Gate) state.Launching = false;
            }
        }, ct);
    }

    private void OnDisconnect(SeatInfo seat, SeatConnState state)
    {
        if (!_options.KillLaunchOnConnectAppsOnDisconnect)
        {
            _logger.LogDebug("Seat {Id}: client disconnected — leaving on-connect apps running", seat.Id);
            return;
        }

        foreach (var pid in state.LaunchedPids)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
                _logger.LogInformation(
                    "Seat {Id}: killed on-connect app (PID {Pid}) after client disconnect", seat.Id, pid);
            }
            catch (ArgumentException) { /* already gone */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Seat {Id}: failed to kill on-connect app PID {Pid}", seat.Id, pid);
            }
        }
        state.LaunchedPids.Clear();
    }

    // ── Log tailing ──────────────────────────────────────────────────────

    /// <summary>
    /// Seed per-seat state from the existing log so we don't replay historical
    /// connect/disconnect events: start reading at end-of-file, and infer the current
    /// connected state from the last marker already present in the file.
    /// </summary>
    internal static SeatConnState SeedState(string logPath)
    {
        var state = new SeatConnState { LogPath = logPath };
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var text = ReadAllText(fs);
            state.Offset = fs.Length;
            state.Connected = LastMarkerIsConnected(text) ?? false;
        }
        catch (IOException) { /* leave defaults: offset 0, disconnected */ }
        return state;
    }

    /// <summary>
    /// Read log bytes appended since the last tick and return the seat's connected
    /// state if a connect/disconnect line appeared, otherwise null (no change to report).
    /// </summary>
    internal static bool? ReadLatestState(string logPath, SeatConnState state)
    {
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = fs.Length;
            if (state.Offset > len) { state.Offset = 0; state.Carry = string.Empty; } // rotated/truncated
            if (len == state.Offset) return null;     // nothing new

            fs.Seek(state.Offset, SeekOrigin.Begin);
            var buffer = new byte[len - state.Offset];
            fs.ReadExactly(buffer, 0, buffer.Length); // exactly len-Offset bytes exist — no partial read
            state.Offset = len;

            // Prepend the carry from the previous tick so a marker split across the boundary is
            // reassembled, then retain a fresh tail for next time.
            var text = state.Carry + Encoding.UTF8.GetString(buffer, 0, buffer.Length);
            state.Carry = text.Length > MaxMarkerLen - 1 ? text[^(MaxMarkerLen - 1)..] : text;

            return LastMarkerIsConnected(text);
        }
        catch (IOException)
        {
            return null; // transient — try again next tick
        }
    }

    /// <summary>
    /// True if the last connect/disconnect marker in <paramref name="text"/> is a
    /// connect, false if a disconnect, null if neither marker is present.
    /// </summary>
    internal static bool? LastMarkerIsConnected(string text)
    {
        var c = text.LastIndexOf(ConnectedMarker, StringComparison.Ordinal);
        var d = text.LastIndexOf(DisconnectedMarker, StringComparison.Ordinal);
        if (c < 0 && d < 0) return null;
        return c > d; // "CLIENT DISCONNECTED" does not contain "CLIENT CONNECTED", so these are distinct
    }

    private static string ReadAllText(FileStream fs)
    {
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return sr.ReadToEnd();
    }

    private static bool AnyTrackedAppAlive(SeatConnState state)
    {
        foreach (var pid in state.LaunchedPids)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited) return true;
            }
            catch (ArgumentException) { /* gone */ }
        }
        return false;
    }

    internal sealed class SeatConnState
    {
        public readonly object Gate = new();
        public long Offset;
        public bool Connected;
        public bool Launching;
        // The log file Offset refers to. Reset the offset when this changes.
        public string LogPath = string.Empty;
        // Tail of the previous read, retained so a marker split across a tick boundary is
        // still detected when the next chunk arrives.
        public string Carry = string.Empty;
        public readonly List<int> LaunchedPids = [];
    }
}
