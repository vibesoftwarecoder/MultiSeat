using System.Text.Json;
using System.Text.Json.Nodes;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Updates;

namespace MultiSeat.Service.Api;

/// <summary>
/// The update-notification API. All three routes sit under /api/system and are protected by the
/// API key like the rest; none is in <c>ApiServer.IsAlwaysPublic</c>, because the installed
/// versions they report would help someone choose an attack.
///
/// GET only reads memory and never calls GitHub. Nothing here forwards text from GitHub: the
/// snapshot's links are built from the repository constant and the parsed tag.
/// </summary>
public static class UpdateEndpoints
{
    private const int MaxSettingsBodyBytes = 1024;
    private static readonly SemaphoreSlim SettingsLock = new(1, 1);

    /// <param name="settingsPath">Where the enable switch is saved; tests pass a temp file. Default: appsettings.local.json beside the exe.</param>
    public static void Map(WebApplication app, string? settingsPath = null)
    {
        settingsPath ??= Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
        var group = app.MapGroup("/api/system").WithTags("System");

        // Reads the in-memory result only, whether or not a cache exists.
        group.MapGet("/updates", (UpdateStatusProvider provider) => Results.Ok(provider.GetSnapshot()));

        // 202 with the same body once the check has run (or is already running); 429 inside the
        // cooldown; 409 when checks are off. The check is bounded (UpdateCheckService.ManualDeadline).
        group.MapPost("/updates/check", (UpdateCheckService service, UpdateStatusProvider provider, CancellationToken ct) =>
            CheckAsync(service, provider, ct));

        // Body { "enabled": true|false } and nothing else. Persists into appsettings.local.json,
        // the file that outranks appsettings.json; the running service picks it up through the
        // configuration reload, no restart.
        group.MapPost("/updates/settings", (HttpRequest request, ILoggerFactory logs) =>
            SetEnabledAsync(request.Body, settingsPath,
                logs.CreateLogger("MultiSeat.Updates"), request.HttpContext.RequestAborted));
    }

    internal static async Task<IResult> CheckAsync(UpdateCheckService service, UpdateStatusProvider provider, CancellationToken ct)
    {
        var r = await service.RequestCheckAsync(ct);
        return r.Outcome switch
        {
            ManualCheckOutcome.Disabled => Results.Json(new { error = "Update checks are off" }, statusCode: StatusCodes.Status409Conflict),
            ManualCheckOutcome.Cooldown => new RetryAfterResult(r.RetryAfter),
            _ => Results.Accepted(value: provider.GetSnapshot()),
        };
    }

    private sealed class RetryAfterResult(TimeSpan wait) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            var seconds = (int)Math.Ceiling(wait.TotalSeconds);
            context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new { error = "Checked a moment ago; try again shortly", retryAfterSeconds = seconds });
        }
    }

    /// <summary>POST /api/system/updates/settings. A method so tests can call the real handler with a temporary file.</summary>
    internal static async Task<IResult> SetEnabledAsync(Stream body, string settingsPath, ILogger log, CancellationToken ct)
    {
        bool enabled;
        try
        {
            var buffer = new MemoryStream();
            var chunk = new byte[512];
            int n;
            while ((n = await body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + n > MaxSettingsBodyBytes) return Bad("Request body is too large");
                buffer.Write(chunk, 0, n);
            }

            using var doc = JsonDocument.Parse(buffer.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Bad("Body must be { \"enabled\": true|false }");
            var props = doc.RootElement.EnumerateObject().ToList();
            if (props.Count != 1 || props[0].Name != "enabled" ||
                props[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Bad("Body must be { \"enabled\": true|false } and nothing else");
            enabled = props[0].Value.GetBoolean();
        }
        catch (JsonException)
        {
            return Bad("Body is not valid JSON");
        }

        await SettingsLock.WaitAsync(ct);
        try
        {
            var error = await PersistEnabledAsync(settingsPath, enabled);
            if (error is not null)
            {
                log.LogWarning("Could not persist UpdateCheckEnabled to {Path}: {Error}", settingsPath, error);
                return Results.Json(new { error = "Could not save the setting: " + error },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }
        finally { SettingsLock.Release(); }

        log.LogInformation("Update checks {State} via the API; persisted to {Path}", enabled ? "enabled" : "disabled", settingsPath);
        return Results.Ok(new { enabled, persistedTo = settingsPath });
    }

    private static IResult Bad(string message) => Results.BadRequest(new { error = message });

    /// <summary>
    /// Replace <paramref name="path"/> atomically WITHOUT losing its permissions. The target can
    /// hold the API key and an administrator may have tightened its ACL, so this must not widen it.
    /// <see cref="AtomicFile"/> cannot be used here: it renames a staging file over the target, and
    /// the result is a new file that inherits the folder's ACL (its callers re-grant on purpose).
    /// <see cref="File.Replace(string, string, string?)"/> swaps the same way but merges the
    /// replaced file's ACL and attributes into the replacement. A target that does not exist yet
    /// is created by a plain move. On any failure the staging file is deleted and the target is
    /// left as it was.
    ///
    /// The swap can fail briefly while another process has the target open without sharing delete
    /// (the configuration reload, an antivirus or backup scan). It is retried a few times with a
    /// growing pause before the failure is reported, so the switch does not fail at random.
    /// </summary>
    internal static async Task WriteReplacingKeepingAclAsync(string path, string contents)
    {
        var full = Path.GetFullPath(path);
        var tmp = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(tmp, contents, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(full)) File.Replace(tmp, full, null);
                    else File.Move(tmp, full);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt <= SwapRetries)
                {
                    OnSwapRetry.Value?.Invoke(attempt);
                    await Task.Delay(TimeSpan.FromMilliseconds(20 * Math.Pow(2, attempt - 1)));
                }
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort; never mask the real error */ }
            throw;
        }
    }

    private const int SwapRetries = 6;

    /// <summary>Test seam: called with the attempt number each time the swap failed and is about to be retried.</summary>
    internal static readonly AsyncLocal<Action<int>?> OnSwapRetry = new();

    /// <summary>
    /// Write <c>MultiSeat:UpdateCheckEnabled</c> into <paramref name="settingsPath"/>, creating it
    /// when absent and keeping every other key. Always appsettings.local.json, which Program.cs
    /// loads last so it outranks appsettings.json (the #61 lesson). The write is atomic. A file
    /// that is not strict JSON (comments, say) is reported and left untouched. Returns an error
    /// text, or null on success.
    /// </summary>
    internal static async Task<string?> PersistEnabledAsync(string settingsPath, bool enabled)
    {
        try
        {
            var node = File.Exists(settingsPath)
                ? JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))
                : new JsonObject();

            if (node is not JsonObject root) return "appsettings.local.json does not hold a JSON object.";
            if (root["MultiSeat"] is not JsonObject ms)
            {
                if (root["MultiSeat"] is not null) return "appsettings.local.json has a MultiSeat entry that is not an object.";
                ms = new JsonObject();
                root["MultiSeat"] = ms;
            }
            ms["UpdateCheckEnabled"] = enabled;

            var dir = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await WriteReplacingKeepingAclAsync(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex is JsonException ? "appsettings.local.json is not strict JSON (comments?); it was left unchanged." : ex.Message;
        }
    }
}
