using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Display;
using MultiSeat.Service.Monitoring;
using MultiSeat.Service.Sessions;

namespace MultiSeat.Service.Api;

public static class SystemEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/system").WithTags("System");

        group.MapGet("/health", (SeatManager seats, MetricsCollector metrics) =>
            Results.Ok(metrics.Collect(seats)));

        // Triggers a full rebuild and service restart.
        // Spawns a detached PowerShell process that runs install-service.ps1 after a 3s delay,
        // giving this HTTP response time to reach the client before the service stops.
        // Requires SourceDir to be set in appsettings.json.
        group.MapPost("/rebuild", (IOptions<MultiSeatOptions> opts, ILoggerFactory logFactory) =>
        {
            var log = logFactory.CreateLogger("MultiSeat.Rebuild");
            var sourceDir = opts.Value.SourceDir;
            if (string.IsNullOrWhiteSpace(sourceDir))
                return Results.BadRequest(new { error = "SourceDir not configured in appsettings.json" });

            var script = Path.Combine(sourceDir, "scripts", "install-service.ps1");
            if (!File.Exists(script))
                return Results.BadRequest(new { error = $"Script not found: {script}" });

            // Detached PowerShell: wait 3s then run the install script (which stops + restarts the service).
            var ps = $"Start-Sleep 3; & '{script}'";
            Process.Start(new ProcessStartInfo(
                "powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{ps}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            log.LogInformation("Rebuild triggered — service will restart in ~3s");
            return Results.Accepted(value: new { message = "Rebuild started — service will restart shortly" });
        });

        // Diagnostic endpoint — dumps all connected display paths from QueryDisplayConfig.
        // Use this to verify SudoVDA virtual displays are visible and check their names.
        // GET /api/system/displays
        group.MapGet("/displays", (VirtualDisplayManager displays) =>
        {
            var allPaths = displays.EnumerateAllConnectedPaths();
            return Results.Ok(new
            {
                totalConnected = allPaths.Count,
                sudoVdaFound = displays.IsDriverAvailable,
                paths = allPaths
            });
        });

        // GET /api/system/auth — returns current authentication state.
        //
        // Deliberately public: the dashboard has to be able to show the auth toggle before it
        // holds a key. What actually makes it public is the exemption in ApiServer's own auth
        // middleware, which matches this path AND the GET method — NOT the AllowAnonymous() below.
        // There is no UseAuthorization() in this pipeline, so that call is inert; it is kept only
        // to state intent, and to be correct if authorization middleware is ever added.
        group.MapGet("/auth", (ApiAuthState authState) =>
            Results.Ok(new { authEnabled = authState.IsEnabled }))
            .AllowAnonymous();

        // POST /api/system/auth — toggles API key authentication on/off.
        // Takes effect immediately (no restart needed); also persists to appsettings.json.
        //
        // This one REQUIRES the API key whenever auth is enabled, and must keep doing so: it is
        // the endpoint that can turn authentication off, so an unauthenticated caller reaching it
        // would be able to disable the protection for everything else. ApiServer's middleware
        // exempts only GET on this path, so POST is gated — which is why the AllowAnonymous() that
        // used to sit here has been removed rather than commented.
        //
        // It carried the note "must be reachable even when auth is currently disabled", which was
        // confused twice over: when auth is disabled the middleware already lets every request
        // through, so no exemption is needed, and the call could not have granted one anyway
        // (nothing reads endpoint authorization metadata in this pipeline). Left in place it would
        // have become a real hole the moment anyone added UseAuthorization().
        group.MapPost("/auth", async (ApiAuthState authState, AuthToggleRequest body, ILoggerFactory logFactory) =>
        {
            var log = logFactory.CreateLogger("MultiSeat.Auth");

            // ── Turning it ON has to leave a usable key behind ──────────────────────────
            // The key is what the operator needs and the one thing the old code never produced:
            // it wrote "" to appsettings.json, which does not mean "use the configured key" — it
            // means "fall back to api-key.txt", a file the operator has never been shown. And on a
            // host configured "disabled" the in-memory key is empty, so enabling authentication
            // rejected every subsequent request including the one that would undo it.
            string? key = null;
            if (body.Enabled)
            {
                key = !string.IsNullOrWhiteSpace(authState.ApiKey)
                    ? authState.ApiKey                      // already have one — keep it
                    : ApiServer.EnsurePersistedKey(log);    // none configured — make one

                authState.Enable(key);                      // sets the key before the flag
            }
            else
            {
                authState.Disable();
            }

            // ── Persist to the file that actually decides ───────────────────────────────
            // Program.cs loads appsettings.json and THEN appsettings.local.json, so the local file
            // wins. Writing the setting to appsettings.json while the local file sets ApiKey meant
            // the change survived until the next restart and then silently reverted (#61).
            var settingsPath = ResolveApiKeySettingsFile(AppContext.BaseDirectory);
            var persistedTo = (string?)null;
            var persistError = (string?)null;
            try
            {
                var node = File.Exists(settingsPath)
                    ? JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))
                    : new JsonObject();

                if (node?["MultiSeat"] is not JsonObject ms)
                {
                    ms = new JsonObject();
                    node!["MultiSeat"] = ms;
                }

                // An explicit key when on, the explicit opt-out when off. Never "", which is the
                // overloaded value that made this confusing in the first place.
                ms["ApiKey"] = body.Enabled ? key! : "disabled";

                await File.WriteAllTextAsync(settingsPath,
                    node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                persistedTo = settingsPath;
            }
            catch (Exception ex)
            {
                // Report it. A toggle that cannot persist must not look like one that did.
                persistError = ex.Message;
                log.LogWarning(ex, "Could not persist the auth setting to {Path}", settingsPath);
            }

            log.LogInformation(
                "API authentication {State} via dashboard; persisted to {Path}",
                body.Enabled ? "enabled" : "disabled", persistedTo ?? "(nothing — see warning)");

            return Results.Ok(new
            {
                authEnabled = authState.IsEnabled,
                // Returned so the dashboard can show and store the key. Only meaningful when
                // enabling; a caller that can reach this endpoint while auth is off already has
                // full control of the API, so this discloses nothing it could not already do.
                apiKey = body.Enabled ? key : null,
                persistedTo,
                persistError,
            });
        });

        // GET /api/system/refresh-rate — the host-wide DWM interval in effect and its rate.
        //
        // Gated like every other /api route. Only GET /api/system/auth is public, and this reveals
        // nothing a dashboard without the key needs before it has one.
        group.MapGet("/refresh-rate", (DwmFrameIntervalSetting setting) =>
            Results.Ok(RefreshRateStatus(setting, DwmFrameIntervalRegistry.Read())));

        // POST /api/system/refresh-rate — set the host-wide DWM interval (issue #74).
        //
        // Follows POST /api/system/auth: takes effect without a service restart and persists so it
        // survives one. Unlike the auth toggle it cannot change anything already running — Windows
        // reads the interval only when a new RDP session's compositor starts — so it applies to
        // each seat's NEXT session, and the response says so.
        group.MapPost("/refresh-rate", (RefreshRateRequest? body, DwmFrameIntervalSetting setting) =>
            SetRefreshRateAsync(
                body,
                setting,
                ms => DwmFrameIntervalRegistry.Write(ms, setting.Logger),
                Path.Combine(AppContext.BaseDirectory, "appsettings.local.json"),
                setting.Logger));
    }

    /// <summary>
    /// What every refresh-rate response says about scope. A change can never reach a session that
    /// already exists, and a caller who does not know that will think it failed.
    /// </summary>
    internal const string RefreshRateAppliesTo =
        "Applies to each seat's next session. Seats already running keep their current rate " +
        "until they are stopped and started again; a reconnect or a resize keeps the same " +
        "session, so it does not pick the change up.";

    internal static object RefreshRateStatus(DwmFrameIntervalSetting setting, int? registryIntervalMs) => new
    {
        intervalMs = setting.IntervalMs,
        refreshRateHz = setting.EffectiveRefreshRateHz,
        // What Windows will actually read for the next session. Differs from intervalMs only when
        // the startup write failed or someone edited the registry by hand.
        registryIntervalMs,
        defaultIntervalMs = new MultiSeatOptions().DwmFrameIntervalMs,
        allowedIntervalsMs = DwmFrameIntervalSetting.AllowedIntervalsMs,
        appliesTo = RefreshRateAppliesTo,
    };

    // One change at a time: the registry write, the in-memory value and the file must end up
    // agreeing, and two interleaved requests could leave each holding a different value.
    private static readonly SemaphoreSlim RefreshRateLock = new(1, 1);

    /// <summary>
    /// POST /api/system/refresh-rate. A method rather than a lambda so tests can call the real
    /// handler with a fake registry write and a temporary settings file.
    ///
    /// Order matters. The registry is written first because it is the only part Windows reads; if
    /// that fails nothing else changes, so the dashboard never shows a rate the next session will
    /// not get. Then the shared in-memory value, which the next provision's fps check reads. Then
    /// the file, so the choice survives a restart — a failure there is reported, as the auth
    /// toggle does, rather than hidden.
    /// </summary>
    internal static async Task<IResult> SetRefreshRateAsync(
        RefreshRateRequest? body,
        DwmFrameIntervalSetting setting,
        Func<int, string?> writeRegistry,
        string settingsPath,
        ILogger log)
    {
        if (body?.IntervalMs is not { } intervalMs)
            return Results.BadRequest(new
            {
                error = "intervalMs is required. Use one of: " +
                        $"{string.Join(", ", DwmFrameIntervalSetting.AllowedIntervalsMs)} (ms).",
            });

        if (!DwmFrameIntervalSetting.IsAllowedInterval(intervalMs))
            return Results.BadRequest(new { error = DwmFrameIntervalSetting.IntervalError(intervalMs) });

        await RefreshRateLock.WaitAsync();
        try
        {
            var previous = setting.IntervalMs;

            if (writeRegistry(intervalMs) is { } registryError)
                return Results.Json(new { error = registryError }, statusCode: StatusCodes.Status500InternalServerError);

            setting.Set(intervalMs);

            var (persistedTo, persistError) = await PersistDwmFrameIntervalAsync(settingsPath, intervalMs);
            if (persistError is not null)
                log.LogWarning(
                    "DWM frame interval set to {Ms}ms but not persisted to {Path}: {Error}. It reverts " +
                    "to the configured value at the next service restart",
                    intervalMs, settingsPath, persistError);

            log.LogInformation(
                "DWM frame interval changed from {Old}ms to {Ms}ms (~{Hz}Hz) via the API; persisted to " +
                "{Path}. Applies to each seat's next session",
                previous, intervalMs, setting.EffectiveRefreshRateHz, persistedTo ?? "(nothing — see warning)");

            return Results.Ok(new
            {
                intervalMs = setting.IntervalMs,
                refreshRateHz = setting.EffectiveRefreshRateHz,
                previousIntervalMs = previous,
                appliesTo = RefreshRateAppliesTo,
                persistedTo,
                persistError,
            });
        }
        finally
        {
            RefreshRateLock.Release();
        }
    }

    /// <summary>
    /// Write <c>MultiSeat:DwmFrameIntervalMs</c> into <paramref name="settingsPath"/>, creating the
    /// file when it does not exist and keeping every other setting in it.
    ///
    /// Always appsettings.local.json: Program.cs loads it last, so it outranks appsettings.json
    /// whatever that says. The installer also keeps the host's appsettings.json byte for byte on
    /// upgrade, so a host installed before this release still has 16 there; writing the local
    /// file is the only way a dashboard choice is sure to win.
    ///
    /// A file that does not parse as strict JSON — one with comments, say — is reported and left
    /// alone rather than rewritten without them.
    /// </summary>
    internal static async Task<(string? PersistedTo, string? PersistError)> PersistDwmFrameIntervalAsync(
        string settingsPath, int intervalMs)
    {
        try
        {
            var node = File.Exists(settingsPath)
                ? JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))
                : new JsonObject();

            if (node is not JsonObject root)
                return (null, $"{settingsPath} does not hold a JSON object.");

            if (root["MultiSeat"] is not JsonObject ms)
            {
                if (root["MultiSeat"] is not null)
                    return (null, $"{settingsPath} has a MultiSeat entry that is not an object.");
                ms = new JsonObject();
                root["MultiSeat"] = ms;
            }

            ms["DwmFrameIntervalMs"] = intervalMs;

            await File.WriteAllTextAsync(settingsPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (settingsPath, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Pick the settings file whose ApiKey actually takes effect. Program.cs registers
    /// appsettings.json first and appsettings.local.json second, and the later source wins — so
    /// when the local file sets ApiKey, it is the only file worth writing to.
    /// </summary>
    internal static string ResolveApiKeySettingsFile(string baseDirectory)
    {
        var local = Path.Combine(baseDirectory, "appsettings.local.json");

        if (File.Exists(local))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(local)) is JsonObject root
                    && root["MultiSeat"] is JsonObject ms
                    && ms.ContainsKey("ApiKey"))
                {
                    return local;
                }
            }
            catch
            {
                // Unparseable local file: fall through to appsettings.json rather than guessing.
            }
        }

        return Path.Combine(baseDirectory, "appsettings.json");
    }

    private record AuthToggleRequest(bool Enabled);

    /// <summary>Body of POST /api/system/refresh-rate, e.g. <c>{ "intervalMs": 8 }</c>.</summary>
    internal record RefreshRateRequest(int? IntervalMs);
}
