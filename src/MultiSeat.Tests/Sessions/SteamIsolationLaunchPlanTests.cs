using System.Reflection.PortableExecutable;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Sessions;
using Xunit;

namespace MultiSeat.Tests.Sessions;

public sealed class SteamIsolationLaunchPlanTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MultiSeat Steam " + Guid.NewGuid());

    public SteamIsolationLaunchPlanTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void DisabledPreservesSteamCommandWithoutRequiringComponents()
    {
        var plan = SteamIsolationLaunchPlan.Create("steam.exe", "-bigpicture", new());
        Assert.Equal("steam.exe", plan.ExecutablePath);
        Assert.Equal("-bigpicture", plan.Arguments);
        Assert.False(plan.Supervised);
    }

    [Theory]
    [InlineData("steamwebhelper.exe")]
    [InlineData("my-steam.exe")]
    [InlineData("sunshine.exe")]
    public void EnabledDoesNotWrapOtherExecutables(string executable)
    {
        var plan = SteamIsolationLaunchPlan.Create(executable, "--unchanged", Enabled());
        Assert.Equal(executable, plan.ExecutablePath);
        Assert.Equal("--unchanged", plan.Arguments);
        Assert.False(plan.Supervised);
    }

    [Theory]
    [InlineData("steam.exe")]
    [InlineData(".\\steam.exe")]
    public void EnabledRefusesRelativeSteamPath(string executable) =>
        Assert.Throws<InvalidOperationException>(() => SteamIsolationLaunchPlan.Create(executable, null, Enabled()));

    [Theory]
    [InlineData(Machine.Amd64)]
    [InlineData(Machine.I386)]
    public void WrapsBothArchitecturesAndPreservesArgumentTail(Machine machine)
    {
        var steam = Fixture("StEaM.exe", machine);
        var options = CompleteComponents(machine);
        const string arguments = "-bigpicture -applaunch 123 \"save game\"";
        var plan = SteamIsolationLaunchPlan.Create(steam, arguments, options);
        Assert.Equal(options.SteamIsolationLauncherPath, plan.ExecutablePath);
        Assert.Equal($"--steam \"{steam}\" -- {arguments}", plan.Arguments);
        Assert.True(plan.Supervised);
    }

    [Fact]
    public void MissingHelperFailsBeforeLaunchingSteam()
    {
        var steam = Fixture("steam.exe", Machine.Amd64);
        var error = Assert.Throws<InvalidOperationException>(() => SteamIsolationLaunchPlan.Create(steam, null, Enabled()));
        Assert.Contains("launcher", error.Message);
    }

    [Fact]
    public void MissingHookFailsBeforeLaunchingSteam()
    {
        var steam = Fixture("steam.exe", Machine.Amd64);
        Fixture("MultiSeat.SteamLauncher.exe", Machine.Amd64);
        var error = Assert.Throws<InvalidOperationException>(() => SteamIsolationLaunchPlan.Create(steam, null, Enabled()));
        Assert.Contains("hook", error.Message);
    }

    [Fact]
    public void X86SteamRequiresX86Helper()
    {
        var steam = Fixture("steam.exe", Machine.I386);
        Fixture("MultiSeat.SteamLauncher.exe", Machine.Amd64);
        Fixture("MultiSeat.SteamHook32.dll", Machine.I386);
        var error = Assert.Throws<InvalidOperationException>(() => SteamIsolationLaunchPlan.Create(steam, null, Enabled()));
        Assert.Contains("32-bit", error.Message);
    }

    [Fact]
    public void WrongHookArchitectureFails()
    {
        var steam = Fixture("steam.exe", Machine.Amd64);
        CompleteComponents(Machine.Amd64);
        Fixture("MultiSeat.SteamHook.dll", Machine.I386);
        var error = Assert.Throws<InvalidOperationException>(() => SteamIsolationLaunchPlan.Create(steam, null, Enabled()));
        Assert.Contains("wrong architecture", error.Message);
    }

    [Fact]
    public void CorruptSteamExecutableFails()
    {
        var steam = Path.Combine(_directory, "steam.exe");
        File.WriteAllText(steam, "not an executable");
        Fixture("MultiSeat.SteamLauncher.exe", Machine.Amd64);
        Assert.Throws<InvalidOperationException>(() => SteamIsolationLaunchPlan.Create(steam, null, Enabled()));
    }

    [Fact]
    public void WrongPrimaryLauncherArchitectureFails()
    {
        var steam = Fixture("steam.exe", Machine.I386);
        CompleteComponents(Machine.I386);
        Fixture("MultiSeat.SteamLauncher.exe", Machine.I386);
        var error = Assert.Throws<InvalidOperationException>(() => SteamIsolationLaunchPlan.Create(steam, null, Enabled()));
        Assert.Contains("wrong architecture", error.Message);
    }

    [Fact]
    public void NoSteamArgumentsStillHasSeparator()
    {
        var steam = Fixture("steam.exe", Machine.Amd64);
        var plan = SteamIsolationLaunchPlan.Create(steam, null, CompleteComponents(Machine.Amd64));
        Assert.Equal($"--steam \"{steam}\" --", plan.Arguments);
    }

    [Fact]
    public void ReadyEventPrecedesSeparatorAndPreservesSteamArgumentTail()
    {
        var steam = Fixture("steam.exe", Machine.Amd64);
        const string arguments = "-bigpicture --literal \"hello -- world\"";
        var eventName = @"Global\MultiSeatSteamReady-" + Guid.NewGuid().ToString("N");
        var plan = SteamIsolationLaunchPlan.Create(steam, arguments, CompleteComponents(Machine.Amd64))
            .WithReadyEvent(eventName);
        Assert.Equal($"--steam \"{steam}\" --ready-event {eventName} -- {arguments}", plan.Arguments);
    }

    [Theory]
    [InlineData("arbitrary-event")]
    [InlineData("Global\\MultiSeatSteamReady-x --evil")]
    public void ReadyEventRejectsInvalidNames(string eventName)
    {
        var steam = Fixture("steam.exe", Machine.Amd64);
        var plan = SteamIsolationLaunchPlan.Create(steam, null, CompleteComponents(Machine.Amd64));
        Assert.Throws<ArgumentException>(() => plan.WithReadyEvent(eventName));
    }

    private MultiSeatOptions Enabled() => new()
    {
        SteamIsolationEnabled = true,
        SteamIsolationLauncherPath = Path.Combine(_directory, "MultiSeat.SteamLauncher.exe")
    };

    private MultiSeatOptions CompleteComponents(Machine steamMachine)
    {
        Fixture("MultiSeat.SteamLauncher.exe", Machine.Amd64);
        Fixture(steamMachine == Machine.I386 ? "MultiSeat.SteamHook32.dll" : "MultiSeat.SteamHook.dll", steamMachine);
        if (steamMachine == Machine.I386) Fixture("MultiSeat.SteamLauncher32.exe", Machine.I386);
        return Enabled();
    }

    // Minimal PE fixtures are never executed. The architecture preflight parses their headers.
    private string Fixture(string name, Machine machine)
    {
        var path = Path.Combine(_directory, name);
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
