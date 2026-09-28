using MultiSeat.Service.Configuration;
using Xunit;

namespace MultiSeat.Tests.Configuration;

/// <summary>
/// Guards the fps-vs-effective-refresh-rate warning added for issue #70. A seat's actual
/// refresh rate is capped by DwmFrameIntervalMs, one host-wide value — the dashboard's fps
/// picker only changes what Apollo advertises to the client, so nothing else catches a
/// requested fps the host cannot compose.
/// </summary>
public class RefreshRateAdvisorTests
{
    [Fact]
    public void RequestedAboveEffective_ReturnsWarningWithTheRightNumbers()
    {
        // DwmFrameIntervalMs = 16 -> effective 62Hz (1000/16, truncated). 120fps is asked for
        // in the dashboard's New Seat form and is well above it.
        var warning = RefreshRateAdvisor.CheckFpsAgainstEffectiveRefreshRate(requestedFps: 120, dwmFrameIntervalMs: 16);

        Assert.NotNull(warning);
        Assert.Contains("120", warning);
        Assert.Contains("62", warning);
    }

    [Fact]
    public void RequestedBelowEffective_ReturnsNoWarning()
    {
        var warning = RefreshRateAdvisor.CheckFpsAgainstEffectiveRefreshRate(requestedFps: 30, dwmFrameIntervalMs: 16);

        Assert.Null(warning);
    }

    [Fact]
    public void RequestedEqualToEffective_ReturnsNoWarning()
    {
        // The boundary: a seat asking for exactly what the host composes is not a mismatch —
        // it is reachable. Catches the threshold flipping from > to >=.
        var effectiveHz = RefreshRateAdvisor.EffectiveRefreshRateHz(16);

        var warning = RefreshRateAdvisor.CheckFpsAgainstEffectiveRefreshRate(effectiveHz, dwmFrameIntervalMs: 16);

        Assert.Null(warning);
    }

    [Fact]
    public void RequestedOneAboveEffective_StillWarns()
    {
        // Catches the threshold flipping direction (e.g. "<" instead of "<=", or the comparison
        // inverted outright) at the tightest margin, not just an obviously-too-high value.
        var effectiveHz = RefreshRateAdvisor.EffectiveRefreshRateHz(16);

        var warning = RefreshRateAdvisor.CheckFpsAgainstEffectiveRefreshRate(effectiveHz + 1, dwmFrameIntervalMs: 16);

        Assert.NotNull(warning);
    }

    [Fact]
    public void EffectiveRefreshRateHz_TruncatesLikeTheAdvertisedRateDoes()
    {
        // Mirrors MultiSeatOptions.DwmFrameIntervalMs's doc comment: 16ms -> 62Hz, not the
        // unreachable exact 60 (1000/16 = 62.5).
        Assert.Equal(62, RefreshRateAdvisor.EffectiveRefreshRateHz(16));
    }

    [Fact]
    public void IfThisCheckIsRemoved_TheseTestsStopCompiling()
    {
        // A regression marker in the spirit of DwmFrameIntervalTests.TheValueWeUsedToShip_
        // IsBelowTheFloor: if RefreshRateAdvisor is deleted or its method renamed, every test
        // above fails to build rather than silently passing.
        Assert.True(RefreshRateAdvisor.EffectiveRefreshRateHz(2) > 0);
    }
}
