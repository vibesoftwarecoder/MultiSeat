using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Streaming;

/// <summary>
/// MultiSeat:SecondVideoStream decides whether a seat's Apollo offers a second video stream. A seat's
/// config is generated and rewritten on every provision, so the line has to be in the generated file,
/// written exactly once, and spelled the way ApolloVibe's parser reads it.
/// </summary>
public class SecondVideoStreamSeatConfigTests
{
    private static string[] BuildLines(MultiSeatOptions options)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"multiseat-svs-{Guid.NewGuid():N}");
        try
        {
            var builder = new ApolloConfigBuilder(
                new TestLogger<ApolloConfigBuilder>(), Options.Create(options));
            var seat = new SeatInfo { AccountName = "MultiSeatSeat01", PortBase = Constants.PortBase };
            return File.ReadAllText(builder.BuildConfig(seat, tempDir)).Split('\n').Select(l => l.Trim()).ToArray();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Default_IsOff()
    {
        Assert.False(new MultiSeatOptions().SecondVideoStream);
    }

    [Fact]
    public void Off_WritesDisabled_ExactlyOnce()
    {
        var lines = BuildLines(new MultiSeatOptions { SecondVideoStream = false });

        Assert.Single(lines, l => l == "second_video_stream = disabled");
        Assert.DoesNotContain(lines, l => l == "second_video_stream = enabled");
    }

    [Fact]
    public void On_WritesEnabled_ExactlyOnce()
    {
        var lines = BuildLines(new MultiSeatOptions { SecondVideoStream = true });

        Assert.Single(lines, l => l == "second_video_stream = enabled");
        Assert.DoesNotContain(lines, l => l == "second_video_stream = disabled");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyThisOneKey_IsWritten(bool on)
    {
        // A seat never gets a window or a display chosen by MultiSeat: the client names its own window,
        // and a whole display is not possible in an RDP session. No capture_window key is written either.
        var lines = BuildLines(new MultiSeatOptions { SecondVideoStream = on });

        Assert.Single(lines, l => l.StartsWith("second_video_stream"));
        Assert.DoesNotContain(lines, l => l.StartsWith("capture_window"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoesNotTouchUsbForwarding_WhichStaysOff(bool on)
    {
        var lines = BuildLines(new MultiSeatOptions { SecondVideoStream = on });

        Assert.Single(lines, l => l == "usb_forwarding_enabled = disabled");
    }
}
