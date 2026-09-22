using Microsoft.Extensions.Configuration;
using MultiSeat.Service.Configuration;
using Xunit;

namespace MultiSeat.Tests.Configuration;

/// <summary>
/// Guards a setting that failed in the worst available way: it reported success and did nothing.
///
/// A seat streams its RDP session surface, so the session's DWM composition rate is the ceiling on
/// how often that seat can produce a new frame, and the interval also sets the refresh rate the
/// seat's display advertises. MultiSeat wrote 1 from the initial release, which is where the seat's
/// documented 1000 Hz display came from: the advertised rate is 1000/interval.
///
/// On Win11 26100.9444 an interval below 2 is out of range. Windows falls back to its own default
/// and says nothing, so seats dropped from 1000 Hz to ~32fps with nothing logged and nothing
/// failing, and reading the value back still returns the 1 that was written.
///
/// Measured on the reference host, one fresh RDP session per value, rate sampled by pacing off
/// DwmFlush, refresh read from both GDI and CCD (which agree): absent = 32.0fps, 1 = 31.9fps,
/// 16 = 62.1fps, 8 = 125.2fps, 4 = 246.1fps, 2 = 464.7fps.
///
/// Nothing here can reach a registry or a compositor. These assert the two things that were wrong
/// and are cheap to get wrong again: the value we ship is one Windows honours, and the C# default
/// agrees with appsettings.json.
/// </summary>
public class DwmFrameIntervalTests
{
    [Fact]
    public void DefaultInterval_IsOneWindowsActuallyHonours()
    {
        var options = new MultiSeatOptions();

        Assert.True(
            options.DwmFrameIntervalMs >= MultiSeatOptions.MinimumHonouredDwmFrameIntervalMs,
            $"DwmFrameIntervalMs defaults to {options.DwmFrameIntervalMs}, which Windows ignores. " +
            "Seats would compose at the ~32fps default while the service reports success.");
    }

    [Fact]
    public void TheValueWeUsedToShip_IsBelowTheFloor()
    {
        // The regression marker. If this ever fails, the floor moved and the measurements above
        // need re-taking before anyone trusts it.
        Assert.True(1 < MultiSeatOptions.MinimumHonouredDwmFrameIntervalMs);
    }

    [Fact]
    public void DefaultInterval_ClearsASixtyFpsStream()
    {
        // 1000/interval is the composition rate. A seat cannot send a new frame faster than this,
        // so the shipped value has to clear the frame rates seats actually stream at.
        var composedFps = 1000 / new MultiSeatOptions().DwmFrameIntervalMs;

        Assert.True(composedFps >= 60, $"composes at {composedFps}fps, below a 60fps stream");
    }

    [Fact]
    public void ShippedAppSettings_AgreesWithTheCodeDefault()
    {
        // Two defaults for one setting is its own failure mode here: the audio mode shipped with
        // the C# default and appsettings.json disagreeing, and which one won depended on whether
        // the host had a config file at all.
        var config = new ConfigurationBuilder()
            .AddJsonFile("service-appsettings.json", optional: false)
            .Build();

        var shipped = config.GetSection("MultiSeat")["DwmFrameIntervalMs"];

        Assert.NotNull(shipped);
        Assert.Equal(new MultiSeatOptions().DwmFrameIntervalMs, int.Parse(shipped!));
    }

    [Fact]
    public void ShippedAppSettings_BindsToAnHonouredInterval()
    {
        var options = new MultiSeatOptions();
        new ConfigurationBuilder()
            .AddJsonFile("service-appsettings.json", optional: false)
            .Build()
            .GetSection("MultiSeat")
            .Bind(options);

        Assert.True(options.DwmFrameIntervalMs >= MultiSeatOptions.MinimumHonouredDwmFrameIntervalMs);
    }
}
