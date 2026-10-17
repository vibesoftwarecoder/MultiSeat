using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;

namespace MultiSeat.Service.SteamShim;

/// <summary>A running <c>steam.exe</c>, as the watch sees it.</summary>
internal sealed record SteamProcessInfo(int Pid, int SessionId, string? Path, DateTime StartedUtc, bool? HasHook);

/// <summary>
/// Says so, once per process, when a <c>steam.exe</c> is running without the isolation hook.
///
/// The shim covers the ordinary ways of starting Steam (shortcuts, the steam:// handler, autostart).
/// Something can still run <c>steam.exe</c> by its path: a third-party launcher, a script, a
/// shortcut made after the shim went in. That Steam is not isolated, and one console or seat Steam
/// that is not takes the others' sessions over. This only reports. It never ends a process: a Steam
/// the user started on purpose is theirs, and the log line says how to start one the right way.
/// </summary>
public sealed class UnisolatedSteamWatch : BackgroundService
{
    // A Steam the launcher is still starting has no hook module for a moment: the hook goes in while
    // the process is suspended, but a process can be listed before that is visible. Judge only after.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ILogger<UnisolatedSteamWatch> _logger;
    private readonly MultiSeatOptions _options;
    private readonly HashSet<(int Pid, DateTime StartedUtc)> _reported = [];

    public UnisolatedSteamWatch(ILogger<UnisolatedSteamWatch> logger, IOptions<MultiSeatOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>Tests replace this; the service reads the real process list.</summary>
    internal Func<IEnumerable<SteamProcessInfo>> Processes { get; set; } = ReadProcesses;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SteamIsolationEnabled)
        {
            _logger.LogInformation("Steam isolation is off, so the watch for unisolated Steam processes is idle.");
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                Scan();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>One pass. Returns how many new unisolated Steams it reported.</summary>
    internal int Scan()
    {
        var reported = 0;
        try
        {
            var seen = new HashSet<(int, DateTime)>();
            foreach (var steam in Processes())
            {
                // A pid can be reused, so the start time is part of a process's identity.
                var identity = (steam.Pid, steam.StartedUtc);
                seen.Add(identity);
                if (steam.HasHook is not false || DateTime.UtcNow - steam.StartedUtc < Settle || !_reported.Add(identity)) continue;
                reported++;
                _logger.LogWarning(
                    "Steam (pid {Pid}, session {Session}, {Path}) is running without Steam isolation, so it can take " +
                    "over or be ended by another Steam on this host. Start Steam from its Start menu or desktop " +
                    "shortcut, which go through the isolation shim, instead of running steam.exe directly. " +
                    "This is only a report; nothing was stopped.",
                    steam.Pid, steam.SessionId, steam.Path ?? "unknown path");
            }

            _reported.RemoveWhere(r => !seen.Contains(r));   // gone: forget it, so a pid reused later is judged afresh
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The scan for unisolated Steam processes failed; it tries again next time.");
        }

        return reported;
    }

    private static IEnumerable<SteamProcessInfo> ReadProcesses()
    {
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                SteamProcessInfo? info = null;
                try
                {
                    var started = process.StartTime.ToUniversalTime();
                    string? path = null;
                    bool? hook = null;   // unknown (access denied, or the process just ended) is never reported
                    try { path = process.MainModule?.FileName; } catch { /* 32-bit view or access denied */ }
                    try
                    {
                        hook = process.Modules.Cast<ProcessModule>().Any(m =>
                            m.ModuleName.StartsWith("MultiSeat.SteamHook", StringComparison.OrdinalIgnoreCase));
                    }
                    catch { /* leave unknown */ }

                    info = new SteamProcessInfo(process.Id, process.SessionId, path, started, hook);
                }
                catch
                {
                    // exited while being read
                }

                if (info is not null) yield return info;
            }
        }
    }
}
