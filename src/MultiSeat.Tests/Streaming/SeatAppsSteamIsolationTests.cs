using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Streaming;

/// <summary>
/// The seat's seeded apps.json, end to end through <see cref="ApolloConfigBuilder.BuildConfig"/>:
/// does the Steam tile reach the seat through the launcher, and only when isolation can work.
/// </summary>
public sealed class SeatAppsSteamIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MultiSeat Apps " + Guid.NewGuid());
    private readonly string _apollo;
    private readonly string _isolation;
    private readonly string _steam;

    public SeatAppsSteamIsolationTests()
    {
        _apollo = Path.Combine(_root, "apollo");
        _isolation = Path.Combine(_root, "SteamIsolation");
        Directory.CreateDirectory(Path.Combine(_apollo, "config"));
        Directory.CreateDirectory(_isolation);
        _steam = Pe(Path.Combine(_root, "Steam", "steam.exe"), Machine.Amd64);
        File.WriteAllText(Path.Combine(_apollo, "config", "apps.json"), """
            { "apps": [
                { "name": "Desktop", "image-path": "desktop.png" },
                { "name": "Steam Big Picture", "detached": [ "steam://open/bigpicture" ],
                  "prep-cmd": [ { "do": "", "undo": "steam://close/bigpicture" } ] }
            ] }
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Launcher => Path.Combine(_isolation, "MultiSeat.SteamLauncher.exe");

    private JsonArray Seed(bool enabled, bool withComponents)
    {
        if (withComponents)
        {
            Pe(Launcher, Machine.Amd64);
            Pe(Path.Combine(_isolation, "MultiSeat.SteamHook.dll"), Machine.Amd64);
        }

        var options = new MultiSeatOptions
        {
            ApolloExePath = Path.Combine(_apollo, "sunshine.exe"),
            SteamIsolationEnabled = enabled,
            SteamIsolationLauncherPath = Launcher,
        };
        var builder = new ApolloConfigBuilder(new TestLogger<ApolloConfigBuilder>(), Options.Create(options))
        {
            SteamExecutableResolver = () => _steam,
        };

        var configDir = Path.Combine(_root, "seats");
        Directory.CreateDirectory(configDir);
        builder.BuildConfig(new SeatInfo { AccountName = "MultiSeatSeat01", PortBase = 47984 }, configDir);

        var seeded = File.ReadAllText(Path.Combine(configDir, "MultiSeatSeat01", "config", "apps.json"));
        return (JsonArray)JsonNode.Parse(seeded)!["apps"]!;
    }

    [Fact]
    public void Enabled_SteamTileStartsThroughTheLauncher()
    {
        var apps = Seed(enabled: true, withComponents: true);

        Assert.Equal($"\"{Launcher}\" --steam \"{_steam}\" -- steam://open/bigpicture", (string)apps[1]!["cmd"]!);
        Assert.Null(apps[1]!["detached"]);
        Assert.Null(apps[0]!["cmd"]);
    }

    [Fact]
    public void Disabled_AppListIsAsTheConsoleHasIt()
    {
        var apps = Seed(enabled: false, withComponents: true);

        Assert.Null(apps[1]!["cmd"]);
        Assert.Equal("steam://open/bigpicture", (string)apps[1]!["detached"]![0]!);
    }

    // The service's own launch refuses when a component is missing. For the app list the entry is
    // kept (and a warning logged) so a seat is never left without its Steam tile.
    [Fact]
    public void Enabled_WithoutTheLauncher_KeepsTheEntryUnchanged()
    {
        var apps = Seed(enabled: true, withComponents: false);

        Assert.Null(apps[1]!["cmd"]);
        Assert.Equal("steam://open/bigpicture", (string)apps[1]!["detached"]![0]!);
    }

    // Minimal PE headers, never executed: the preflight reads only the machine type.
    private static string Pe(string path, Machine machine)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[1024];
        using (var writer = new BinaryWriter(new MemoryStream(bytes)))
        {
            writer.Write((ushort)0x5a4d);
            writer.BaseStream.Position = 0x3c;
            writer.Write(0x80);
            writer.BaseStream.Position = 0x80;
            writer.Write(0x00004550);
            writer.Write((ushort)machine);
            writer.BaseStream.Position = 0x94;
            writer.Write((ushort)(machine == Machine.Amd64 ? 240 : 224));
            writer.Write((ushort)0x0002);
            writer.Write((ushort)(machine == Machine.Amd64 ? 0x20b : 0x10b));
        }
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
