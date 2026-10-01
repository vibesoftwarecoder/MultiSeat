using MultiSeat.Service.Api;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Monitoring;

/// <summary>
/// Requests for a <see cref="SeatReconciler"/> pass. Written from wherever something happens that
/// can leave seats stale - the PC resuming, the health check finding a seat's session gone - and
/// drained by <see cref="MultiSeatWorker"/>, which runs one pass at a time.
///
/// Deliberately has no dependencies, so the service's host lifetime can write to it without
/// pulling the whole seat graph into existence before the host has started.
/// </summary>
public sealed class SeatReconcileRequests
{
    private readonly object _lock = new();
    private readonly List<(string Reason, DateTimeOffset NotBefore, bool IncludeMissing)> _pending = [];

    internal static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ask for a pass no earlier than <paramref name="notBefore"/> (now when null).
    /// <paramref name="includeMissing"/> also provisions auto-start seats that do not exist at
    /// all, as service startup would; see <see cref="SeatReconciler.ReconcileAsync"/>.
    /// </summary>
    public void Request(string reason, bool includeMissing, DateTimeOffset? notBefore = null)
    {
        lock (_lock)
            _pending.Add((reason, notBefore ?? DateTimeOffset.MinValue, includeMissing));
    }

    /// <summary>
    /// Take every request that is due by <paramref name="now"/>, merged into one pass. Requests
    /// not due yet stay queued. Null when nothing is due.
    /// </summary>
    public ReconcilePass? TakeDue(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_pending.Any(p => p.NotBefore <= now)) return null;

            // Once one request is due, take those due within the next few seconds as well. A
            // resume raises two or three power events at once, and they should make one pass.
            var horizon = now + MergeWindow;
            var due = _pending.Where(p => p.NotBefore <= horizon).ToList();
            _pending.RemoveAll(p => p.NotBefore <= horizon);
            return new ReconcilePass(
                string.Join("; ", due.Select(p => p.Reason).Distinct()),
                due.Any(p => p.IncludeMissing));
        }
    }
}

/// <summary>One reconciliation pass: why it runs, and whether it also creates missing seats.</summary>
public sealed record ReconcilePass(string Reasons, bool IncludeMissing);

/// <summary>
/// Brings the seat list back in line with reality after something that can break seats behind
/// the service's back (issue #87).
///
/// The case it exists for: the PC hibernates. The service process is suspended and resumed, not
/// restarted, so the auto-start that runs at service startup never runs again. If a seat's
/// session did not survive, the health check parks the seat in Error, and nothing ever took a
/// seat out of Error by itself. This pass does, for seats with auto-start on.
///
/// For each seat it asks two things: is the seat's Windows session still there and still logged
/// on as the seat's account, and is the seat saved for auto-start. See <see cref="Decide"/>.
/// It reuses the normal teardown and provisioning paths rather than repairing a seat in place.
/// </summary>
public sealed class SeatReconciler
{
    /// <summary>
    /// Automatic re-provisions allowed per account within <see cref="BudgetWindow"/>. A seat
    /// that keeps failing must not be torn down and rebuilt forever.
    /// </summary>
    internal const int BudgetPerAccount = 3;

    internal static readonly TimeSpan BudgetWindow = TimeSpan.FromHours(1);

    private readonly ILogger<SeatReconciler> _logger;
    private readonly SeatManager _seatManager;
    private readonly SessionLauncher _sessionLauncher;
    private readonly SeatPresetStore _presets;
    private readonly AutoStartProvisioner _provisioner;
    private readonly Dictionary<string, List<DateTimeOffset>> _recentRecoveries =
        new(StringComparer.OrdinalIgnoreCase);

    public SeatReconciler(
        ILogger<SeatReconciler> logger,
        SeatManager seatManager,
        SessionLauncher sessionLauncher,
        SeatPresetStore presets,
        AutoStartProvisioner provisioner)
    {
        _logger = logger;
        _seatManager = seatManager;
        _sessionLauncher = sessionLauncher;
        _presets = presets;
        _provisioner = provisioner;
    }

    /// <summary>What a pass does with one seat.</summary>
    internal enum ReconcileAction
    {
        /// <summary>Healthy, already accurately in Error, or in the middle of something.</summary>
        Leave,

        /// <summary>Its session is gone and auto-start is off: say so, and do nothing else.</summary>
        MarkError,

        /// <summary>Auto-start is on and the seat is broken: tear it down and set it up again.</summary>
        Reprovision,
    }

    /// <summary>
    /// The decision for one seat. <paramref name="verdict"/> is null when the session could not
    /// be asked about, which counts as not lost.
    ///
    /// - A seat mid-provision, mid-recovery or mid-teardown is left to whatever is moving it.
    /// - A seat in Error is rebuilt when it has auto-start on. Otherwise it is left as it is: it
    ///   already says it is broken, and it is the user's to tear down or reconnect.
    /// - A seat whose session is gone, or logged on as another account, is rebuilt when it has
    ///   auto-start on, and otherwise moved to Error with the reason.
    /// - Anything else is healthy as far as this pass can tell. A Disconnected session is the
    ///   health check's existing reconnect path, not this one.
    /// </summary>
    internal static ReconcileAction Decide(
        SeatStatus status, SessionLauncher.SessionVerdict? verdict, bool autoStart)
    {
        if (status is SeatStatus.Idle or SeatStatus.Provisioning or SeatStatus.Configuring
            or SeatStatus.Connecting or SeatStatus.TearingDown)
            return ReconcileAction.Leave;

        if (status == SeatStatus.Error)
            return autoStart ? ReconcileAction.Reprovision : ReconcileAction.Leave;

        var lost = verdict is SessionLauncher.SessionVerdict.Gone or SessionLauncher.SessionVerdict.NotOurs;
        if (!lost)
            return ReconcileAction.Leave;

        return autoStart ? ReconcileAction.Reprovision : ReconcileAction.MarkError;
    }

    /// <summary>
    /// Whether one more automatic re-provision of an account fits the budget, recording it if so.
    /// Times older than the window are dropped first.
    /// </summary>
    internal static bool TryUseBudget(List<DateTimeOffset> recent, DateTimeOffset now)
    {
        recent.RemoveAll(t => now - t >= BudgetWindow);
        if (recent.Count >= BudgetPerAccount)
            return false;

        recent.Add(now);
        return true;
    }

    /// <summary>
    /// Run one pass. Every seat is looked at and its outcome logged at Information or above, so
    /// a bug report's Event Log shows what the service found and what it did after a resume.
    ///
    /// With <see cref="ReconcilePass.IncludeMissing"/>, an auto-start preset that has no seat at
    /// all is provisioned too, as service startup would have done. That is set for a resume, which
    /// stands in for the restart that did not happen. It is not set when the health check asks,
    /// so a seat someone just tore down by hand is not recreated behind their back mid-session.
    /// </summary>
    public async Task ReconcileAsync(ReconcilePass pass, CancellationToken ct)
    {
        _logger.LogInformation("Seat reconciliation starting: {Reasons}", pass.Reasons);

        var autoStart = _presets.GetAutoStart();

        foreach (var seat in _seatManager.GetAllSeats())
        {
            ct.ThrowIfCancellationRequested();

            var preset = autoStart.FirstOrDefault(p =>
                string.Equals(p.AccountName, seat.AccountName, StringComparison.OrdinalIgnoreCase));

            SessionLauncher.SessionCheck? check = null;
            try { check = _sessionLauncher.CheckSession(seat.SessionId, seat.AccountName); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Seat {Id} ({Account}): could not check session {Sid}",
                    seat.Id, seat.AccountName, seat.SessionId);
            }

            var action = Decide(seat.Status, check?.Verdict, preset is not null);

            _logger.LogInformation(
                "Seat {Id} ({Account}): status {Status}, session {Sid} is {Verdict}{Owner}, " +
                "auto-start {AutoStart} -> {Action}",
                seat.Id, seat.AccountName, seat.Status, seat.SessionId,
                check?.Verdict.ToString() ?? "unknown",
                check?.Verdict == SessionLauncher.SessionVerdict.NotOurs ? $" (logged on as '{check?.Owner}')" : "",
                preset is not null ? "on" : "off", action);

            try
            {
                switch (action)
                {
                    case ReconcileAction.MarkError:
                        await MarkSessionLostAsync(seat, check!.Value.Verdict);
                        break;

                    case ReconcileAction.Reprovision:
                        await ReprovisionAsync(seat, preset!, pass.Reasons, ct);
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Seat {Id} ({Account}): reconciliation could not {Action} it",
                    seat.Id, seat.AccountName, action);
            }
        }

        if (pass.IncludeMissing)
        {
            foreach (var preset in autoStart)
            {
                ct.ThrowIfCancellationRequested();

                var exists = _seatManager.GetAllSeats().Any(s =>
                    string.Equals(s.AccountName, preset.AccountName, StringComparison.OrdinalIgnoreCase));
                if (exists) continue;

                if (!TryUseBudgetFor(preset.AccountName)) continue;

                _logger.LogInformation(
                    "Auto-start seat '{Account}' does not exist; provisioning it", preset.AccountName);
                await _provisioner.ProvisionAsync(preset, pass.Reasons, ct);
            }
        }

        _logger.LogInformation("Seat reconciliation finished");
    }

    private async Task MarkSessionLostAsync(SeatInfo seat, SessionLauncher.SessionVerdict verdict)
    {
        if (verdict == SessionLauncher.SessionVerdict.Gone)
        {
            // As the health check does: release the seat's own mstsc on the way to Error. Not for
            // NotOurs, where the mstsc tracked under the number may be the new owner's.
            try { _sessionLauncher.DisconnectSession(seat.SessionId); } catch { /* best effort */ }
        }

        seat.TransitionTo(SeatStatus.Error, _logger);
        seat.ErrorMessage = verdict == SessionLauncher.SessionVerdict.NotOurs
            ? $"Windows session {seat.SessionId} no longer belongs to {seat.AccountName}. " +
              "Auto-start is off, so the seat was not set up again; tear it down and create it again."
            : $"Windows session {seat.SessionId} has ended. Auto-start is off, so the seat was not " +
              "set up again; tear it down and create it again, and turn on auto-start to keep it " +
              "after a restart.";

        _logger.LogWarning("Seat {Id} ({Account}): {Message}", seat.Id, seat.AccountName, seat.ErrorMessage);
        await BroadcastAsync(seat);
    }

    private async Task ReprovisionAsync(SeatInfo seat, SeatPreset preset, string reasons, CancellationToken ct)
    {
        if (!TryUseBudgetFor(seat.AccountName)) return;

        _logger.LogWarning(
            "Seat {Id} ({Account}): auto-start is on and the seat is not usable; tearing it down " +
            "and setting it up again", seat.Id, seat.AccountName);

        await _seatManager.TeardownSeatAsync(seat.Id, ct);
        await _provisioner.ProvisionAsync(preset, reasons, ct);
    }

    private bool TryUseBudgetFor(string accountName)
    {
        if (!_recentRecoveries.TryGetValue(accountName, out var recent))
            _recentRecoveries[accountName] = recent = [];

        if (TryUseBudget(recent, DateTimeOffset.UtcNow))
            return true;

        _logger.LogWarning(
            "Auto-start seat '{Account}': not setting it up again automatically; it has already " +
            "been rebuilt {Count} times in the last {Minutes} minutes. Check the errors above, then " +
            "create it from the dashboard or restart the service",
            accountName, BudgetPerAccount, BudgetWindow.TotalMinutes);
        return false;
    }

    private async Task BroadcastAsync(SeatInfo seat)
    {
        try { await WebSocketHub.BroadcastSeatUpdateAsync(seat); }
        catch (Exception ex) { _logger.LogDebug(ex, "Seat {Id}: could not broadcast", seat.Id); }
    }
}
