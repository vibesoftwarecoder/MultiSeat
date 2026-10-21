using MultiSeat.Service.Monitoring;
using MultiSeat.Service.Streaming;
using Xunit;

namespace MultiSeat.Tests.Monitoring;

/// <summary>
/// Issue #124: what the health check does when a seat's Apollo is no longer running, by exit code.
///
/// With no console window, Apollo exits 1115 (ERROR_SHUTDOWN_IN_PROGRESS) on Quit and 0 on
/// Restart, and starts nothing itself. Quit must stay stopped and spend no restart attempt.
/// Restart must relaunch without spending one. Anything else, including an exit code that could
/// not be read, is a crash and behaves as every exit did before.
///
/// What stays live-only: that a real Apollo run with CREATE_NO_WINDOW exits with these codes,
/// and the wiring inside SessionHealthCheck.CheckSeatAsync around this decision.
/// </summary>
public class ApolloExitTests
{
    private static readonly TimeSpan LongRun = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan JustStarted = TimeSpan.FromSeconds(5);

    // ── Classifier ───────────────────────────────────────────────────────

    [Fact]
    public void ExitCode1115_IsQuit() =>
        Assert.Equal(ApolloExitKind.Quit, ApolloExit.Classify(1115));

    [Fact]
    public void ExitCode0_IsRestart() =>
        Assert.Equal(ApolloExitKind.Restart, ApolloExit.Classify(0));

    [Theory]
    [InlineData(1)]       // taskkill /f
    [InlineData(-1)]      // TerminateProcess(..., -1), what Process.Kill uses
    [InlineData(2)]       // Apollo's config parse failure
    [InlineData(7)]       // Apollo's unknown command
    [InlineData(259)]     // STILL_ACTIVE, should it ever leak through
    [InlineData(-1073741819)] // 0xC0000005, access violation
    public void AnyOtherExitCode_IsCrash(int code) =>
        Assert.Equal(ApolloExitKind.Crash, ApolloExit.Classify(code));

    [Fact]
    public void UnknownExitCode_IsCrash() =>
        // No handle, or the code could not be read. Never mistaken for a clean Restart.
        Assert.Equal(ApolloExitKind.Crash, ApolloExit.Classify(null));

    // ── Decision ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Quit_IsLeftStopped_EvenStraightAfterLaunch(bool isEarlyExit) =>
        Assert.Equal(ApolloExitAction.LeaveStopped, ApolloExit.Decide(ApolloExitKind.Quit, isEarlyExit));

    [Fact]
    public void Restart_RelaunchesUncounted() =>
        Assert.Equal(ApolloExitAction.RelaunchUncounted, ApolloExit.Decide(ApolloExitKind.Restart, isEarlyExit: false));

    [Fact]
    public void RestartInsideTheStartupWindow_IsCounted()
    {
        // The loop bound: an Apollo that exits 0 by itself right after every launch must still
        // run out of attempts, not relaunch every 5 seconds forever.
        Assert.Equal(ApolloExitAction.RelaunchCounted, ApolloExit.Decide(ApolloExitKind.Restart, isEarlyExit: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Crash_RelaunchesCounted(bool isEarlyExit) =>
        Assert.Equal(ApolloExitAction.RelaunchCounted, ApolloExit.Decide(ApolloExitKind.Crash, isEarlyExit));

    // ── The health check's branch (SessionHealthCheck.ResolveApolloExitAsync) ──

    private sealed class Calls
    {
        public int LeaveStopped;
        public List<bool> Relaunches { get; } = [];
    }

    private static async Task<(ApolloExitAction Action, int Pid, Calls Calls)> Resolve(int? exitCode, TimeSpan? uptime)
    {
        var calls = new Calls();
        var (action, pid) = await SessionHealthCheck.ResolveApolloExitAsync(
            exitCode, uptime,
            leaveStopped: () => calls.LeaveStopped++,
            relaunch: countsAsCrash => { calls.Relaunches.Add(countsAsCrash); return Task.FromResult(4242); });
        return (action, pid, calls);
    }

    [Fact]
    public async Task Quit_LeavesStoppedAndNeverRelaunches()
    {
        var (action, pid, calls) = await Resolve(1115, LongRun);

        Assert.Equal(ApolloExitAction.LeaveStopped, action);
        Assert.Equal(1, calls.LeaveStopped);
        Assert.Empty(calls.Relaunches);   // no relaunch, so no restart attempt can be spent
        Assert.Equal(0, pid);
    }

    [Fact]
    public async Task Restart_RelaunchesOnceWithoutCounting()
    {
        var (action, pid, calls) = await Resolve(0, LongRun);

        Assert.Equal(ApolloExitAction.RelaunchUncounted, action);
        Assert.Equal(0, calls.LeaveStopped);
        Assert.Equal([false], calls.Relaunches);
        Assert.Equal(4242, pid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(null)]
    public async Task Crash_RelaunchesOnceCounted(int? exitCode)
    {
        var (action, pid, calls) = await Resolve(exitCode, LongRun);

        Assert.Equal(ApolloExitAction.RelaunchCounted, action);
        Assert.Equal(0, calls.LeaveStopped);
        Assert.Equal([true], calls.Relaunches);
        Assert.Equal(4242, pid);
    }

    [Fact]
    public async Task RestartJustAfterLaunch_IsCounted()
    {
        var (_, _, calls) = await Resolve(0, JustStarted);
        Assert.Equal([true], calls.Relaunches);
    }

    // ── The restart budget (ApolloManager.RestartBudget) ─────────────────

    [Fact]
    public void CrashRelaunch_SpendsOneAttempt()
    {
        Assert.Equal((false, 1), ApolloManager.RestartBudget(0, countsAsCrash: true));
        Assert.Equal((false, ApolloManager.MaxRestartAttempts),
            ApolloManager.RestartBudget(ApolloManager.MaxRestartAttempts - 1, countsAsCrash: true));
    }

    [Fact]
    public void CrashRelaunch_IsRefusedOnceTheBudgetIsSpent() =>
        Assert.True(ApolloManager.RestartBudget(ApolloManager.MaxRestartAttempts, countsAsCrash: true).GiveUp);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ApolloManager.MaxRestartAttempts)]
    public void RestartRelaunch_SpendsNothingAndIsNeverRefused(int used)
    {
        var (giveUp, next) = ApolloManager.RestartBudget(used, countsAsCrash: false);
        Assert.False(giveUp);
        Assert.Equal(used, next);
    }

    [Fact]
    public async Task ManyUserRestarts_NeverExhaustTheBudget_ButCrashesStillDo()
    {
        // Walk the health check's decisions through ten user Restarts, then crashes, feeding each
        // relaunch's countsAsCrash into the real budget function.
        var count = 0;
        for (var i = 0; i < 10; i++)
        {
            var (_, _, calls) = await Resolve(0, LongRun);
            var budget = ApolloManager.RestartBudget(count, calls.Relaunches.Single());
            Assert.False(budget.GiveUp);
            count = budget.NextCount;
        }
        Assert.Equal(0, count);

        for (var i = 0; i < ApolloManager.MaxRestartAttempts; i++)
        {
            var (_, _, calls) = await Resolve(1, LongRun);
            var budget = ApolloManager.RestartBudget(count, calls.Relaunches.Single());
            Assert.False(budget.GiveUp);
            count = budget.NextCount;
        }

        var (_, _, last) = await Resolve(1, LongRun);
        Assert.True(ApolloManager.RestartBudget(count, last.Relaunches.Single()).GiveUp);
    }
}
