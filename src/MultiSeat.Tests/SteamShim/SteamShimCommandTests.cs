using MultiSeat.Service.SteamShim;
using Xunit;

namespace MultiSeat.Tests.SteamShim;

/// <summary>Reading and writing the command lines that start Steam, with no registry or shortcut involved.</summary>
public sealed class SteamShimCommandTests
{
    private const string Steam = @"C:\Program Files (x86)\Steam\steam.exe";
    private const string Shim = @"C:\Program Files\MultiSeat\SteamIsolation\MultiSeat.SteamShim.exe";

    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -- \"%1\"", "-- \"%1\"")]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent", "-silent")]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\"", "")]
    [InlineData("C:\\Program Files (x86)\\Steam\\steam.exe -silent", "-silent")]
    [InlineData("  \"C:\\Program Files (x86)\\Steam\\STEAM.EXE\"   -applaunch 730  ", "-applaunch 730")]
    public void ParsesTheWaysSteamIsWritten(string command, string expectedRest)
    {
        Assert.True(SteamShimCommand.TryParseSteam(command, out var exe, out var rest));
        Assert.Equal(Steam, exe, ignoreCase: true);
        Assert.Equal(expectedRest, rest);
    }

    [Theory]
    [InlineData("steam.exe -silent")]                                  // relative: the shim needs the real path
    [InlineData("\"C:\\Tools\\notsteam.exe\" -silent")]
    [InlineData("\"C:\\Tools\\runner.exe\" \"C:\\Steam\\steam.exe\"")]  // Steam only as someone else's argument
    [InlineData("\"C:\\Steam\\steam.exe:evil\" -silent")]               // a named stream is not Steam
    [InlineData("\"C:\\Steam\\steam.exe -silent")]                      // unclosed quote
    [InlineData("")]
    [InlineData(null)]
    public void RefusesAnythingThatIsNotAnAbsoluteSteam(string? command)
    {
        Assert.False(SteamShimCommand.TryParseSteam(command, out _, out _));
    }

    [Theory]
    [InlineData("\"C:\\Steam\\steam.exe.\" -silent", "C:\\Steam\\steam.exe")]
    [InlineData("\"C:\\Steam\\steam.exe::$DATA\" -silent", "C:\\Steam\\steam.exe")]
    public void NamesWindowsResolvesToSteamExeCountAsSteam(string command, string expected)
    {
        // These start Steam, so leaving them out would leave a way around the shim.
        Assert.True(SteamShimCommand.TryParseSteam(command, out var exe, out _));
        Assert.Equal(expected, exe);
    }

    [Fact]
    public void TheShimCommandKeepsTheOriginalArgumentsAfterTheDoubleDash()
    {
        Assert.Equal($"\"{Shim}\" --steam \"{Steam}\" -- -- \"%1\"", SteamShimCommand.ShimCommand(Shim, Steam, "-- \"%1\""));
        Assert.Equal($"\"{Shim}\" --steam \"{Steam}\" -- -silent", SteamShimCommand.ShimCommand(Shim, Steam, "-silent"));
        Assert.Equal($"\"{Shim}\" --steam \"{Steam}\" --", SteamShimCommand.ShimCommand(Shim, Steam, ""));
        Assert.Equal($"--steam \"{Steam}\" -- -bigpicture", SteamShimCommand.ShimArguments(Steam, " -bigpicture "));
    }

    [Fact]
    public void AShimmedCommandParsesBackToTheOriginal()
    {
        // The installer must be able to tell an entry it already changed from one it has not touched.
        var shimmed = SteamShimCommand.ShimCommand(Shim, Steam, "-- \"%1\"");
        Assert.True(SteamShimCommand.StartsShim(shimmed, Shim));
        Assert.True(SteamShimCommand.StartsShim(shimmed.ToUpperInvariant(), Shim.ToUpperInvariant()));
        Assert.False(SteamShimCommand.StartsShim($"\"{Steam}\" -silent", Shim));
        Assert.False(SteamShimCommand.StartsShim($"\"{Shim}2.exe\" -silent", Shim));
        Assert.False(SteamShimCommand.StartsShim(null, Shim));
        // and the shim is not itself taken for Steam
        Assert.False(SteamShimCommand.TryParseSteam(shimmed, out _, out _));
    }

    [Theory]
    [InlineData("", @"C:\Program Files (x86)\Steam\steam.exe,0")]
    [InlineData(",3", @"C:\Program Files (x86)\Steam\steam.exe,3")]
    [InlineData(@"C:\Icons\steam.ico,0", @"C:\Icons\steam.ico,0")]
    public void ThePinnedIconKeepsTheShortcutShowingSteam(string current, string expected)
    {
        Assert.Equal(expected, SteamShimCommand.PinnedIcon(current, Steam));
    }
}
