using Microsoft.Extensions.Logging;
using MultiSeat.Service.Sessions;
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// Issue #70: a resolution or scale change disconnects the seat's session and relaunches it at
/// the new geometry. Killing mstsc does not make the session Disconnected at once, and
/// LaunchSessionAsync returns early for a session that is still ACTIVE — so relaunching straight
/// away wrote no new Default.rdp, and only the health check's later rescue applied the change.
///
/// These fake the session state through the same delegates the seat manager passes in.
/// </summary>
public class SeatReconnectOrderingTests
{
    [Fact]
    public async Task RelaunchWaitsUntilTheSessionHasLeftActive()
    {
        var active = true;
        var disconnected = false;
        var pollsAfterDisconnect = 0;
        bool? activeWhenRelaunched = null;

        var id = await SeatManager.DisconnectAndRelaunchAsync(
            sessionId: 22,
            disconnect: _ => disconnected = true,
            isSessionActive: _ =>
            {
                // Windows takes a few polls to notice mstsc is gone.
                if (disconnected && ++pollsAfterDisconnect >= 3) active = false;
                return active;
            },
            relaunch: _ =>
            {
                activeWhenRelaunched = active;
                return Task.FromResult(22);
            },
            new RecordingLogger(), CancellationToken.None, pollMs: 5, timeoutMs: 5_000);

        Assert.True(disconnected);
        Assert.False(activeWhenRelaunched);   // null = never relaunched, true = relaunched too early
        Assert.Equal(22, id);
    }

    [Fact]
    public async Task RelaunchesAnywayWithAWarning_WhenTheSessionNeverLeavesActive()
    {
        var polls = 0;
        var relaunches = 0;
        var logger = new RecordingLogger();

        var id = await SeatManager.DisconnectAndRelaunchAsync(
            sessionId: 22,
            disconnect: _ => { },
            isSessionActive: _ => { polls++; return true; },
            relaunch: _ => { relaunches++; return Task.FromResult(31); },
            logger, CancellationToken.None, pollMs: 5, timeoutMs: 50);

        Assert.Equal(1, relaunches);          // a timeout must not leave the seat stuck
        Assert.Equal(31, id);
        Assert.True(polls > 1, "the session state was never waited on");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task DoesNotWait_WhenTheSessionIsAlreadyDisconnected()
    {
        var polls = 0;
        int? disconnectedId = null;
        var logger = new RecordingLogger();

        await SeatManager.DisconnectAndRelaunchAsync(
            sessionId: 22,
            disconnect: sid => disconnectedId = sid,
            isSessionActive: _ => { polls++; return false; },
            relaunch: _ => Task.FromResult(22),
            logger, CancellationToken.None, pollMs: 5_000, timeoutMs: 10_000);

        Assert.Equal(22, disconnectedId);
        Assert.Equal(1, polls);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
