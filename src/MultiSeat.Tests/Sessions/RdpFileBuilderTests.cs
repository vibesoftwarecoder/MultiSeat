using MultiSeat.Service.Configuration;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// Default.rdp is the only thing that sets a seat's desktop size — the seat streams its RDP
/// session surface, and that surface's geometry is fixed by mstsc at connect and cannot be
/// changed from inside the session. These assert the file we hand mstsc, which is otherwise
/// only observable by provisioning a seat on a live host.
/// </summary>
public class RdpFileBuilderTests
{
    private static string BuildWith(int width, int height) =>
        RdpFileBuilder.Build(AudioMode.SharedHost, RdpGeometry.ForClient(width, height));

    [Fact]
    public void WritesRequestedGeometry()
    {
        var rdp = BuildWith(1280, 720);

        Assert.Contains("desktopwidth:i:1280", rdp);
        Assert.Contains("desktopheight:i:720", rdp);
    }

    // smart sizing and dynamic resolution let the session's resolution follow the mstsc WINDOW,
    // which is hidden and minimized here. Left on, a window nobody ever sees would dictate the
    // resolution a player streams at.
    [Fact]
    public void PinsTheSessionSoAHiddenWindowCannotResizeIt()
    {
        var rdp = BuildWith(1920, 1080);

        Assert.Contains("smart sizing:i:0", rdp);
        Assert.Contains("dynamic resolution:i:0", rdp);
    }

    [Fact]
    public void OmitsGeometryEntirelyWhenNoneRequested()
    {
        var rdp = RdpFileBuilder.Build(AudioMode.SharedHost, geometry: null);

        Assert.DoesNotContain("desktopwidth", rdp);
        Assert.DoesNotContain("desktopheight", rdp);
    }

    // A nonsense size is silently ignored by mstsc, which would leave the seat inheriting the
    // console's size while the config claimed otherwise. Drop the keys instead of writing junk.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(320, 240)]
    [InlineData(99999, 99999)]
    public void IgnoresGeometryThatMstscWouldReject(int width, int height)
    {
        var rdp = BuildWith(width, height);

        Assert.DoesNotContain("desktopwidth", rdp);
    }

    // Scale exists so a high-resolution client does not render microscopic UI: the seat desktop
    // is viewed on the client's screen at the client's size.
    [Theory]
    [InlineData(1280, 100)]
    [InlineData(1920, 100)]
    [InlineData(2560, 125)]
    [InlineData(3024, 150)]
    [InlineData(3840, 200)]
    public void DerivesScaleFromWidth(int width, int expectedScale)
    {
        Assert.Equal(expectedScale, RdpGeometry.DeriveScaleFactor(width));
        Assert.Contains($"desktopscalefactor:i:{expectedScale}", BuildWith(width, 1080));
    }

    // ── Scale overrides (issue #70) ────────────────────────────────────
    //
    // The heuristic only knows the width. A tablet renders its own UI at about 200%, so the
    // user has to be able to say what the seat should use.

    [Theory]
    [InlineData(1280)]
    [InlineData(1920)]   // derives 100 — the iPad case from #70
    [InlineData(3840)]   // derives 200 — the override must win even when it is SMALLER
    public void SeatOverride_IsUsedWhateverTheWidth(int width)
    {
        var geometry = RdpGeometry.ForSeat(width, 1080, seatScale: 175, defaultScale: null);

        Assert.Equal(175, geometry.ScaleFactor);
        Assert.Equal(ScaleFactorSource.Seat, geometry.ScaleSource);
        Assert.Contains("desktopscalefactor:i:175", RdpFileBuilder.Build(AudioMode.SharedHost, geometry));
    }

    [Fact]
    public void SeatOverride_BeatsTheHostDefault()
    {
        var geometry = RdpGeometry.ForSeat(1920, 1080, seatScale: 250, defaultScale: 150);

        Assert.Equal(250, geometry.ScaleFactor);
        Assert.Equal(ScaleFactorSource.Seat, geometry.ScaleSource);
    }

    [Theory]
    [InlineData(1280)]
    [InlineData(1920)]
    [InlineData(3840)]
    public void HostDefault_IsUsedWhenTheSeatHasNoOverride(int width)
    {
        var geometry = RdpGeometry.ForSeat(width, 1080, seatScale: null, defaultScale: 300);

        Assert.Equal(300, geometry.ScaleFactor);
        Assert.Equal(ScaleFactorSource.HostDefault, geometry.ScaleSource);
        Assert.Contains("desktopscalefactor:i:300", RdpFileBuilder.Build(AudioMode.SharedHost, geometry));
    }

    // With neither override the result must be exactly what it was before #70. These are the
    // same width/scale pairs DerivesScaleFromWidth pins, plus each threshold's edge, so a change
    // to the heuristic's path through ForSeat shows up here and not only in production.
    [Theory]
    [InlineData(1280, 100)]
    [InlineData(1920, 100)]
    [InlineData(1921, 125)]
    [InlineData(2560, 125)]
    [InlineData(2561, 150)]
    [InlineData(3024, 150)]
    [InlineData(3200, 150)]
    [InlineData(3201, 200)]
    [InlineData(3840, 200)]
    public void NoOverride_FallsBackToTheWidthHeuristic(int width, int expectedScale)
    {
        var geometry = RdpGeometry.ForSeat(width, 1080, seatScale: null, defaultScale: null);

        Assert.Equal(expectedScale, geometry.ScaleFactor);
        Assert.Equal(ScaleFactorSource.Derived, geometry.ScaleSource);
        Assert.Equal(RdpGeometry.ForClient(width, 1080), geometry);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(110)]    // between two allowed values: refused, not rounded to 100 or 125
    [InlineData(600)]
    [InlineData(-200)]
    public void ScalesRdpWouldIgnore_AreRefusedNotClamped(int scale)
    {
        Assert.False(RdpGeometry.IsAllowedScaleFactor(scale));
        Assert.Throws<ArgumentException>(() => RdpGeometry.ForSeat(1920, 1080, scale, null));
        Assert.Throws<ArgumentException>(() => RdpGeometry.ForSeat(1920, 1080, null, scale));
    }

    [Fact]
    public void EveryAllowedScale_ReachesTheFile()
    {
        foreach (var scale in RdpGeometry.AllowedScales)
        {
            var rdp = RdpFileBuilder.Build(AudioMode.SharedHost, RdpGeometry.ForSeat(1920, 1080, scale, null));
            Assert.Contains($"desktopscalefactor:i:{scale}", rdp);
        }

        Assert.Equal(new[] { 100, 125, 150, 175, 200, 250, 300, 400, 500 }, RdpGeometry.AllowedScales);
    }

    [Fact]
    public void TheErrorNamesTheValuesThatWouldWork()
    {
        var message = RdpGeometry.ScaleFactorError(110);

        Assert.Contains("110", message);
        Assert.Contains("100, 125, 150, 175, 200, 250, 300, 400, 500", message);
    }

    // ── Audio mode must keep working alongside the new keys ───────────

    [Theory]
    // SharedHost stays i:1 even though i:1 is what wedges the host's audio endpoint stack: i:2
    // avoids the wedge but hides the host's audio devices from the session, and SharedHost has
    // nothing to capture without them (measured 2026-08-19 — 14 endpoints visible under i:1,
    // 0 under i:2). See the trade-off documented in RdpFileBuilder.
    [InlineData(AudioMode.SharedHost, "audiomode:i:1")]
    [InlineData(AudioMode.PerSession, "audiomode:i:0")]
    public void KeepsAudioModeIndependentOfGeometry(AudioMode mode, string expected)
    {
        Assert.Contains(expected, RdpFileBuilder.Build(mode, RdpGeometry.ForClient(1920, 1080)));
        Assert.Contains(expected, RdpFileBuilder.Build(mode, geometry: null));
    }

    // audiocapturemode triggers a Windows mic-access dialog the dismisser cannot catch, which
    // hangs the RDP connection outright.
    [Fact]
    public void NeverRequestsMicrophoneRedirection()
    {
        Assert.DoesNotContain("audiocapturemode", BuildWith(1920, 1080));
    }
}
