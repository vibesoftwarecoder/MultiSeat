using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MultiSeat.Service.Sessions;
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// Issue #80: while LogonUI.exe is in a new seat's session it holds the input desktop, and Apollo
/// logs "Failed to Open Input Desktop" until LogonUI leaves. Provisioning therefore waits for
/// LogonUI to leave before starting Apollo, with a cap, and goes ahead anyway at the cap.
///
/// These fake the process check through the same delegate the seat manager passes in.
/// </summary>
public class LogonUiWaitTests
{
    [Fact]
    public async Task ReturnsAfterOneCheck_WhenLogonUiIsNotInTheSession()
    {
        var checks = 0;
        var logger = new RecordingLogger();
        var sw = Stopwatch.StartNew();

        // A poll of 5 s would make any wasted loop iteration obvious.
        var outcome = await SeatManager.WaitForLogonUiToLeaveAsync(
            sessionId: 7,
            isLogonUiInSession: _ => { checks++; return false; },
            logger, CancellationToken.None, pollMs: 5_000, timeoutMs: 15_000);

        Assert.Equal(SeatManager.LogonUiWaitOutcome.NotPresent, outcome);
        Assert.Equal(1, checks);
        Assert.True(sw.ElapsedMilliseconds < 1_000, $"took {sw.ElapsedMilliseconds}ms");
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task WaitsUntilLogonUiLeaves_ThenReturns()
    {
        var checks = 0;
        var logger = new RecordingLogger();

        // Present for the first three checks, then gone.
        var outcome = await SeatManager.WaitForLogonUiToLeaveAsync(
            sessionId: 7,
            isLogonUiInSession: sid =>
            {
                Assert.Equal(7, sid);
                return ++checks <= 3;
            },
            logger, CancellationToken.None, pollMs: 5, timeoutMs: 5_000);

        Assert.Equal(SeatManager.LogonUiWaitOutcome.Left, outcome);
        Assert.Equal(4, checks);   // stopped at the first check that found it gone
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task TimesOutWithAWarning_WhenLogonUiNeverLeaves()
    {
        var checks = 0;
        var logger = new RecordingLogger();
        var sw = Stopwatch.StartNew();

        var outcome = await SeatManager.WaitForLogonUiToLeaveAsync(
            sessionId: 7,
            isLogonUiInSession: _ => { checks++; return true; },
            logger, CancellationToken.None, pollMs: 5, timeoutMs: 100);

        Assert.Equal(SeatManager.LogonUiWaitOutcome.TimedOut, outcome);
        Assert.True(checks > 1, "LogonUI was never waited on");
        Assert.True(sw.ElapsedMilliseconds >= 100, $"returned after {sw.ElapsedMilliseconds}ms, before the cap");
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"took {sw.ElapsedMilliseconds}ms, far past the cap");
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SeatManager.WaitForLogonUiToLeaveAsync(
                sessionId: 7, isLogonUiInSession: _ => true,
                new RecordingLogger(), cts.Token, pollMs: 5, timeoutMs: 1_000));
    }

    [Fact]
    public void RealProbe_FindsNoLogonUiInASessionThatDoesNotExist()
    {
        Assert.False(SessionLauncher.IsLogonUiInSession(987_654));
    }

    [Fact]
    public void TheCapCoversTheMeasuredFirstLogon()
    {
        // The slowest first logon measured for #80 kept LogonUI up about 9 s.
        Assert.InRange(SeatManager.LogonUiWaitTimeoutMs, 10_000, 20_000);
        Assert.InRange(SeatManager.LogonUiPollMs, 100, 500);
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
