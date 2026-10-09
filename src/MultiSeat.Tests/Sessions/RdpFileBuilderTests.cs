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

    // ── Does mstsc actually SEND the scale? (issue #93) ────────────────
    //
    // Writing desktopscalefactor is not the same as the session getting it. mstsc forwards the
    // scale from the file only under rules read from its own code (mstsc.exe and mstscax.dll
    // 10.0.26100.9444):
    //
    //   1. The file loader reads "DesktopScaleFactor" and "DeviceScaleFactor" as integers that
    //      default to 0 when the key is absent.
    //   2. Before connecting, mstsc sets the control's DeviceScaleFactor and DesktopScaleFactor
    //      only when BOTH are non-zero. Otherwise it sets neither, and the control sends the
    //      scale of the console monitor its window is on.
    //   3. The control rejects (E_INVALIDARG) a DeviceScaleFactor other than 100, 140 or 180,
    //      and a DesktopScaleFactor below 100. Since mstsc sets the device scale first and only
    //      sets the desktop scale when that succeeded, a bad device scale drops both.
    //   4. MS-RDPBCGR 2.2.1.3.2: the server ignores desktopScaleFactor above 500%.
    //
    // MstscSendsScale models exactly that, and returns the scale the server receives, or null
    // when mstsc falls back to the console's. Master before this fix wrote no devicescalefactor,
    // so rule 2 dropped every requested scale; that is what the live seat showed.

    /// <summary>The desktop scale mstsc would send for this file, or null for "the console's".</summary>
    private static int? MstscSendsScale(string rdp)
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in rdp.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':', 3);
            if (parts.Length == 3 && parts[1] == "i" && int.TryParse(parts[2], out var v))
                values[parts[0]] = v;
        }

        var desktop = values.GetValueOrDefault("desktopscalefactor");   // rule 1: absent = 0
        var device = values.GetValueOrDefault("devicescalefactor");

        if (desktop == 0 || device == 0) return null;                     // rule 2
        if (device is not (100 or 140 or 180)) return null;               // rule 3, device first
        if (desktop < 100) return null;                                    // rule 3
        if (desktop > 500) return null;                                    // rule 4
        return desktop;
    }

    // The model has to reproduce what the live seat did, or it proves nothing. This is the file
    // master wrote for the 150% test on 2026-10-08 (audio PerSession), as read back from the
    // Default.rdp mstsc loaded. It carried desktopscalefactor:i:150, and the session, even when
    // created fresh, ran at 100%, the console's scale.
    [Fact]
    public void TheModelReproducesTheLiveFailure()
    {
        const string fileMasterWrote =
            "authentication level:i:0\r\nprompt for credentials:i:0\r\naudiomode:i:0\r\n" +
            "session bpp:i:8\r\nconnection type:i:1\r\ndisable wallpaper:i:1\r\n" +
            "disable full window drag:i:1\r\ndisable menu anims:i:1\r\ndisable themes:i:1\r\n" +
            "allow font smoothing:i:0\r\nallow desktop composition:i:0\r\n" +
            "desktopwidth:i:1920\r\ndesktopheight:i:1080\r\ndesktopscalefactor:i:150\r\n" +
            "smart sizing:i:0\r\ndynamic resolution:i:0\r\nscreen mode id:i:1\r\n";

        Assert.Null(MstscSendsScale(fileMasterWrote));
        Assert.Equal(150, MstscSendsScale(fileMasterWrote + "devicescalefactor:i:100\r\n"));
        Assert.Null(MstscSendsScale(fileMasterWrote + "devicescalefactor:i:120\r\n"));   // rule 3
    }

    [Fact]
    public void EveryAllowedScale_IsActuallySentByMstsc()
    {
        foreach (var scale in RdpGeometry.AllowedScales)
        {
            var rdp = RdpFileBuilder.Build(AudioMode.PerSession, RdpGeometry.ForSeat(1920, 1080, scale, null));
            Assert.Equal(scale, MstscSendsScale(rdp));
        }
    }

    [Theory]
    [InlineData(AudioMode.SharedHost)]
    [InlineData(AudioMode.PerSession)]
    public void TheDeviceScaleIsTheNeutralOne(AudioMode mode)
    {
        var rdp = RdpFileBuilder.Build(mode, RdpGeometry.ForClient(1920, 1080));

        Assert.Equal(100, RdpFileBuilder.DeviceScaleFactor);
        Assert.Contains("devicescalefactor:i:100\r\n", rdp);
    }

    // Without a geometry mstsc is meant to pick its own size AND scale, as it always has.
    [Fact]
    public void NoGeometry_LeavesTheScaleToMstsc()
    {
        var rdp = RdpFileBuilder.Build(AudioMode.PerSession, geometry: null);

        Assert.DoesNotContain("scalefactor", rdp);
        Assert.Null(MstscSendsScale(rdp));
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
