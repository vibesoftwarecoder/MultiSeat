using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Streaming;

/// <summary>
/// G11: startup string options with a documented Apollo-side whitelist must not reach
/// sunshine.conf verbatim when they name nothing Apollo understands. <c>MultiSeat:Encoder</c>
/// accepts nvenc/quicksync/amdvce/software and <c>MultiSeat:ApolloLogLevel</c> accepts
/// verbose/debug/info/warning/error (per their own option comments); anything else used to
/// be written into the live config, so a typo silently broke every seat provisioned under
/// it. Invalid values now warn and keep the documented default; startup continues.
/// </summary>
public class ApolloConfigValidationTests
{
    [Theory]
    [InlineData("nvenc")]
    [InlineData("quicksync")]
    [InlineData("amdvce")]
    [InlineData("software")]
    public void ValidEncoder_IsAcceptedUnchanged(string encoder)
    {
        var content = BuildConf(new MultiSeatOptions { Encoder = encoder });

        Assert.Contains($"encoder = {encoder}", content);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("NVENC")]   // Apollo's keys are lowercase; only documented spellings pass
    public void InvalidEncoder_FallsBackToDefault(string encoder)
    {
        var content = BuildConf(new MultiSeatOptions { Encoder = encoder });

        Assert.Contains("encoder = nvenc", content);
        Assert.DoesNotContain($"encoder = {encoder}", content);
    }

    [Fact]
    public void PaddedEncoder_IsTrimmedThenAccepted()
    {
        // Pre-existing trim behavior is preserved: whitespace never forces a fallback.
        var content = BuildConf(new MultiSeatOptions { Encoder = " nvenc " });

        Assert.Contains("encoder = nvenc", content);
    }

    [Theory]
    [InlineData("verbose")]
    [InlineData("debug")]
    [InlineData("info")]
    [InlineData("warning")]
    [InlineData("error")]
    public void ValidLogLevel_IsAcceptedUnchanged(string level)
    {
        var content = BuildConf(new MultiSeatOptions { ApolloLogLevel = level });

        Assert.Contains($"min_log_level = {level}", content);
    }

    [Theory]
    [InlineData("chatty")]
    [InlineData("INFO")]
    [InlineData("")]
    public void InvalidLogLevel_FallsBackToDefault(string level)
    {
        var content = BuildConf(new MultiSeatOptions { ApolloLogLevel = level });

        Assert.Contains("min_log_level = info", content);
        if (level.Length > 0)
            Assert.DoesNotContain($"min_log_level = {level}", content);
    }

    private static string BuildConf(MultiSeatOptions options)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"multiseat-test-{Guid.NewGuid():N}");
        try
        {
            var builder = new ApolloConfigBuilder(
                new TestLogger<ApolloConfigBuilder>(), Options.Create(options));
            var seat = new SeatInfo { AccountName = "MultiSeatSeat01", PortBase = 47984 };
            return File.ReadAllText(builder.BuildConfig(seat, tempDir));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
