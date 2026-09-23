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
}
