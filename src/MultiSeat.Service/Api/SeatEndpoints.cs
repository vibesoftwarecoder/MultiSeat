using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Api;

public static class SeatEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/seats").WithTags("Seats");

        group.MapGet("/", (SeatManager mgr) =>
            Results.Ok(mgr.GetAllSeats()));

        group.MapGet("/{id:guid}", (Guid id, SeatManager mgr) =>
        {
            var seat = mgr.GetSeat(id);
            return seat is null ? Results.NotFound() : Results.Ok(seat);
        });

        group.MapPost("/", async (SeatRequest request, SeatManager mgr, CancellationToken ct) =>
        {
            if (!ApiInputValidation.IsValidAccountName(request.AccountName))
                return ApiInputValidation.AccountNameError();
            try
            {
                var seat = await mgr.ProvisionSeatAsync(request, ct);
                return Results.Created($"/api/seats/{seat.Id}", seat);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{id:guid}/launch",
            async (Guid id, LaunchAppRequest request, SeatManager mgr, CancellationToken ct) =>
            {
                var seat = mgr.GetSeat(id);
                if (seat is null)
                    return Results.NotFound();

                try
                {
                    await mgr.LaunchAppInSeatAsync(id, request, ct);
                    return Results.Ok(new { status = "launched" });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        group.MapDelete("/{id:guid}", async (Guid id, SeatManager mgr, CancellationToken ct) =>
        {
            var seat = mgr.GetSeat(id);
            if (seat is null)
                return Results.NotFound();

            await mgr.TeardownSeatAsync(id, ct);
            return Results.NoContent();
        });

        // ── Per-seat service management ────────────────────────────────

        group.MapGet("/{id:guid}/services", async (Guid id, SeatManager mgr, CancellationToken ct) =>
        {
            if (mgr.GetSeat(id) is null)
                return Results.NotFound();
            return Results.Ok(await mgr.GetSeatServicesAsync(id, ct));
        });

        group.MapPost("/{id:guid}/apollo/stop", (Guid id, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null)
                return Results.NotFound();
            try
            {
                mgr.StopApollo(id);
                return Results.Ok(new { status = "stopped" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{id:guid}/apollo/start",
            async (Guid id, SeatManager mgr, CancellationToken ct) =>
            {
                if (mgr.GetSeat(id) is null)
                    return Results.NotFound();
                try
                {
                    await mgr.StartApolloAsync(id, ct);
                    return Results.Ok(new { status = "started" });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        group.MapPost("/{id:guid}/apollo/restart",
            async (Guid id, SeatManager mgr, CancellationToken ct) =>
            {
                if (mgr.GetSeat(id) is null)
                    return Results.NotFound();
                try
                {
                    await mgr.RestartApolloAsync(id, ct);
                    return Results.Ok(new { status = "restarted" });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        group.MapPost("/{id:guid}/audio/reset", (Guid id, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null)
                return Results.NotFound();
            try
            {
                mgr.ResetAudio(id);
                return Results.Ok(new { status = "reset" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{id:guid}/display/reset",
            async (Guid id, SeatManager mgr, CancellationToken ct) =>
            {
                if (mgr.GetSeat(id) is null)
                    return Results.NotFound();
                try
                {
                    await mgr.ResetDisplayAsync(id, ct);
                    return Results.Ok(new { status = "reset" });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        group.MapPost("/{id:guid}/controller/reset", (Guid id, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null)
                return Results.NotFound();
            try
            {
                mgr.ResetController(id);
                return Results.Ok(new { status = "reset" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ── Presets ────────────────────────────────────────────────────

        group.MapGet("/presets", (SeatPresetStore presets) =>
            Results.Ok(presets.GetAll()));

        group.MapPut("/{id:guid}/autostart",
            (Guid id, AutoStartRequest req, SeatManager mgr, SeatPresetStore presets) =>
            {
                var seat = mgr.GetSeat(id);
                if (seat is null) return Results.NotFound();

                seat.AutoStart = req.Enabled;

                if (req.Enabled)
                {
                    presets.Upsert(new SeatPreset
                    {
                        AccountName = seat.AccountName,
                        Width = seat.Width,
                        Height = seat.Height,
                        Fps = seat.Fps,
                        AutoStart = true,
                        NvencPreset = seat.NvencPreset,
                    });
                }
                else
                {
                    presets.DeleteByAccount(seat.AccountName);
                }

                return Results.Ok(new { autoStart = seat.AutoStart });
            });

        group.MapPost("/{id:guid}/session-reconnect",
            async (Guid id, SeatManager mgr, SessionLauncher sessionLauncher, CancellationToken ct) =>
            {
                var seat = mgr.GetSeat(id);
                if (seat is null)
                    return Results.NotFound();

                // Pass the seat's geometry: if the session has to be recreated rather than
                // reattached, it must come back at the seat's own size, not inherit the
                // console desktop's.
                //
                // And keep the id it answers with. A recreated session is a NEW session,
                // and everything that acts on this seat afterwards reads SessionId:
                // ProcessInjector, the health check, display isolation. Dropped, they all
                // keep aiming at the session that just went away — apollo/start fails with
                // 500 and the seat can never come back.
                seat.SessionId = await sessionLauncher.LaunchSessionAsync(
                    seat.AccountName, ct, RdpGeometry.ForClient(seat.Width, seat.Height));

                // The health check parks a seat in Error when its session dies, and nothing
                // ever takes it out again. Leave it there and the checks that would restart
                // Apollo are skipped, so the seat stays broken although it now has a live
                // session. Hand it back to the health check in the state it is actually in.
                if (seat.Status == SeatStatus.Error)
                {
                    seat.Status = SeatStatus.Ready;
                    seat.ErrorMessage = null;
                }

                return Results.Ok(new { sessionId = seat.SessionId, message = "Session reconnected" });
            });

        // Change a live seat's resolution. The seat streams its RDP session surface, whose size
        // only mstsc can set, so this takes the session down and brings it back at the new size.
        group.MapPost("/{id:guid}/resolution",
            async (Guid id, ResolutionRequest req, SeatManager mgr,
                   SeatPresetStore presets, CancellationToken ct) =>
            {
                if (mgr.GetSeat(id) is null)
                    return Results.NotFound();
                try
                {
                    await mgr.SetResolutionAsync(id, req.Width, req.Height, presets, ct);
                    var seat = mgr.GetSeat(id);
                    return Results.Ok(new
                    {
                        width = seat?.Width,
                        height = seat?.Height,
                        sessionId = seat?.SessionId,
                    });
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        // Diagnostic: what advanced colour (HDR) does this seat's session advertise, and what is
        // actually active? Answers whether the RdpIdd target inside a seat is HDR-capable at all
        // — the premise the terminal-session HDR work in issue #15 rests on. Runs the probe
        // inside the seat's own session, because display APIs see nothing from Session 0.
        group.MapGet("/{id:guid}/diagnostics/advanced-color",
            async (Guid id, bool? enable, SeatManager mgr, SessionLauncher launcher, CancellationToken ct) =>
            {
                var seat = mgr.GetSeat(id);
                if (seat is null) return Results.NotFound();
                if (seat.SessionId < 0)
                    return Results.BadRequest(new { error = "Seat has no session." });

                // ProgramData rather than %TEMP%: the helper runs as the seat user and the
                // service reads the result back as SYSTEM.
                var outFile = Path.Combine(
                    @"C:\ProgramData\MultiSeat", $"ms_advcolor_{Guid.NewGuid():N}.json");
                var exe = Path.Combine(AppContext.BaseDirectory, "MultiSeat.Service.exe");

                try
                {
                    // ?enable=true asks Windows to turn Advanced Color on before re-reading, which
                    // distinguishes "Windows refused" from "nobody ever asked".
                    var arg = enable == true ? " enable" : "";
                    launcher.RunHelperInSeatSession(
                        seat.SessionId, seat.AccountName, $"\"{exe}\" --advanced-color \"{outFile}\"{arg}");

                    // Fire-and-forget launch, so wait for the artefact rather than an exit code.
                    for (var i = 0; i < 40 && !File.Exists(outFile); i++)
                        await Task.Delay(200, ct);

                    if (!File.Exists(outFile))
                        return Results.Problem("The probe did not produce a result in the seat session.");

                    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outFile, ct));
                    return Results.Ok(doc.RootElement.Clone());
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message);
                }
                finally
                {
                    try { File.Delete(outFile); } catch { /* best effort */ }
                }
            });

        // ── Paired client management ───────────────────────────────────

        group.MapGet("/{id:guid}/clients", (Guid id, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null) return Results.NotFound();
            return Results.Ok(mgr.GetPairedClients(id));
        });

        group.MapDelete("/{id:guid}/clients", (Guid id, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null) return Results.NotFound();
            mgr.UnpairAllClients(id);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}/clients/{name}", (Guid id, string name, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null) return Results.NotFound();
            var removed = mgr.UnpairClient(id, name);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        group.MapPost("/{id:guid}/nvenc-preset",
            async (Guid id, NvencPresetRequest req, SeatManager mgr,
                   SeatPresetStore presets, CancellationToken ct) =>
            {
                if (mgr.GetSeat(id) is null)
                    return Results.NotFound();
                try
                {
                    await mgr.SetNvencPresetAsync(id, req.Preset, presets, ct);
                    return Results.Ok(new { preset = req.Preset.ToString() });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });
    }
}
