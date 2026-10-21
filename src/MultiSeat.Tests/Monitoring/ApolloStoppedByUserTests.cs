using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Emulators;
using MultiSeat.Service.Monitoring;
using MultiSeat.Service.Sessions;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Monitoring;

/// <summary>
/// Issue #124: once the user stops a seat's Apollo, it stays stopped until the user starts it
/// again. That covers Quit from Apollo's web UI or tray (exit 1115) and the dashboard's Stop, and
/// it must hold across a session drop: the health check's reconnect after a sleep reconnects the
/// session but must not start Apollo.
///
/// These run the real ApolloManager and SeatManager methods with no Apollo installed and no seat
/// session (SessionId -1), so nothing is launched. Stop also runs a read-only WMI query for a
/// sunshine.exe passing this test seat's config path, which matches nothing.
///
/// Live-only: that an explicit Start or Restart then actually brings Apollo up.
/// </summary>
public class ApolloStoppedByUserTests
{
    private const string Account = "StopMarkerTest";

    private static ApolloManager NewApolloManager() =>
        new(NullLogger<ApolloManager>.Instance, Options.Create(new MultiSeatOptions()), null!, null!);

    private static SeatManager NewSeatManager(ApolloManager apollo) =>
        new(NullLogger<SeatManager>.Instance,
            Options.Create(new MultiSeatOptions()),
            accounts: null!, sessionLauncher: null!, processInjector: null!, displayManager: null!,
            apolloManager: apollo, configBuilder: null!, portAllocator: null!, firewall: null!,
            audioRouter: null!, controllerManager: null!, inputRouter: null!, inputHookManager: null!,
            hidHide: null!, onConnectApps: null!, serverQuery: null!, hostApollo: null!,
            emulatorSeeders: Array.Empty<IEmulatorConfigSeeder>(),
            lifecycleGate: new SeatLifecycleGate());

    private static SeatInfo NewSeat(SeatStatus status = SeatStatus.Ready) =>
        new() { AccountName = Account, Status = status, ApolloProcessId = 4242 };

    /// <summary>Check 2's real decision, with the real Quit handler and a fake relaunch.</summary>
    private static async Task<int> ResolveExit(ApolloManager apollo, SeatInfo seat, int? exitCode)
    {
        var relaunches = 0;
        await SessionHealthCheck.ResolveApolloExitAsync(
            exitCode, TimeSpan.FromMinutes(10),
            leaveStopped: () => SessionHealthCheck.LeaveApolloStoppedAfterQuit(
                apollo, seat, exitCode, NullLogger.Instance),
            relaunch: _ => { relaunches++; return Task.FromResult(5151); });
        return relaunches;
    }

    /// <summary>The reconnect rescue's real Apollo step, with a fake restart.</summary>
    private static async Task<(int? Pid, int Restarts)> Reconnect(ApolloManager apollo, SeatInfo seat)
    {
        var restarts = 0;
        var pid = await SessionHealthCheck.RestartApolloAfterReconnectAsync(
            stoppedByUser: () => apollo.IsStoppedByUser(seat.Id),
            restart: _ => { restarts++; return Task.FromResult(6262); },
            CancellationToken.None);
        return (pid, restarts);
    }

    [Fact]
    public async Task Quit_SetsTheMarker_AndLeavesTheSeatStopped()
    {
        var apollo = NewApolloManager();
        var seat = NewSeat(SeatStatus.Streaming);

        var relaunches = await ResolveExit(apollo, seat, 1115);

        Assert.Equal(0, relaunches);
        Assert.True(apollo.IsStoppedByUser(seat.Id));
        Assert.Equal(0, seat.ApolloProcessId);
        Assert.Equal(SeatStatus.Ready, seat.Status);
        Assert.Contains("quit", seat.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]     // Apollo's own Restart
    [InlineData(1)]     // crash / taskkill
    [InlineData(-1)]    // killed
    [InlineData(null)]  // unknown
    public async Task RestartExitAndCrash_DoNotSetTheMarker(int? exitCode)
    {
        var apollo = NewApolloManager();
        var seat = NewSeat();

        var relaunches = await ResolveExit(apollo, seat, exitCode);

        Assert.Equal(1, relaunches);
        Assert.False(apollo.IsStoppedByUser(seat.Id));
    }

    [Fact]
    public async Task ReconnectAfterQuit_DoesNotStartApollo()
    {
        var apollo = NewApolloManager();
        var seat = NewSeat();
        await ResolveExit(apollo, seat, 1115);

        var (pid, restarts) = await Reconnect(apollo, seat);

        Assert.Null(pid);
        Assert.Equal(0, restarts);
        Assert.True(apollo.IsStoppedByUser(seat.Id));   // still stopped after the reconnect
    }

    [Fact]
    public async Task ReconnectWithoutTheMarker_StartsApollo()
    {
        // Positive control for the test above: the same step does restart an unmarked seat.
        var apollo = NewApolloManager();
        var seat = NewSeat();

        var (pid, restarts) = await Reconnect(apollo, seat);

        Assert.Equal(6262, pid);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task ReconnectAfterACrash_StartsApollo()
    {
        var apollo = NewApolloManager();
        var seat = NewSeat();
        await ResolveExit(apollo, seat, 1);

        var (_, restarts) = await Reconnect(apollo, seat);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task DashboardStop_SetsTheMarker_SoReconnectLeavesItStopped()
    {
        var apollo = NewApolloManager();
        var manager = NewSeatManager(apollo);
        var seat = NewSeat();
        manager.RegisterSeatDirect(seat);

        await manager.StopApollo(seat.Id);

        Assert.True(apollo.IsStoppedByUser(seat.Id));
        Assert.Equal(0, seat.ApolloProcessId);
        var (pid, restarts) = await Reconnect(apollo, seat);
        Assert.Null(pid);
        Assert.Equal(0, restarts);
    }

    [Fact]
    public async Task ExplicitStart_ClearsTheMarker()
    {
        var apollo = NewApolloManager();
        var manager = NewSeatManager(apollo);
        var seat = NewSeat();
        manager.RegisterSeatDirect(seat);
        await ResolveExit(apollo, seat, 1115);
        Assert.True(apollo.IsStoppedByUser(seat.Id));

        // No session, so the start itself is refused after the mark is cleared. The user asked
        // for Apollo, so a later reconnect may start it.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.StartApolloAsync(seat.Id, CancellationToken.None));

        Assert.False(apollo.IsStoppedByUser(seat.Id));
        var (_, restarts) = await Reconnect(apollo, seat);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task ExplicitRestart_ClearsTheMarker()
    {
        var apollo = NewApolloManager();
        var manager = NewSeatManager(apollo);
        var seat = NewSeat();
        manager.RegisterSeatDirect(seat);
        await manager.StopApollo(seat.Id);
        Assert.True(apollo.IsStoppedByUser(seat.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.RestartApolloAsync(seat.Id, CancellationToken.None));

        Assert.False(apollo.IsStoppedByUser(seat.Id));
    }

    [Fact]
    public async Task Teardown_ClearsTheMarker_AndAReprovisionedSeatStartsUnmarked()
    {
        var apollo = NewApolloManager();
        var manager = NewSeatManager(apollo);
        var seat = NewSeat();
        manager.RegisterSeatDirect(seat);
        await ResolveExit(apollo, seat, 1115);

        await manager.TeardownSeatAsync(seat.Id, CancellationToken.None);

        Assert.False(apollo.IsStoppedByUser(seat.Id));
        // A provision builds a new SeatInfo, so the replacement has a new id with no mark.
        var reprovisioned = NewSeat();
        Assert.NotEqual(seat.Id, reprovisioned.Id);
        Assert.False(apollo.IsStoppedByUser(reprovisioned.Id));
    }

    [Fact]
    public void TheMarkerIsInMemoryOnly()
    {
        // A service restart builds a new ApolloManager, and seats are provisioned again from their
        // presets with Apollo started as usual.
        var before = NewApolloManager();
        var seat = NewSeat();
        before.StopByUser(seat);
        Assert.True(before.IsStoppedByUser(seat.Id));

        Assert.False(NewApolloManager().IsStoppedByUser(seat.Id));
    }
}
