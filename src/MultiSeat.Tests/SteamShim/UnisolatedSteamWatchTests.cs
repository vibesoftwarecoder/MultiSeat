using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.SteamShim;
using Xunit;

namespace MultiSeat.Tests.SteamShim;

public sealed class UnisolatedSteamWatchTests
{
    private readonly CapturingWatchLogger _log = new();

    private UnisolatedSteamWatch Watch(params SteamProcessInfo[] processes)
    {
        var options = Options.Create(new MultiSeatOptions { SteamIsolationEnabled = true });
        return new UnisolatedSteamWatch(_log, options) { Processes = () => processes };
    }

    private static SteamProcessInfo Steam(int pid, bool? hook, int ageSeconds = 60) =>
        new(pid, 1, @"C:\Program Files (x86)\Steam\steam.exe", DateTime.UtcNow.AddSeconds(-ageSeconds), hook);

    [Fact]
    public void ASteamWithoutTheHookIsReportedOnce()
    {
        var watch = Watch(Steam(100, hook: false));

        Assert.Equal(1, watch.Scan());
        Assert.Equal(0, watch.Scan());   // not again for the same process

        var warning = Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("pid 100", warning.Message);
        Assert.Contains("nothing was stopped", warning.Message);
    }

    [Fact]
    public void AnIsolatedSteamIsNeverReported()
    {
        Assert.Equal(0, Watch(Steam(100, hook: true)).Scan());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void ASteamThatCannotBeReadIsNeverReported()
    {
        // Access denied, or a process that just exited: unknown is not the same as unisolated.
        Assert.Equal(0, Watch(Steam(100, hook: null)).Scan());
    }

    [Fact]
    public void ASteamThatIsStillStartingGetsAMomentBeforeItIsJudged()
    {
        var starting = Steam(100, hook: false, ageSeconds: 2);
        Assert.Equal(0, Watch(starting).Scan());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void OnlyTheUnisolatedOneAmongSeveralIsReported()
    {
        var watch = Watch(Steam(100, hook: true), Steam(200, hook: false), Steam(300, hook: true));

        Assert.Equal(1, watch.Scan());
        Assert.Contains("pid 200", Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning).Message);
    }
}

internal sealed class CapturingWatchLogger : ILogger<UnisolatedSteamWatch>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
}
