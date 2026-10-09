using System.Net;
using System.Text.Json;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

/// <summary>
/// The background service on a manual clock and a counting fake handler. Nothing here opens a
/// socket or waits for real minutes: a simulated day runs in a fraction of a second.
/// </summary>
public class UpdateCheckServiceTests
{
    private static readonly TimeSpan Min10 = TimeSpan.FromMinutes(10);

    // ── Off by default: zero network ─────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task Off_MakesNoCallForASimulatedDay()
    {
        using var rig = new ServiceRig(enabled: false);
        await rig.StartAsync();

        await rig.RunAsync(TimeSpan.FromHours(24));

        Assert.Equal(0, rig.Handler.Count);
        Assert.Equal(0, rig.Time.PendingTimers); // it idles on the options token, not on a clock
        Assert.False(rig.Provider.GetSnapshot().Enabled);
    }

    [Fact(Timeout = 60_000)]
    public async Task Off_ManualCheck_IsRefusedWithoutANetworkCall()
    {
        using var rig = new ServiceRig(enabled: false);
        await rig.StartAsync();

        var r = await rig.Service.RequestCheckAsync(CancellationToken.None);

        Assert.Equal(ManualCheckOutcome.Disabled, r.Outcome);
        Assert.Equal(0, rig.Handler.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task On_ReadingTheStatusNeverCallsGitHub_EvenWithAnEmptyCache()
    {
        using var rig = new ServiceRig(enabled: true);
        await rig.StartAsync();
        await rig.RunAsync(TimeSpan.FromMinutes(5)); // before the first check is due

        for (var i = 0; i < 50; i++) _ = rig.Provider.GetSnapshot();

        Assert.Equal(0, rig.Handler.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task FlippingTheOptionOn_StartsChecksWithoutARestart()
    {
        using var rig = new ServiceRig(enabled: false);
        await rig.StartAsync();
        await rig.RunAsync(TimeSpan.FromHours(1));
        Assert.Equal(0, rig.Handler.Count);

        rig.Options.Set(new MultiSeatOptions { UpdateCheckEnabled = true, ApolloExePath = rig.ApolloExe });
        await rig.RunAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(3, rig.Handler.Count);
        Assert.True(rig.Provider.GetSnapshot().Enabled);
    }

    [Fact(Timeout = 60_000)]
    public async Task FlippingTheOptionBackOff_StopsFurtherChecks()
    {
        using var rig = new ServiceRig(enabled: true);
        await rig.StartAsync();
        await rig.RunAsync(TimeSpan.FromMinutes(20));
        var after = rig.Handler.Count;
        Assert.Equal(3, after);

        rig.Options.Set(new MultiSeatOptions { UpdateCheckEnabled = false });
        await rig.RunAsync(TimeSpan.FromHours(48));

        Assert.Equal(after, rig.Handler.Count);
    }

    // ── Schedule ─────────────────────────────────────────────────────

    [Theory(Timeout = 60_000)]
    [InlineData(0.0, 10)]   // no random part: exactly the 10 minute floor
    [InlineData(1.0, 20)]   // the whole random part
    public async Task FirstCheck_IsNotBeforeTenMinutes_AndAddsTheRandomPart(double random, int minutes)
    {
        using var rig = new ServiceRig(enabled: true, random: () => random);
        await rig.StartAsync();

        await rig.RunAsync(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, rig.Handler.Count);

        await rig.RunAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, rig.Handler.Count);
        Assert.All(rig.Handler.Calls, c => Assert.True(c.At - ServiceRig.Start >= Min10));
    }

    [Fact(Timeout = 60_000)]
    public async Task NextChecks_FollowTheIntervalWithJitter()
    {
        // random 0 -> startup 10 min, and the interval factor 0.9
        using var rig = new ServiceRig(enabled: true, intervalHours: 10, random: () => 0.0);
        await rig.StartAsync();
        await rig.RunAsync(TimeSpan.FromHours(25));

        var rounds = Rounds(rig);
        Assert.Equal(TimeSpan.FromMinutes(10), rounds[0] - ServiceRig.Start);
        Assert.Equal(TimeSpan.FromHours(9), rounds[1] - rounds[0]);
        Assert.Equal(TimeSpan.FromHours(9), rounds[2] - rounds[1]);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(0, 1)]       // below the minimum: one hour
    [InlineData(1000, 168)]  // above the maximum: a week
    public async Task TheInterval_IsClamped(int configured, int effectiveHours)
    {
        using var rig = new ServiceRig(enabled: true, intervalHours: configured, random: () => 0.5);
        await rig.StartAsync();
        await rig.RunAsync(TimeSpan.FromDays(9));

        var rounds = Rounds(rig);
        Assert.Equal(TimeSpan.FromHours(effectiveHours), rounds[1] - rounds[0]);
    }

    [Fact(Timeout = 60_000)]
    public async Task AFreshCache_SkipsTheStartupCheck()
    {
        using var rig = new ServiceRig(enabled: true, random: () => 0.5, seedStateFile: path =>
        {
            var seed = new UpdateState();
            foreach (var c in Enum.GetValues<UpdateComponent>())
                seed.GetOrAdd(c).CheckedAt = ServiceRig.Start - TimeSpan.FromHours(1);
            new UpdateStateStore(path, (_, _) => true).Save(seed);
        });
        await rig.StartAsync();

        await rig.RunAsync(TimeSpan.FromHours(10));
        Assert.Equal(0, rig.Handler.Count); // one hour old, interval 12 h

        await rig.RunAsync(TimeSpan.FromHours(2));
        Assert.Equal(3, rig.Handler.Count);
    }

    // ── Failure backoff ──────────────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task Failures_BackOff_15Min_1Hour_4Hours_ThenTheNormalInterval()
    {
        using var rig = new ServiceRig(enabled: true, random: () => 0.5);
        rig.Handler.Respond = (_, _, _) => Task.FromResult(CountingHandler.Status(HttpStatusCode.InternalServerError));
        await rig.StartAsync();
        await rig.RunAsync(TimeSpan.FromHours(40));

        var rounds = Rounds(rig);
        Assert.Equal(TimeSpan.FromMinutes(15), rounds[1] - rounds[0]);
        Assert.Equal(TimeSpan.FromHours(1), rounds[2] - rounds[1]);
        Assert.Equal(TimeSpan.FromHours(4), rounds[3] - rounds[2]);
        Assert.Equal(TimeSpan.FromHours(12), rounds[4] - rounds[3]);
    }

    [Fact(Timeout = 60_000)]
    public async Task ARateLimitReply_HoldsUntilTheReset_NotJustFifteenMinutes()
    {
        using var rig = new ServiceRig(enabled: true, random: () => 0.5);
        var limited = true;
        rig.Handler.Respond = (r, _, _) =>
        {
            if (!limited) return Task.FromResult(CountingHandler.Releases(r, "v0.6.19"));
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.TryAddWithoutValidation("Retry-After", "10800"); // 3 hours
            return Task.FromResult(resp);
        };
        await rig.StartAsync();
        await rig.RunAsync(TimeSpan.FromMinutes(16));
        Assert.Equal(3, rig.Handler.Count);
        limited = false;

        await rig.RunAsync(TimeSpan.FromHours(2));
        Assert.Equal(3, rig.Handler.Count); // still held

        await rig.RunAsync(TimeSpan.FromHours(1.2));
        Assert.Equal(6, rig.Handler.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task Offline_KeepsTheLastGoodResult_KeepsAnnouncingTheKnownUpdate_AndShowsTheError()
    {
        using var rig = new ServiceRig(enabled: true, random: () => 0.5);
        await rig.StartAsync();
        await rig.RunAsync(TimeSpan.FromMinutes(20)); // first round succeeds
        var good = rig.Provider.GetSnapshot().Components.Single(c => c.Id == "multiseat");
        Assert.Equal("0.6.19", good.Latest!.Version);

        rig.Handler.Respond = (_, _, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "no route");
        await rig.RunAsync(TimeSpan.FromHours(13));

        var now = rig.Provider.GetSnapshot().Components.Single(c => c.Id == "multiseat");
        Assert.Equal("0.6.19", now.Latest!.Version);
        Assert.NotNull(now.Error);
        Assert.True(now.Announce); // installed 0.6.18 is still behind the last good 0.6.19
    }

    // ── Manual check ─────────────────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task ManualCheck_HasASixtySecondCooldown()
    {
        using var rig = new ServiceRig(enabled: true);
        await rig.StartAsync();

        var first = await rig.Service.RequestCheckAsync(CancellationToken.None);
        Assert.Equal(ManualCheckOutcome.Accepted, first.Outcome);
        Assert.Equal(3, rig.Handler.Count);

        rig.Time.Advance(TimeSpan.FromSeconds(30));
        var second = await rig.Service.RequestCheckAsync(CancellationToken.None);
        Assert.Equal(ManualCheckOutcome.Cooldown, second.Outcome);
        Assert.InRange(second.RetryAfter, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(31));
        Assert.Equal(3, rig.Handler.Count);

        rig.Time.Advance(TimeSpan.FromSeconds(31));
        var third = await rig.Service.RequestCheckAsync(CancellationToken.None);
        Assert.Equal(ManualCheckOutcome.Accepted, third.Outcome);
        Assert.Equal(6, rig.Handler.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task OnlyOneCheckRunsAtATime()
    {
        using var rig = new ServiceRig(enabled: true);
        await rig.StartAsync();
        var gate = new TaskCompletionSource();
        rig.Handler.Respond = async (r, _, _) =>
        {
            await gate.Task;
            return CountingHandler.Releases(r, "v0.6.19");
        };

        var running = rig.Service.RequestCheckAsync(CancellationToken.None);
        while (rig.Handler.InFlight == 0) await Task.Delay(2);

        // A second request while one is running does not start another.
        var second = await rig.Service.RequestCheckAsync(CancellationToken.None);
        Assert.Equal(ManualCheckOutcome.Accepted, second.Outcome);
        Assert.Equal(1, rig.Handler.Count);

        gate.SetResult();
        await running;

        Assert.Equal(3, rig.Handler.Count);
        Assert.Equal(1, rig.Handler.MaxInFlight);
    }

    // ── The loop never dies and never blocks ─────────────────────────

    public static IEnumerable<object[]> HandlerFailures() =>
    [
        [new HttpRequestException(HttpRequestError.NameResolutionError, "x")],
        [new TaskCanceledException("timeout")],
        [new InvalidOperationException("boom")],
        [new IOException("disk")],
        [new JsonException("bad")],
        [new ArgumentException("arg")],
        [new NullReferenceException()],
    ];

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(HandlerFailures))]
    public async Task TheLoopSurvives_WhateverTheHandlerThrows(Exception ex)
    {
        using var rig = new ServiceRig(enabled: true, random: () => 0.5);
        rig.Handler.Respond = (_, _, _) => throw ex;
        await rig.StartAsync();

        await rig.RunAsync(TimeSpan.FromHours(3));

        Assert.False(ServiceStopped(rig.Service));
        Assert.True(rig.Handler.Count >= 6); // it kept trying, after backoff
        Assert.NotNull(rig.Provider.GetSnapshot().Error);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheLoopSurvives_AnExceptionOfItsOwn_AndCarriesOn()
    {
        var calls = 0;
        // The first scheduling decision throws; later ones work.
        using var rig = new ServiceRig(enabled: true, random: () => ++calls == 1 ? throw new InvalidOperationException("seam") : 0.5);
        await rig.StartAsync();

        await rig.RunAsync(TimeSpan.FromHours(1));

        Assert.False(ServiceStopped(rig.Service));
        Assert.Equal(3, rig.Handler.Count); // the check still happened, after the 15 minute pause
    }

    [Fact(Timeout = 60_000)]
    public async Task StartAsync_ReturnsAtOnce_EvenIfEveryRequestHangs()
    {
        using var rig = new ServiceRig(enabled: true);
        rig.Handler.Respond = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); };

        // Throws TimeoutException (a failure) if StartAsync does not return.
        await rig.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await rig.RunAsync(TimeSpan.FromMinutes(16)); // the first check is due at 15 minutes and hangs
        Assert.True(rig.Handler.Count >= 1); // a request went out and hung; startup was never waiting on it
    }

    [Theory(Timeout = 60_000)]
    [InlineData("off")]
    [InlineData("waiting")]
    [InlineData("mid-request")]
    public async Task TheStopToken_EndsTheLoopPromptly(string where)
    {
        using var rig = new ServiceRig(enabled: where != "off");
        if (where == "mid-request")
            rig.Handler.Respond = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); };
        await rig.StartAsync();
        if (where == "mid-request") await rig.RunAsync(TimeSpan.FromMinutes(16));
        else await rig.SettleAsync();

        // Throws TimeoutException (a failure) if StopAsync does not return.
        await rig.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ServiceStopped(rig.Service));
    }

    // ── What a check records ─────────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task AFirstSuccess_RecordsTheBaseline_AndPersistsTheState()
    {
        using var rig = new ServiceRig(enabled: true);
        await rig.StartAsync();
        await rig.RunAsync(TimeSpan.FromMinutes(20));

        var saved = rig.Store.Load();
        Assert.Equal("0.6.19", saved.Repos["multiseat"].Baseline);
        Assert.Equal("2026.6.1-ms19", saved.Repos["apollovibe"].Baseline);
        Assert.NotNull(saved.Repos["multiseat"].CheckedAt);
    }

    [Fact(Timeout = 60_000)]
    public async Task Requests_GoOnlyToTheThreeRepositories()
    {
        using var rig = new ServiceRig(enabled: true);
        await rig.StartAsync();
        await rig.RunAsync(TimeSpan.FromHours(30));

        Assert.All(rig.Handler.Calls, c => Assert.Matches(@"^/repos/vibesoftwarecoder/(MultiSeat|ApolloVibe|MoonlightVibe)/releases$", c.Path));
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>The time of the first request of each round (a round is three requests).</summary>
    private static List<DateTimeOffset> Rounds(ServiceRig rig)
    {
        List<(DateTimeOffset At, string Path)> calls;
        lock (rig.Handler.Calls) calls = [.. rig.Handler.Calls];
        return calls.Where((_, i) => i % 3 == 0).Select(c => c.At).ToList();
    }

    private static bool ServiceStopped(UpdateCheckService s) => s.ExecuteTask is { IsCompleted: true };
}
