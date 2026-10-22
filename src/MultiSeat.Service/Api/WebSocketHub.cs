using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Api;

/// <summary>
/// Lightweight WebSocket hub that pushes seat state changes to connected dashboard clients.
/// </summary>
public static class WebSocketHub
{
    /// <summary>
    /// A connected dashboard client. Each holds its own send lock so concurrent broadcasts
    /// never issue overlapping SendAsync calls on the same socket (which throws
    /// "outstanding send" and would otherwise drop the client).
    /// </summary>
    private sealed class Client(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private static readonly ConcurrentDictionary<string, Client> _clients = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Map(WebApplication app)
    {
        app.Map("/ws/seats", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            var clientId = Guid.NewGuid().ToString("N");
            _clients.TryAdd(clientId, new Client(ws));

            // A receive that ignores shutdown holds Kestrel's graceful stop open until its
            // timeout, so the read loop watches ApplicationStopping as well as the request.
            var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
            var shuttingDown = false;

            try
            {
                // Keep connection alive — read loop (we only push, but must drain client frames)
                var buffer = new byte[256];
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                shuttingDown = true;
            }
            catch (WebSocketException)
            {
                // The client vanished without a close handshake; nothing left to close.
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                try
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    if (shuttingDown && ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        // Output only: do not wait for a client that may never answer.
                        await ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable,
                            "server stopping", closeTimeout.Token);
                    }
                    else if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                {
                    // Best effort: the socket is already gone or the peer is not answering.
                }
            }
        });
    }

    /// <summary>
    /// Broadcast a seat state change to all connected WebSocket clients.
    /// Called by SeatManager whenever a seat transitions state.
    /// </summary>
    public static async Task BroadcastSeatUpdateAsync(SeatInfo seat)
    {
        var json = JsonSerializer.Serialize(seat, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var (id, client) in _clients)
        {
            if (client.Socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }

            // Serialize sends per socket: two concurrent broadcasts must not call
            // SendAsync on the same WebSocket at the same time.
            await client.SendLock.WaitAsync();
            try
            {
                if (client.Socket.State == WebSocketState.Open)
                    await client.Socket.SendAsync(bytes, WebSocketMessageType.Text,
                        endOfMessage: true, CancellationToken.None);
            }
            catch
            {
                _clients.TryRemove(id, out _);
            }
            finally
            {
                client.SendLock.Release();
            }
        }
    }
}
