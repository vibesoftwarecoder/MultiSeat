using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Sessions;

/// <summary>
/// Sets up a seat from its saved auto-start preset, retrying a failed attempt a few times
/// before giving up (issue #87). Used at service startup and by <see cref="SeatReconciler"/>.
///
/// Before this, a failed auto-start at boot was logged once and never tried again, so the seat
/// was simply missing until someone created it by hand. A first attempt early in a cold boot
/// failing because something it needs (RDP Wrapper's patched listener, the SudoVDA driver) is
/// not ready yet is plausible, but has NOT been seen happening; the retry is defensive, not the
/// fix for a reproduced race.
///
/// Every step is logged at Information or above, so the Event Log of a reporter's machine says
/// what happened: which attempt, why it failed, when the next one comes, and the final verdict.
/// </summary>
public sealed class AutoStartProvisioner
{
    /// <summary>Attempts per seat, the first included.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>The wait after the first failure. Each later wait is this times the attempt number.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly ILogger<AutoStartProvisioner> _logger;
    private readonly SeatManager _seatManager;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public AutoStartProvisioner(ILogger<AutoStartProvisioner> logger, SeatManager seatManager)
        : this(logger, seatManager, Task.Delay)
    {
    }

    /// <summary>For tests: the wait between attempts is passed in, so they do not take 45 seconds.</summary>
    internal AutoStartProvisioner(
        ILogger<AutoStartProvisioner> logger, SeatManager seatManager,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _logger = logger;
        _seatManager = seatManager;
        _delay = delay;
    }

    /// <summary>
    /// Provision the preset's seat, retrying as described above. Returns the seat, or null once
    /// every attempt has failed. The last failed seat stays registered in Error so the dashboard
    /// shows why; earlier failed attempts are removed before the next one.
    /// </summary>
    /// <param name="trigger">Why this runs, for the log ("service startup", "resume", ...).</param>
    public Task<SeatInfo?> ProvisionAsync(SeatPreset preset, string trigger, CancellationToken ct) =>
        ProvisionWithRetryAsync(
            preset, trigger,
            provision: (request, c) => _seatManager.ProvisionSeatAsync(request, c),
            discardFailedAttempt: DiscardFailedAttemptAsync,
            delay: _delay,
            _logger, ct);

    /// <summary>
    /// Remove the Error entry a failed attempt left for this account, so the next attempt does
    /// not leave a second one beside it. Its resources were released when it failed, and
    /// teardown knows not to release them again.
    /// </summary>
    private async Task DiscardFailedAttemptAsync(string accountName, CancellationToken ct)
    {
        foreach (var seat in _seatManager.GetAllSeats())
        {
            if (seat.Status == SeatStatus.Error
                && string.Equals(seat.AccountName, accountName, StringComparison.OrdinalIgnoreCase))
            {
                await _seatManager.TeardownSeatAsync(seat.Id, ct);
            }
        }
    }

    /// <summary>
    /// The retry loop, with its effects passed in so it can be tested without Windows. A
    /// failure that a retry cannot change (a bad saved scale, the account already having a
    /// seat, no free seat slot) gives up at once instead of waiting to fail the same way.
    /// </summary>
    internal static async Task<SeatInfo?> ProvisionWithRetryAsync(
        SeatPreset preset,
        string trigger,
        Func<SeatRequest, CancellationToken, Task<SeatInfo>> provision,
        Func<string, CancellationToken, Task> discardFailedAttempt,
        Func<TimeSpan, CancellationToken, Task> delay,
        ILogger logger,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                // Clear the previous attempt's Error entry. A failure here must not stop the
                // retry: the next attempt registers a new seat whatever is left behind.
                try { await discardFailedAttempt(preset.AccountName, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Auto-start ({Trigger}): could not remove the failed attempt for '{Account}' " +
                        "before retrying", trigger, preset.AccountName);
                }
            }

            logger.LogInformation(
                "Auto-start ({Trigger}): provisioning seat '{Account}', attempt {Attempt} of {Max}",
                trigger, preset.AccountName, attempt, MaxAttempts);

            try
            {
                var seat = await provision(MultiSeatWorker.RequestFor(preset), ct);
                seat.AutoStart = true;
                logger.LogInformation(
                    "Auto-start ({Trigger}): seat '{Account}' is up (ID {Id}) after {Attempt} attempt(s)",
                    trigger, preset.AccountName, seat.Id, attempt);
                return seat;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!IsWorthRetrying(ex))
            {
                logger.LogWarning(ex,
                    "Auto-start ({Trigger}): giving up on seat '{Account}' after attempt {Attempt}; " +
                    "this failure would not change on a retry", trigger, preset.AccountName, attempt);
                return null;
            }
            catch (Exception ex) when (attempt >= MaxAttempts)
            {
                logger.LogWarning(ex,
                    "Auto-start ({Trigger}): giving up on seat '{Account}' after {Max} failed " +
                    "attempts. Create it again from the dashboard, or fix the cause in the error " +
                    "above and restart the service", trigger, preset.AccountName, MaxAttempts);
                return null;
            }
            catch (Exception ex)
            {
                var wait = RetryDelay * attempt;
                logger.LogWarning(ex,
                    "Auto-start ({Trigger}): attempt {Attempt} of {Max} for seat '{Account}' failed; " +
                    "trying again in {Seconds}s", trigger, attempt, MaxAttempts, preset.AccountName,
                    wait.TotalSeconds);
                await delay(wait, ct);
            }
        }
    }

    /// <summary>
    /// False for failures that come from the request or the host's seat bookkeeping rather than
    /// from Windows not being ready: retrying those can only fail the same way.
    /// </summary>
    internal static bool IsWorthRetrying(Exception ex) => ex is not (
        ArgumentException
        or ResourceConflictException
        or CapacityExhaustedException);
}
