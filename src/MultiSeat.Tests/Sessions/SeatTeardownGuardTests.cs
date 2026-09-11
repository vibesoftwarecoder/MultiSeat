using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Emulators;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// PR C of the #29 sequence: teardown and session-replacement guards, built on the
/// <see cref="SeatLifecycleGate"/> already in this repo.
///
/// Two distinct races, both of which produced INVISIBLE orphans — a Windows session, an mstsc, an
/// Apollo or a cable assignment that no seat owned, so no teardown would ever reach them:
///
/// 1. TOCTOU across the gate. A caller resolved the seat, then awaited the gate. The gate can be
///    held by a teardown for up to 30s, so the captured seat could be gone by the time the caller
///    was let in — and acting on it recreated exactly what teardown had just destroyed.
///
/// 2. Teardown removed the seat from the registry BEFORE acquiring the gate. Acquisition throws
///    TimeoutException, and that left the seat unregistered with everything still running.
///
/// Ported from @Dani6ca-T's MultiSeat-Extended (da0390c, 2c63995, f9fe532, d4f2888, a334d77,
/// 8a9279a).
/// </summary>
public class SeatTeardownGuardTests
{
    private sealed class NoopLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex,
            Func<TState, Exception?, string> f) { }
    }

    private static SeatInfo NewSeat(SeatStatus status = SeatStatus.Ready) => new()
    {
        Id = Guid.NewGuid(),
        AccountName = "GuestTest",
        Status = status
    };

    // ── 1. the post-gate re-check, as a pure decision ───────────────────────────

    [Fact]
    public void ResolveLiveSeat_ReturnsTheSeat_WhenItIsStillLive()
    {
        var seat = NewSeat();
        var seats = new ConcurrentDictionary<Guid, SeatInfo>();
        seats[seat.Id] = seat;

        var (resolved, reason) = SeatManager.ResolveLiveSeat(seats, seat.Id);

        Assert.Same(seat, resolved);
        Assert.Null(reason);
    }

    [Fact]
    public void ResolveLiveSeat_RejectsASeatRemovedWhileWaiting()
    {
        // The seat existed when the caller started and is gone by the time the gate let it in.
        var seats = new ConcurrentDictionary<Guid, SeatInfo>();

        var (resolved, reason) = SeatManager.ResolveLiveSeat(seats, Guid.NewGuid());

        Assert.Null(resolved);
        Assert.Contains("torn down", reason);
    }

    [Fact]
    public void ResolveLiveSeat_RejectsASeatThatIsTearingDown()
    {
        // Still in the registry, but teardown owns it now. Acting on it races the cleanup.
        var seat = NewSeat(SeatStatus.TearingDown);
        var seats = new ConcurrentDictionary<Guid, SeatInfo>();
        seats[seat.Id] = seat;

        var (resolved, reason) = SeatManager.ResolveLiveSeat(seats, seat.Id);

        Assert.Null(resolved);
        Assert.Contains("tearing down", reason);
    }

    [Theory]
    [InlineData(SeatStatus.Ready)]
    [InlineData(SeatStatus.Streaming)]
    [InlineData(SeatStatus.Provisioning)]
    public void ResolveLiveSeat_AcceptsEveryNonTeardownStatus(SeatStatus status)
    {
        // Only TearingDown is disqualifying. Rejecting more would break legitimate operations
        // on a seat that is merely busy.
        var seat = NewSeat(status);
        var seats = new ConcurrentDictionary<Guid, SeatInfo>();
        seats[seat.Id] = seat;

        var (resolved, _) = SeatManager.ResolveLiveSeat(seats, seat.Id);

        Assert.Same(seat, resolved);
    }

    // ── 2. teardown must hold the gate BEFORE it deregisters ────────────────────

    /// <summary>
    /// Only the fields the timeout path touches are real; the rest of the pipeline is never
    /// reached, because acquisition throws before any cleanup starts.
    /// </summary>
    private static SeatManager NewManagerForTimeoutPath(SeatLifecycleGate gate) => new(
        new NoopLogger<SeatManager>(),
        Options.Create(new MultiSeatOptions()),
        accounts: null!, sessionLauncher: null!, processInjector: null!, displayManager: null!,
        apolloManager: null!, configBuilder: null!, portAllocator: null!, firewall: null!,
        audioRouter: null!, controllerManager: null!, inputRouter: null!, inputHookManager: null!,
        hidHide: null!, onConnectApps: null!, serverQuery: null!,
        // Null on purpose. Teardown consults this only to warn about disturbing a standalone
        // Apollo's stream, and that check must never fail a teardown — so the timeout path below
        // exercises exactly that: the warning blows up internally and teardown proceeds regardless.
        hostApollo: null!,
        emulatorSeeders: Array.Empty<IEmulatorConfigSeeder>(),
        lifecycleGate: gate);

    [Fact]
    public async Task TeardownSeat_WhenTheGateTimesOut_LeavesTheSeatRegistered()
    {
        var gate = new SeatLifecycleGate();
        var mgr = NewManagerForTimeoutPath(gate);
        var seat = NewSeat();
        mgr.RegisterSeatDirect(seat);

        // Somebody else holds the gate — a resolution change, a restart, anything.
        using var held = await gate.AcquireAsync(seat.Id, CancellationToken.None);

        // The REAL TeardownSeatAsync, via the internal overload that only shortens the gate wait.
        // Calling a copy of the ordering here would pass whether or not the fix is present.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            mgr.TeardownSeatAsync(seat.Id, TimeSpan.FromMilliseconds(200), CancellationToken.None));

        // ⭐ The regression. Before the fix the seat was removed BEFORE the gate was acquired, so
        // this returned null while its session, mstsc, Apollo and ports were all still alive and
        // unowned. Revert the ordering in TeardownSeatAsync and this assertion fails.
        Assert.NotNull(mgr.GetSeat(seat.Id));
        Assert.Equal(SeatStatus.Ready, mgr.GetSeat(seat.Id)!.Status);
    }

    [Fact]
    public async Task TeardownSeat_ForAnUnknownSeat_IsANoOp()
    {
        // Returns before touching the gate, so the null dependencies are never reached.
        var mgr = NewManagerForTimeoutPath(new SeatLifecycleGate());

        await mgr.TeardownSeatAsync(
            Guid.NewGuid(), TimeSpan.FromMilliseconds(200), CancellationToken.None);
    }

    [Fact]
    public async Task DifferentSeats_DoNotBlockEachOther()
    {
        // The guard must not serialise unrelated seats — that would turn a per-seat lock into a
        // global one and stall a four-seat host behind one slow teardown.
        var gate = new SeatLifecycleGate();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        using var heldA = await gate.AcquireAsync(a, CancellationToken.None);
        using var heldB = await gate.AcquireAsync(b, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.NotNull(heldB);
    }
}
