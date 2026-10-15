using System.Text.Json.Nodes;
using MultiSeat.Service.Streaming;
using Xunit;

namespace MultiSeat.Tests.Streaming;

public class SteamAppListRewriterTests
{
    private const string Launcher = @"C:\Program Files\MultiSeat\SteamIsolation\MultiSeat.SteamLauncher.exe";
    private const string Steam = @"C:\Program Files (x86)\Steam\steam.exe";

    private static JsonObject App(string json, out int rewritten, string? launcher = null)
    {
        var result = SteamAppListRewriter.Rewrite(json, launcher ?? Launcher, Steam, out rewritten);
        return (JsonObject)JsonNode.Parse(result)!["apps"]![0]!;
    }

    // The stock "Steam Big Picture" entry: a steam:// link in detached, an undo that closes it.
    private const string StockBigPicture = """
        { "apps": [ {
            "name": "Steam Big Picture",
            "detached": [ "steam://open/bigpicture" ],
            "prep-cmd": [ { "do": "", "undo": "steam://close/bigpicture", "elevated": false } ],
            "image-path": "steam.png"
        } ] }
        """;

    [Fact]
    public void StockBigPicture_MovesTheLinkToCmdThroughTheLauncher()
    {
        var app = App(StockBigPicture, out var count);

        Assert.Equal(1, count);
        Assert.Equal($"\"{Launcher}\" --steam \"{Steam}\" -- steam://open/bigpicture", (string)app["cmd"]!);
        // The supervisor must be the app's own process, so nothing is left in detached.
        Assert.Null(app["detached"]);
        Assert.Equal("steam.png", (string)app["image-path"]!);
    }

    [Fact]
    public void StockBigPicture_BlanksTheUndoThatWouldRunUnwrapped()
    {
        var app = App(StockBigPicture, out _);

        var prep = (JsonObject)app["prep-cmd"]![0]!;
        Assert.Equal("", (string)prep["undo"]!);
    }

    [Theory]
    [InlineData(@"steam.exe -bigpicture", Steam, "-bigpicture")]
    [InlineData(@"steam", Steam, "")]
    [InlineData(@"""C:\Games\Steam\steam.exe"" -silent", @"C:\Games\Steam\steam.exe", "-silent")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe -bigpicture", @"C:\Program Files (x86)\Steam\steam.exe", "-bigpicture")]
    [InlineData(@"STEAM.EXE steam://rungameid/730", Steam, "steam://rungameid/730")]
    public void SteamCmd_IsWrappedWithItsArgumentsKept(string cmd, string expectedSteam, string expectedArgs)
    {
        var json = new JsonObject
        {
            ["apps"] = new JsonArray(new JsonObject { ["name"] = "Steam", ["cmd"] = cmd })
        }.ToJsonString();

        var app = App(json, out var count);

        var expected = $"\"{Launcher}\" --steam \"{expectedSteam}\" --" + (expectedArgs.Length == 0 ? "" : " " + expectedArgs);
        Assert.Equal(1, count);
        Assert.Equal(expected, (string)app["cmd"]!);
    }

    [Theory]
    [InlineData(@"notsteam.exe")]
    [InlineData(@"steamcmd.exe +login x")]
    [InlineData(@"C:\Emu\EmulationStation.exe")]
    [InlineData(@"cmd /c start steam://open/bigpicture")]
    [InlineData(@"steam.exe.bak")]
    [InlineData(@"..\Steam\steam.exe")]
    public void OtherCommands_AreLeftAlone(string cmd)
    {
        var json = new JsonObject
        {
            ["apps"] = new JsonArray(new JsonObject { ["name"] = "X", ["cmd"] = cmd })
        }.ToJsonString();

        var result = SteamAppListRewriter.Rewrite(json, Launcher, Steam, out var count);

        Assert.Equal(0, count);
        Assert.Equal(json, result);
    }

    [Fact]
    public void DesktopEntry_IsUntouched()
    {
        const string json = """{ "apps": [ { "name": "Desktop", "image-path": "desktop.png" } ] }""";

        var result = SteamAppListRewriter.Rewrite(json, Launcher, Steam, out var count);

        Assert.Equal(0, count);
        Assert.Equal(json, result);
    }

    [Fact]
    public void AppWithItsOwnNonSteamCmd_KeepsSteamLinksInDetached()
    {
        // A game launcher that happens to open a steam:// link on the side is not a Steam app.
        const string json = """
            { "apps": [ { "name": "Game", "cmd": "C:\\Games\\game.exe", "detached": [ "steam://open/main" ] } ] }
            """;

        var result = SteamAppListRewriter.Rewrite(json, Launcher, Steam, out var count);

        Assert.Equal(0, count);
        Assert.Equal(json, result);
    }

    [Fact]
    public void OtherDetachedItems_Survive()
    {
        const string json = """
            { "apps": [ { "name": "S", "detached": [ "steam://open/bigpicture", "C:\\tools\\helper.exe" ] } ] }
            """;

        var app = App(json, out var count);

        Assert.Equal(1, count);
        var detached = (JsonArray)app["detached"]!;
        Assert.Single(detached);
        Assert.Equal(@"C:\tools\helper.exe", (string)detached[0]!);
    }

    [Fact]
    public void OnlySteamEntries_AreCounted()
    {
        const string json = """
            { "apps": [
                { "name": "Desktop" },
                { "name": "Steam", "detached": [ "steam://open/bigpicture" ] },
                { "name": "Other", "cmd": "other.exe" },
                { "name": "Steam 2", "cmd": "steam.exe" }
            ] }
            """;

        var result = SteamAppListRewriter.Rewrite(json, Launcher, Steam, out var count);

        Assert.Equal(2, count);
        var apps = (JsonArray)JsonNode.Parse(result)!["apps"]!;
        Assert.Null(apps[0]!["cmd"]);
        Assert.Equal("other.exe", (string)apps[2]!["cmd"]!);
    }

    [Fact]
    public void RewriteTwice_DoesNotWrapTheLauncherAgain()
    {
        var once = SteamAppListRewriter.Rewrite(StockBigPicture, Launcher, Steam, out _);

        var twice = SteamAppListRewriter.Rewrite(once, Launcher, Steam, out var count);

        Assert.Equal(0, count);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void UnparseableJson_PassesThrough()
    {
        const string garbage = "{ this is not json";

        var result = SteamAppListRewriter.Rewrite(garbage, Launcher, Steam, out var count);

        Assert.Equal(garbage, result);
        Assert.Equal(0, count);
    }
}
