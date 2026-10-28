using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiSeat.Service.Api;
using MultiSeat.Service.HostUsb;
using MultiSeat.Service.Interop;
using Xunit;

namespace MultiSeat.Tests.HostUsb;

/// <summary>
/// The /api/host/usb routes over real HTTP on a loopback port, through the real route mapping, the real
/// JSON options and the real API key middleware. The control behind them is a recording fake. The
/// console-session rule runs for real in some tests (this process is both client and server, so the
/// real TCP-table lookup finds it) and with scripted answers in the rest. It covers every route here,
/// reading included.
/// </summary>
public class HostUsbEndpointsTests
{
    private sealed class FakeControl : IHostUsbControl
    {
        public List<string> Calls { get; } = new();
        public string? FailWith { get; set; }

        private UsbOutcome<T> Result<T>(string call, T value)
        {
            lock (Calls) Calls.Add(call);
            return FailWith is null ? UsbOutcome<T>.Success(value) : UsbOutcome<T>.Failure(FailWith);
        }

        public Task<UsbOutcome<UsbStatus>> StatusAsync(CancellationToken ct) =>
            Task.FromResult(Result("status", new UsbStatus(true, true, 47996, false,
                new[] { new UsbForward("AB-1", "Player One", "1-2", 1700000000) },
                new[] { new UsbClient("AB-1", "Player One", true) }, new[] { "EF-3" })));

        public Task<UsbOutcome<UsbSetEnabledResult>> SetEnabledAsync(bool enabled, CancellationToken ct) =>
            Task.FromResult(Result($"set_enabled:{enabled}", new UsbSetEnabledResult(enabled, enabled, 47996, false)));

        public Task<UsbOutcome<UsbRevokeResult>> RevokeAsync(string clientUuid, CancellationToken ct) =>
            Task.FromResult(Result($"revoke:{clientUuid}", new UsbRevokeResult(1, true, true)));

        public Task<UsbOutcome<UsbClearResult>> ClearRevokeAsync(string clientUuid, CancellationToken ct) =>
            Task.FromResult(Result($"clear:{clientUuid}", new UsbClearResult(false, true)));
    }

    private sealed class Harness : IAsyncDisposable
    {
        public FakeControl Control { get; } = new();
        public HttpClient Http { get; }
        private readonly WebApplication _app;

        /// <summary>Every (method, path) mapped under /api/host/usb, read from the route table itself.</summary>
        public IReadOnlyList<(string Method, string Path)> Routes() =>
            _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Where(e => e.RoutePattern.RawText!.StartsWith("/api/host/usb", StringComparison.Ordinal))
                .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>())
                    .Select(m => (m, e.RoutePattern.RawText!)))
                .ToList();

        private Harness(WebApplication app, HttpClient http, FakeControl control)
        { _app = app; Http = http; Control = control; }

        public static async Task<Harness> StartAsync(ConsoleCallerCheck check, string? apiKey = null)
        {
            var control = new FakeControl();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.ConfigureHttpJsonOptions(o => ApiServer.ConfigureApiJson(o.SerializerOptions));
            builder.Services.AddSingleton<IHostUsbControl>(control);
            builder.Services.AddSingleton(check);
            var app = builder.Build();
            ApiServer.UseApiKeyAuth(app, new ApiAuthState(apiKey is not null, apiKey ?? ""));
            HostUsbEndpoints.Map(app);
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Harness(app, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) }, control);
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private const string ConsoleUser = "S-1-5-21-1-1-1-1001";

    private sealed class StubOwner(uint session, string user) : IOwnerProcess
    {
        public long CreationTime => 100;
        public uint? SessionId() => session;
        public string? UserSid() => user;
        public void Dispose() { }
    }

    /// <summary>The rule with a fixed answer for "which session is the caller in".</summary>
    private static ConsoleCallerCheck Verdict(bool allowed) =>
        new((_, _) => new TcpTable.Owner(4242, 200), _ => new StubOwner(allowed ? 1u : 3u, ConsoleUser), () => 1u, _ => ConsoleUser, () => []);

    /// <summary>
    /// The real rule, told that the console session is <paramref name="consoleOffset"/> away from this
    /// process's own, with this process's user as the console user. The real TCP table names this process
    /// as the client, and the real process lookup reads its session, user and creation time.
    /// </summary>
    private static ConsoleCallerCheck RealRule(uint consoleOffset, bool meIsASeatAccount = false)
    {
        Kernel32.ProcessIdToSessionId((uint)Environment.ProcessId, out var mine);
        string? me;
        using (var self = OwnerProcess.Open((uint)Environment.ProcessId)) me = self?.UserSid();
        return new ConsoleCallerCheck(
            (clientPort, serverPort) => TcpTable.ClientOwner(TcpTable.ReadConnectionsWithTimes(), clientPort, serverPort),
            OwnerProcess.Open,
            () => mine + consoleOffset,
            _ => me,
            () => meIsASeatAccount && me is not null ? [me] : []);
    }

    private static HttpRequestMessage Post(string path, string body, string? key = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (key is not null) r.Headers.Add("X-MultiSeat-Key", key);
        return r;
    }

    // ── reading is for the console too ───────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task Status_FromTheConsoleSession_ReturnsTheStatus()
    {
        await using var h = await Harness.StartAsync(Verdict(allowed: true));

        var resp = await h.Http.GetAsync("/api/host/usb");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(doc.RootElement.GetProperty("available").GetBoolean());
        var status = doc.RootElement.GetProperty("status");
        Assert.Equal(47996, status.GetProperty("port").GetInt32());
        Assert.Equal("Player One", status.GetProperty("clients")[0].GetProperty("name").GetString());
        Assert.Equal("EF-3", status.GetProperty("denied")[0].GetString());
        Assert.Equal(1700000000, status.GetProperty("forwards")[0].GetProperty("sinceUnix").GetInt64());
    }

    [Fact(Timeout = 60_000)]
    public async Task Status_FromAnotherSession_IsRefused_AndNeverAsksTheControl()
    {
        await using var h = await Harness.StartAsync(Verdict(allowed: false));

        var resp = await h.Http.GetAsync("/api/host/usb");
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("""{"error":"console_session_required","reason":"NotConsoleSession"}""", text);
        Assert.DoesNotContain("Player One", text);
        Assert.Empty(h.Control.Calls);
    }

    [Fact(Timeout = 60_000)]
    public async Task Status_WhenTheCallerIsUnknown_IsRefused()
    {
        var unknown = new ConsoleCallerCheck((_, _) => null, _ => new StubOwner(1, ConsoleUser), () => 1u, _ => ConsoleUser, () => []);
        await using var h = await Harness.StartAsync(unknown);

        var resp = await h.Http.GetAsync("/api/host/usb");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Empty(h.Control.Calls);
    }

    [Fact(Timeout = 60_000)]
    public async Task EveryRouteUnderHostUsb_IsRefusedToAnOutsider()
    {
        // Read from the route table, so a route added later is covered without anyone remembering to test it.
        await using var h = await Harness.StartAsync(Verdict(allowed: false));
        var routes = h.Routes();
        Assert.True(routes.Count >= 4, "the route table was not read");

        foreach (var (method, path) in routes)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), path);
            if (method != "GET") req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await h.Http.SendAsync(req);
            Assert.True(resp.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} answered {(int)resp.StatusCode}");
        }
        Assert.Empty(h.Control.Calls);
    }

    [Fact(Timeout = 60_000)]
    public async Task Status_WhenControlIsUnavailable_IsStill200WithAReason()
    {
        await using var h = await Harness.StartAsync(Verdict(allowed: true));
        h.Control.FailWith = UsbReasons.PipeUnavailable;

        var resp = await h.Http.GetAsync("/api/host/usb");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(doc.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("pipe_unavailable", doc.RootElement.GetProperty("reason").GetString());
    }

    // ── changing is for the console session only ─────────────────────

    public static IEnumerable<object[]> Actions() => new[]
    {
        new object[] { "/api/host/usb/enabled", """{"enabled":true}""", "set_enabled:True" },
        new object[] { "/api/host/usb/revoke", """{"clientUuid":"AB-1"}""", "revoke:AB-1" },
        new object[] { "/api/host/usb/clear-revoke", """{"clientUuid":"AB-1"}""", "clear:AB-1" },
    };

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(Actions))]
    public async Task AnAction_FromTheConsoleSession_GoesThrough(string path, string body, string expectedCall)
    {
        await using var h = await Harness.StartAsync(Verdict(allowed: true));

        var resp = await h.Http.SendAsync(Post(path, body));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(expectedCall, Assert.Single(h.Control.Calls));
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(Actions))]
    public async Task AnAction_FromAnotherSession_IsRefused_AndNothingIsDone(string path, string body, string _)
    {
        await using var h = await Harness.StartAsync(Verdict(allowed: false));

        var resp = await h.Http.SendAsync(Post(path, body));
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("""{"error":"console_session_required","reason":"NotConsoleSession"}""", text);
        Assert.Empty(h.Control.Calls);
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(Actions))]
    public async Task AnAction_WhenTheCallerIsUnknown_IsRefused(string path, string body, string _)
    {
        var unknown = new ConsoleCallerCheck((_, _) => null, _ => new StubOwner(1, ConsoleUser), () => 1u, _ => ConsoleUser, () => []);
        await using var h = await Harness.StartAsync(unknown);

        var resp = await h.Http.SendAsync(Post(path, body));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Empty(h.Control.Calls);
    }

    // The real rule, end to end: this process is the client, so the real TCP table names it, and its
    // real session is compared with the (told) console session.

    [Fact(Timeout = 60_000)]
    public async Task TheRealRule_AllowsARequestFromThisProcess_WhenItIsInTheConsoleSession()
    {
        await using var h = await Harness.StartAsync(RealRule(consoleOffset: 0));
        var resp = await h.Http.SendAsync(Post("/api/host/usb/enabled", """{"enabled":false}"""));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("set_enabled:False", Assert.Single(h.Control.Calls));
    }

    [Fact(Timeout = 60_000)]
    public async Task TheRealRule_RefusesARequestFromThisProcess_WhenTheConsoleIsAnotherSession()
    {
        await using var h = await Harness.StartAsync(RealRule(consoleOffset: 1));
        var resp = await h.Http.SendAsync(Post("/api/host/usb/enabled", """{"enabled":true}"""));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Empty(h.Control.Calls);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheRealRule_RefusesThisProcess_WhenItsUserIsASeatAccount()
    {
        await using var h = await Harness.StartAsync(RealRule(consoleOffset: 0, meIsASeatAccount: true));

        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.GetAsync("/api/host/usb")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.SendAsync(Post("/api/host/usb/enabled", """{"enabled":true}"""))).StatusCode);
        Assert.Empty(h.Control.Calls);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheRealRule_AllowsReading_FromThisProcess_WhenItIsTheConsoleUser()
    {
        await using var h = await Harness.StartAsync(RealRule(consoleOffset: 0));
        var resp = await h.Http.GetAsync("/api/host/usb");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("status", Assert.Single(h.Control.Calls));
    }

    // ── a polling outsider must not flood the Event Log ──────────────

    private sealed class StepClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ARefusedCallerThatPolls_IsLoggedOncePerMinutePerOutcome_WithTheCountOfTheRest()
    {
        var clock = new StepClock();
        var log = new HostUsbEndpoints.RefusalLog(clock);

        Assert.True(log.ShouldLog(CallerVerdict.NotConsoleSession, out var skipped));
        Assert.Equal(0, skipped);
        for (var i = 0; i < 99; i++) Assert.False(log.ShouldLog(CallerVerdict.NotConsoleSession, out _));

        // another outcome has its own line
        Assert.True(log.ShouldLog(CallerVerdict.UnknownCaller, out _));

        clock.Now += TimeSpan.FromSeconds(59);
        Assert.False(log.ShouldLog(CallerVerdict.NotConsoleSession, out _));

        clock.Now += TimeSpan.FromSeconds(2);
        Assert.True(log.ShouldLog(CallerVerdict.NotConsoleSession, out skipped));
        Assert.Equal(100, skipped);
    }

    // ── bad requests do nothing ──────────────────────────────────────

    [Theory(Timeout = 60_000)]
    [InlineData("/api/host/usb/enabled", "{}")]
    [InlineData("/api/host/usb/enabled", """{"enabled":"yes"}""")]
    [InlineData("/api/host/usb/enabled", """{"enabled":null}""")]
    [InlineData("/api/host/usb/enabled", "not json")]
    [InlineData("/api/host/usb/revoke", "{}")]
    [InlineData("/api/host/usb/revoke", """{"clientUuid":""}""")]
    [InlineData("/api/host/usb/revoke", """{"clientUuid":"a b"}""")]
    [InlineData("/api/host/usb/revoke", """{"clientUuid":"x;y"}""")]
    [InlineData("/api/host/usb/revoke", """{"clientUuid":5}""")]
    [InlineData("/api/host/usb/clear-revoke", """{"clientUuid":"ÄÖ"}""")]
    public async Task ABadRequest_IsRejected_AndNothingIsDone(string path, string body)
    {
        await using var h = await Harness.StartAsync(Verdict(allowed: true));
        var resp = await h.Http.SendAsync(Post(path, body));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(h.Control.Calls);
    }

    [Fact(Timeout = 60_000)]
    public async Task ATooLongUuid_IsRejected()
    {
        await using var h = await Harness.StartAsync(Verdict(allowed: true));
        var resp = await h.Http.SendAsync(Post("/api/host/usb/revoke", $$"""{"clientUuid":"{{new string('A', 65)}}"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(h.Control.Calls);
    }

    // ── failures keep their fixed words ──────────────────────────────

    [Theory(Timeout = 60_000)]
    [InlineData("no_host_apollo", 503)]
    [InlineData("pipe_unavailable", 503)]
    [InlineData("busy", 503)]
    [InlineData("shutting_down", 503)]
    [InlineData("timeout", 504)]
    [InlineData("port_refused", 409)]
    [InlineData("deny_list_full", 409)]
    [InlineData("untrusted_server", 502)]
    [InlineData("bad_reply", 502)]
    [InlineData("start_failed", 502)]
    public async Task AFailure_ComesBackAsItsCodeWithAMatchingStatus(string reason, int status)
    {
        await using var h = await Harness.StartAsync(Verdict(allowed: true));
        h.Control.FailWith = reason;

        var resp = await h.Http.SendAsync(Post("/api/host/usb/enabled", """{"enabled":true}"""));

        Assert.Equal(status, (int)resp.StatusCode);
        Assert.Equal($$"""{"error":"{{reason}}"}""", await resp.Content.ReadAsStringAsync());
    }

    // ── the API key still applies ────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task WithAnApiKey_BothReadingAndChangingNeedIt()
    {
        await using var h = await Harness.StartAsync(Verdict(allowed: true), apiKey: "secret-key");

        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Http.GetAsync("/api/host/usb")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Http.SendAsync(Post("/api/host/usb/enabled", """{"enabled":true}"""))).StatusCode);
        Assert.Empty(h.Control.Calls);

        Assert.Equal(HttpStatusCode.OK, (await h.Http.SendAsync(Post("/api/host/usb/enabled", """{"enabled":true}""", key: "secret-key"))).StatusCode);
    }
}
