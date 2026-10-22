using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiSeat.Service.Api;
using Xunit;

namespace MultiSeat.Tests.Api;

/// <summary>
/// Stopping the API must not wait on an open dashboard socket. Kestrel's graceful stop waits for
/// in-flight requests, and a /ws/seats receive that ignores shutdown is one that never ends, so
/// the service stop sat silent for ~28 s after "Input router stopped" (2026-10-21 event log).
/// ApiServer.Build needs the whole host, so this runs the real auth middleware and the real
/// WebSocketHub route on a loopback port, the same way UpdateEndpointsTests does.
/// </summary>
public class WebSocketShutdownTests
{
    [Fact]
    public async Task StopAsync_WithAnOpenDashboardSocket_CompletesPromptly()
    {
        const string key = "test-key";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseWebSockets();
        ApiServer.UseApiKeyAuth(app, new ApiAuthState(true, key));
        WebSocketHub.Map(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var wsUri = new Uri(address.Replace("http://", "ws://") + "/ws/seats?key=" + key);

        using var client = new ClientWebSocket();
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(wsUri, connectTimeout.Token);
        Assert.Equal(WebSocketState.Open, client.State);

        // The client never closes and never reads: an idle dashboard tab.
        var sw = Stopwatch.StartNew();
        var stop = app.StopAsync();
        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(60)));
        sw.Stop();

        Assert.True(ReferenceEquals(finished, stop),
            "ApiServer StopAsync was still waiting after 60 s with one open /ws/seats socket.");
        await stop;
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"StopAsync took {sw.Elapsed.TotalSeconds:F1} s with one open /ws/seats socket; expected under 5 s.");

        await app.DisposeAsync();
    }
}
