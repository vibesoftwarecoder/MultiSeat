namespace MultiSeat.Service.Configuration;

/// <summary>
/// Warns when a seat's requested fps cannot actually be reached.
///
/// A seat streams its RDP session surface, so DWM's composition interval
/// (<see cref="MultiSeatOptions.DwmFrameIntervalMs"/>) is the ceiling on how often it can
/// produce a new frame — one value, shared by every seat on the host. The dashboard's New Seat
/// form offers 30-240 fps, but that number only changes the fps list Apollo advertises to the
/// Moonlight client (<c>ApolloConfigBuilder</c>) — nothing about it changes the composition
/// interval, so a seat asked for more than the host can compose is silently capped with nothing
/// to say why (issue #70).
/// </summary>
public static class RefreshRateAdvisor
{
    /// <summary>
    /// Windows' own composition rate when <see cref="MultiSeatOptions.DwmFrameIntervalMs"/> is
    /// below <see cref="MultiSeatOptions.MinimumHonouredDwmFrameIntervalMs"/> and so goes
    /// unwritten — measured, not derived, in that constant's doc comment.
    /// </summary>
    private const int WindowsDefaultComposedFps = 32;

    /// <summary>
    /// The refresh rate DWM actually composes seats at, truncated to a whole number — the same
    /// arithmetic <see cref="MultiSeatOptions.DwmFrameIntervalMs"/>'s doc comment uses for what
    /// the interval advertises (16ms -> 62Hz, not the unreachable exact 60). Below
    /// <see cref="MultiSeatOptions.MinimumHonouredDwmFrameIntervalMs"/> the value is never
    /// written (see <c>MultiSeatWorker.SetDwmFrameInterval</c>), so Windows stays at its own
    /// default rather than whatever the arithmetic on the unwritten value would suggest —
    /// dividing by an interval that could be zero would also throw.
    /// </summary>
    public static int EffectiveRefreshRateHz(int dwmFrameIntervalMs) =>
        dwmFrameIntervalMs < MultiSeatOptions.MinimumHonouredDwmFrameIntervalMs
            ? WindowsDefaultComposedFps
            : 1000 / dwmFrameIntervalMs;

    /// <summary>
    /// Null when <paramref name="requestedFps"/> is within what the host can actually compose.
    /// A requested fps equal to the effective rate is not a mismatch — only exceeding it is,
    /// since the seat can still reach exactly that rate.
    /// </summary>
    public static string? CheckFpsAgainstEffectiveRefreshRate(int requestedFps, int dwmFrameIntervalMs)
    {
        var effectiveHz = EffectiveRefreshRateHz(dwmFrameIntervalMs);
        if (requestedFps <= effectiveHz)
            return null;

        return $"Requested {requestedFps}fps exceeds this host's effective refresh rate of " +
               $"~{effectiveHz}Hz (DwmFrameIntervalMs = {dwmFrameIntervalMs}ms, shared by every " +
               $"seat on this host). The stream will not actually exceed ~{effectiveHz}fps no " +
               "matter what Apollo advertises to the client.";
    }
}
