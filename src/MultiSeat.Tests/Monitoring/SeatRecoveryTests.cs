using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiSeat.Service;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Emulators;
using MultiSeat.Service.Monitoring;
using MultiSeat.Service.Sessions;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;
using MultiSeat.Tests.Streaming;   // TestLogger<T>
using Xunit;
using static MultiSeat.Service.Monitoring.SeatReconciler;
using static MultiSeat.Service.Sessions.SessionLauncher;

namespace MultiSeat.Tests.Monitoring;

/// <summary>
/// Issue #87: seats that break behind the service's back - a hibernate, a lost session - are
/// set up again when they have auto-start on, and a failed auto-start is retried instead of
/// being given up after one try. Windows is faked or kept out of reach throughout: the seat
/// manager is built with null subsystems, so anything that would touch a real session, Apollo
/// or driver fails inside the code under test rather than doing it.
/// </summary>
public class SeatRecoveryTests : IDisposable
{
    private readonly string _presetPath =
        Path.Combine(Path.GetTempPath(), $"multiseat-recovery-presets-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_presetPath); } catch { /* best effort */ }
        try { File.Delete(_presetPath + ".tmp"); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId e, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter) => Entries.Add((level, formatter(state, ex)));
    }

    private static SeatPreset Preset(string account = "Seat1") => new()
    {
        AccountName = account, Width = 2560, Height = 1440, Fps = 120, AutoStart = true,
    };

    private static SeatInfo ReadySeat(SeatRequest request) => new()
    {
        Id = Guid.NewGuid(), AccountName = request.AccountName, Status = SeatStatus.Ready,
    };

    // ── C2: retrying a failed auto-start ──────────────────────────────────────

    [Fact]
    public async Task Retry_FirstAttemptWorks_NoWaitAndNothingDiscarded()
    {
        var delays = new List<TimeSpan>();
        var discarded = 0;

        var seat = await AutoStartProvisioner.ProvisionWithRetryAsync(
            Preset(), "test",
            provision: (r, _) => Task.FromResult(ReadySeat(r)),
            discardFailedAttempt: (_, _) => { discarded++; return Task.CompletedTask; },
            delay: (d, _) => { delays.Add(d); return Task.CompletedTask; },
            new RecordingLogger(), CancellationToken.None);

        Assert.NotNull(seat);
        Assert.True(seat.AutoStart);
        Assert.Empty(delays);
        Assert.Equal(0, discarded);
    }

    [Fact]
    public async Task Retry_TwoFailuresThenSuccess_WaitsLonger_AndClearsEachFailedAttempt()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var discardedFor = new List<string>();
        SeatRequest? seen = null;

        var seat = await AutoStartProvisioner.ProvisionWithRetryAsync(
            Preset(), "test",
            provision: (r, _) =>
            {
                seen = r;
                return ++attempts < 3
                    ? throw new InvalidOperationException("Failed to create session for account 'Seat1'")
                    : Task.FromResult(ReadySeat(r));
            },
            discardFailedAttempt: (account, _) => { discardedFor.Add(account); return Task.CompletedTask; },
            delay: (d, _) => { delays.Add(d); return Task.CompletedTask; },
            new RecordingLogger(), CancellationToken.None);

        Assert.NotNull(seat);
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { AutoStartProvisioner.RetryDelay, AutoStartProvisioner.RetryDelay * 2 }, delays);
        Assert.Equal(new[] { "Seat1", "Seat1" }, discardedFor);
        // The preset, in full, is what each attempt asks for.
        Assert.Equal(2560, seen!.Width);
        Assert.Equal(120, seen.Fps);
    }

    [Fact]
    public async Task Retry_GivesUpAfterTheLastAttempt_AndSaysSo()
    {
        var attempts = 0;
        var log = new RecordingLogger();

        var seat = await AutoStartProvisioner.ProvisionWithRetryAsync(
            Preset(), "service startup",
            provision: (_, _) => { attempts++; throw new InvalidOperationException("RDP Wrapper is not active"); },
            discardFailedAttempt: (_, _) => Task.CompletedTask,
            delay: (_, _) => Task.CompletedTask,
            log, CancellationToken.None);

        Assert.Null(seat);
        Assert.Equal(AutoStartProvisioner.MaxAttempts, attempts);
        // A reporter's log must say it gave up, at Warning, naming the seat.
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("giving up") && e.Message.Contains("Seat1"));
        Assert.Equal(AutoStartProvisioner.MaxAttempts,
            log.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("attempt")));
    }

    [Fact]
    public async Task Retry_DoesNotRetry_AFailureARetryCannotChange()
    {
        var attempts = 0;

        var seat = await AutoStartProvisioner.ProvisionWithRetryAsync(
            Preset(), "test",
            provision: (_, _) => { attempts++; throw new ArgumentException("scale 110 is not allowed"); },
            discardFailedAttempt: (_, _) => Task.CompletedTask,
            delay: (_, _) => throw new Xunit.Sdk.XunitException("should not wait"),
            new RecordingLogger(), CancellationToken.None);

        Assert.Null(seat);
        Assert.Equal(1, attempts);
        Assert.False(AutoStartProvisioner.IsWorthRetrying(new ResourceConflictException("taken")));
        Assert.False(AutoStartProvisioner.IsWorthRetrying(new CapacityExhaustedException("full")));
        Assert.True(AutoStartProvisioner.IsWorthRetrying(new InvalidOperationException("no session")));
    }

    [Fact]
    public async Task Retry_StopsAtShutdown()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AutoStartProvisioner.ProvisionWithRetryAsync(
                Preset(), "test",
                provision: (_, _) => throw new InvalidOperationException("not yet"),
                discardFailedAttempt: (_, _) => Task.CompletedTask,
                delay: (_, _) => { cts.Cancel(); return Task.FromCanceled(cts.Token); },
                new RecordingLogger(), cts.Token));
    }

    // ── C1: what a reconciliation pass does with each seat ─────────────────────

    // Ints, not the enums: SessionVerdict is internal, and a public test method cannot take it.
    [Theory]
    // Healthy sessions are left alone, whatever auto-start says.
    [InlineData(SeatStatus.Ready, (int)SessionVerdict.Active, true, (int)ReconcileAction.Leave)]
    [InlineData(SeatStatus.Streaming, (int)SessionVerdict.Active, false, (int)ReconcileAction.Leave)]
    // Disconnected is the health check's reconnect path, not this one.
    [InlineData(SeatStatus.Ready, (int)SessionVerdict.Disconnected, true, (int)ReconcileAction.Leave)]
    // Lost session: rebuilt with auto-start, otherwise reported.
    [InlineData(SeatStatus.Ready, (int)SessionVerdict.Gone, true, (int)ReconcileAction.Reprovision)]
    [InlineData(SeatStatus.Ready, (int)SessionVerdict.Gone, false, (int)ReconcileAction.MarkError)]
    [InlineData(SeatStatus.Streaming, (int)SessionVerdict.NotOurs, true, (int)ReconcileAction.Reprovision)]
    [InlineData(SeatStatus.Streaming, (int)SessionVerdict.NotOurs, false, (int)ReconcileAction.MarkError)]
    // Error is no longer a dead end for an auto-start seat, whatever its session looks like.
    [InlineData(SeatStatus.Error, (int)SessionVerdict.Gone, true, (int)ReconcileAction.Reprovision)]
    [InlineData(SeatStatus.Error, (int)SessionVerdict.Active, true, (int)ReconcileAction.Reprovision)]
    [InlineData(SeatStatus.Error, (int)SessionVerdict.Gone, false, (int)ReconcileAction.Leave)]
    // Something else owns the seat right now.
    [InlineData(SeatStatus.Provisioning, (int)SessionVerdict.Gone, true, (int)ReconcileAction.Leave)]
    [InlineData(SeatStatus.Configuring, (int)SessionVerdict.Gone, true, (int)ReconcileAction.Leave)]
    [InlineData(SeatStatus.Connecting, (int)SessionVerdict.Gone, true, (int)ReconcileAction.Leave)]
    [InlineData(SeatStatus.TearingDown, (int)SessionVerdict.Gone, true, (int)ReconcileAction.Leave)]
    // Could not ask about the session: not treated as lost.
    [InlineData(SeatStatus.Ready, -1, true, (int)ReconcileAction.Leave)]
    public void Decide_PerSeat(SeatStatus status, int verdict, bool autoStart, int expected)
    {
        SessionVerdict? v = verdict < 0 ? null : (SessionVerdict)verdict;
        Assert.Equal((ReconcileAction)expected, Decide(status, v, autoStart));
    }

    [Fact]
    public void Budget_AllowsThreeRebuildsAnHour_ThenRefuses_ThenRecovers()
    {
        var recent = new List<DateTimeOffset>();
        var t0 = DateTimeOffset.UtcNow;

        Assert.True(TryUseBudget(recent, t0));
        Assert.True(TryUseBudget(recent, t0.AddMinutes(1)));
        Assert.True(TryUseBudget(recent, t0.AddMinutes(2)));
        Assert.False(TryUseBudget(recent, t0.AddMinutes(3)));
        // The first one has aged out.
        Assert.True(TryUseBudget(recent, t0 + BudgetWindow));
    }

    // ── The request queue between the triggers and the worker ──────────────────

    [Fact]
    public void Requests_WaitUntilDue_AndMergeIntoOnePass()
    {
        var requests = new SeatReconcileRequests();
        var now = DateTimeOffset.UtcNow;

        requests.Request("resume A", includeMissing: true, notBefore: now.AddSeconds(15));
        requests.Request("resume B", includeMissing: true, notBefore: now.AddSeconds(16));

        Assert.Null(requests.TakeDue(now));

        var pass = requests.TakeDue(now.AddSeconds(15));
        Assert.NotNull(pass);
        Assert.Contains("resume A", pass.Reasons);
        Assert.Contains("resume B", pass.Reasons);   // within the merge window
        Assert.True(pass.IncludeMissing);
        Assert.Null(requests.TakeDue(now.AddMinutes(5)));
    }

    [Fact]
    public void Requests_FromTheHealthCheck_DoNotCreateMissingSeats()
    {
        var requests = new SeatReconcileRequests();
        requests.Request("seat lost its session", includeMissing: false);

        var pass = requests.TakeDue(DateTimeOffset.UtcNow);

        Assert.NotNull(pass);
        Assert.False(pass.IncludeMissing);
    }

    // ── C1: the power event that starts it ─────────────────────────────────────

    [Theory]
    [InlineData(PowerBroadcastStatus.ResumeAutomatic)]
    [InlineData(PowerBroadcastStatus.ResumeSuspend)]
    [InlineData(PowerBroadcastStatus.ResumeCritical)]
    public void AResume_QueuesAPass_AfterTheSettleDelay(PowerBroadcastStatus status)
    {
        var requests = new SeatReconcileRequests();
        var now = DateTimeOffset.UtcNow;

        Assert.NotNull(PowerAwareServiceLifetime.HandlePowerEvent(status, requests, now));

        Assert.Null(requests.TakeDue(now));
        var pass = requests.TakeDue(now + PowerAwareServiceLifetime.ResumeSettleDelay);
        Assert.NotNull(pass);
        Assert.True(pass.IncludeMissing);   // stands in for the startup that did not happen
    }

    [Theory]
    [InlineData(PowerBroadcastStatus.Suspend)]
    [InlineData(PowerBroadcastStatus.QuerySuspend)]
    [InlineData(PowerBroadcastStatus.PowerStatusChange)]
    [InlineData(PowerBroadcastStatus.BatteryLow)]
    public void OtherPowerEvents_QueueNothing(PowerBroadcastStatus status)
    {
        var requests = new SeatReconcileRequests();

        Assert.Null(PowerAwareServiceLifetime.HandlePowerEvent(status, requests, DateTimeOffset.UtcNow));
        Assert.Null(requests.TakeDue(DateTimeOffset.MaxValue));
    }

    // ── C1: a pass, end to end, against a seat manager with no Windows behind it ──

    private SeatManager NewManager(PortAllocator? ports = null) => new(
        new TestLogger<SeatManager>(),
        Options.Create(new MultiSeatOptions()),
        accounts: null!, sessionLauncher: null!, processInjector: null!, displayManager: null!,
        apolloManager: null!, configBuilder: null!, portAllocator: ports!, firewall: null!,
        audioRouter: null!, controllerManager: null!, inputRouter: null!, inputHookManager: null!,
        hidHide: null!, onConnectApps: null!, serverQuery: null!, hostApollo: null!,
        emulatorSeeders: Array.Empty<IEmulatorConfigSeeder>(),
        lifecycleGate: new SeatLifecycleGate());

    private SeatReconciler NewReconciler(SeatManager mgr, SeatPresetStore presets) => new(
        new TestLogger<SeatReconciler>(),
        mgr,
        // Never reaches Windows here: every seat below has SessionId -1, which is Gone without
        // a WTS call.
        new SessionLauncher(new TestLogger<SessionLauncher>(),
            Options.Create(new MultiSeatOptions()), accounts: null!, rdpWrapper: null!),
        presets,
        new AutoStartProvisioner(new TestLogger<AutoStartProvisioner>(), mgr, (_, _) => Task.CompletedTask));

    private SeatPresetStore NewPresetStore() => new(new TestLogger<SeatPresetStore>(), _presetPath);

    [Fact]
    public async Task Pass_SeatWithoutAutoStart_WhoseSessionIsGone_IsMarkedError_NotRemoved()
    {
        var mgr = NewManager();
        var seat = new SeatInfo { Id = Guid.NewGuid(), AccountName = "Seat1", Status = SeatStatus.Ready };
        mgr.RegisterSeatDirect(seat);

        await NewReconciler(mgr, NewPresetStore()).ReconcileAsync(
            new ReconcilePass("test", IncludeMissing: true), CancellationToken.None);

        Assert.Same(seat, mgr.GetSeat(seat.Id));
        Assert.Equal(SeatStatus.Error, seat.Status);
        Assert.Contains("Auto-start is off", seat.ErrorMessage);
    }

    [Fact]
    public async Task Pass_AutoStartSeatInError_IsTornDown_AndProvisionedAgain()
    {
        var mgr = NewManager();
        var presets = NewPresetStore();
        presets.Upsert(Preset());
        var broken = new SeatInfo
        {
            Id = Guid.NewGuid(), AccountName = "Seat1", Status = SeatStatus.Error,
            ErrorMessage = "Windows session terminated unexpectedly",
        };
        mgr.RegisterSeatDirect(broken);

        await NewReconciler(mgr, presets).ReconcileAsync(
            new ReconcilePass("test", IncludeMissing: false), CancellationToken.None);

        // The broken seat went through teardown. Provisioning was then attempted - and fails
        // here, before registering anything, because there is no account manager behind it.
        Assert.Null(mgr.GetSeat(broken.Id));
        Assert.Empty(mgr.GetAllSeats());
    }

    [Fact]
    public async Task Pass_DoesNotRebuildTheSameSeatForever()
    {
        var mgr = NewManager();
        var presets = NewPresetStore();
        presets.Upsert(Preset());
        var reconciler = NewReconciler(mgr, presets);

        for (var i = 0; i < BudgetPerAccount; i++)
        {
            var broken = new SeatInfo { Id = Guid.NewGuid(), AccountName = "Seat1", Status = SeatStatus.Error };
            mgr.RegisterSeatDirect(broken);
            await reconciler.ReconcileAsync(new ReconcilePass("test", false), CancellationToken.None);
            Assert.Null(mgr.GetSeat(broken.Id));
        }

        var last = new SeatInfo { Id = Guid.NewGuid(), AccountName = "Seat1", Status = SeatStatus.Error };
        mgr.RegisterSeatDirect(last);
        await reconciler.ReconcileAsync(new ReconcilePass("test", false), CancellationToken.None);

        Assert.Same(last, mgr.GetSeat(last.Id));   // over budget: left in Error
    }

    // ── Tearing down a seat whose provisioning failed ──────────────────────────

    [Fact]
    public async Task TearingDownAFailedProvision_DoesNotReleaseItsPortsTwice()
    {
        // The failure path in ProvisionSeatAsync already released this seat's port block, and a
        // newer seat may hold that block now. Retries tear failed seats down routinely, so a
        // second release here would let two seats share ports.
        var ports = new PortAllocator();
        var mgr = NewManager(ports);

        var block = ports.Allocate();
        var failed = new SeatInfo
        {
            Id = Guid.NewGuid(), AccountName = "Seat1", Status = SeatStatus.Error, PortBase = block,
        };
        mgr.RegisterSeatDirect(failed);
        ports.Release(block);                  // what the failure path did
        mgr.MarkProvisionFailedDirect(failed.Id);
        Assert.Equal(block, ports.Allocate()); // a newer seat now holds the same block

        await mgr.TeardownSeatAsync(failed.Id, CancellationToken.None);

        Assert.Null(mgr.GetSeat(failed.Id));
        Assert.NotEqual(block, ports.Allocate()); // still the newer seat's
    }
}
