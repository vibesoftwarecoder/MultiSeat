using Microsoft.AspNetCore.Http;
using MultiSeat.Service;
using Xunit;

namespace MultiSeat.Tests.Api;

/// <summary>
/// PR D of the #29 sequence: API contract. Every failure below used to answer <b>400 Bad
/// Request</b>, which told a caller their request was malformed when it was not.
///
/// The three that were wrong, and why the distinction is worth carrying:
///
/// - <b>503</b> — the host is full. The request was fine and the same one may succeed later. A
///   400 tells a client to stop retrying, which is the opposite of the truth.
/// - <b>409</b> — the request conflicts with state that exists. Retrying identically cannot work
///   until something changes, so this is NOT the same as 503.
/// - <b>404</b> — the seat is not there. After the PR C guards this also covers a seat torn down
///   while the operation waited for the lifecycle gate: not a bad request, and never was.
///
/// A plain <see cref="InvalidOperationException"/> still means 400, which is what keeps this an
/// improvement in precision rather than a reshuffle.
///
/// Ported from @Dani6ca-T's MultiSeat-Extended (18a733c, d0446ae).
/// </summary>
public class HttpStatusSemanticsTests
{
    [Fact]
    public void CapacityExhausted_Is503_NotBadRequest()
    {
        var ex = new CapacityExhaustedException("Maximum seat count (4) reached.");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ApiErrors.StatusFor(ex));
        Assert.NotEqual(StatusCodes.Status400BadRequest, ApiErrors.StatusFor(ex));
    }

    [Fact]
    public void ResourceConflict_Is409()
    {
        var ex = new ResourceConflictException("Account 'GuestA' already has a seat.");

        Assert.Equal(StatusCodes.Status409Conflict, ApiErrors.StatusFor(ex));
    }

    [Fact]
    public void SeatNotFound_Is404()
    {
        Assert.Equal(StatusCodes.Status404NotFound, ApiErrors.StatusFor(new SeatNotFoundException()));
    }

    [Fact]
    public void APlainInvalidOperation_StaysBadRequest()
    {
        // The fallback has to keep working, or this change would silently reclassify every
        // genuine bad request as something else.
        Assert.Equal(
            StatusCodes.Status400BadRequest,
            ApiErrors.StatusFor(new InvalidOperationException("No active session.")));
    }

    [Fact]
    public void CapacityAndConflict_AreNotTheSameStatus()
    {
        // The whole point: "the host is full, try later" and "this conflicts with what exists"
        // are different answers, and both used to be 400.
        Assert.NotEqual(
            ApiErrors.StatusFor(new CapacityExhaustedException("full")),
            ApiErrors.StatusFor(new ResourceConflictException("conflict")));
    }

    // ── the compatibility guarantee that makes this safe ────────────────────────

    [Theory]
    [InlineData(typeof(CapacityExhaustedException))]
    [InlineData(typeof(ResourceConflictException))]
    [InlineData(typeof(SeatNotFoundException))]
    public void EveryTypedException_IsStillAnInvalidOperationException(Type type)
    {
        // ⭐ Load-bearing. Non-HTTP callers — worker autostart, smoke scripts, tooling — catch
        // InvalidOperationException and must keep catching these unchanged. If this ever fails,
        // those callers stop handling errors they used to handle, silently.
        Assert.True(typeof(InvalidOperationException).IsAssignableFrom(type),
            $"{type.Name} must derive from InvalidOperationException.");
    }

    [Fact]
    public void TypedExceptions_PreserveTheirMessage()
    {
        const string message = "Maximum seat count (4) reached.";
        Assert.Equal(message, new CapacityExhaustedException(message).Message);
        Assert.Equal("Seat not found.", new SeatNotFoundException().Message);
    }

    [Fact]
    public void ToResult_AgreesWithStatusFor()
    {
        // StatusFor exists so tests can assert without an HTTP pipeline. If the two ever drift,
        // every assertion above becomes decorative — so pin that they are the same switch.
        foreach (InvalidOperationException ex in new InvalidOperationException[]
        {
            new CapacityExhaustedException("x"),
            new ResourceConflictException("x"),
            new SeatNotFoundException(),
            new InvalidOperationException("x")
        })
        {
            var result = ApiErrors.ToResult(ex);
            Assert.NotNull(result);

            var expected = ApiErrors.StatusFor(ex);
            var actual = result switch
            {
                IStatusCodeHttpResult s => s.StatusCode,
                _ => (int?)null
            };

            Assert.Equal(expected, actual);
        }
    }
}
