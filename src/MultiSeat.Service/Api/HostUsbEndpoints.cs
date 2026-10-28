using System.Text.RegularExpressions;
using MultiSeat.Service.HostUsb;

namespace MultiSeat.Service.Api;

/// <summary>
/// USB forwarding of the host's own Apollo, through its control pipe.
///
/// The API has no key on this host by the owner's choice and every seat user can reach it on loopback, so
/// the whole group, reading included, first asks <see cref="ConsoleCallerCheck"/> whether the request came
/// from the person at the console. Reading is covered because the status names paired clients and the
/// devices they forward. Reasons and codes are fixed words; nothing Apollo or the caller sent is echoed back.
/// </summary>
public static partial class HostUsbEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex UuidShape();

    public sealed record SetEnabledRequest(bool? Enabled);

    public sealed record ClientRequest(string? ClientUuid);

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/host/usb").WithTags("Host");

        // On the GROUP, so every route mapped under it, now or later, is refused before its handler runs.
        var refusals = new RefusalLog(TimeProvider.System);
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var refused = Guard(
                http, http.RequestServices.GetRequiredService<ConsoleCallerCheck>(),
                http.RequestServices.GetRequiredService<ILoggerFactory>(), refusals);
            return refused ?? await next(context);
        });

        // Always 200: "not available" is a state the card shows, not a failure of this request.
        group.MapGet("/", async (IHostUsbControl usb, CancellationToken ct) =>
        {
            var result = await usb.StatusAsync(ct);
            return result.Ok
                ? Results.Ok(new { available = true, status = result.Value })
                : Results.Ok(new { available = false, reason = result.Reason });
        });

        group.MapPost("/enabled", async (
            SetEnabledRequest? body, IHostUsbControl usb, CancellationToken ct) =>
        {
            if (body?.Enabled is not bool enabled) return Results.Json(new { error = "bad_request" }, statusCode: 400);
            return Respond(await usb.SetEnabledAsync(enabled, ct));
        });

        group.MapPost("/revoke", async (
            ClientRequest? body, IHostUsbControl usb, CancellationToken ct) =>
        {
            if (body?.ClientUuid is not { } uuid || !UuidShape().IsMatch(uuid))
                return Results.Json(new { error = "bad_request" }, statusCode: 400);
            return Respond(await usb.RevokeAsync(uuid, ct));
        });

        group.MapPost("/clear-revoke", async (
            ClientRequest? body, IHostUsbControl usb, CancellationToken ct) =>
        {
            if (body?.ClientUuid is not { } uuid || !UuidShape().IsMatch(uuid))
                return Results.Json(new { error = "bad_request" }, statusCode: 400);
            return Respond(await usb.ClearRevokeAsync(uuid, ct));
        });
    }

    /// <summary>
    /// At most one line per outcome per interval. The dashboard polls the status, so a browser outside the
    /// console would otherwise write a warning every few seconds for as long as it stays open.
    /// </summary>
    internal sealed class RefusalLog(TimeProvider time)
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
        private readonly object _gate = new();
        private readonly Dictionary<CallerVerdict, (DateTimeOffset At, long Skipped)> _last = new();

        /// <summary>True when a line should be written now; <paramref name="skipped"/> counts the ones left out since the last.</summary>
        public bool ShouldLog(CallerVerdict verdict, out long skipped)
        {
            lock (_gate)
            {
                var now = time.GetUtcNow();
                if (_last.TryGetValue(verdict, out var last) && now - last.At < Interval)
                {
                    _last[verdict] = (last.At, last.Skipped + 1);
                    skipped = 0;
                    return false;
                }
                skipped = _last.TryGetValue(verdict, out last) ? last.Skipped : 0;
                _last[verdict] = (now, 0);
                return true;
            }
        }
    }

    /// <summary>Null when the caller may use the host's USB forwarding; otherwise the refusal to send.</summary>
    internal static IResult? Guard(HttpContext http, ConsoleCallerCheck check, ILoggerFactory logs, RefusalLog refusals)
    {
        var verdict = check.Check(
            http.Connection.RemoteIpAddress, http.Connection.RemotePort, http.Connection.LocalPort);
        if (verdict == CallerVerdict.Allowed)
            return null;

        if (refusals.ShouldLog(verdict, out var skipped))
            logs.CreateLogger("MultiSeat.Service.Api.HostUsbEndpoints").LogWarning(
                "Refused a host USB forwarding request: {Verdict} ({Skipped} like it since the last line). Only the person at the console is served.",
                verdict, skipped);
        return Results.Json(new { error = "console_session_required", reason = verdict.ToString() }, statusCode: 403);
    }

    private static IResult Respond<T>(UsbOutcome<T> result) =>
        result.Ok ? Results.Ok(result.Value) : Results.Json(new { error = result.Reason }, statusCode: StatusFor(result.Reason!));

    private static int StatusFor(string reason) => reason switch
    {
        UsbReasons.NoHostApollo or UsbReasons.PipeUnavailable or UsbReasons.Busy or "shutting_down" => 503,
        UsbReasons.Timeout => 504,
        "port_refused" or "deny_list_full" => 409,
        "bad_request" or "bad_field" or "too_large" => 400,
        _ => 502,
    };
}
