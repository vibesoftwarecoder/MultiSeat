using MultiSeat.Service.Streaming;
using Xunit;

namespace MultiSeat.Tests.Streaming;

public class ApolloLogScanTests
{
    private static readonly DateTime Start = new(2026, 10, 7, 9, 13, 35);

    [Fact]
    public void FindsTheNoEncoderLineWrittenAfterLaunch()
    {
        var log = "[2026-10-07 09:13:36.100]: Info: Trying encoder [nvenc]\n" +
                  "[2026-10-07 09:13:49.035]: Error: Video failed to find working encoder: probe failed\n";
        Assert.Contains("Video failed to find working encoder", ApolloLogScan.FindNoEncoderLine(log, Start));
    }

    [Fact]
    public void IgnoresALineLeftByAnEarlierRunOfTheSameLog()
    {
        var log = "[2026-10-06 21:00:00.000]: Error: Video failed to find working encoder: probe failed\n" +
                  "[2026-10-07 09:13:36.100]: Info: Trying encoder [nvenc]\n";
        Assert.Null(ApolloLogScan.FindNoEncoderLine(log, Start));
    }

    [Fact]
    public void IgnoresLinesWithoutAReadableStamp()
    {
        Assert.Null(ApolloLogScan.FindNoEncoderLine("Error: Video failed to find working encoder\n", Start));
    }

    [Fact]
    public void HealthyLogReturnsNull()
    {
        var log = "[2026-10-07 09:13:36.100]: Info: Found H.264 encoder: h264_nvenc [nvenc]\n";
        Assert.Null(ApolloLogScan.FindNoEncoderLine(log, Start));
    }
}
