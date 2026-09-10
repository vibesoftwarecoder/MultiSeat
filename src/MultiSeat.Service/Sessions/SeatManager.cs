using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Accounts;
using MultiSeat.Service.Api;
using MultiSeat.Service.Audio;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Display;
using MultiSeat.Service.Emulators;
using MultiSeat.Service.Input;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Sessions;

/// <summary>
/// Top-level orchestrator for seat lifecycle.
/// Coordinates all subsystems to provision, configure, and tear down seats.
///
/// Provisioning pipeline (order matters — each step depends on previous):
///   1. Validate capacity + account
///   2. Allocate port block
///   3. Launch background Windows session
///   4. Create virtual display (SudoVDA)
///   5. Open firewall ports
///   6. Start Apollo streaming server (needs display + ports)
///   7. Assign VAC audio cable + update Apollo config
///   8. Create ViGEm controller + HidHide cloaking
///   9. Broadcast Ready state to WebSocket clients
///
/// Teardown is reverse order with best-effort exception handling.
/// </summary>
public sealed class SeatManager
{
    private readonly ConcurrentDictionary<Guid, SeatInfo> _seats = new();
    private readonly ILogger<SeatManager> _logger;
    private readonly MultiSeatOptions _options;
    private readonly AccountManager _accounts;
    private readonly SessionLauncher _sessionLauncher;
    private readonly ProcessInjector _processInjector;
    private readonly VirtualDisplayManager _displayManager;
    private readonly ApolloManager _apolloManager;
    private readonly ApolloConfigBuilder _configBuilder;
    private readonly PortAllocator _portAllocator;
    private readonly FirewallManager _firewall;
    private readonly AudioRouter _audioRouter;
    private readonly ControllerManager _controllerManager;
    private readonly InputRouter _inputRouter;
    private readonly InputHookManager _inputHookManager;
    private readonly HidHideConfigurator _hidHide;
    private readonly OnConnectAppLauncher _onConnectApps;
    private readonly Monitoring.ApolloServerQuery _serverQuery;
    private readonly IEnumerable<IEmulatorConfigSeeder> _emulatorSeeders;
    private readonly SeatLifecycleGate _lifecycleGate;

    public SeatManager(
        ILogger<SeatManager> logger,
        IOptions<MultiSeatOptions> options,
        AccountManager accounts,
        SessionLauncher sessionLauncher,
        ProcessInjector processInjector,
        VirtualDisplayManager displayManager,
        ApolloManager apolloManager,
        ApolloConfigBuilder configBuilder,
        PortAllocator portAllocator,
        FirewallManager firewall,
        AudioRouter audioRouter,
        ControllerManager controllerManager,
        InputRouter inputRouter,
        InputHookManager inputHookManager,
        HidHideConfigurator hidHide,
        OnConnectAppLauncher onConnectApps,
        Monitoring.ApolloServerQuery serverQuery,
        IEnumerable<IEmulatorConfigSeeder> emulatorSeeders,
        SeatLifecycleGate lifecycleGate)
    {
        _logger = logger;
        _options = options.Value;
        _accounts = accounts;
        _sessionLauncher = sessionLauncher;
        _processInjector = processInjector;
        _displayManager = displayManager;
        _apolloManager = apolloManager;
        _configBuilder = configBuilder;
        _portAllocator = portAllocator;
        _firewall = firewall;
        _audioRouter = audioRouter;
        _controllerManager = controllerManager;
        _inputRouter = inputRouter;
        _inputHookManager = inputHookManager;
        _hidHide = hidHide;
        _onConnectApps = onConnectApps;
        _serverQuery = serverQuery;
        _emulatorSeeders = emulatorSeeders;
        _lifecycleGate = lifecycleGate;
    }

    // Guards the account-ownership critical section in ProvisionSeatAsync (dedup check +
    // seat registration). It is held only for that short check-and-insert — never across the
    // long-running provisioning work, which serializes per seat via SeatLifecycleGate. The
    // seat registry itself is concurrent, so this lock exists only to make
    // "is AccountName free?" + "register seat" atomic: two concurrent provisions for the same
    // account must not both pass the check and register.
    private readonly object _accountOwnershipLock = new();

    /// <summary>
    /// True when any seat in <paramref name="seats"/> occupies <paramref name="accountName"/> —
    /// i.e. is live or currently provisioning that account. Mirrors <see cref="ActiveSeatCount"/>'s
    /// notion of "live": Idle entries were never provisioned and Error entries hold no resources
    /// (their ports/sessions were released on failure), so neither blocks a fresh provision of the
    /// same account. Comparison is case-insensitive because Windows account names are, and the
    /// per-account config directory (ApolloConfigBuilder) is case-insensitive on NTFS.
    /// </summary>
    internal static bool AccountNameHasLiveSeat(IEnumerable<SeatInfo> seats, string accountName) =>
        seats.Any(s => s.Status is not (SeatStatus.Idle or SeatStatus.Error)
            && string.Equals(s.AccountName, accountName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Atomically register <paramref name="seat"/> in <paramref name="seats"/> unless another
    /// live/provisioning seat already occupies its AccountName. The check and the insert run
    /// under <paramref name="ownershipLock"/> — a lock covering only this short ownership
    /// decision, never the long provisioning work — so two concurrent provisions of the same
    /// account cannot both register. Returns true when the seat was registered.
    /// </summary>
    internal static bool TryRegisterSeat(
        ConcurrentDictionary<Guid, SeatInfo> seats, object ownershipLock, SeatInfo seat)
    {
        lock (ownershipLock)
        {
            if (AccountNameHasLiveSeat(seats.Values, seat.AccountName))
                return false;

            return seats.TryAdd(seat.Id, seat);
        }
    }

    public int ActiveSeatCount => _seats.Count(s => s.Value.Status is not SeatStatus.Idle and not SeatStatus.Error);
    public IReadOnlyCollection<SeatInfo> GetAllSeats() => _seats.Values.ToList().AsReadOnly();
    public SeatInfo? GetSeat(Guid id) => _seats.GetValueOrDefault(id);

    /// <summary>
    /// Re-resolve a seat AFTER the lifecycle gate has been acquired, returning null when it is
    /// gone or on its way out.
    ///
    /// ⭐ Every gated operation must call this. Resolving the seat before <c>AcquireAsync</c> and
    /// then acting on that reference is a time-of-check/time-of-use race: the gate can be held by
    /// a teardown for up to <see cref="SeatLifecycleGate.DefaultAcquisitionTimeout"/>, and the
    /// seat we captured may not exist by the time we are let in. Acting on it then recreates the
    /// very things teardown just destroyed — a Windows session, an mstsc, an Apollo, a display
    /// assignment — with nothing left in <c>_seats</c> that would ever clean them up. An orphan
    /// created this way is invisible: no seat owns it, so no teardown reaches it.
    ///
    /// The captured reference is deliberately NOT reused. A stale <see cref="SeatInfo"/> can
    /// still be mutated and still looks healthy, which is what makes this class of bug quiet.
    /// </summary>
    private SeatInfo? LiveSeatAfterGate(Guid seatId, string operation)
    {
        var (seat, reason) = ResolveLiveSeat(_seats, seatId);

        if (reason is not null)
            _logger.LogInformation(
                "Seat {Id}: {Operation} abandoned — {Reason}", seatId, operation, reason);

        return seat;
    }

    /// <summary>
    /// The decision behind <see cref="LiveSeatAfterGate"/>, as a pure function of the registry so
    /// it can be tested without a constructed <see cref="SeatManager"/> — the same shape as
    /// <see cref="TryRegisterSeat"/>. Returns the seat, or null plus why it was rejected.
    /// </summary>
    internal static (SeatInfo? Seat, string? Reason) ResolveLiveSeat(
        ConcurrentDictionary<Guid, SeatInfo> seats, Guid seatId)
    {
        if (!seats.TryGetValue(seatId, out var seat))
            return (null, "the seat was torn down while this operation waited for the lifecycle gate");

        if (seat.Status == SeatStatus.TearingDown)
            return (null, "the seat is tearing down");

        return (seat, null);
    }

    /// <summary>
    /// Put a pre-built seat straight into the registry.
    ///
    /// ⚠️ Exists for tests. The registry is otherwise only written by the full provisioning
    /// pipeline, which needs a real Windows session, a virtual display and a running Apollo —
    /// none of which exist on a build agent. Nothing in production calls this.
    /// </summary>
    internal void RegisterSeatDirect(SeatInfo seat) => _seats[seat.Id] = seat;

    /// <summary>
    /// Full seat provisioning pipeline.
    /// </summary>
    public async Task<SeatInfo> ProvisionSeatAsync(SeatRequest request, CancellationToken ct)
    {
        // Count only live seats — Error/Idle entries hold no resources (their ports and
        // sessions were already released on failure) and must not block new provisioning.
        if (ActiveSeatCount >= _options.MaxSeats)
            throw new CapacityExhaustedException($"Maximum seat count ({_options.MaxSeats}) reached.");

        if (!_accounts.AccountExists(request.AccountName))
            throw new InvalidOperationException($"Account '{request.AccountName}' does not exist. Create it first via /api/accounts.");

        // Correct the account's groups before the session is created, so a seat provisioned by an
        // older build stops being a local administrator and gains the Remote Desktop Users
        // membership the RDP loopback logon needs. Idempotent, and a no-op for linked accounts.
        _accounts.ApplySeatGroupMembership(request.AccountName);

        var seat = new SeatInfo
        {
            AccountName = request.AccountName,
            Width = request.Width,
            Height = request.Height,
            Fps = request.Fps,
            LaunchApp = request.LaunchApp,
            NvencPreset = request.NvencPreset,
            Status = SeatStatus.Provisioning,
            ProvisioningStep = "Session"
        };

        // Register the seat under the account-ownership lock so the "already provisioned?"
        // check and the dictionary insert are one atomic step. At most one live/provisioning
        // seat may exist per AccountName: the per-account Apollo config directory, log, and
        // sunshine_state.json are keyed by AccountName (not seat id), so a second live seat
        // for the same account would share them — last-writer-wins config, and the first
        // seat's later restart would re-read the second seat's ports.
        //
        // The per-seat SeatLifecycleGate cannot protect this: each provision creates a fresh
        // seat Guid, so two provisions of the same account hold different gates. This lock
        // covers only the ownership decision; it is released before any provisioning work.
        if (!TryRegisterSeat(_seats, _accountOwnershipLock, seat))
            throw new ResourceConflictException(
                $"Account '{request.AccountName}' already has a seat — tear it down first.");

        await BroadcastState(seat);

        // Serialize everything that follows against recovery/reconnect/teardown for this seat.
        // Acquired AFTER TryAdd so the id exists and a parallel caller waits rather than racing.
        using var lease = await _lifecycleGate.AcquireAsync(seat.Id, ct);

        try
        {
            // ── 1. Allocate ports ─────────────────────────────────────
            seat.PortBase = _portAllocator.Allocate();
            _logger.LogInformation("Seat {Id}: ports {Base}-{End}",
                seat.Id, seat.PortBase, seat.PortBase + Shared.Constants.PortsPerSeat - 1);

            // ── 1.5. Assign emulator netplay port from this seat's block ──
            // A free offset in the 30-port block gives each seat a unique, collision-free netplay
            // host port. Seats netplay each other over loopback (127.0.0.1:<this port>).
            if (_options.EnableEmulatorNetplay)
            {
                seat.RetroArchNetplayPort = seat.PortBase + Shared.Constants.OffsetRetroArchNetplay;
                _logger.LogInformation(
                    "Seat {Id}: RetroArch netplay host port {Port}", seat.Id, seat.RetroArchNetplayPort);
            }

            // ── 2. Launch background session ──────────────────────────
            // Pass the seat's resolution as the RDP geometry. The seat streams its RDP session
            // surface (there is no in-seat virtual display — issue #15), and that surface's size
            // is set by mstsc at connect time and cannot be changed from inside the session. So
            // this is what makes the dashboard resolution actually take effect; without it the
            // session inherits whatever size mstsc picks, which tracks the console desktop.
            seat.SessionId = await _sessionLauncher.LaunchSessionAsync(
                seat.AccountName, ct, RdpGeometry.ForClient(seat.Width, seat.Height));
            _logger.LogInformation("Seat {Id}: Windows session {Sid}", seat.Id, seat.SessionId);

            seat.TransitionTo(SeatStatus.Configuring, _logger);
            seat.ProvisioningStep = "Display";
            await BroadcastState(seat);

            // ── 2.5. Suppress RustDesk audio capture in seat session ──────────
            // RustDesk.exe runs in every session and opens the default render
            // endpoint in exclusive WASAPI mode at startup, causing
            // AUDCLNT_E_DEVICE_IN_USE (0x8889000A) for Apollo's loopback.
            // Write a per-user RustDesk2.toml with enable-audio=N before the
            // audio default is set, then kill any RustDesk that started before
            // the config landed. RustDesk re-reads config on each launch, so
            // the service's auto-restart will pick up the new setting.
            try
            {
                var rustDeskConfigDir = Path.Combine(
                    @"C:\Users", seat.AccountName,
                    @"AppData\Roaming\RustDesk\config");
                Directory.CreateDirectory(rustDeskConfigDir);
                var rustDeskConfig = Path.Combine(rustDeskConfigDir, "RustDesk2.toml");
                await File.WriteAllTextAsync(rustDeskConfig,
                    "[options]\nenable-audio = \"N\"\n", ct);
                _logger.LogInformation(
                    "Seat {Id}: wrote RustDesk audio-disable config to {Path}",
                    seat.Id, rustDeskConfig);

                var killed = 0;
                foreach (var p in Process.GetProcessesByName("RustDesk"))
                {
                    try
                    {
                        if (p.SessionId == seat.SessionId)
                        {
                            p.Kill();
                            killed++;
                        }
                    }
                    catch { /* already exited */ }
                    finally { p.Dispose(); }
                }
                if (killed > 0)
                    _logger.LogInformation(
                        "Seat {Id}: killed {N} RustDesk process(es) in session {Sid}",
                        seat.Id, killed, seat.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Seat {Id}: could not suppress RustDesk audio (non-critical)", seat.Id);
            }

            // ── 2.7. Pre-write gamepad jail rules ───────────────
            // Before Apollo exists, on purpose. HidHide filters at OPEN time, so a rule
            // written after the pad is created is late by definition - and dwm, explorer and
            // GameInputSvc of every session open each new pad inside that window and keep
            // handles that never expire. A rule for an absent device matches nothing, so this
            // is inert when the seat has no known pad path. No-op unless
            // EnableHidHideCloaking + EnablePadRulePreWrite are both on.
            try { _hidHide.PreWriteRules(seat); }
            catch (Exception ex) { _logger.LogWarning(ex, "Seat {Id}: pre-writing gamepad jail rules failed (non-critical)", seat.Id); }

            // ── 3. Virtual display ────────────────────────────────────
            await _displayManager.CreateDisplayAsync(seat, ct);
            _logger.LogDebug("Seat {Id}: VDA ready ({W}x{H}@{F})",
                seat.Id, seat.Width, seat.Height, seat.Fps);

            // ── 4. Firewall ───────────────────────────────────────────
            await _firewall.OpenPortsAsync(seat, ct);

            // ── 5. Audio routing ──────────────────────────────────────
            seat.ProvisioningStep = "Audio";
            await BroadcastState(seat);

            // PerSession needs no host-side audio device: the seat's RDP session has its own
            // "Remote Audio" endpoint and Apollo captures that. Skipping AssignCable is not just
            // an optimisation — it throws when no virtual cables are installed, and uninstalling
            // VB-CABLE/VoiceMeeter is a supported (indeed expected) state in this mode.
            if (_options.AudioMode == AudioMode.PerSession)
            {
                _logger.LogInformation(
                    "Seat {Id}: per-session audio — no virtual cable assigned; Apollo captures " +
                    "the session's own Remote Audio endpoint", seat.Id);
            }
            else
            {
                // Assign VAC before Apollo so the config has the audio device
                seat.VacCableIndex = _audioRouter.AssignCable(seat);
                _logger.LogDebug("Seat {Id}: VAC cable {C}", seat.Id, seat.VacCableIndex);
            }

            // ── 5.5. Set seat session default capture for mic routing ────
            // Apollo renders Moonlight mic audio into CABLE Input (virtual_sink) from
            // inside the seat session. CABLE Output (the capture counterpart) receives
            // that audio at the kernel WDM level — visible in the seat session as a
            // capture endpoint. Setting CABLE Output as the DEFAULT capture for THIS
            // seat session means games automatically use Moonlight mic without any
            // manual device selection.
            //   Moonlight mic → Apollo → CABLE Input → CABLE Output → games (session default)
            // Running inside the seat session scopes the IPolicyConfig call to that
            // session's HKCU, so multiple seats don't conflict with each other.
            if (!string.IsNullOrEmpty(seat.AudioCaptureDeviceId))
            {
                try
                {
                    var helperExe = Path.Combine(AppContext.BaseDirectory, "MultiSeat.Service.exe");
                    _sessionLauncher.RunHelperInSeatSession(
                        seat.SessionId, seat.AccountName,
                        $"\"{helperExe}\" --set-default-capture \"{seat.AudioCaptureDeviceId}\"");
                    _logger.LogInformation(
                        "Seat {Id}: session capture default set to {DeviceId} (mic)",
                        seat.Id, seat.AudioCaptureDeviceId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Seat {Id}: could not set session capture device (non-critical)", seat.Id);
                }
            }

            // NOTE: MultiSeat intentionally does NOT set the seat's game-audio device as the
            // session default render. The Windows default output device is machine-wide (shared
            // by the console and every seat), so doing so hijacked the host's audio (issue #10).
            // Apollo points the game at the seat's device itself via virtual_sink in sunshine.conf
            // (for the duration of the stream, restored afterwards) — see ApolloConfigBuilder.

            // ── 5.7. Seed emulator configs (opt-in, best-effort) ──────────
            // Write each enabled emulator's per-seat netplay config into the seat user's profile
            // (e.g. RetroArch netplay port + shared ROM dir). Mirrors the RustDesk seed above:
            // best-effort, never fails provisioning.
            foreach (var seeder in _emulatorSeeders)
            {
                if (!seeder.IsEnabled) continue;
                try
                {
                    await seeder.SeedAsync(seat, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Seat {Id}: {Emulator} config seed failed (non-critical)",
                        seat.Id, seeder.EmulatorName);
                }
            }

            // ── 6. Apollo streaming ───────────────────────────────────────
            // Apollo is launched AFTER display + audio so it can capture both.
            // The session is still ACTIVE (mstsc connected) so Apollo's SudoVDA IPC
            // can initialize the virtual display. Without an active session,
            // QueryDisplayConfig returns ERROR_ACCESS_DENIED and the encoder probe fails.
            seat.ProvisioningStep = "Apollo";
            await BroadcastState(seat);

            seat.ApolloProcessId = await _apolloManager.StartAsync(seat, ct);
            _logger.LogInformation("Seat {Id}: Apollo PID {Pid}", seat.Id, seat.ApolloProcessId);

            // ── 6.5: Discover SudoVDA UUID from Apollo's startup log ──────
            // Apollo enumerates displays at startup and writes device UUIDs to its log.
            // UUID (device_id) works at stream LAUNCH time; GDI path (\\.\DISPLAYx) causes
            // Apollo to fall back to the primary monitor.
            // After the first-pass probe completes, Apollo has cached encoder results.
            // The second start with UUID skips the full probe (uses cache), so the
            // SudoVDA IddCx watchdog has time to establish its connection properly.
            seat.ProvisioningStep = "DetectDisplay";
            await BroadcastState(seat);

            {
                var logPath = _apolloManager.GetLogPath(seat.AccountName, _options.ApolloConfigDir);
                var configPath = _apolloManager.GetConfigPath(seat.Id);

                // Wait for Apollo to initialize SudoVDA IPC and write its display log.
                // The session MUST stay ACTIVE (mstsc connected) — Apollo calls QueryDisplayConfig
                // both at startup AND when each Moonlight client connects. Disconnected sessions
                // return ERROR_ACCESS_DENIED, causing "Failed to initialize video capture/encoding".
                await Task.Delay(5000, ct);

                // NOTE: We intentionally do NOT disconnect mstsc here.
                // The session stays Active for the lifetime of the seat so Apollo can
                // always query and set display modes when clients connect.

                var displayId = _apolloManager.ParseSudoVdaDisplayId(logPath);
                if (displayId != null && configPath != null)
                {
                    seat.DisplayDevicePath = displayId;
                    _configBuilder.UpdateDisplayOutput(configPath, displayId);

                    _logger.LogInformation(
                        "Seat {Id}: SudoVDA UUID discovered ({Dev}) — restarting Apollo with display target",
                        seat.Id, displayId);

                    // Restart Apollo with the correct output_name (UUID).
                    // Brief delay to let Apollo finish writing logs before we kill it.
                    _apolloManager.Stop(seat);
                    await Task.Delay(2000, ct);
                    seat.ApolloProcessId = await _apolloManager.StartAsync(seat, ct);

                    // ── 6.6/6.7: Display isolation + refresh-rate clamp ─────
                    await ApplyDisplayIsolationAsync(seat, ct);
                }
                else
                {
                    // Not a fault, and deliberately not a warning. Apollo does not create the
                    // seat's virtual display at startup — it creates it when a client connects
                    // and launches an app — so there is nothing to find at provisioning time on
                    // ANY host. TryLateDisplayDetectionAsync retries from the health-check tick
                    // and applies isolation if it ever appears. The old text here claimed the
                    // seat would "capture the primary monitor instead", which read as a broken
                    // install and cost issue #15's reporter two days of driver debugging.
                    _logger.LogDebug(
                        "Seat {Id}: no virtual display in the Apollo log yet — expected at this " +
                        "point; Apollo creates one on client connect and the health check retries",
                        seat.Id);
                }
            }

            // ── 7. Controller + Input Routing ────────────────────────────
            // Only create a MultiSeat-managed ViGEm controller when explicitly enabled.
            // Apollo already handles controller forwarding from Moonlight clients natively
            // (controller = enabled / gamepad = auto in sunshine.conf). Creating a second
            // ViGEm controller here causes duplicate Xbox controllers in the session.
            if (_options.EnableViGEmController)
            {
                seat.ViGEmControllerIndex = _controllerManager.CreateController(seat);
                _logger.LogDebug("Seat {Id}: ViGEm controller {C}", seat.Id, seat.ViGEmControllerIndex);

                if (_options.AutoAssignControllers)
                {
                    var connected = _inputRouter.GetConnectedControllers();
                    var assigned = _inputRouter.GetAssignments();
                    var freeIdx = connected.FirstOrDefault(idx => !assigned.ContainsKey(idx), -1);
                    if (freeIdx >= 0)
                    {
                        _inputRouter.AssignController(freeIdx, seat.Id);
                        _logger.LogInformation("Seat {Id}: auto-assigned XInput {Idx}", seat.Id, freeIdx);
                    }
                }
            }
            else
            {
                _logger.LogDebug("Seat {Id}: ViGEm controller skipped — Apollo handles Moonlight client input natively", seat.Id);
            }

            // ── 8. HidHide + Keyboard/Mouse Hooks ──────────────────────
            _hidHide.CloakForSession(seat);

            // Install keyboard/mouse hooks to filter input for this session
            _inputHookManager.InstallForSession((uint)seat.SessionId);

            // ── 9. Ready ──────────────────────────────────────────────
            seat.TransitionTo(SeatStatus.Ready, _logger);
            seat.ReadyAt = DateTimeOffset.UtcNow;
            seat.ProvisioningStep = null;
            await BroadcastState(seat);
            _logger.LogInformation(
                "Seat {Id}: READY for Moonlight connection on port {P}",
                seat.Id, seat.PortBase + Shared.Constants.OffsetGfeHttp);

            return seat;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seat {Id}: provisioning failed at {Status}", seat.Id, seat.Status);
            seat.TransitionTo(SeatStatus.Error, _logger);
            seat.ErrorMessage = ex.Message;
            await BroadcastState(seat);

            // Best-effort teardown of whatever was already provisioned
            await TeardownSeatInternalAsync(seat, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Launch an application inside an active seat's session.
    /// </summary>
    public async Task LaunchAppInSeatAsync(Guid seatId, LaunchAppRequest request, CancellationToken ct)
    {
        if (GetSeat(seatId) is null)
            throw new SeatNotFoundException();

        // Without the gate, a teardown could remove the seat — disconnecting and logging off its
        // session — between the status check and the process creation below, orphaning the app in
        // a session nothing in _seats would ever tear down.
        using var lease = await _lifecycleGate.AcquireAsync(seatId, ct);

        var seat = LiveSeatAfterGate(seatId, "app launch");
        if (seat is null)
            throw new SeatNotFoundException();

        if (seat.Status is not SeatStatus.Ready and not SeatStatus.Streaming)
            throw new ResourceConflictException($"Seat is in {seat.Status} state — cannot launch apps.");

        await _processInjector.LaunchInSessionAsync(
            seat.SessionId, seat.AccountName,
            request.ExecutablePath, request.Arguments, request.WorkingDirectory, ct);

        seat.TransitionTo(SeatStatus.Streaming, _logger);
        seat.LaunchApp = request.ExecutablePath;
        await BroadcastState(seat);
    }

    /// <summary>
    /// Teardown a single seat — reverse order of provisioning.
    /// </summary>
    public Task TeardownSeatAsync(Guid seatId, CancellationToken ct) =>
        TeardownSeatAsync(seatId, SeatLifecycleGate.DefaultAcquisitionTimeout, ct);

    /// <summary>
    /// As <see cref="TeardownSeatAsync(Guid, CancellationToken)"/>, with the gate wait exposed so
    /// the timeout path can be tested without a 30-second test. Production always uses the
    /// default; this overload exists so the ordering guarantee is pinned by the REAL method
    /// rather than by a copy of it in a test.
    /// </summary>
    internal async Task TeardownSeatAsync(Guid seatId, TimeSpan gateTimeout, CancellationToken ct)
    {
        if (GetSeat(seatId) is null)
            return;

        // ⛔ Acquire the gate BEFORE removing the seat from _seats, not after.
        //
        // The reverse order looks harmless and is not: AcquireAsync throws TimeoutException after
        // DefaultAcquisitionTimeout when another lifecycle operation holds the gate. Removing
        // first meant that timeout left the seat OUT of the registry with its session, mstsc,
        // Apollo and ports all still alive — an orphan nothing owned and no retry could reach,
        // because every path into teardown starts by looking the seat up.
        //
        // Gate first, and a timeout propagates with the seat still registered and its status
        // untouched, so the caller can simply try again.
        //
        // CancellationToken.None deliberately: a teardown that abandons half its cleanup leaks a
        // session, an mstsc and a virtual display.
        using var lease = await _lifecycleGate.AcquireAsync(
            seatId, gateTimeout, CancellationToken.None);

        // Only now is removal safe. Double teardown stays a no-op: the gate serialises the two
        // callers and the loser finds the seat already gone.
        if (!_seats.TryRemove(seatId, out var seat))
            return;

        seat.TransitionTo(SeatStatus.TearingDown, _logger);
        await BroadcastState(seat);

        await TeardownSeatInternalAsync(seat, ct);
        _logger.LogInformation("Seat {Id}: torn down", seat.Id);
    }

    /// <summary>
    /// Teardown all seats — called on service shutdown.
    /// </summary>
    public async Task TeardownAllAsync(CancellationToken ct)
    {
        var ids = _seats.Keys.ToList();
        await Task.WhenAll(ids.Select(id => TeardownSeatAsync(id, ct)));
    }

    private async Task TeardownSeatInternalAsync(SeatInfo seat, CancellationToken ct)
    {
        // Reverse order of provisioning — each step is best-effort
        try { _onConnectApps.Forget(seat.Id); } catch { /* best effort */ }
        try { _inputHookManager.Uninstall(); } catch { /* best effort */ }
        try { _hidHide.UncloakForSession(seat); } catch { /* best effort */ }
        try { UnassignControllersForSeat(seat.Id); } catch { /* best effort */ }
        try { _controllerManager.DestroyController(seat); } catch { /* best effort */ }
        try { _apolloManager.Stop(seat); } catch { /* best effort */ }
        try { _audioRouter.ReleaseCable(seat); } catch { /* best effort */ }
        try { await _firewall.ClosePortsAsync(seat, ct); } catch { /* best effort */ }
        try { await _displayManager.DestroyDisplayAsync(seat, ct); } catch { /* best effort */ }
        try { _sessionLauncher.DisconnectSession(seat.SessionId); } catch { /* best effort */ }
        try { _sessionLauncher.LogoffSession(seat.SessionId); } catch { /* best effort */ }
        try { _portAllocator.Release(seat.PortBase); } catch { /* best effort */ }

        // Clean up per-seat Apollo config directory
        try { _configBuilder.CleanupConfig(seat.AccountName, _options.ApolloConfigDir); } catch { /* best effort */ }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PER-SEAT SERVICE MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get the live status of each subsystem for a seat.
    ///
    /// Async because it asks the seat's Apollo whether it actually answers, rather than only
    /// whether its process exists — the two differ exactly when a seat is broken in the way a
    /// user notices. Only queried when the process is alive and the seat has a port, so a torn
    /// down or provisioning seat costs nothing.
    /// </summary>
    public async Task<SeatServices> GetSeatServicesAsync(Guid seatId, CancellationToken ct = default)
    {
        var seat = GetSeat(seatId);
        if (seat is null) return new SeatServices();

        var apolloAlive = seat.ApolloProcessId > 0 && _apolloManager.IsAlive(seatId);

        Monitoring.ApolloServerInfo? server = null;
        if (apolloAlive && seat.PortBase > 0)
            server = await _serverQuery.QueryAsync(
                seat.PortBase + Shared.Constants.OffsetGfeHttp, ct);

        return new SeatServices
        {
            Apollo = apolloAlive,
            ApolloReachable = server is not null,
            ApolloStreaming = server?.Streaming ?? false,
            ApolloRestarts = _apolloManager.GetRestartCount(seatId),
            Display = !string.IsNullOrEmpty(seat.DisplayDevicePath),
            // PerSession: the endpoint is created by Windows with the session itself, so there
            // is no device assignment that could be missing — report healthy, and let
            // AudioManaged tell the dashboard not to read this as a device light.
            Audio = _options.AudioMode == AudioMode.PerSession || seat.VacCableIndex >= 0,
            AudioManaged = _options.AudioMode == AudioMode.SharedHost,
            Controller = seat.ViGEmControllerIndex >= 0,
            ControllerManaged = _options.EnableViGEmController,
            InputHooks = _inputHookManager.IsInstalled,
            Firewall = seat.PortBase > 0,
            Session = seat.SessionId >= 0
        };
    }

    /// <summary>Stop Apollo for a seat without tearing down everything else.</summary>
    public async Task StopApollo(Guid seatId)
    {
        if (GetSeat(seatId) is null)
            throw new SeatNotFoundException();

        // Mutates ApolloProcessId and the ApolloManager instance record.
        using var lease = await _lifecycleGate.AcquireAsync(seatId, CancellationToken.None);

        var seat = LiveSeatAfterGate(seatId, "Apollo stop");
        if (seat is null) return;

        _apolloManager.Stop(seat);
        seat.ApolloProcessId = 0;
        _ = BroadcastState(seat);
        _logger.LogInformation("Seat {Id}: Apollo stopped by user", seatId);
    }

    /// <summary>Start Apollo for a seat (must already have session + display).</summary>
    public async Task StartApolloAsync(Guid seatId, CancellationToken ct)
    {
        if (GetSeat(seatId) is null)
            throw new SeatNotFoundException();

        // Per-seat lifecycle gate: starts Apollo and mutates ApolloProcessId.
        using var lease = await _lifecycleGate.AcquireAsync(seatId, ct);

        var seat = LiveSeatAfterGate(seatId, "Apollo start");
        if (seat is null) return;

        if (seat.SessionId < 0)
            throw new InvalidOperationException("No active session — provision the seat first.");

        seat.ApolloProcessId = await _apolloManager.StartAsync(seat, ct);

        // Re-apply display config
        var configPath = _apolloManager.GetConfigPath(seat.Id);
        if (configPath is not null)
        {
            if (!string.IsNullOrEmpty(seat.DisplayDevicePath))
                _configBuilder.UpdateDisplayOutput(configPath, seat.DisplayDevicePath);
        }

        _ = BroadcastState(seat);
        _logger.LogInformation("Seat {Id}: Apollo started by user (PID {Pid})", seatId, seat.ApolloProcessId);
    }

    /// <summary>Restart Apollo for a seat (stop + start).</summary>
    public async Task RestartApolloAsync(Guid seatId, CancellationToken ct)
    {
        if (GetSeat(seatId) is null)
            throw new SeatNotFoundException();

        // Per-seat lifecycle gate: Stop + Start is one compound mutation, not two.
        using var lease = await _lifecycleGate.AcquireAsync(seatId, ct);

        var seat = LiveSeatAfterGate(seatId, "Apollo restart");
        if (seat is null) return;

        _apolloManager.Stop(seat);
        seat.ApolloProcessId = 0;

        seat.ApolloProcessId = await _apolloManager.StartAsync(seat, ct);

        var configPath = _apolloManager.GetConfigPath(seat.Id);
        if (configPath is not null)
        {
            if (!string.IsNullOrEmpty(seat.DisplayDevicePath))
                _configBuilder.UpdateDisplayOutput(configPath, seat.DisplayDevicePath);
        }

        if (seat.ApolloProcessId > 0)
            await ApplyDisplayIsolationAsync(seat, ct);

        _ = BroadcastState(seat);
        _logger.LogInformation("Seat {Id}: Apollo restarted by user (PID {Pid})", seatId, seat.ApolloProcessId);
    }

    /// <summary>
    /// Second chance at finding the seat's SudoVDA display, run from the health-check tick.
    ///
    /// Provisioning looks for the display ~5s after Apollo starts, but Apollo does not create
    /// one then. Creation happens in its <c>proc_t::execute()</c> — i.e. when a client connects
    /// and launches an app — gated on <c>headless_mode</c> (which ApolloConfigBuilder now sets).
    /// At provisioning time there is therefore nothing to find, DisplayDevicePath stays null,
    /// and display isolation is skipped for the seat's whole life, leaving TermService CPU high.
    ///
    /// So retry while the seat runs. Once Apollo creates the display it logs a fresh
    /// "Currently available display devices:" block, this picks it up, and isolation is applied.
    ///
    /// Deliberately does NOT restart Apollo the way the provisioning path does: a client is
    /// streaming by the time this succeeds, and Apollo has already pointed itself at the new
    /// display (it assigns config::video.output_name after creating it). Writing output_name to
    /// the config here only makes the next start target it directly.
    ///
    /// Returns true when the display was found and isolation was attempted.
    /// </summary>
    public async Task<bool> TryLateDisplayDetectionAsync(SeatInfo seat, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(seat.DisplayDevicePath)) return false; // already known
        if (seat.ApolloProcessId <= 0) return false;                     // Apollo not running

        string text;
        try
        {
            var logPath = _apolloManager.GetLogPath(seat.AccountName, _options.ApolloConfigDir);
            if (!File.Exists(logPath)) return false;

            // Apollo holds the log open, so share read AND write.
            using var fs = new FileStream(
                logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            text = await sr.ReadToEndAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Seat {Id}: late display detection could not read Apollo log", seat.Id);
            return false;
        }

        // ParseSudoVdaDisplayIdFromLogText matches the FIRST display block, which is Apollo's
        // startup enumeration — exactly the one that never contains the virtual display. Slice
        // from the last block so we parse Apollo's most recent view instead.
        const string marker = "Currently available display devices:";
        var last = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (last < 0) return false;

        var result = ApolloManager.ParseSudoVdaDisplayIdFromLogText(text[last..]);
        if (result.DeviceId is null) return false; // still nothing — stay quiet, we run every tick

        seat.DisplayDevicePath = result.DeviceId;

        var configPath = _apolloManager.GetConfigPath(seat.Id);
        if (configPath is not null)
            _configBuilder.UpdateDisplayOutput(configPath, result.DeviceId);

        _logger.LogInformation(
            "Seat {Id}: SudoVDA display found after client connect ({Dev}) — applying display isolation",
            seat.Id, result.DeviceId);

        await ApplyDisplayIsolationAsync(seat, ct);
        return true;
    }

    /// <summary>
    /// Make SudoVDA the session primary, shrink the RDP virtual display to 640×480,
    /// and clamp SudoVDA's refresh rate to seat.Fps. Runs inside the seat's RDP session
    /// via the --setup-display-isolation and --set-display-hz helper modes.
    ///
    /// This state does not survive a session disconnect (sleep/wake) or an Apollo restart,
    /// so this method is called from every code path that (re)starts Apollo:
    ///   - Initial provisioning (after the SudoVDA-output restart).
    ///   - User-triggered RestartApolloAsync.
    ///   - SessionHealthCheck after sleep-reconnect or crash auto-restart.
    ///
    /// Without re-applying after a wake event, SudoVDA stops being primary and the
    /// stream falls back to the Microsoft Remote Display Adapter at its default
    /// 1024×768 — even though Apollo logs request 1920×1080.
    /// Both steps are best-effort; failures are logged and ignored.
    /// </summary>
    public async Task ApplyDisplayIsolationAsync(SeatInfo seat, CancellationToken ct)
    {
        var helperExe = Path.Combine(AppContext.BaseDirectory, "MultiSeat.Service.exe");

        // Skip isolation entirely if we don't know which SudoVDA Apollo created — the helper
        // would otherwise risk grabbing an orphan SudoVDA attached to another session
        // (e.g. the console's RustDesk display) and dragging its resolution along with the seat's.
        if (string.IsNullOrEmpty(seat.DisplayDevicePath))
        {
            _logger.LogWarning(
                "Seat {Id}: skipping display isolation — DisplayDevicePath is unset, " +
                "TermService CPU may be elevated",
                seat.Id);
            return;
        }

        // Let Apollo + SudoVDA IPC settle so the helper sees both displays.
        await Task.Delay(2000, ct);
        try
        {
            _sessionLauncher.RunHelperInSeatSession(
                seat.SessionId, seat.AccountName,
                $"\"{helperExe}\" --setup-display-isolation \"{seat.DisplayDevicePath}\"");
            _logger.LogInformation(
                "Seat {Id}: display isolation applied — SudoVDA is primary, RDP display shrunk to 640×480",
                seat.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Seat {Id}: display isolation failed (non-critical — TermService CPU may be elevated)",
                seat.Id);
        }

        // SudoVDA is now primary, so ChangeDisplaySettingsEx(null,...) in the helper
        // targets it directly. Clamp Hz to seat.Fps so games don't try to render at 1000fps.
        await Task.Delay(500, ct);
        try
        {
            _sessionLauncher.RunHelperInSeatSession(
                seat.SessionId, seat.AccountName,
                $"\"{helperExe}\" --set-display-hz {seat.Fps}");
            _logger.LogInformation(
                "Seat {Id}: SudoVDA refresh rate set to {Hz}Hz",
                seat.Id, seat.Fps);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Seat {Id}: could not set SudoVDA refresh rate (non-critical)", seat.Id);
        }
    }

    /// <summary>Reset the audio routing for a seat (release + re-assign cable + re-apply session defaults).</summary>
    public async Task ResetAudioAsync(Guid seatId)
    {
        if (GetSeat(seatId) is null)
            throw new SeatNotFoundException();

        // Nothing to reset under per-session audio: MultiSeat assigns no device, and the
        // session's Remote Audio endpoint lives and dies with the session itself. Re-assigning
        // here would throw on a host that has (legitimately) no virtual cables installed.
        if (_options.AudioMode == AudioMode.PerSession)
        {
            _logger.LogInformation(
                "Seat {Id}: audio reset is a no-op under per-session audio — the session owns " +
                "its own Remote Audio endpoint. Restart the seat's Apollo if capture is wrong.",
                seatId);
            return;
        }

        // Gate only the SharedHost path — PerSession returned above and stays gate-free, since it
        // touches no shared cable state. A concurrent teardown (which releases the cable and logs
        // the session off) interleaving between the release and the re-assign below would hand a
        // cable to a seat no longer in _seats, orphaning the AudioRouter assignment.
        using var lease = await _lifecycleGate.AcquireAsync(seatId, CancellationToken.None);

        var seat = LiveSeatAfterGate(seatId, "audio reset");
        if (seat is null) return;

        _audioRouter.ReleaseCable(seat);
        seat.VacCableIndex = _audioRouter.AssignCable(seat);

        ApplyAudioDefaults(seat);

        _ = BroadcastState(seat);
        _logger.LogInformation("Seat {Id}: audio reset, cable #{C}", seatId, seat.VacCableIndex);
    }

    /// <summary>
    /// Re-run the --set-default-capture helper in the seat's session without reassigning devices.
    /// Call this to fix mic routing when the initial helper invocation during provisioning failed
    /// or ran in the wrong session. Does NOT touch the default render device — that is machine-wide
    /// and would hijack the host's audio (issue #10); Apollo manages the game-audio sink itself.
    /// </summary>
    public void ApplyAudioDefaults(Guid seatId)
    {
        var seat = GetSeat(seatId)
            ?? throw new SeatNotFoundException();
        ApplyAudioDefaults(seat);
    }

    private void ApplyAudioDefaults(SeatInfo seat)
    {
        var helperExe = Path.Combine(AppContext.BaseDirectory, "MultiSeat.Service.exe");

        // Only the capture (mic) default is set here. The render default is intentionally left
        // alone — see the note in ProvisionSeatAsync and ApolloConfigBuilder (issue #10).
        if (!string.IsNullOrEmpty(seat.AudioCaptureDeviceId))
        {
            try
            {
                _sessionLauncher.RunHelperInSeatSession(
                    seat.SessionId, seat.AccountName,
                    $"\"{helperExe}\" --set-default-capture \"{seat.AudioCaptureDeviceId}\"");
                _logger.LogInformation(
                    "Seat {Id}: applied capture default {Dev} in session {Sid}",
                    seat.Id, seat.AudioCaptureDeviceId, seat.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Seat {Id}: could not apply capture default (non-critical)", seat.Id);
            }
        }
    }

    /// <summary>
    /// Change the NVENC quality preset for a live seat.
    /// Updates the seat's NvencPreset, regenerates sunshine.conf, and restarts Apollo.
    /// Also persists the change to the autostart preset if AutoStart is enabled.
    /// </summary>
    public async Task SetNvencPresetAsync(Guid seatId, NvencQualityPreset preset,
        SeatPresetStore presetStore, CancellationToken ct)
    {
        if (GetSeat(seatId) is null)
            throw new SeatNotFoundException();

        // Per-seat lifecycle gate: KillForReconnect + Start mutate ApolloProcessId.
        using var lease = await _lifecycleGate.AcquireAsync(seatId, ct);

        var seat = LiveSeatAfterGate(seatId, "NVENC preset change");
        if (seat is null) return;

        seat.NvencPreset = preset;

        _apolloManager.KillForReconnect(seat);
        await Task.Delay(500, ct);
        seat.ApolloProcessId = await _apolloManager.StartAsync(seat, ct);

        if (seat.AutoStart)
        {
            presetStore.Upsert(new SeatPreset
            {
                AccountName = seat.AccountName,
                Width = seat.Width,
                Height = seat.Height,
                Fps = seat.Fps,
                AutoStart = true,
                NvencPreset = preset,
            });
        }

        _ = BroadcastState(seat);
        _logger.LogInformation(
            "Seat {Id}: NVENC preset changed to {Preset} (Apollo PID {Pid})",
            seatId, preset, seat.ApolloProcessId);
    }

    /// <summary>
    /// Change a live seat's resolution.
    ///
    /// The seat streams its RDP session surface, and that surface's size is fixed by mstsc when
    /// the session is created (issue #15 — there is no in-seat virtual display to resize, and
    /// ChangeDisplaySettingsEx from inside the session returns success while doing nothing).
    /// So changing resolution means giving the seat a new session at the new size: disconnect,
    /// reconnect with the new geometry, and restart Apollo so it re-reads the desktop.
    ///
    /// The Windows session id is preserved — mstsc reconnects to the same session rather than
    /// logging it off — so anything running in the seat survives.
    /// </summary>
    public async Task SetResolutionAsync(Guid seatId, int width, int height,
        SeatPresetStore presetStore, CancellationToken ct)
    {
        if (GetSeat(seatId) is null)
            throw new SeatNotFoundException();

        // Per-seat lifecycle gate: rebuilds the session: SessionId, mstsc and ApolloProcessId all change.
        using var lease = await _lifecycleGate.AcquireAsync(seatId, ct);

        // This one is the sharpest case for the re-check: everything below creates a NEW Windows
        // session and a new Apollo. Run it against a seat a concurrent DELETE already removed and
        // both are orphaned immediately, owned by nothing.
        var seat = LiveSeatAfterGate(seatId, "resolution change");
        if (seat is null) return;

        var geometry = RdpGeometry.ForClient(width, height);
        if (!geometry.IsValid)
            throw new ArgumentException(
                $"{width}x{height} is not a usable desktop size — mstsc would ignore it.");

        if (seat.Width == width && seat.Height == height)
        {
            _logger.LogDebug("Seat {Id}: already {W}x{H}, nothing to do", seatId, width, height);
            return;
        }

        _logger.LogInformation(
            "Seat {Id}: changing resolution {OldW}x{OldH} -> {W}x{H}",
            seatId, seat.Width, seat.Height, width, height);

        seat.Width = width;
        seat.Height = height;

        // Take the session down and bring it back at the new size. Apollo is stopped first so it
        // is not capturing a desktop that is about to change under it.
        _apolloManager.KillForReconnect(seat);
        _sessionLauncher.DisconnectSession(seat.SessionId);

        seat.SessionId = await _sessionLauncher.LaunchSessionAsync(seat.AccountName, ct, geometry);

        // Apollo advertises the seat's resolution in its config, so regenerate before starting.
        _configBuilder.BuildConfig(seat, _options.ApolloConfigDir);
        seat.ApolloProcessId = await _apolloManager.StartAsync(seat, ct);

        if (seat.AutoStart)
        {
            presetStore.Upsert(new SeatPreset
            {
                AccountName = seat.AccountName,
                Width = width,
                Height = height,
                Fps = seat.Fps,
                AutoStart = true,
                NvencPreset = seat.NvencPreset,
            });
        }

        _ = BroadcastState(seat);
        _logger.LogInformation(
            "Seat {Id}: resolution now {W}x{H} on session {Sid} (Apollo PID {Pid})",
            seatId, width, height, seat.SessionId, seat.ApolloProcessId);
    }

    /// <summary>Recreate the virtual display for a seat.</summary>
    public async Task ResetDisplayAsync(Guid seatId, CancellationToken ct)
    {
        if (GetSeat(seatId) is null)
            throw new SeatNotFoundException();

        // A concurrent teardown between the destroy and create below — teardown releases the
        // display assignment and cleans the seat's Apollo config — would re-register a display
        // record and rewrite a config for a seat that no longer exists.
        using var lease = await _lifecycleGate.AcquireAsync(seatId, ct);

        var seat = LiveSeatAfterGate(seatId, "display reset");
        if (seat is null) return;

        await _displayManager.DestroyDisplayAsync(seat, ct);
        await _displayManager.CreateDisplayAsync(seat, ct);

        // Update Apollo config
        var configPath = _apolloManager.GetConfigPath(seat.Id);
        if (configPath is not null && !string.IsNullOrEmpty(seat.DisplayDevicePath))
            _configBuilder.UpdateDisplayOutput(configPath, seat.DisplayDevicePath);

        _ = BroadcastState(seat);
        _logger.LogInformation("Seat {Id}: display reset", seatId);
    }

    /// <summary>Recreate the virtual controller for a seat.</summary>
    public async Task ResetControllerAsync(Guid seatId)
    {
        if (GetSeat(seatId) is null)
            throw new SeatNotFoundException();

        if (!_options.EnableViGEmController)
        {
            _logger.LogDebug("Seat {Id}: controller reset skipped — ViGEm controller disabled", seatId);
            return;
        }

        // Destroy + recreate is not atomic on its own: a teardown interleaving here leaves a
        // ViGEm pad created for a seat that no longer exists, and nothing to destroy it.
        using var lease = await _lifecycleGate.AcquireAsync(seatId, CancellationToken.None);

        var seat = LiveSeatAfterGate(seatId, "controller reset");
        if (seat is null) return;

        UnassignControllersForSeat(seatId);
        _controllerManager.DestroyController(seat);
        seat.ViGEmControllerIndex = _controllerManager.CreateController(seat);

        if (_options.AutoAssignControllers)
        {
            var connected = _inputRouter.GetConnectedControllers();
            var assigned = _inputRouter.GetAssignments();
            var freeIdx = connected.FirstOrDefault(idx => !assigned.ContainsKey(idx), -1);
            if (freeIdx >= 0)
                _inputRouter.AssignController(freeIdx, seatId);
        }

        _ = BroadcastState(seat);
        _logger.LogInformation("Seat {Id}: controller reset", seatId);
    }

    /// <summary>
    /// True when MultiSeat manages ViGEm virtual controllers + physical-XInput routing
    /// (EnableViGEmController). When false (default), Apollo forwards the Moonlight client's
    /// controller natively and the Input-tab assignment UI has no effect. Read from the
    /// bound options here (the API's inner DI container doesn't bind MultiSeatOptions).
    /// </summary>
    public bool ControllerRoutingEnabled => _options.EnableViGEmController;

    /// <summary>Get the InputRouter for API access to controller assignments.</summary>
    public InputRouter InputRouter => _inputRouter;

    /// <summary>Get the InputHookManager for API status queries.</summary>
    public InputHookManager InputHookManager => _inputHookManager;

    /// <summary>Get the ApolloManager for API queries.</summary>
    public ApolloManager ApolloManager => _apolloManager;

    public IReadOnlyList<string> GetPairedClients(Guid seatId)
    {
        var seat = GetSeat(seatId);
        if (seat is null) return Array.Empty<string>();
        return _configBuilder.GetPairedClients(seat.AccountName, _options.ApolloConfigDir);
    }

    public bool UnpairClient(Guid seatId, string clientName)
    {
        var seat = GetSeat(seatId);
        if (seat is null) return false;
        return _configBuilder.UnpairClient(seat.AccountName, _options.ApolloConfigDir, clientName);
    }

    public void UnpairAllClients(Guid seatId)
    {
        var seat = GetSeat(seatId);
        if (seat is null) return;
        _configBuilder.UnpairAllClients(seat.AccountName, _options.ApolloConfigDir);
    }

    private void UnassignControllersForSeat(Guid seatId)
    {
        foreach (var (idx, assignedSeat) in _inputRouter.GetAssignments())
        {
            if (assignedSeat == seatId)
                _inputRouter.UnassignController(idx);
        }
    }

    private static Task BroadcastState(SeatInfo seat) =>
        WebSocketHub.BroadcastSeatUpdateAsync(seat);
}
