using MultiSeat.Service.Streaming;
using Xunit;

namespace MultiSeat.Tests.Streaming;

/// <summary>
/// <c>ApolloOwnership.CommandLineNamesConfig</c> picks the Apollo processes that serve ONE seat, so
/// the service can kill a stray copy before it launches the seat's Apollo or while it stops it
/// (issue #124). It is the path that kills, so a false match ends someone else's stream: another
/// seat's, or the console player's. These pin the exact-path rule.
/// </summary>
public class ApolloSeatConfigMatchTests
{
    private const string GamingConfig = @"C:\ProgramData\MultiSeat\apollo\Gaming\sunshine.conf";

    private const string OurExe = @"""C:\Program Files\ApolloVibe\sunshine.exe""";

    [Fact]
    public void SeatApolloWithThisSeatsConfig_Matches()
    {
        // The command line ProcessInjector.ApolloLaunchRequestFor produces.
        Assert.True(ApolloOwnership.CommandLineNamesConfig(
            $@"{OurExe} ""{GamingConfig}""", GamingConfig));
    }

    [Fact]
    public void MatchIgnoresCase()
    {
        Assert.True(ApolloOwnership.CommandLineNamesConfig(
            $@"{OurExe} ""c:\programdata\multiseat\APOLLO\gaming\SUNSHINE.CONF""", GamingConfig));
    }

    [Fact]
    public void UnquotedConfigPathWithoutSpaces_Matches()
    {
        Assert.True(ApolloOwnership.CommandLineNamesConfig(
            $@"{OurExe} {GamingConfig}", GamingConfig));
    }

    [Fact]
    public void NearMissSeat_Gaming2_DoesNotMatchGaming()
    {
        // Another seat whose name starts with this one's. A substring test on the seat directory
        // would claim it; the whole-path comparison must not.
        Assert.False(ApolloOwnership.CommandLineNamesConfig(
            $@"{OurExe} ""C:\ProgramData\MultiSeat\apollo\Gaming2\sunshine.conf""", GamingConfig));
    }

    [Fact]
    public void NearMissSeat_GamingIsNotMatchedByGaming2sConfig()
    {
        // The same pair the other way round.
        Assert.False(ApolloOwnership.CommandLineNamesConfig(
            $@"{OurExe} ""{GamingConfig}""", @"C:\ProgramData\MultiSeat\apollo\Gaming2\sunshine.conf"));
    }

    [Fact]
    public void ConfigPathAsAPrefixOfALongerArgument_DoesNotMatch()
    {
        Assert.False(ApolloOwnership.CommandLineNamesConfig(
            $@"{OurExe} ""{GamingConfig}.bak""", GamingConfig));
    }

    [Theory]
    // The standalone console Apollo on the reference host: launched by its own service, no config
    // argument at all.
    [InlineData(@"""C:\Program Files\Apollo\sunshine.exe""")]
    // The same Apollo given its own config, relative and absolute.
    [InlineData(@"""C:\Program Files\Apollo\sunshine.exe"" config\sunshine.conf")]
    [InlineData(@"""C:\Program Files\Apollo\sunshine.exe"" ""C:\Program Files\Apollo\config\sunshine.conf""")]
    public void ConsoleApollo_NeverMatches(string consoleCmdLine)
    {
        Assert.False(ApolloOwnership.CommandLineNamesConfig(consoleCmdLine, GamingConfig));
    }

    [Fact]
    public void ConfigPathInTheExecutablePosition_DoesNotMatch()
    {
        // The first argument is the program, never its config.
        Assert.False(ApolloOwnership.CommandLineNamesConfig($@"""{GamingConfig}""", GamingConfig));
    }

    [Theory]
    [InlineData(null, GamingConfig)]
    [InlineData("", GamingConfig)]
    [InlineData("   ", GamingConfig)]
    [InlineData(@"""C:\Program Files\ApolloVibe\sunshine.exe"" ""C:\ProgramData\MultiSeat\apollo\Gaming\sunshine.conf""", null)]
    [InlineData(@"""C:\Program Files\ApolloVibe\sunshine.exe"" ""C:\ProgramData\MultiSeat\apollo\Gaming\sunshine.conf""", "")]
    // A relative "config path" would resolve against the service's own working directory.
    [InlineData(@"""C:\Program Files\ApolloVibe\sunshine.exe"" sunshine.conf", "sunshine.conf")]
    public void UnknownInputs_MatchNothing(string? cmdLine, string? configPath)
    {
        // An empty config path is the dangerous one: "anything".Contains("") is true.
        Assert.False(ApolloOwnership.CommandLineNamesConfig(cmdLine, configPath));
    }

    [Fact]
    public void SplitCommandLine_FollowsTheWindowsQuoteRules()
    {
        Assert.Equal(
            [@"C:\Program Files\ApolloVibe\sunshine.exe", @"C:\a b\c.conf", "x"],
            ApolloOwnership.SplitCommandLine(@"""C:\Program Files\ApolloVibe\sunshine.exe""  ""C:\a b\c.conf"" x"));

        // Backslashes are literal unless they precede a quote.
        Assert.Equal([@"a\\b", @"c\""d"], ApolloOwnership.SplitCommandLine(@"a\\b c\\\""d"));
    }
}
