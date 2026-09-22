using System.Diagnostics;
using System.Management;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using MultiSeat.Service.Api;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Input;
using MultiSeat.Service.Monitoring;
using MultiSeat.Service.Sessions;
using MultiSeat.Service.Storage;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service;

/// <summary>
/// Primary background service. Runs the embedded API server and
/// periodic health checks for all active seats.
/// </summary>
public sealed class MultiSeatWorker : BackgroundService
{
    private readonly ILogger<MultiSeatWorker> _logger;
    private readonly MultiSeatOptions _options;
    private readonly SeatManager _seatManager;
    private readonly RdpWrapper _rdpWrapper;
    private readonly SessionHealthCheck _healthCheck;
    private readonly InputRouter _inputRouter;
    private readonly InputHookManager _inputHookManager;
    private readonly HidHideConfigurator _hidHide;
    private readonly FirewallManager _firewall;
    private readonly SeatPresetStore _presets;
    private readonly SharedLibraryProvisioner _sharedLibrary;
    private readonly Accounts.AccountManager _accounts;
    private readonly IServiceProvider _services;

    private WebApplication? _apiApp;

    public MultiSeatWorker(
        ILogger<MultiSeatWorker> logger,
        IOptions<MultiSeatOptions> options,
        SeatManager seatManager,
        RdpWrapper rdpWrapper,
        SessionHealthCheck healthCheck,
        InputRouter inputRouter,
        InputHookManager inputHookManager,
        HidHideConfigurator hidHide,
        FirewallManager firewall,
        SeatPresetStore presets,
        SharedLibraryProvisioner sharedLibrary,
        Accounts.AccountManager accounts,
        IServiceProvider services)
    {
        _accounts = accounts;
        _logger = logger;
        _options = options.Value;
        _seatManager = seatManager;
        _rdpWrapper = rdpWrapper;
        _healthCheck = healthCheck;
        _inputRouter = inputRouter;
        _inputHookManager = inputHookManager;
        _hidHide = hidHide;
        _firewall = firewall;
        _presets = presets;
        _sharedLibrary = sharedLibrary;
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MultiSeat Service starting — Windows {Build}",
            Environment.OSVersion.VersionString);

        // ── Step 0: Kill orphaned MultiSeat Apollo processes from previous run ──
        // On restart, in-memory seat state is lost. Any Apollo instances MultiSeat
        // started in a previous run hold their ports, causing conflicts when new
        // seats are provisioned. Kill ONLY MultiSeat-managed instances — a standalone
        // Apollo the user runs separately (different install dir, no per-seat config)
        // and its ApolloService are left untouched, so MultiSeat coexists with it.
        KillOrphanedApolloProcesses();

        // ── Step 0a: Take administrator rights back off seat accounts ──
        // Seats created by an older build are local administrators, which was never needed (the
        // SudoVDA justification does not hold — see MultiSeatOptions.GrantSeatAdministrator) and
        // undoes MultiSeat's own protection of its credential store, since an administrator can
        // become SYSTEM. Done at startup as well as at provisioning so an install that simply sits
        // idle is corrected too. Managed accounts only; linked accounts are left alone.
        _accounts.NormalizeManagedAccountPrivileges();

        // ── Step 0b: Set DWM frame interval for RDP sessions ────────
        // The Microsoft Remote Display Adapter's DWM composition rate defaults to ~32fps, which
        // is the ceiling on how often a seat can produce a new frame. Lowering the interval
        // raises it — measured 125fps at the default 8ms. Applies to sessions created after this
        // point, so seats provisioned later in this service's life get it.
        SetDwmFrameInterval();

        // ── Step 1: Verify multi-session is available ────────────────
        if (!_rdpWrapper.EnsureMultiSession())
        {
            _logger.LogError(
                "RDP Wrapper multi-session patch not detected. " +
                "Concurrent sessions will not work. Install RDP Wrapper Library and restart.");
        }

        // ── Step 2: Start input subsystems ─────────────────────────────
        // Clear any HidHide state left over from a previous run before starting.
        // HidHide is a kernel driver — its blacklist and cloak state survive reboots.
        // Without this reset, devices hidden in a previous session stay hidden after reboot.
        _hidHide.ResetOnStartup();

        // The physical-controller → ViGEm routing (and its ~1ms XInput poll thread) only
        // matters when EnableViGEmController is on. With it off (default), Apollo forwards
        // Moonlight controller input natively and there is no virtual pad to forward to, so
        // skip the poll loop to avoid burning CPU/battery for nothing.
        if (_options.EnableViGEmController)
            _inputRouter.Start();
        else
            _logger.LogInformation(
                "InputRouter XInput polling disabled — EnableViGEmController is off " +
                "(Apollo forwards Moonlight controller input natively)");

        _inputHookManager.Start();
        _logger.LogInformation("Input subsystems started");

        // ── Step 3: Ensure API port is open in Windows Firewall ──────
        // The dashboard must be reachable from LAN devices (e.g. ROG Ally).
        // Windows Firewall blocks inbound connections by default; no install
        // script adds this rule, so we ensure it exists on every startup.
        //
        // Skipped when the API is bound to loopback, because then nothing is listening on an
        // address the rule could admit traffic to: the hole would be permanently useless, and an
        // open inbound port that leads nowhere is exactly the sort of thing that reads as an
        // exposure later. Flipping ApiBindLoopbackOnly back to false re-creates it on next start.
        if (_options.ApiBindLoopbackOnly)
        {
            _logger.LogInformation(
                "Skipping the firewall rule for API port {Port} — the API is bound to loopback, so " +
                "an inbound rule would admit nothing. Any existing MultiSeat-API rule is now " +
                "redundant and can be removed.", _options.ApiPort);
        }
        else
        {
            await _firewall.EnsureApiPortOpenAsync(_options.ApiPort, stoppingToken);
        }

        // ── Step 3b: Provision the shared game library ───────────────
        // Create the shared Steam library + ROM folders (once, idempotent) and grant seat
        // accounts access, so games/ROMs aren't siloed per Windows account. No-op when disabled.
        await _sharedLibrary.EnsureSharedLibraryAsync(stoppingToken);

        // ── Step 4: Start embedded API server ────────────────────────
        _apiApp = ApiServer.Build(_services, _options);
        _ = _apiApp.RunAsync(stoppingToken);
        _logger.LogInformation("API server listening on port {Port}", _options.ApiPort);

        // ── Step 5: Auto-provision seats ─────────────────────────────
        await AutoProvisionSeatsAsync(stoppingToken);

        // ── Step 6: Health-check loop ────────────────────────────────
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(_options.HealthCheckIntervalMs));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _healthCheck.CheckAllSeatsAsync(_seatManager, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Health check cycle failed");
            }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }

    private async Task AutoProvisionSeatsAsync(CancellationToken ct)
    {
        var autoStart = _presets.GetAutoStart();
        if (autoStart.Count == 0) return;

        _logger.LogInformation("Auto-provisioning {Count} seat(s) from presets", autoStart.Count);

        foreach (var preset in autoStart)
        {
            try
            {
                var seat = await _seatManager.ProvisionSeatAsync(
                    new SeatRequest
                    {
                        AccountName = preset.AccountName,
                        Width = preset.Width,
                        Height = preset.Height,
                        Fps = preset.Fps,
                        NvencPreset = preset.NvencPreset,
                    }, ct);

                seat.AutoStart = true;
                _logger.LogInformation(
                    "Auto-provisioned seat '{Account}' (ID {Id})", preset.AccountName, seat.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to auto-provision seat '{Account}'", preset.AccountName);
            }
        }
    }

    private void KillOrphanedApolloProcesses()
    {
        var exeName = Path.GetFileNameWithoutExtension(_options.ApolloExePath); // "sunshine"
        try
        {
            var procs = Process.GetProcessesByName(exeName);
            if (procs.Length == 0) return;

            // Only reap Apollo instances MultiSeat itself launched. A standalone Apollo
            // the user runs (e.g. for their main console) shares the same exe name, so we
            // must NOT kill every "sunshine" process. MultiSeat-managed instances are
            // identified by their install dir or per-seat config path on the command line.
            var managed = GetManagedApolloPids(exeName);

            foreach (var proc in procs)
            {
                try
                {
                    if (!managed.Contains(proc.Id))
                    {
                        _logger.LogInformation(
                            "Skipping non-MultiSeat Apollo PID {Pid} — leaving standalone Apollo running",
                            proc.Id);
                        continue;
                    }

                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                    _logger.LogInformation("Killed orphaned MultiSeat Apollo PID {Pid}", proc.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kill orphaned Apollo PID {Pid}", proc.Id);
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during orphaned Apollo cleanup");
        }
    }

    /// <summary>
    /// Returns the PIDs of Apollo processes that MultiSeat launched, identified via WMI by
    /// either their executable path (under MultiSeat's own Apollo install dir) or a per-seat
    /// MultiSeat config path on their command line. Used so cleanup never touches a standalone
    /// Apollo running on the same host. On any WMI failure, returns an empty set (fail-safe:
    /// we would rather skip cleanup than kill an unrelated Apollo).
    /// </summary>
    private HashSet<int> GetManagedApolloPids(string exeName)
    {
        var managed = new HashSet<int>();
        var managedExeDir = Path.GetDirectoryName(_options.ApolloExePath);   // C:\Program Files\ApolloVibe
        var managedConfigDir = _options.ApolloConfigDir;                     // C:\ProgramData\MultiSeat\apollo

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '{exeName}.exe'");

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var exePath = obj["ExecutablePath"] as string;
                    var cmdLine = obj["CommandLine"] as string;

                    // One shared decision with the reporting path in HostApolloMonitor. This copy
                    // used to omit the emptiness check on managedConfigDir, and Contains("") is
                    // true for every process - an empty ApolloConfigDir would have marked every
                    // Apollo on the host as ours, and this is the path that kills them.
                    if (ApolloOwnership.IsMultiSeatManaged(exePath, cmdLine, managedExeDir, managedConfigDir))
                        managed.Add(Convert.ToInt32(obj["ProcessId"]));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WMI query for managed Apollo processes failed — skipping orphan cleanup " +
                "to avoid killing a standalone Apollo");
        }

        return managed;
    }

    /// <summary>
    /// Set DWMFRAMEINTERVAL in the Terminal Server WinStations registry key, which controls the
    /// DWM composition interval for RDP sessions — and therefore how often a seat can produce a
    /// new frame. The composition rate is 1000/interval; the Windows default is ~32fps.
    /// Takes effect on the next RDP session, not on sessions already running.
    /// See MultiSeatOptions.DwmFrameIntervalMs for the measurements behind the default.
    /// </summary>
    private void SetDwmFrameInterval()
    {
        const string keyPath = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations";
        const string valueName = "DWMFRAMEINTERVAL";

        var intervalMs = _options.DwmFrameIntervalMs;

        // Windows treats an interval below 2 as out of range and silently uses the default, so
        // writing one would report success and change nothing — which is exactly how the old
        // hardcoded 1 went unnoticed. Refuse it and say why rather than write a value we have
        // measured to be inert.
        if (intervalMs < MultiSeatOptions.MinimumHonouredDwmFrameIntervalMs)
        {
            _logger.LogWarning(
                "DwmFrameIntervalMs is {Ms}, which Windows ignores — RDP sessions would compose " +
                "at the ~32fps default. Not writing it. Use 8 (125Hz) unless you have measured " +
                "a reason for another value",
                intervalMs);
            return;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
            if (key is null)
            {
                _logger.LogWarning(
                    "Registry key HKLM\\{Path} not found — cannot set DWM frame interval",
                    keyPath);
                return;
            }

            var current = key.GetValue(valueName);
            if (current is int currentVal && currentVal == intervalMs)
            {
                _logger.LogDebug("DWMFRAMEINTERVAL already set to {Ms}ms", intervalMs);
                return;
            }

            key.SetValue(valueName, intervalMs, RegistryValueKind.DWord);
            _logger.LogInformation(
                "Set DWMFRAMEINTERVAL to {Ms}ms (was {Old}) — RDP sessions created from now on " +
                "compose at ~{Hz}fps instead of the ~32fps default",
                intervalMs, current ?? "unset", 1000 / intervalMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to set DWMFRAMEINTERVAL — RDP sessions stay capped at the ~32fps default");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MultiSeat Service stopping — tearing down all seats");

        await _seatManager.TeardownAllAsync(cancellationToken);

        // Stop input subsystems
        _inputHookManager.Stop();
        _inputRouter.Stop();

        if (_apiApp is not null)
            await _apiApp.StopAsync(cancellationToken);

        await base.StopAsync(cancellationToken);
    }
}
