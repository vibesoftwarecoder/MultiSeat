using Microsoft.Win32;

namespace MultiSeat.Service.Configuration;

/// <summary>
/// The host-wide DWM composition interval in effect right now (issue #74).
///
/// <see cref="MultiSeatOptions.DwmFrameIntervalMs"/> is read once at startup, and the rest of the
/// service holds <c>IOptions&lt;MultiSeatOptions&gt;.Value</c>, which never changes after that.
/// The API also runs in its own WebApplication container, so it cannot reach the host's options
/// instance at all. A dashboard change therefore needs one shared, mutable place to live — the
/// same shape as <c>ApiAuthState</c>, which solves the same problem for the auth toggle. The seat
/// manager reads the interval from here when it provisions a seat, so a change made through the
/// API is what the next provision's refresh-rate check sees.
///
/// ⚠️ Changing this value changes nothing on its own. The registry write
/// (<see cref="DwmFrameIntervalRegistry"/>) is what Windows reads, and it reads it only when a new
/// RDP session's compositor starts. A seat that is already running keeps its current rate until it
/// is stopped and started again. A reconnect or a resize reuses the running session, so it does not
/// pick the new value up either.
/// </summary>
public sealed class DwmFrameIntervalSetting
{
    /// <summary>
    /// The intervals the dashboard and API offer, in milliseconds. Each one was composed and
    /// delivered by RDP under a real D3D11 game-like load on the reference host (issue #74):
    ///
    ///     interval   rate     CPU, one core, one busy seat
    ///     33 ms      30 Hz    below the 16 ms figure (not separately measured)
    ///     16 ms      62 Hz    87.7%  (the old default)
    ///      8 ms     125 Hz    101.3% (+15%)
    ///      6 ms     166 Hz    118.1% (+35%)
    ///
    /// ⛔ 4 ms and 2 ms compose idle (246 and 465 fps) but were never measured under load, so they
    /// are not offered here. They can still be set by hand in appsettings.local.json, which is the
    /// honest place for an unmeasured value.
    /// </summary>
    private static readonly int[] AllowedIntervals = [33, 16, 8, 6];

    /// <summary>The intervals the API accepts, slowest first.</summary>
    public static IReadOnlyList<int> AllowedIntervalsMs => AllowedIntervals;

    /// <summary>True when the API may set this interval.</summary>
    public static bool IsAllowedInterval(int intervalMs) => AllowedIntervals.Contains(intervalMs);

    /// <summary>
    /// The error text for an interval outside the allowed set. It names the allowed values, the
    /// same way <c>RdpGeometry.ScaleFactorError</c> does, so a caller always learns what works.
    /// </summary>
    public static string IntervalError(int intervalMs) =>
        $"DWM frame interval {intervalMs}ms is not one MultiSeat offers. Use one of: " +
        $"{string.Join(", ", AllowedIntervals)} (ms). These are the values measured under load " +
        "(issue #74).";

    private volatile int _intervalMs;

    public DwmFrameIntervalSetting(int intervalMs, ILogger? logger = null)
    {
        _intervalMs = intervalMs;
        Logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>
    /// A logger from the HOST container. The API's own container has no Event Log provider (see
    /// ApiServer.Build), so a change logged through it would never reach the only log a Windows
    /// Service has.
    /// </summary>
    internal ILogger Logger { get; }

    /// <summary>The interval a seat provisioned from now on gets, in milliseconds.</summary>
    public int IntervalMs => _intervalMs;

    /// <summary>The refresh rate that interval composes at, as RefreshRateAdvisor computes it.</summary>
    public int EffectiveRefreshRateHz => RefreshRateAdvisor.EffectiveRefreshRateHz(_intervalMs);

    /// <summary>
    /// Record a new interval. Only an allowed value is accepted; anything else would leave the
    /// in-memory value disagreeing with what the API is willing to write to the registry.
    /// </summary>
    public void Set(int intervalMs)
    {
        if (!IsAllowedInterval(intervalMs))
            throw new ArgumentOutOfRangeException(nameof(intervalMs), intervalMs, IntervalError(intervalMs));

        _intervalMs = intervalMs;
    }
}

/// <summary>
/// Reads and writes <c>DWMFRAMEINTERVAL</c>, the registry value Windows uses for the composition
/// interval of every RDP session on the host. Used at service startup and by the settings API.
/// </summary>
public static class DwmFrameIntervalRegistry
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations";
    private const string ValueName = "DWMFRAMEINTERVAL";

    /// <summary>The value in the registry now, or null when it is absent or cannot be read.</summary>
    public static int? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: false);
            return key?.GetValue(ValueName) is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Write <paramref name="intervalMs"/>. Returns null when the registry holds that value
    /// afterwards, or the reason it does not. RDP sessions created after a successful write compose
    /// at 1000/interval; sessions already running are not affected.
    /// </summary>
    public static string? Write(int intervalMs, ILogger logger)
    {
        // Windows treats an interval below 2 as out of range and silently uses the default, so
        // writing one would report success and change nothing — which is exactly how the old
        // hardcoded 1 went unnoticed. Refuse it and say why rather than write a value we have
        // measured to be inert.
        if (intervalMs < MultiSeatOptions.MinimumHonouredDwmFrameIntervalMs)
        {
            logger.LogWarning(
                "DwmFrameIntervalMs is {Ms}, which Windows ignores — RDP sessions would compose " +
                "at the ~32fps default. Not writing it. Use 8 (125Hz) unless you have measured " +
                "a reason for another value",
                intervalMs);
            return $"DwmFrameIntervalMs {intervalMs} is below " +
                   $"{MultiSeatOptions.MinimumHonouredDwmFrameIntervalMs}, which Windows ignores.";
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key is null)
            {
                logger.LogWarning(
                    "Registry key HKLM\\{Path} not found — cannot set DWM frame interval",
                    KeyPath);
                return $@"Registry key HKLM\{KeyPath} not found.";
            }

            var current = key.GetValue(ValueName);
            if (current is int currentVal && currentVal == intervalMs)
            {
                logger.LogDebug("DWMFRAMEINTERVAL already set to {Ms}ms", intervalMs);
                return null;
            }

            key.SetValue(ValueName, intervalMs, RegistryValueKind.DWord);
            logger.LogInformation(
                "Set DWMFRAMEINTERVAL to {Ms}ms (was {Old}) — RDP sessions created from now on " +
                "compose at ~{Hz}fps; sessions already running keep their current rate",
                intervalMs, current ?? "unset", RefreshRateAdvisor.EffectiveRefreshRateHz(intervalMs));
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to set DWMFRAMEINTERVAL — new RDP sessions keep the previous interval");
            return $"Could not write DWMFRAMEINTERVAL: {ex.Message}";
        }
    }
}
