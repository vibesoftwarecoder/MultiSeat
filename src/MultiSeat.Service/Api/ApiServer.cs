using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiSeat.Service.Configuration;
using MultiSeat.Shared;

namespace MultiSeat.Service.Api;

/// <summary>
/// Builds and configures the embedded ASP.NET Core Minimal API server
/// that runs inside the Windows Service process.
/// </summary>
public static class ApiServer
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddEndpointsApiExplorer();
    }

    public static WebApplication Build(IServiceProvider hostServices, MultiSeatOptions options)
    {
        // Set ContentRootPath explicitly — WebApplication.CreateBuilder() defaults to
        // Directory.GetCurrentDirectory() which is C:\Windows\system32 for a Windows Service,
        // causing UseStaticFiles / MapFallbackToFile to fail to find the wwwroot folder.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        // Re-use the host's singleton registrations
        builder.Services.AddSingleton(hostServices.GetRequiredService<Sessions.SeatManager>());
        builder.Services.AddSingleton(hostServices.GetRequiredService<Sessions.RdpWrapper>());
        builder.Services.AddSingleton(hostServices.GetRequiredService<Sessions.SessionLauncher>());
        builder.Services.AddSingleton(hostServices.GetRequiredService<Accounts.AccountManager>());
        builder.Services.AddSingleton(hostServices.GetRequiredService<Monitoring.GpuMonitor>());
        builder.Services.AddSingleton(hostServices.GetRequiredService<Monitoring.MetricsCollector>());
        builder.Services.AddSingleton(hostServices.GetRequiredService<Monitoring.SessionHealthCheck>());
        builder.Services.AddSingleton(hostServices.GetRequiredService<Monitoring.HostApolloMonitor>());
        builder.Services.AddSingleton(hostServices.GetRequiredService<Display.VirtualDisplayManager>());
        builder.Services.AddSingleton(hostServices.GetRequiredService<Configuration.SeatPresetStore>());

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (options.ApiBindLoopbackOnly)
                kestrel.ListenLocalhost(options.ApiPort);
            else
                kestrel.ListenAnyIP(options.ApiPort);
        });

        builder.Services.ConfigureHttpJsonOptions(opts =>
        {
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        // Register CORS services (required before UseCors)
        builder.Services.AddCors();

        // Log through the HOST's logger, not app.Logger.
        //
        // ApiServer builds its own WebApplication, and that builder's logging has no Event Log
        // provider — the service's does, via AddWindowsService. So everything written to
        // app.Logger went to a console that a Windows Service does not have, and none of it has
        // ever appeared in the Event Log: not the port, not the key notice, and not "API
        // authentication is DISABLED", which is the loudest warning this service can raise.
        // Verified before changing it: three days of events contain no ApiServer-category entries.
        //
        // Created before Build() because ResolveApiKey needs somewhere to report a failure to
        // restrict the key file's permissions.
        var log = hostServices.GetRequiredService<ILoggerFactory>()
                              .CreateLogger("MultiSeat.Service.Api.ApiServer");

        // Resolve effective API key before Build() so ApiAuthState can be registered in DI.
        // Key is saved to C:\ProgramData\MultiSeat\api-key.txt so the operator can
        // copy it into the dashboard Settings page. It is never embedded in appsettings.json.
        var apiKey = ResolveApiKey(options.ApiKey, log);
        var authState = new ApiAuthState(!string.IsNullOrEmpty(apiKey), apiKey);
        builder.Services.AddSingleton(authState);

        var app = builder.Build();

        // ── Middleware ───────────────────────────────────────────────
        app.UseWebSockets();

        // Serve dashboard static files from wwwroot/ if present
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        // API key auth — enforced for /api/ routes unless explicitly disabled.
        // Checks ApiAuthState.IsEnabled on every request so toggling via the dashboard
        // takes effect immediately without a service restart.
        // Static files are exempt; /api and /ws are not.
        //
        // /ws USED TO BE EXEMPT, which was a hole rather than a convenience: /ws/seats
        // broadcasts whole SeatInfo objects — account names, session ids, ports, Apollo PIDs,
        // audio device ids — so with authentication switched on, anyone who could reach the
        // port could still stream all of it by opening a socket instead of calling the API.
        app.Use(async (context, next) =>
        {
            var isProtected = context.Request.Path.StartsWithSegments("/api")
                              || context.Request.Path.StartsWithSegments("/ws");

            if (!authState.IsEnabled ||
                !isProtected ||
                IsAlwaysPublic(context.Request.Path, context.Request.Method))
            {
                await next();
                return;
            }

            // Browsers cannot set headers on a WebSocket handshake, so the key may also arrive
            // as ?key=. That does put a secret in a URL; it is accepted because the alternative
            // for a browser client is a post-upgrade handshake, and because this API is
            // loopback/LAN with no request logging of query strings. Header is preferred and
            // checked first — non-browser clients should use it.
            var presented = context.Request.Headers.TryGetValue(Constants.ApiKeyHeader, out var header)
                ? header.ToString()
                : context.Request.Query["key"].ToString();

            if (string.IsNullOrEmpty(presented) || presented != authState.ApiKey)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }

            await next();
        });

        // State the network posture rather than leaving it to be discovered. The API is plaintext
        // HTTP; bound beyond loopback that means the key (and everything it protects) crosses the
        // network in clear, which is exactly what the old RequireHttps option implied was handled.
        if (options.ApiBindLoopbackOnly)
        {
            log.LogInformation(
                "API bound to loopback only on port {Port} — reachable on this host, not from the network.",
                options.ApiPort);
        }
        else
        {
            log.LogWarning(
                "API is listening on ALL interfaces on port {Port} over plaintext HTTP — there is no " +
                "HTTPS, so the API key is sent in clear to anyone who can see the traffic. Set " +
                "MultiSeat:ApiBindLoopbackOnly to true if the dashboard is only opened on this host, " +
                "or restrict the port with a firewall rule.",
                options.ApiPort);
        }

        if (!authState.IsEnabled)
        {
            log.LogWarning(
                "API authentication is DISABLED — the dashboard is open to anyone on the network.");
        }
        else if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            log.LogWarning(
                "No ApiKey set in appsettings.json — a key was auto-generated. " +
                "Copy it from C:\\ProgramData\\MultiSeat\\api-key.txt into the dashboard Settings page.");
        }

        // CORS — the DEFAULT is restrictive. This used to send AllowAnyOrigin whenever
        // CorsOrigins was empty, which is the out-of-the-box configuration, so every install
        // shipped the most permissive policy available and only a deliberate edit narrowed it.
        // The dashboard is served from this same origin and needs no CORS at all; cross-origin
        // callers are the exception and now have to say so.
        if (options.CorsOrigins.Length > 0)
        {
            app.UseCors(policy => policy
                .WithOrigins(options.CorsOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials());
        }
        else
        {
            // Loopback only, so a dashboard opened on the host itself keeps working while a page
            // on some other machine cannot read this API from a browser. Set MultiSeat:CorsOrigins
            // to allow specific remote origins.
            app.UseCors(policy => policy
                .WithOrigins(
                    $"http://localhost:{options.ApiPort}",
                    $"http://127.0.0.1:{options.ApiPort}")
                .AllowAnyMethod()
                .AllowAnyHeader());
        }

        // ── Map endpoint groups ──────────────────────────────────────
        SeatEndpoints.Map(app);
        AccountEndpoints.Map(app);
        SystemEndpoints.Map(app);
        HostEndpoints.Map(app);
        InputEndpoints.Map(app);
        WebSocketHub.Map(app);

        // Fallback to index.html for SPA routing (dashboard)
        if (Directory.Exists(wwwroot))
        {
            app.MapFallbackToFile("index.html");
        }

        return app;
    }

    /// <summary>
    /// The one request that bypasses the API key while authentication is enabled: reading the
    /// auth state, so the dashboard can show the toggle before it holds a key.
    ///
    /// The method check is the load-bearing half. The same path also accepts POST, which is what
    /// turns authentication OFF — exempting that would let anyone who can reach the port disable
    /// the protection for every other endpoint. Note that the endpoint's own AllowAnonymous()
    /// metadata grants nothing here; this predicate is the whole rule, which is why it is pinned
    /// by tests rather than left inline.
    /// </summary>
    internal static bool IsAlwaysPublic(PathString path, string method) =>
        path.Equals("/api/system/auth") && method == "GET";

    /// <summary>
    /// Returns the configured API key, or generates+persists a random one if none is set.
    /// Reads an existing persisted key so the same key survives service restarts.
    /// </summary>
    private static string ResolveApiKey(string configured, ILogger log)
    {
        // "disabled" is an explicit opt-out — return empty so the auth middleware is skipped.
        if (configured.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return EnsurePersistedKey(log);
    }

    /// <summary>
    /// Return the key held in <c>C:\ProgramData\MultiSeat\api-key.txt</c>, generating and
    /// hardening one if the file is absent or empty. Always returns a usable key.
    ///
    /// Split out of <see cref="ResolveApiKey"/> so that turning authentication ON at runtime can
    /// obtain a key the same way startup does, instead of enabling against an empty one (#61).
    /// </summary>
    internal static string EnsurePersistedKey(ILogger log)
    {
        var keyFile = Path.Combine(@"C:\ProgramData\MultiSeat", "api-key.txt");

        if (File.Exists(keyFile))
        {
            // Harden on the read path as well as the write path. This file is written once and
            // then read on every start, so an install created before this change would otherwise
            // keep the inherited ProgramData ACL — which grants BUILTIN\Users read, i.e. every
            // local account including every seat could read the key that guards the API.
            HardenKeyFile(keyFile, log);

            var persisted = File.ReadAllText(keyFile).Trim();
            if (!string.IsNullOrWhiteSpace(persisted))
                return persisted;
        }

        // Generate a URL-safe 32-char random key
        var raw = RandomNumberGenerator.GetBytes(24);
        var generated = Convert.ToBase64String(raw)
            .Replace('+', 'A').Replace('/', 'B').Replace('=', 'C');

        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        File.WriteAllText(keyFile, generated);
        HardenKeyFile(keyFile, log);
        return generated;
    }

    private static void HardenKeyFile(string keyFile, ILogger log) =>
        Storage.SecureFile.TryRestrictToSystemAndAdmins(keyFile, ex =>
            log.LogWarning(ex,
                "Could not restrict permissions on {Path} — the API key may still be readable by " +
                "other local accounts on this host.", keyFile));
}

