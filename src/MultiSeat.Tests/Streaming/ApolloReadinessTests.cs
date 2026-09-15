using System.Net;
using MultiSeat.Service.Streaming;
using Xunit;

namespace MultiSeat.Tests.Streaming;

public class ApolloReadinessTests
{
    private static readonly Guid SeatId = Guid.NewGuid();
    private static readonly Uri Endpoint = new("http://127.0.0.1:48100/serverinfo");
    private static string ServerInfo(Guid id) => $"<root status_code=\"200\"><uniqueid>{id}</uniqueid></root>";
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task RefusedConnectionThenWrongSeatThenExpectedSeat_BecomesReady()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            if (calls == 1) throw new HttpRequestException("Connection refused");
            return Task.FromResult(Response(ServerInfo(calls == 2 ? Guid.NewGuid() : SeatId)));
        }));
        await ApolloReadiness.WaitAsync(Endpoint, SeatId, () => true, default, client, TimeSpan.FromSeconds(3));
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("<html>OK</html>")]
    [InlineData("not XML")]
    [InlineData("<root status_code=\"200\"><uniqueid>invalid</uniqueid></root>")]
    [InlineData("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///nope'>]><root>&x;</root>")]
    public async Task ArbitraryHttpSuccess_DoesNotCountAsReady(string body)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(body))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApolloReadiness.WaitAsync(Endpoint, SeatId, () => true, default, client, TimeSpan.FromMilliseconds(80)));
    }

    [Fact]
    public async Task HttpFailureWithValidBody_DoesNotCountAsReady()
    {
        using var client = new HttpClient(new Handler((_, _) =>
            Task.FromResult(Response(ServerInfo(SeatId), HttpStatusCode.ServiceUnavailable))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApolloReadiness.WaitAsync(Endpoint, SeatId, () => true, default, client, TimeSpan.FromMilliseconds(80)));
    }

    [Fact]
    public async Task ExitedProcess_IsRejectedBeforeMakingRequest()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new Exception("Must not request")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApolloReadiness.WaitAsync(Endpoint, SeatId, () => false, default, client));
        Assert.Contains("exited", error.Message);
    }

    [Fact]
    public async Task ProcessExitsDuringSuccessfulRequest_IsNotReady()
    {
        var alive = true;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            alive = false;
            return Task.FromResult(Response(ServerInfo(SeatId)));
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApolloReadiness.WaitAsync(Endpoint, SeatId, () => alive, default, client));
        Assert.Contains("exited", error.Message);
    }

    [Fact]
    public async Task HungRequest_RespectsOverallDeadline()
    {
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Response(ServerInfo(SeatId));
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApolloReadiness.WaitAsync(Endpoint, SeatId, () => true, default, client, TimeSpan.FromMilliseconds(80)));
    }

    [Fact]
    public async Task CallerCancellation_RemainsCancellation()
    {
        using var cancel = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Response(ServerInfo(SeatId));
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ApolloReadiness.WaitAsync(Endpoint, SeatId, () => true, cancel.Token, client));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
