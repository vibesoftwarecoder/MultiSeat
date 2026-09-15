using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Streaming;

public class ApolloReadinessCleanupTests
{
    [Fact]
    public async Task RealLoopbackServerInfo_MakesSeatReadyWithoutStoppingItsProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "multiseat-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        using var victim = Process.Start(new ProcessStartInfo("ping.exe", "127.0.0.1 -n 120")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true
        })!;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        listener.Start();
        try
        {
            var serverId = Guid.NewGuid();
            await File.WriteAllTextAsync(Path.Combine(directory, "config", "sunshine_state.json"),
                $"{{\"root\":{{\"uniqueid\":\"{serverId}\"}}}}");
            var identity = new ProcessIdentity(victim.Id, victim.StartTime.ToUniversalTime());
            var seat = new SeatInfo
            {
                AccountName = "ReadinessTest", Status = SeatStatus.Error, ErrorMessage = "Previous failure",
                ApolloProcessId = victim.Id, ApolloIdentity = identity,
                PortBase = ((IPEndPoint)listener.LocalEndpoint).Port
            };
            var instance = new ApolloInstance(seat.Id, victim.Id, Path.Combine(directory, "sunshine.conf"),
                1, seat.AccountName, DateTimeOffset.UtcNow, 0, identity);
            var manager = new ApolloManager(NullLogger<ApolloManager>.Instance,
                Options.Create(new MultiSeatOptions()), null!, null!);
            var ready = manager.WaitForReadinessAsync(seat, instance, deadline.Token);
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            Assert.StartsWith("GET /serverinfo HTTP/", await reader.ReadLineAsync(deadline.Token));
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
            Assert.Equal(SeatStatus.Connecting, seat.Status);
            var body = $"<root status_code=\"200\"><uniqueid>{serverId}</uniqueid></root>";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), deadline.Token);
            await ready;
            Assert.Equal(SeatStatus.Ready, seat.Status);
            Assert.Null(seat.ErrorMessage);
            Assert.False(victim.HasExited);
            Assert.Equal(victim.Id, seat.ApolloProcessId);
        }
        finally
        {
            if (!victim.HasExited) victim.Kill();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledReadiness_TerminatesOwnedProcessAndClearsSeat(bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "multiseat-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        using var victim = Process.Start(new ProcessStartInfo("ping.exe", "127.0.0.1 -n 120")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true
        })!;
        try
        {
            Assert.False(victim.HasExited); // positive control: cleanup has a live process to stop
            await File.WriteAllTextAsync(Path.Combine(directory, "config", "sunshine_state.json"), "invalid JSON");
            var identity = new ProcessIdentity(victim.Id, victim.StartTime.ToUniversalTime());
            var seat = new SeatInfo
            {
                AccountName = "ReadinessTest", Status = SeatStatus.Ready,
                ApolloProcessId = victim.Id, ApolloIdentity = identity, PortBase = 48100
            };
            var instance = new ApolloInstance(seat.Id, victim.Id, Path.Combine(directory, "sunshine.conf"),
                1, seat.AccountName, DateTimeOffset.UtcNow, 0, identity);
            var manager = new ApolloManager(NullLogger<ApolloManager>.Instance,
                Options.Create(new MultiSeatOptions()), null!, null!);
            using var cancellation = new CancellationTokenSource();
            if (cancel) cancellation.Cancel();
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    manager.WaitForReadinessAsync(seat, instance, cancellation.Token));
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    manager.WaitForReadinessAsync(seat, instance, cancellation.Token));
            Assert.True(victim.WaitForExit(5000));
            Assert.Equal(0, seat.ApolloProcessId);
            Assert.Null(seat.ApolloIdentity);
            Assert.Equal(SeatStatus.Error, seat.Status);
            if (!cancel) Assert.Contains("cause is not established", seat.ErrorMessage);
        }
        finally
        {
            if (!victim.HasExited) victim.Kill();
            Directory.Delete(directory, recursive: true);
        }
    }
}
