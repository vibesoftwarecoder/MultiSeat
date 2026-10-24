using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Streaming;

/// <summary>
/// ApolloVibe has a USB tunnel on main port + 7, off by default. A forwarded USB device is a
/// machine-wide PnP device, not a per-session one, so a seat must never run a tunnel. These tests
/// pin the three things that keep it that way: +7 is a reserved offset that collides with nothing,
/// every seat config says "off" explicitly, and the firewall never opens +7 for a seat.
/// </summary>
public class UsbForwardingSeatSafetyTests
{
    private static readonly int[] OtherUsedOffsets =
    {
        Constants.OffsetGfeHttps, Constants.OffsetGfeHttp, Constants.OffsetWebUi,
        Constants.OffsetVideo, Constants.OffsetControl, Constants.OffsetAudio,
        Constants.OffsetMic, Constants.OffsetRtsp, Constants.OffsetRetroArchNetplay,
    };

    [Fact]
    public void Offset_IsSeven()
    {
        Assert.Equal(7, Constants.OffsetUsbForwarding);
    }

    [Fact]
    public void Offset_DoesNotEqualAnyOtherOffset_AndFitsTheBlock()
    {
        Assert.DoesNotContain(Constants.OffsetUsbForwarding, OtherUsedOffsets);
        Assert.InRange(Constants.OffsetUsbForwarding, 0, Constants.PortsPerSeat - 1);
    }

    [Fact]
    public void SeatUsbPort_NeverEqualsAnyOtherUsedPortOfAnySeat()
    {
        for (int seat = 0; seat < Constants.MaxSeats; seat++)
        {
            var usb = Constants.PortBase + seat * Constants.PortsPerSeat + Constants.OffsetUsbForwarding;
            for (int other = 0; other < Constants.MaxSeats; other++)
            {
                var otherBase = Constants.PortBase + other * Constants.PortsPerSeat;
                foreach (var off in OtherUsedOffsets)
                    Assert.NotEqual(otherBase + off, usb);
                if (other != seat)
                    Assert.NotEqual(otherBase + Constants.OffsetUsbForwarding, usb);
            }
        }
    }

    [Fact]
    public void HostConsoleApollo_PortsAreOutsideEverySeatBlock()
    {
        // Stock Apollo: main port 47989, HTTPS at -5, its own USB tunnel at +7 (47996), up to RTSP at +26.
        // ASSUMPTION: the console Apollo keeps its default port (47989) and MultiSeat:PortBase keeps 48100.
        // Both can be overridden by config; this test proves the shipped defaults only, it cannot see a host override.
        const int hostMain = 47989;
        int[] hostOffsets = OtherUsedOffsets.Append(Constants.OffsetUsbForwarding).ToArray();
        Assert.Equal(47996, hostMain + Constants.OffsetUsbForwarding);

        var seatPorts = new HashSet<int>();
        for (int seat = 0; seat < Constants.MaxSeats; seat++)
        {
            var b = Constants.PortBase + seat * Constants.PortsPerSeat;
            foreach (var off in hostOffsets) seatPorts.Add(b + off);
        }

        var firstSeatLow = Constants.PortBase + hostOffsets.Min();
        foreach (var off in hostOffsets)
        {
            var p = hostMain + off;
            Assert.DoesNotContain(p, seatPorts);
            Assert.True(p < firstSeatLow, $"host port {p} reaches into the seat blocks (first used seat port {firstSeatLow})");
        }
    }

    // ── Seat config ───────────────────────────────────────────────────

    private static string BuildConf(MultiSeatOptions options)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"multiseat-usb-{Guid.NewGuid():N}");
        try
        {
            var builder = new ApolloConfigBuilder(
                new TestLogger<ApolloConfigBuilder>(), Options.Create(options));
            var seat = new SeatInfo { AccountName = "MultiSeatSeat01", PortBase = Constants.PortBase };
            return File.ReadAllText(builder.BuildConfig(seat, tempDir));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static IEnumerable<object[]> OptionSets()
    {
        yield return new object[] { new MultiSeatOptions() };
        yield return new object[] { new MultiSeatOptions { EnableEmulatorNetplay = false } };
        yield return new object[] { new MultiSeatOptions { AudioMode = AudioMode.SharedHost } };
    }

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void SeatConfig_KeepsUsbForwardingOff_AndWritesNoPort(MultiSeatOptions options)
    {
        var lines = BuildConf(options).Split('\n').Select(l => l.Trim()).ToArray();

        Assert.Single(lines, l => l == "usb_forwarding_enabled = disabled");
        Assert.DoesNotContain(lines, l => l.StartsWith("usb_forwarding_port"));
        Assert.DoesNotContain(lines, l => l.StartsWith("usb_forwarding_require_stream"));
        // No other spelling of "on" for the key.
        Assert.DoesNotContain(lines, l => l.StartsWith("usb_forwarding_enabled") && l != "usb_forwarding_enabled = disabled");
    }

    // ── Firewall ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Firewall_NeverOpensTheUsbPort_ForAnySeat(bool netplay)
    {
        for (int seat = 0; seat < Constants.MaxSeats; seat++)
        {
            var b = Constants.PortBase + seat * Constants.PortsPerSeat;
            var usb = b + Constants.OffsetUsbForwarding;
            Assert.DoesNotContain(usb, FirewallManager.TcpPorts(b, netplay));
            Assert.DoesNotContain(usb, FirewallManager.UdpPorts(b));
        }
    }
}
