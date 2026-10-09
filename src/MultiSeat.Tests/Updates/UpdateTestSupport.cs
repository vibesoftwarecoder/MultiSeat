using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

/// <summary>
/// A clock the test moves by hand. Timers (and so Task.Delay and CancellationTokenSource with a
/// TimeProvider) fire only when <see cref="Advance"/> passes their due time, in due order.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;
    private long _version;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    /// <summary>Counts every timer created, fired or disposed, so a test can tell when the code under test has gone quiet.</summary>
    public long Version => Interlocked.Read(ref _version);

    public int PendingTimers { get { lock (_gate) return _timers.Count(t => t.Due is not null); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(t);
        t.Change(dueTime, period);
        return t;
    }

    /// <summary>Move time forward by <paramref name="by"/>, firing due timers one at a time in due order.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null) { _now = target; break; }
                if (next.Due > _now) _now = next.Due!.Value;
                next.TakeFire();
            }
            Interlocked.Increment(ref _version);
            next.Fire();
        }
        Interlocked.Increment(ref _version);
    }

    /// <summary>The due time of the earliest pending timer, or null.</summary>
    public DateTimeOffset? NextDue { get { lock (_gate) return _timers.Where(t => t.Due is not null).Select(t => t.Due).Min(); } }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                _period = period;
            }
            Interlocked.Increment(ref owner._version);
            return true;
        }

        // Called under the owner's lock: clear or re-arm before the callback runs.
        public void TakeFire()
        {
            Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : owner._now + _period;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate) { Due = null; owner._timers.Remove(this); }
            Interlocked.Increment(ref owner._version);
        }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>An <see cref="IOptionsMonitor{TOptions}"/> whose value the test replaces, notifying listeners like a config reload.</summary>
internal sealed class FakeOptionsMonitor : IOptionsMonitor<MultiSeatOptions>
{
    private readonly List<Action<MultiSeatOptions, string?>> _listeners = [];
    private MultiSeatOptions _value;

    public FakeOptionsMonitor(MultiSeatOptions value) => _value = value;

    public MultiSeatOptions CurrentValue => Volatile.Read(ref _value);
    public MultiSeatOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<MultiSeatOptions, string?> listener)
    {
        lock (_listeners) _listeners.Add(listener);
        return null;
    }

    public void Set(MultiSeatOptions value)
    {
        Volatile.Write(ref _value, value);
        Action<MultiSeatOptions, string?>[] copy;
        lock (_listeners) copy = [.. _listeners];
        foreach (var l in copy) l(value, null);
    }
}

/// <summary>
/// Stands in for api.github.com: counts every request, tracks concurrency, and answers from a
/// function. Nothing in the tests ever opens a socket.
/// </summary>
internal sealed class CountingHandler : HttpMessageHandler
{
    private int _count;
    private int _inFlight;
    private int _maxInFlight;
    public Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }
    public List<(DateTimeOffset At, string Path)> Calls { get; } = [];
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.MinValue;

    public CountingHandler() => Respond = (r, _, _) => Task.FromResult(Releases(r, "v0.6.19"));

    public int Count => Volatile.Read(ref _count);
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);
    public int InFlight => Volatile.Read(ref _inFlight);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _count);
        lock (Calls) Calls.Add((Clock(), request.RequestUri!.AbsolutePath));
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while ((seen = Volatile.Read(ref _maxInFlight)) < now && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
        try { return await Respond(request, n, ct); }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    /// <summary>A release list for whichever repository the request names, with one release per tag given.</summary>
    public static HttpResponseMessage Releases(HttpRequestMessage request, params string[] tags) => Json(ListFor(request, tags));

    public static string ListFor(HttpRequestMessage request, params string[] tags)
    {
        var path = request.RequestUri!.AbsolutePath;
        var repoTags = tags.Select(t => path.Contains("/ApolloVibe/") ? ApolloTag(t) : path.Contains("/MoonlightVibe/") ? MoonTag(t) : t);
        return "[" + string.Join(",", repoTags.Select(t =>
            $$"""{"tag_name":"{{t}}","draft":false,"prerelease":false,"published_at":"2026-10-09T10:20:56Z","target_commitish":"master","body":"notes","assets":[]}""")) + "]";
    }

    // The tests name MultiSeat tags; the other repos get their own grammar with the same number.
    private static string ApolloTag(string t) => "v2026.6.1-ms" + t.Split('.').Last();
    private static string MoonTag(string t) => "v" + t.TrimStart('v').Replace("0.6.", "6.3.");

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);
}

/// <summary>Builds a service with its provider on a manual clock and a counting handler.</summary>
internal sealed class ServiceRig : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    public ManualTimeProvider Time { get; }
    public FakeOptionsMonitor Options { get; }
    public CountingHandler Handler { get; } = new();
    public UpdateStateStore Store { get; }
    public UpdateStatusProvider Provider { get; }
    public UpdateCheckService Service { get; }
    public string Dir { get; }
    public string ApolloExe { get; }

    public ServiceRig(bool enabled, int intervalHours = 12, Func<double>? random = null,
        string multiSeatVersion = "0.6.18+abc1234", Action<string>? seedStateFile = null, bool apolloPresent = true)
    {
        Dir = Path.Combine(Path.GetTempPath(), "multiseat-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        ApolloExe = Path.Combine(Dir, "sunshine.exe");
        if (apolloPresent) File.WriteAllText(ApolloExe, "not a release build");

        Time = new ManualTimeProvider(Start);
        Handler.Clock = Time.GetUtcNow;
        Options = new FakeOptionsMonitor(new MultiSeatOptions
        {
            UpdateCheckEnabled = enabled,
            UpdateCheckIntervalHours = intervalHours,
            ApolloExePath = ApolloExe,
        });

        var statePath = Path.Combine(Dir, "update-check.json");
        seedStateFile?.Invoke(statePath);
        Store = new UpdateStateStore(statePath, restrictAcl: (_, _) => true);
        Provider = new UpdateStatusProvider(Options, Store, () => multiSeatVersion);
        Service = new UpdateCheckService(
            Options, Provider, new GitHubReleaseClient(new HttpClient(Handler), timeProvider: Time),
            NullLogger<UpdateCheckService>.Instance, Time, random ?? (() => 0.5));
    }

    /// <summary>Start the service and wait until its loop is parked on the clock or on a change. No sleeping: it is the loop itself that says so.</summary>
    public async Task StartAsync()
    {
        await Service.StartAsync(CancellationToken.None);
        await WaitBlockedAsync(0);
    }

    /// <summary>
    /// Wait until the loop has parked again after <paramref name="above"/> earlier parks, with no
    /// request in flight. This is how a test knows the service has finished reacting to the last
    /// clock step or option change; nothing here guesses with a quiet period. Fails with a clear
    /// message instead of hanging.
    /// </summary>
    public Task WaitBlockedAsync(long above) =>
        WaitUntilAsync(() => Service.BlockedCount > above && Handler.InFlight == 0,
            $"the service never parked again after {above} parks (blocked={Service.BlockedCount}, in flight={Handler.InFlight})");

    /// <summary>Poll a condition with short yields until it holds, or fail after 30 s of real time with <paramref name="failure"/>.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException(failure);
            await Task.Delay(1);
        }
    }

    /// <summary>Replace the options and wait until the loop has reacted and parked again.</summary>
    public async Task SetOptionsAsync(MultiSeatOptions options)
    {
        var before = Service.BlockedCount;
        Options.Set(options);
        await WaitBlockedAsync(before);
    }

    /// <summary>Run simulated time forward one timer at a time, waiting after each until the service has parked again.</summary>
    public async Task RunAsync(TimeSpan by)
    {
        var target = Time.GetUtcNow() + by;
        while (Time.NextDue is { } due && due <= target)
        {
            var before = Service.BlockedCount;
            Time.Advance(due - Time.GetUtcNow());
            await WaitBlockedAsync(before);
        }
        Time.Advance(target - Time.GetUtcNow());
    }

    /// <summary>
    /// Like <see cref="RunAsync"/> for a handler that never answers: steps the clock until a
    /// request is in flight (proved by the handler, not assumed), and fails if none ever starts.
    /// </summary>
    public async Task RunUntilRequestInFlightAsync(TimeSpan by)
    {
        var target = Time.GetUtcNow() + by;
        while (Handler.InFlight == 0 && Time.NextDue is { } due && due <= target)
        {
            var before = Service.BlockedCount;
            Time.Advance(due - Time.GetUtcNow());
            await WaitUntilAsync(() => Service.BlockedCount > before || Handler.InFlight > 0,
                "neither a park nor a request followed a clock step");
        }
        Assert.True(Handler.InFlight > 0, "no request went out within the simulated time");
    }

    public void Dispose()
    {
        try { Service.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(30)); } catch { }
        try { Directory.Delete(Dir, true); } catch { }
    }
}
