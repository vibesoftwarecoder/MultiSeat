using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Sessions;

/// <summary>
/// The seat status state machine, and the only supported way to move a seat between states.
///
/// ⚠️ This table was decorative until 2026-09-03: <see cref="CanTransitionTo"/> was called from
/// tests only, which asserted the table against itself, so nothing noticed that the code performed
/// four transitions the table forbade — including <c>Error -> Ready</c>, the deliberate recovery
/// path added for PR #22. The table was wrong, not the code. Both are now reconciled and
/// <see cref="TransitionTo"/> checks every move.
/// </summary>
public static class SeatState
{
    private static readonly Dictionary<SeatStatus, SeatStatus[]> ValidTransitions = new()
    {
        // Idle is the enum default and is never assigned by the service; a seat is constructed
        // straight into Provisioning. TearingDown is listed because teardown accepts a seat in
        // any state, including one that never left its default.
        [SeatStatus.Idle] = [SeatStatus.Provisioning, SeatStatus.TearingDown],

        [SeatStatus.Provisioning] = [SeatStatus.Configuring, SeatStatus.Error, SeatStatus.TearingDown],

        // Configuring can meet a sleep mid-provision: the health check watches it (see
        // SessionHealthCheck.IsWorthChecking), so it can be pulled into recovery.
        [SeatStatus.Configuring] = [SeatStatus.Ready, SeatStatus.Connecting, SeatStatus.Error, SeatStatus.TearingDown],

        [SeatStatus.Ready] = [SeatStatus.Streaming, SeatStatus.Connecting, SeatStatus.Error, SeatStatus.TearingDown],
        [SeatStatus.Streaming] = [SeatStatus.Ready, SeatStatus.Connecting, SeatStatus.Error, SeatStatus.TearingDown],

        // Recovery returns the seat to whichever operational state it held, or fails.
        [SeatStatus.Connecting] = [SeatStatus.Ready, SeatStatus.Streaming, SeatStatus.Error, SeatStatus.TearingDown],

        [SeatStatus.TearingDown] = [SeatStatus.Idle],

        // Error -> Ready is POST /api/seats/{id}/session-reconnect handing a repaired seat back to
        // the health check. Without it the seat keeps a live session that nothing ever checks,
        // which is the bug PR #22 fixed.
        [SeatStatus.Error] = [SeatStatus.Ready, SeatStatus.Connecting, SeatStatus.Provisioning, SeatStatus.TearingDown],
    };

    /// <summary>
    /// Whether <paramref name="target"/> is reachable from <paramref name="current"/>.
    /// A state is always reachable from itself: re-asserting the current status is a no-op, not
    /// a transition (launching a second app on an already-Streaming seat does exactly that).
    /// </summary>
    public static bool CanTransitionTo(this SeatStatus current, SeatStatus target) =>
        current == target ||
        (ValidTransitions.TryGetValue(current, out var allowed) && allowed.Contains(target));

    /// <summary>
    /// When true, an illegal transition throws instead of being logged. Tests turn this on so a
    /// bad transition fails the build; production leaves it off deliberately — see
    /// <see cref="TransitionTo"/>.
    /// </summary>
    internal static bool StrictTransitions { get; set; }

    /// <summary>
    /// Move a seat to <paramref name="target"/>, checking the transition is legal.
    ///
    /// An illegal transition is a programming error, so it is logged at Error with both states
    /// named. It is applied anyway: this table has never been enforced, the four discrepancies
    /// found when it was first checked were all in the *table*, and refusing a transition on a
    /// deployed host would strand a real seat over a bookkeeping disagreement. Failing loudly in
    /// the log while keeping the seat moving is the safer half of the trade.
    ///
    /// Tests set <see cref="StrictTransitions"/> so the same mistake is fatal before it ships.
    /// </summary>
    public static void TransitionTo(this SeatInfo seat, SeatStatus target, ILogger logger)
    {
        var from = seat.Status;
        if (!from.CanTransitionTo(target))
        {
            if (StrictTransitions)
                throw new InvalidOperationException(
                    $"Illegal seat status transition {from} -> {target} (seat {seat.Id}).");

            logger.LogError(
                "Seat {Id}: illegal status transition {From} -> {To} — applying it anyway, but one "
                + "of the two is wrong and should be fixed",
                seat.Id, from, target);
        }

        seat.Status = target;
    }
}
