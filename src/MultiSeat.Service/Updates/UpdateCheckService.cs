using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;

namespace MultiSeat.Service.Updates;

public enum ManualCheckOutcome
{
    /// <summary>A check ran, or one was already running.</summary>
    Accepted,
    /// <summary>Update checks are off; no network call was made.</summary>
    Disabled,
    /// <summary>A check started less than the cooldown ago.</summary>
    Cooldown,
}

public readonly record struct ManualCheckResult(ManualCheckOutcome Outcome, TimeSpan RetryAfter);

/// <summary>
/// Runs the update check on a schedule. Its own hosted service: nothing on the seat path, the
/// health check or startup references it or waits for it.
///
/// Off (the default): logs one line and idles until the options change. Zero network calls.
/// Because it reads <see cref="IOptionsMonitor{TOptions}"/>, editing
/// <c>appsettings.local.json</c> (loaded with reloadOnChange) takes effect without a restart.
///
/// On: first check 10 minutes plus 0..10 random minutes after start (later if the cached result
/// is still fresh or a backoff is running), then every <c>UpdateCheckIntervalHours</c> +/-10%.
/// A failure backs a repository off 15 min, 1 h, 4 h, then the normal interval, and a rate-limit
/// reply holds it until GitHub's reset time. One check runs at a time. The loop catches every
/// exception except cancellation, so it can never stop the host (an exception out of
/// ExecuteAsync would), and an offline host just shows "last checked" with no alert.
///
/// All time goes through <see cref="TimeProvider"/> and all randomness through one function, so
/// tests drive a simulated day in milliseconds.
/// </summary>
public sealed class UpdateCheckService : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StartupJitter = TimeSpan.FromMinutes(10);
    /// <summary>Delay before the first check when the option is switched on while the service is already running.</summary>
    public static readonly TimeSpan EnableDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ManualCooldown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ManualDeadline = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MinBackoff = TimeSpan.FromMinutes(15);
    private const double IntervalJitter = 0.10;

    private readonly IOptionsMonitor<MultiSeatOptions> _options;
    private readonly UpdateStatusProvider _provider;
    private readonly GitHubReleaseClient _client;
    private readonly ILogger<UpdateCheckService> _log;
    private readonly TimeProvider _time;
    private readonly Func<double> _random;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly object _signalLock = new();

    private TaskCompletionSource _changed = NewSignal();
    private CancellationToken _stopping = CancellationToken.None;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _nextCheckAt;
    private DateTimeOffset? _lastCheckStartedAt;
    private int _lastIntervalHours = -1;
    private long _blocked;
    private bool _wasEnabled;

    public UpdateCheckService(
        IOptionsMonitor<MultiSeatOptions> options,
        UpdateStatusProvider provider,
        GitHubReleaseClient client,
        ILogger<UpdateCheckService> log,
        TimeProvider? time = null,
        Func<double>? random = null)
    {
        _options = options;
        _provider = provider;
        _client = client;
        _log = log;
        _time = time ?? TimeProvider.System;
        _random = random ?? Random.Shared.NextDouble;
        _startedAt = _time.GetUtcNow();
        _options.OnChange(_ => Signal());
    }

    /// <summary>
    /// How many times the loop has reached a point where it waits for the clock or for a change.
    /// Tests use it to know, without sleeping, that the loop has finished reacting and is parked
    /// again; it has no other use.
    /// </summary>
    internal long BlockedCount => Interlocked.Read(ref _blocked);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_signalLock)
        {
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult();
    }

    private Task CurrentSignal()
    {
        lock (_signalLock) return _changed.Task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Return to the host at once; nothing below may hold up startup.
        await Task.Yield();
        _stopping = stoppingToken;
        _startedAt = _time.GetUtcNow();

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await LoopOnceAsync(stoppingToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                _log.LogWarning("Update check loop error ({Type}); waiting before it continues", ex.GetType().Name);
                try
                {
                    _provider.Publish(null, _nextCheckAt, "internal error (" + ex.GetType().Name + ")");
                    Interlocked.Increment(ref _blocked);
                    await Task.Delay(BackoffFor(failures, IntervalNow()), _time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { /* even the recovery path must not end the loop */ }
            }
        }
    }

    private TimeSpan IntervalNow()
    {
        try { return UpdateIntervals.ToInterval(_options.CurrentValue.UpdateCheckIntervalHours); }
        catch { return UpdateIntervals.ToInterval(Shared.Constants.DefaultUpdateCheckIntervalHours); }
    }

    private async Task LoopOnceAsync(CancellationToken ct)
    {
        // Take the signal BEFORE reading the options: a change after this point completes it.
        var change = CurrentSignal();
        var opts = _options.CurrentValue;

        if (!opts.UpdateCheckEnabled)
        {
            if (_wasEnabled || _lastIntervalHours == -1)
                _log.LogInformation("Update checks are off; MultiSeat makes no connection to the internet.");
            _wasEnabled = false;
            _lastIntervalHours = 0;
            _nextCheckAt = null;
            _provider.SetNextCheck(null);
            Interlocked.Increment(ref _blocked);
            await change.WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        var interval = UpdateIntervals.ToInterval(opts.UpdateCheckIntervalHours);
        var hours = UpdateIntervals.ClampHours(opts.UpdateCheckIntervalHours);

        if (!_wasEnabled)
        {
            _wasEnabled = true;
            _log.LogInformation("Update checks are on (every {Hours} h). Reading installed versions.", hours);
            await Task.Run(() => RefreshInstalled(), ct).ConfigureAwait(false);
            _nextCheckAt = null;
        }
        else if (hours != _lastIntervalHours)
        {
            _nextCheckAt = null; // the interval changed: schedule again
        }
        _lastIntervalHours = hours;

        if (_nextCheckAt is null)
        {
            _nextCheckAt = InitialWake(_time.GetUtcNow(), interval);
            _provider.SetNextCheck(_nextCheckAt);
        }

        var wait = _nextCheckAt.Value - _time.GetUtcNow();
        if (wait > TimeSpan.Zero)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(wait, _time, linked.Token);
            Interlocked.Increment(ref _blocked);
            var first = await Task.WhenAny(delay, change).ConfigureAwait(false);
            linked.Cancel();
            try { await delay.ConfigureAwait(false); } catch (OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            if (first == change) return; // options or schedule changed: look again
        }

        // Due. Wait behind a manual check if one is running, then re-test: it will have moved the schedule.
        await _checkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_options.CurrentValue.UpdateCheckEnabled) return;
            if (_nextCheckAt is { } due && due > _time.GetUtcNow()) return;
            await RunChecksAsync(manual: false, ct).ConfigureAwait(false);
        }
        finally { _checkLock.Release(); }
    }

    /// <summary>When the first check of an enabled period should run.</summary>
    private DateTimeOffset InitialWake(DateTimeOffset now, TimeSpan interval)
    {
        var state = _provider.State;
        // Right after start: 10 minutes plus 0..10 random, so a fleet restart does not synchronise.
        // Switched on later: soon, but never earlier than the startup delay would have allowed.
        var startup = _startedAt + StartupDelay + TimeSpan.FromTicks((long)(Clamp01(_random()) * StartupJitter.Ticks));
        var earliest = startup > now + EnableDelay ? startup : now + EnableDelay;
        var wake = earliest;

        // A fresh cached result: nothing to do until it is a full interval old.
        var comps = Enum.GetValues<UpdateComponent>().Select(c => state.Repos.GetValueOrDefault(UpdateRepos.Id(c))).ToList();
        if (comps.All(r => r?.CheckedAt is not null && r.Backoff.ConsecutiveFailures == 0))
        {
            var oldest = comps.Min(r => r!.CheckedAt!.Value);
            var due = oldest + Jitter(interval);
            if (due > wake) wake = due;
        }

        // Every repository held back: wait for the first to be allowed again.
        var holds = comps.Select(r => r?.Backoff.NotBefore).ToList();
        if (holds.All(h => h is not null && h > now))
        {
            var hold = holds.Min()!.Value;
            if (hold > wake) wake = hold;
        }
        return wake;
    }

    private DateTimeOffset NextAfterCheck(DateTimeOffset now, TimeSpan interval)
    {
        var holds = _provider.State.Repos.Values
            .Select(r => r.Backoff.NotBefore)
            .Where(h => h is not null && h > now)
            .Select(h => h!.Value)
            .ToList();
        var normal = now + Jitter(interval);
        // Some repository failed: come back when its backoff ends, which is never sooner than 15 minutes.
        if (holds.Count > 0)
        {
            var earliest = holds.Min();
            return earliest < normal ? earliest : normal;
        }
        return normal;
    }

    private TimeSpan Jitter(TimeSpan interval)
    {
        var factor = 1 + (Clamp01(_random()) * 2 - 1) * IntervalJitter;
        return TimeSpan.FromTicks((long)(interval.Ticks * factor));
    }

    private static double Clamp01(double v) => double.IsNaN(v) ? 0.5 : Math.Clamp(v, 0, 1);

    /// <summary>Delay after the nth consecutive failure: 15 min, 1 h, 4 h, then the normal interval.</summary>
    public static TimeSpan BackoffFor(int consecutiveFailures, TimeSpan interval)
    {
        var d = consecutiveFailures switch
        {
            <= 1 => MinBackoff,
            2 => TimeSpan.FromHours(1),
            3 => TimeSpan.FromHours(4),
            _ => interval,
        };
        return d < MinBackoff ? MinBackoff : d;
    }

    /// <summary>
    /// Run a check now on behalf of a person. Off: refused, no network. Inside the cooldown:
    /// refused. If a check is already running it is not repeated. Never throws except
    /// cancellation requested by the caller.
    /// </summary>
    public async Task<ManualCheckResult> RequestCheckAsync(CancellationToken ct)
    {
        if (!_options.CurrentValue.UpdateCheckEnabled)
            return new ManualCheckResult(ManualCheckOutcome.Disabled, TimeSpan.Zero);

        if (!await _checkLock.WaitAsync(0, ct).ConfigureAwait(false))
            return new ManualCheckResult(ManualCheckOutcome.Accepted, TimeSpan.Zero);
        try
        {
            var now = _time.GetUtcNow();
            if (_lastCheckStartedAt is { } last && now - last < ManualCooldown)
                return new ManualCheckResult(ManualCheckOutcome.Cooldown, ManualCooldown - (now - last));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping);
            using var deadline = new CancellationTokenSource(ManualDeadline, _time);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, deadline.Token);
            try
            {
                await RunChecksAsync(manual: true, both.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !linked.IsCancellationRequested)
            {
                // Out of time: what finished is kept.
            }
            Signal(); // the schedule moved; the loop should look again
            return new ManualCheckResult(ManualCheckOutcome.Accepted, TimeSpan.Zero);
        }
        finally { _checkLock.Release(); }
    }

    /// <summary>One pass over the three repositories. The caller holds <c>_checkLock</c>.</summary>
    private async Task RunChecksAsync(bool manual, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        _lastCheckStartedAt = now;
        var state = _provider.State;
        var interval = IntervalNow();

        try
        {
        foreach (var component in Enum.GetValues<UpdateComponent>())
        {
            ct.ThrowIfCancellationRequested();
            var repo = state.GetOrAdd(component);
            now = _time.GetUtcNow();

            // A running backoff holds a repository back. A person asking by hand may bypass an
            // ordinary failure's wait, but never a rate-limit hold.
            if (repo.Backoff.NotBefore is { } hold && hold > now &&
                !(manual && repo.LastError != GitHubReleaseClient.RateLimitedError))
                continue;

            ReleaseFetchResult result;
            try
            {
                result = await _client.FetchAsync(component, repo.ETag, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                result = new ReleaseFetchResult(FetchOutcome.Failed, [], null, "unexpected error (" + ex.GetType().Name + ")", null, null);
            }

            Apply(component, repo, result, _time.GetUtcNow(), interval);
        }
        }
        finally
        {
            // Whatever finished is kept and shown, even when the pass was cancelled part way.
            RefreshInstalled();
            _provider.Store.Save(state);
            _nextCheckAt = NextAfterCheck(_time.GetUtcNow(), interval);
            _provider.Publish(null, _nextCheckAt, null);
        }
    }

    private static void Apply(UpdateComponent component, RepoState repo, ReleaseFetchResult r, DateTimeOffset now, TimeSpan interval)
    {
        switch (r.Outcome)
        {
            case FetchOutcome.Ok:
                repo.Candidates = ReleaseSelector.BuildCandidates(component, r.Releases);
                repo.ETag = r.ETag;
                Succeeded(component, repo, now);
                break;
            case FetchOutcome.NotModified:
                if (r.ETag is not null) repo.ETag = r.ETag;
                Succeeded(component, repo, now);
                break;
            default:
                repo.LastError = Short(r.Error);
                repo.Backoff.ConsecutiveFailures = Math.Min(repo.Backoff.ConsecutiveFailures + 1, 1000);
                var until = now + BackoffFor(repo.Backoff.ConsecutiveFailures, interval);
                if (r.Outcome == FetchOutcome.RateLimited && r.RetryNotBefore is { } reset && reset > until) until = reset;
                repo.Backoff.NotBefore = until;
                break;
        }
    }

    private static void Succeeded(UpdateComponent component, RepoState repo, DateTimeOffset now)
    {
        repo.CheckedAt = now;
        repo.LastError = null;
        repo.Backoff = new BackoffState();
        // The baseline is the newest release seen the first time checks work: later releases are news, earlier ones are not.
        if (repo.Baseline is null && ReleaseSelector.SelectLatest(component, repo.Candidates) is { } latest &&
            ReleaseVersion.TryParse(component, latest.Tag) is { } v)
            repo.Baseline = v.Display;
    }

    private static string Short(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "check failed";
        return error.Length <= 80 ? error : error[..80];
    }

    /// <summary>Re-read the installed ApolloVibe (cached by size and mtime) and publish. Local only.</summary>
    private void RefreshInstalled()
    {
        try
        {
            var state = _provider.State;
            var apollo = state.GetOrAdd(UpdateComponent.ApolloVibe);
            var result = InstalledVersions.DetectApollo(_options.CurrentValue.ApolloExePath, apollo.Candidates, state.ApolloExeHash);
            state.ApolloExeHash = result.HashCache;
            _provider.Publish(result.Detection, _nextCheckAt, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not read installed versions ({Type})", ex.GetType().Name);
        }
    }
}
