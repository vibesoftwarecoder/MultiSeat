using System.Collections.Concurrent;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// Tests for the per-account provisioning dedup (H1 from the concurrency audit):
/// at most one live/provisioning seat may exist for a given AccountName.
///
/// The exercises use the real production seam — <see cref="SeatManager.TryRegisterSeat"/>
/// and <see cref="SeatManager.AccountNameHasLiveSeat"/> — with a bare dictionary + lock,
/// because ProvisionSeatAsync's full pipeline needs real Windows sessions/accounts. The
/// ownership decision is the only part this fix changes, so it is the part pinned here.
/// </summary>
public class SeatManagerAccountOwnershipTests
{
    private static readonly object Lock = new();

    private static SeatInfo Seat(string accountName, SeatStatus status = SeatStatus.Provisioning) =>
        new() { AccountName = accountName, Status = status };

    [Fact]
    public void ProvisionAccountA_Succeeds_WhenNoSeatExists()
    {
        var seats = new ConcurrentDictionary<Guid, SeatInfo>();

        var registered = SeatManager.TryRegisterSeat(seats, Lock, Seat("AccountA"));

        Assert.True(registered);
        Assert.Single(seats);
    }

    [Fact]
    public void ProvisionAccountA_IsRejected_WhenSeatForAccountAAlreadyExists()
    {
        var seats = new ConcurrentDictionary<Guid, SeatInfo>();
        SeatManager.TryRegisterSeat(seats, Lock, Seat("AccountA"));

        var second = SeatManager.TryRegisterSeat(seats, Lock, Seat("AccountA"));

        Assert.False(second);
        Assert.Single(seats); // the rejected attempt added nothing
    }

    [Fact]
    public async Task TwoConcurrentProvisionsOfAccountA_CannotBothRegister()
    {
        var seats = new ConcurrentDictionary<Guid, SeatInfo>();

        // Ten concurrent attempts for the same account: exactly one may win the lock and
        // register. Each attempt is a fresh SeatInfo (fresh Guid) — exactly the shape of
        // two callers hitting POST /api/seats with the same AccountName.
        var attempts = Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => SeatManager.TryRegisterSeat(seats, Lock, Seat("AccountA"))));

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r));          // one winner…
        var winner = Assert.Single(seats);               // …and only one seat registered
        Assert.Equal("AccountA", winner.Value.AccountName);
    }

    [Fact]
    public void ProvisioningAccountA_AndAccountB_ProceedIndependently()
    {
        var seats = new ConcurrentDictionary<Guid, SeatInfo>();

        Assert.True(SeatManager.TryRegisterSeat(seats, Lock, Seat("AccountA")));
        Assert.True(SeatManager.TryRegisterSeat(seats, Lock, Seat("AccountB")));

        Assert.Equal(2, seats.Count);
    }

    [Fact]
    public void FailedProvision_DoesNotPermanentlyBlockRetryForSameAccount()
    {
        var seats = new ConcurrentDictionary<Guid, SeatInfo>();
        Assert.True(SeatManager.TryRegisterSeat(seats, Lock, Seat("AccountA")));

        // A failed provision parks the seat in Error and tears its resources down, but leaves
        // the entry in _seats (ProvisionSeatAsync's catch does exactly this). An Error seat
        // must not count as occupying the account, or the retry would be blocked forever.
        var failed = seats.Values.Single();
        failed.Status = SeatStatus.Error;

        var retry = SeatManager.TryRegisterSeat(seats, Lock, Seat("AccountA"));

        Assert.True(retry);
        Assert.Equal(2, seats.Count); // the Error entry lingers, the retry registers alongside
    }

    [Fact]
    public void SeatForDifferentAccount_DoesNotBlock()
    {
        var seats = new ConcurrentDictionary<Guid, SeatInfo>();
        SeatManager.TryRegisterSeat(seats, Lock, Seat("AccountA"));

        Assert.False(SeatManager.AccountNameHasLiveSeat(seats.Values, "AccountB"));
        Assert.True(SeatManager.TryRegisterSeat(seats, Lock, Seat("AccountB")));
    }

    [Fact]
    public void AccountComparison_IsCaseInsensitive()
    {
        var seats = new ConcurrentDictionary<Guid, SeatInfo>();
        SeatManager.TryRegisterSeat(seats, Lock, Seat("AccountA"));

        // Windows account names and the per-account config directory are case-insensitive,
        // so "accounta" is the same account as "AccountA" — it must be rejected too.
        Assert.False(SeatManager.TryRegisterSeat(seats, Lock, Seat("accounta")));
    }
}
