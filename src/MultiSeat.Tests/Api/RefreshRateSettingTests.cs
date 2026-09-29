using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Api;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Emulators;
using MultiSeat.Service.Sessions;
using MultiSeat.Tests.Streaming;   // TestLogger<T>
using Xunit;

namespace MultiSeat.Tests.Api;

/// <summary>
/// Issue #74: the host-wide DWM interval can be changed from the dashboard instead of only by
/// hand-editing appsettings.local.json.
///
/// The interval caps every seat's frame rate, and the API offers only the values measured under a
/// real game-like load: 33, 16, 8 and 6 ms. These tests call the real handler with a fake registry
/// write and a temporary settings file, so nothing here touches the host's registry or its config.
///
/// Three things are checked that would each fail silently in production:
///   - an unmeasured value (4, 2) or an arbitrary one (10) is refused, not written;
///   - a change reaches the value the NEXT provision's fps check reads — the service holds its
///     options as a startup snapshot, so a change that only lands there is invisible;
///   - the change survives a restart, by landing in the file Program.cs loads last.
/// </summary>
public class RefreshRateSettingTests : IDisposable
{
    private readonly string _dir;

    public RefreshRateSettingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"multiseat-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string LocalPath => Path.Combine(_dir, "appsettings.local.json");
    private string SharedPath => Path.Combine(_dir, "appsettings.json");

    /// <summary>A registry write that records what it was asked to write and reports success.</summary>
    private sealed class FakeRegistry
    {
        public List<int> Writes { get; } = [];
        public string? Failure { get; init; }

        public string? Write(int ms)
        {
            Writes.Add(ms);
            return Failure;
        }
    }

    private Task<IResult> Post(int? intervalMs, DwmFrameIntervalSetting setting, FakeRegistry registry) =>
        SystemEndpoints.SetRefreshRateAsync(
            new SystemEndpoints.RefreshRateRequest(intervalMs),
            setting,
            registry.Write,
            LocalPath,
            NullLogger.Instance);

    private static int StatusOf(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static JsonElement Body(IResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    // ── The allowed set ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(33, 30)]
    [InlineData(16, 62)]
    [InlineData(8, 125)]
    [InlineData(6, 166)]
    public async Task EachMeasuredInterval_IsAccepted_Written_AndApplied(int intervalMs, int expectedHz)
    {
        var setting = new DwmFrameIntervalSetting(intervalMs == 16 ? 8 : 16);
        var registry = new FakeRegistry();

        var result = await Post(intervalMs, setting, registry);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal([intervalMs], registry.Writes);
        Assert.Equal(intervalMs, setting.IntervalMs);
        Assert.Equal(expectedHz, setting.EffectiveRefreshRateHz);

        var body = Body(result);
        Assert.Equal(intervalMs, body.GetProperty("intervalMs").GetInt32());
        Assert.Equal(expectedHz, body.GetProperty("refreshRateHz").GetInt32());
        // Every change has to say it does not reach a running seat.
        Assert.Contains("next session", body.GetProperty("appliesTo").GetString());
    }

    [Theory]
    [InlineData(10)]    // between two allowed values
    [InlineData(7)]     // would advertise 142 Hz — plausible-looking, never measured
    [InlineData(4)]     // composes idle at 246 fps, never load-tested
    [InlineData(2)]     // the honoured floor, never load-tested
    [InlineData(1)]     // below the floor: Windows silently ignores it
    [InlineData(0)]
    [InlineData(-8)]
    [InlineData(1000)]
    public async Task AnythingElse_IsRefused_BeforeTheRegistryOrTheFile(int intervalMs)
    {
        var setting = new DwmFrameIntervalSetting(16);
        var registry = new FakeRegistry();

        var result = await Post(intervalMs, setting, registry);

        Assert.Equal(400, StatusOf(result));
        var error = Body(result).GetProperty("error").GetString()!;
        // The error names the allowed set, so the caller learns what would work.
        Assert.Contains("33, 16, 8, 6", error);
        Assert.Contains($"{intervalMs}ms", error);

        Assert.Empty(registry.Writes);
        Assert.Equal(16, setting.IntervalMs);
        Assert.False(File.Exists(LocalPath));
    }

    [Fact]
    public async Task AMissingInterval_IsRefused()
    {
        var setting = new DwmFrameIntervalSetting(16);
        var registry = new FakeRegistry();

        var result = await Post(null, setting, registry);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("33, 16, 8, 6", Body(result).GetProperty("error").GetString());
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void TheSettingItself_RefusesAnUnmeasuredValue()
    {
        // A second line of defence: nothing can put the shared value out of step with what the
        // API is willing to write.
        var setting = new DwmFrameIntervalSetting(16);

        Assert.Throws<ArgumentOutOfRangeException>(() => setting.Set(4));
        Assert.Equal(16, setting.IntervalMs);
    }

    // ── A failed registry write changes nothing ──────────────────────────────────────

    [Fact]
    public async Task ARegistryFailure_LeavesTheValueAndTheFileAlone()
    {
        var setting = new DwmFrameIntervalSetting(16);
        var registry = new FakeRegistry { Failure = "access denied" };

        var result = await Post(8, setting, registry);

        Assert.Equal(500, StatusOf(result));
        Assert.Contains("access denied", Body(result).GetProperty("error").GetString());
        // The dashboard must not show a rate the next session will not get.
        Assert.Equal(16, setting.IntervalMs);
        Assert.False(File.Exists(LocalPath));
    }

    // ── The change reaches the next provision ────────────────────────────────────────

    private static SeatManager NewManager(MultiSeatOptions options, DwmFrameIntervalSetting setting) => new(
        new TestLogger<SeatManager>(),
        Options.Create(options),
        accounts: null!, sessionLauncher: null!, processInjector: null!, displayManager: null!,
        apolloManager: null!, configBuilder: null!, portAllocator: null!, firewall: null!,
        audioRouter: null!, controllerManager: null!, inputRouter: null!, inputHookManager: null!,
        hidHide: null!, onConnectApps: null!, serverQuery: null!, hostApollo: null!,
        emulatorSeeders: Array.Empty<IEmulatorConfigSeeder>(),
        lifecycleGate: new SeatLifecycleGate(),
        dwmFrameInterval: setting);

    [Fact]
    public async Task AChangeThroughTheApi_IsWhatTheNextProvisionChecksAgainst()
    {
        // The service's options are a startup snapshot, and here they still say 16. Only the
        // shared setting changes — so this passes only if the seat manager reads that, not the
        // snapshot.
        var options = new MultiSeatOptions { DwmFrameIntervalMs = 16 };
        var setting = new DwmFrameIntervalSetting(options.DwmFrameIntervalMs);
        var mgr = NewManager(options, setting);

        Assert.NotNull(mgr.RefreshRateWarningFor(120));   // 62 Hz cannot carry 120 fps

        Assert.Equal(200, StatusOf(await Post(8, setting, new FakeRegistry())));

        Assert.Null(mgr.RefreshRateWarningFor(120));      // 125 Hz can
        Assert.NotNull(mgr.RefreshRateWarningFor(144));   // and still cannot carry 144
        Assert.Equal(16, options.DwmFrameIntervalMs);     // the snapshot really was left alone
    }

    [Fact]
    public async Task ChangingItBack_IsSeenToo()
    {
        var setting = new DwmFrameIntervalSetting(8);
        var mgr = NewManager(new MultiSeatOptions { DwmFrameIntervalMs = 8 }, setting);
        Assert.Null(mgr.RefreshRateWarningFor(120));

        await Post(16, setting, new FakeRegistry());

        Assert.NotNull(mgr.RefreshRateWarningFor(120));
    }

    [Fact]
    public void WithoutASharedSetting_TheManagerStartsFromItsOptions()
    {
        // The optional constructor argument falls back to the configured value, not to a
        // hard-coded one.
        var mgr = new SeatManager(
            new TestLogger<SeatManager>(),
            Options.Create(new MultiSeatOptions { DwmFrameIntervalMs = 16 }),
            accounts: null!, sessionLauncher: null!, processInjector: null!, displayManager: null!,
            apolloManager: null!, configBuilder: null!, portAllocator: null!, firewall: null!,
            audioRouter: null!, controllerManager: null!, inputRouter: null!, inputHookManager: null!,
            hidHide: null!, onConnectApps: null!, serverQuery: null!, hostApollo: null!,
            emulatorSeeders: Array.Empty<IEmulatorConfigSeeder>(),
            lifecycleGate: new SeatLifecycleGate());

        Assert.NotNull(mgr.RefreshRateWarningFor(120));
    }

    // ── It survives a restart ────────────────────────────────────────────────────────

    /// <summary>Bind MultiSeatOptions exactly the way Program.cs layers its two files.</summary>
    private MultiSeatOptions LoadLikeTheService()
    {
        var options = new MultiSeatOptions();
        new ConfigurationBuilder()
            .AddJsonFile(SharedPath, optional: true)
            .AddJsonFile(LocalPath, optional: true)
            .Build()
            .GetSection(MultiSeatOptions.SectionName)
            .Bind(options);
        return options;
    }

    [Fact]
    public async Task TheChange_WinsOverAnUpgradedHostsAppSettings_AfterARestart()
    {
        // A host installed before this release: the installer keeps its appsettings.json byte for
        // byte, so it still says 16, and its local file overrides something else entirely.
        File.WriteAllText(SharedPath, """{"MultiSeat":{"DwmFrameIntervalMs":16,"AudioMode":"PerSession"}}""");
        File.WriteAllText(LocalPath, """{"MultiSeat":{"ApiKey":"disabled","MaxSeats":3}}""");
        Assert.Equal(16, LoadLikeTheService().DwmFrameIntervalMs);

        var result = await Post(6, new DwmFrameIntervalSetting(16), new FakeRegistry());

        Assert.Equal(LocalPath, Body(result).GetProperty("persistedTo").GetString());
        var reloaded = LoadLikeTheService();
        Assert.Equal(6, reloaded.DwmFrameIntervalMs);
        // Everything else in the local file is kept.
        Assert.Equal("disabled", reloaded.ApiKey);
        Assert.Equal(3, reloaded.MaxSeats);
        Assert.Equal(AudioMode.PerSession, reloaded.AudioMode);
    }

    [Fact]
    public async Task TheLocalFile_IsCreated_WhenTheHostHasNone()
    {
        File.WriteAllText(SharedPath, """{"MultiSeat":{"DwmFrameIntervalMs":16}}""");

        await Post(33, new DwmFrameIntervalSetting(16), new FakeRegistry());

        Assert.True(File.Exists(LocalPath));
        Assert.Equal(33, LoadLikeTheService().DwmFrameIntervalMs);
        // appsettings.json itself is never rewritten.
        Assert.Equal("""{"MultiSeat":{"DwmFrameIntervalMs":16}}""", File.ReadAllText(SharedPath));
    }

    [Fact]
    public async Task ALocalFileWithComments_IsReportedAndLeftIntact()
    {
        // Rewriting it would silently drop the operator's comments. The change still applies to
        // the registry and to the next provision; it just does not persist, and the response says so.
        const string commented = "{\n  // pinned for the mic\n  \"MultiSeat\": { \"AudioMode\": \"SharedHost\" }\n}\n";
        File.WriteAllText(LocalPath, commented);
        var setting = new DwmFrameIntervalSetting(16);
        var registry = new FakeRegistry();

        var result = await Post(8, setting, registry);

        Assert.Equal(200, StatusOf(result));
        var body = Body(result);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("persistedTo").ValueKind);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("persistError").GetString()));
        Assert.Equal(commented, File.ReadAllText(LocalPath));
        Assert.Equal([8], registry.Writes);
        Assert.Equal(8, setting.IntervalMs);
    }

    // ── What GET reports ─────────────────────────────────────────────────────────────

    [Fact]
    public void Status_ReportsTheIntervalItsRate_AndTheChoices()
    {
        var body = JsonSerializer.SerializeToElement(
            SystemEndpoints.RefreshRateStatus(new DwmFrameIntervalSetting(6), registryIntervalMs: 6));

        Assert.Equal(6, body.GetProperty("intervalMs").GetInt32());
        Assert.Equal(RefreshRateAdvisor.EffectiveRefreshRateHz(6), body.GetProperty("refreshRateHz").GetInt32());
        Assert.Equal(6, body.GetProperty("registryIntervalMs").GetInt32());
        Assert.Equal(8, body.GetProperty("defaultIntervalMs").GetInt32());
        Assert.Equal([33, 16, 8, 6],
            body.GetProperty("allowedIntervalsMs").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Contains("next session", body.GetProperty("appliesTo").GetString());
    }
}
