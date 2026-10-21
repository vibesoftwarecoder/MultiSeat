using MultiSeat.Service.Configuration;
using MultiSeat.Service.Interop;
using MultiSeat.Service.Sessions;
using Xunit;

namespace MultiSeat.Tests.Sessions;

public sealed class SteamIsolationOptionsTests
{
    [Fact]
    public void ReadyTimeoutDefaultsToThreeMinutes() =>
        Assert.Equal(TimeSpan.FromSeconds(180), new MultiSeatOptions().UsableSteamIsolationReadyTimeout);

    // A typo must not make every isolated launch fail at once (0, negative) or hold a seat's
    // lifecycle gate for hours (very large), so the value is kept in 10 s .. 15 min.
    [Theory]
    [InlineData(-5, 10)]
    [InlineData(0, 10)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(45, 45)]
    [InlineData(900, 900)]
    [InlineData(901, 900)]
    [InlineData(int.MaxValue, 900)]
    public void ReadyTimeoutIsClampedToAUsableRange(int configured, int expectedSeconds)
    {
        var options = new MultiSeatOptions { SteamIsolationReadyTimeoutSeconds = configured };
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.UsableSteamIsolationReadyTimeout);
    }

    [Fact]
    public void ShippedSettingsFileCarriesTheSameDefault()
    {
        // The default in code and the default in the shipped appsettings.json are two copies of
        // one number; if they drift, a host that never touched the file behaves differently from
        // what the code's own default says.
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        // The shipped file carries comments, which the default JSON reader rejects.
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path),
            new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        var shipped = document.RootElement.GetProperty("MultiSeat")
            .GetProperty("SteamIsolationReadyTimeoutSeconds").GetInt32();
        Assert.Equal(new MultiSeatOptions().SteamIsolationReadyTimeoutSeconds, shipped);
    }

    // The supervised launcher must not get a console window in the seat; an ordinary launch
    // keeps the new console it always had. Seat Apollo is the other exception (issue #124).
    [Fact]
    public void SupervisedLaunchGetsNoConsoleWindow()
    {
        var flags = ProcessInjector.CreationFlagsFor(supervised: true);
        Assert.True((flags & Kernel32.CREATE_NO_WINDOW) != 0);
        Assert.True((flags & Kernel32.CREATE_NEW_CONSOLE) == 0);
        Assert.True((flags & Kernel32.CREATE_UNICODE_ENVIRONMENT) != 0);
        Assert.True((flags & Kernel32.NORMAL_PRIORITY_CLASS) != 0);
    }

    [Fact]
    public void OrdinaryLaunchKeepsItsNewConsole()
    {
        var flags = ProcessInjector.CreationFlagsFor(supervised: false);
        Assert.True((flags & Kernel32.CREATE_NEW_CONSOLE) != 0);
        Assert.True((flags & Kernel32.CREATE_NO_WINDOW) == 0);
        Assert.Equal(
            Kernel32.CREATE_UNICODE_ENVIRONMENT | Kernel32.CREATE_NEW_CONSOLE | Kernel32.NORMAL_PRIORITY_CLASS,
            flags);
    }

    // Seat Apollo starts minimized without stealing focus (issue #120); everything else is shown.
    // Since #124 seat Apollo has no console window for this to apply to; the request is kept as a
    // harmless fallback (see ProcessInjector.ShowWindowFor).
    [Fact]
    public void ApolloLaunchIsMinimizedWithoutActivation()
    {
        Assert.Equal(Kernel32.SW_SHOWMINNOACTIVE, ProcessInjector.ShowWindowFor(minimized: true));
    }

    [Fact]
    public void OrdinaryLaunchStaysVisible()
    {
        Assert.Equal(Kernel32.SW_SHOW, ProcessInjector.ShowWindowFor(minimized: false));
    }

    // ⚠️ REVERSED ON PURPOSE (issue #124). Until #124 this test asserted the opposite: seat Apollo
    // kept CREATE_NEW_CONSOLE so GetConsoleWindow stayed non-NULL (issues #120/#121). That console
    // is what made Apollo's Restart spawn an untracked copy of itself, leaving two Apollos on one
    // seat, and made Quit exit 0 so the service could not tell it from a Restart. With no console
    // window Apollo exits 0 on Restart and 1115 on Quit and starts nothing, and the service decides.
    [Fact]
    public void ApolloLaunchHasNoConsoleWindow()
    {
        var request = ProcessInjector.ApolloLaunchRequestFor(
            @"C:\Program Files\ApolloVibe\sunshine.exe",
            @"C:\ProgramData\MultiSeat\apollo\Gaming\sunshine.conf");
        Assert.True(request.NoConsoleWindow);

        // Apollo is not a supervised Steam launch; its own flag is what removes the console.
        var flags = ProcessInjector.CreationFlagsFor(supervised: false, request.NoConsoleWindow);
        Assert.True((flags & Kernel32.CREATE_NO_WINDOW) != 0);
        Assert.True((flags & Kernel32.CREATE_NEW_CONSOLE) == 0);
        Assert.Equal(
            Kernel32.CREATE_UNICODE_ENVIRONMENT | Kernel32.CREATE_NO_WINDOW | Kernel32.NORMAL_PRIORITY_CLASS,
            flags);
    }

    [Fact]
    public void NoConsoleWindowDefaultsOff_SoGamesKeepTheirConsole()
    {
        // Games and on-connect apps go through LaunchInSessionAsync, which never asks for it.
        Assert.Equal(
            ProcessInjector.CreationFlagsFor(supervised: false, noConsoleWindow: false),
            ProcessInjector.CreationFlagsFor(supervised: false));
        Assert.True((ProcessInjector.CreationFlagsFor(supervised: false) & Kernel32.CREATE_NEW_CONSOLE) != 0);
    }

    [Fact]
    public void SupervisedAndNoConsoleWindowTogether_StillOneNoWindowFlag()
    {
        var flags = ProcessInjector.CreationFlagsFor(supervised: true, noConsoleWindow: true);
        Assert.Equal(
            Kernel32.CREATE_UNICODE_ENVIRONMENT | Kernel32.CREATE_NO_WINDOW | Kernel32.NORMAL_PRIORITY_CLASS,
            flags);
    }

    [Fact]
    public void SupervisedLaunchWindowStateIsUnchanged()
    {
        // The supervised Steam launch is not minimized: only the Apollo launch asks for that.
        Assert.Equal(Kernel32.SW_SHOW, ProcessInjector.ShowWindowFor(minimized: false));
        Assert.True((ProcessInjector.CreationFlagsFor(supervised: true) & Kernel32.CREATE_NO_WINDOW) != 0);
    }

    // The wiring: the Apollo launch request is minimized with no console window, quotes the config
    // path, and runs from the config file's directory.
    [Fact]
    public void ApolloLaunchRequestIsMinimizedWithSeatConfigAndWorkingDir()
    {
        var request = ProcessInjector.ApolloLaunchRequestFor(
            @"C:\Program Files\ApolloVibe\sunshine.exe",
            @"C:\Seats\seat1\config\sunshine.conf");
        Assert.True(request.Minimized);
        Assert.True(request.NoConsoleWindow);
        Assert.Equal(@"C:\Program Files\ApolloVibe\sunshine.exe", request.ExePath);
        Assert.Equal("\"C:\\Seats\\seat1\\config\\sunshine.conf\"", request.Arguments);
        Assert.Equal(@"C:\Seats\seat1\config", request.WorkingDir);
    }
}
