using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Monitoring;

namespace MultiSeat.Service;

/// <summary>
/// The Windows Service host lifetime, plus power notifications (issue #87).
///
/// A PC that hibernates or sleeps does not restart the service: the process is suspended and
/// resumed. So the auto-start that runs at service startup does not run after a resume, and
/// nothing else re-checked seats. This asks for a <see cref="SeatReconciler"/> pass when the PC
/// resumes.
///
/// How the notification arrives, and why this way:
///
/// - <see cref="WindowsServiceLifetime"/>, which <c>AddWindowsService()</c> registers, IS a
///   <see cref="ServiceBase"/>. Setting <see cref="ServiceBase.CanHandlePowerEvent"/> before the
///   service starts makes it report SERVICE_ACCEPT_POWEREVENT to the service control manager,
///   which then sends SERVICE_CONTROL_POWEREVENT with the PBT_* event to the service's control
///   handler; ServiceBase turns that into <see cref="ServiceBase.OnPowerEvent"/>. This is the
///   documented channel for services (HandlerEx, SERVICE_STATUS.dwControlsAccepted).
///   PBT_APMRESUMEAUTOMATIC is documented as sent on every resume from sleep or hibernation.
/// - Microsoft.Win32.SystemEvents.PowerModeChanged was NOT used. Its documentation says that in
///   a Windows service it is not raised unless the service runs a message pump (a hidden form),
///   which this one does not.
///
/// What has not been verified: that this fires on this project's hosts. The machine it was built
/// on is never hibernated by us. Also not known: whether a boot with Windows Fast Startup (which
/// resumes session 0 from hibernation) delivers a resume event to services. The health check
/// covers both gaps independently - a seat whose session is gone asks for the same pass.
///
/// Registered only when the process really runs as a service; everything else keeps the
/// console lifetime.
/// </summary>
public sealed class PowerAwareServiceLifetime : WindowsServiceLifetime
{
    /// <summary>
    /// How long after a resume the pass waits. The RDP stack, networking and drivers come back
    /// over the first seconds after a resume, and the health check's own reconnect of
    /// Disconnected sessions gets the first go.
    /// </summary>
    internal static readonly TimeSpan ResumeSettleDelay = TimeSpan.FromSeconds(15);

    private readonly ILogger<PowerAwareServiceLifetime> _logger;
    private readonly SeatReconcileRequests _requests;

    public PowerAwareServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        SeatReconcileRequests requests)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _logger = loggerFactory.CreateLogger<PowerAwareServiceLifetime>();
        _requests = requests;

        // Must be set before ServiceBase.Run, which happens in WaitForStartAsync.
        CanHandlePowerEvent = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        // Runs on the service control dispatcher's side. Only record the request here; the
        // worker runs the pass on its own loop.
        try
        {
            if (HandlePowerEvent(powerStatus, _requests, DateTimeOffset.UtcNow) is { } reason)
                _logger.LogInformation(
                    "Power event {Event}: {Reason}; seats will be re-checked in {Seconds}s",
                    powerStatus, reason, ResumeSettleDelay.TotalSeconds);
            else
                _logger.LogInformation("Power event {Event}", powerStatus);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Power event {Event}: could not queue a seat check", powerStatus);
        }

        return base.OnPowerEvent(powerStatus);
    }

    /// <summary>
    /// Queue a pass for a resume, and only for a resume. Returns why, or null for an event that
    /// needs nothing. ResumeAutomatic comes on every resume; ResumeSuspend may follow it when a
    /// user is present, and ResumeCritical after a critical suspend. All three ask for the pass,
    /// and the requests merge into one pass when they arrive together.
    /// </summary>
    internal static string? HandlePowerEvent(
        PowerBroadcastStatus status, SeatReconcileRequests requests, DateTimeOffset now)
    {
        if (status is not (PowerBroadcastStatus.ResumeAutomatic
                           or PowerBroadcastStatus.ResumeSuspend
                           or PowerBroadcastStatus.ResumeCritical))
            return null;

        var reason = $"the PC resumed ({status})";
        requests.Request(reason, includeMissing: true, notBefore: now + ResumeSettleDelay);
        return reason;
    }
}
