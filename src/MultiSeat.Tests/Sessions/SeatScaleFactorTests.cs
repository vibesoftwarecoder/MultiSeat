using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MultiSeat.Service;
using MultiSeat.Service.Api;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Emulators;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared.Models;
using MultiSeat.Tests.Streaming;   // TestLogger<T>
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// Issue #70: a seat's DPI scale can be set per seat and as a host default, instead of only
/// being derived from the desktop's width. These cover the parts that can be exercised without
/// a live Windows session: which scale wins, that a bad value is refused before anything is
/// provisioned or reconnected, and that an override survives the preset file.
///
/// The seat manager here is built with null subsystems on purpose. Each test only passes if the
/// code under test returns or throws before touching them, so reaching one — a session launch,
/// an Apollo restart — shows up as a NullReferenceException instead of passing quietly.
/// </summary>
public class SeatScaleFactorTests : IDisposable
{
    private readonly string _presetPath =
        Path.Combine(Path.GetTempPath(), $"multiseat-scale-presets-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_presetPath); } catch { /* best effort */ }
        try { File.Delete(_presetPath + ".tmp"); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private SeatPresetStore NewPresetStore() => new(new TestLogger<SeatPresetStore>(), _presetPath);

    private static SeatManager NewManager(int? defaultScale = null) => new(
        new TestLogger<SeatManager>(),
        Options.Create(new MultiSeatOptions { DefaultScaleFactor = defaultScale }),
        accounts: null!, sessionLauncher: null!, processInjector: null!, displayManager: null!,
        apolloManager: null!, configBuilder: null!, portAllocator: null!, firewall: null!,
        audioRouter: null!, controllerManager: null!, inputRouter: null!, inputHookManager: null!,
        hidHide: null!, onConnectApps: null!, serverQuery: null!, hostApollo: null!,
        emulatorSeeders: Array.Empty<IEmulatorConfigSeeder>(),
        lifecycleGate: new SeatLifecycleGate());

    private static SeatInfo NewSeat(int width = 1920, int? scaleOverride = null) => new()
    {
        Id = Guid.NewGuid(),
        AccountName = "GuestTest",
        Status = SeatStatus.Ready,
        Width = width,
        Height = 1080,
        ScaleFactorOverride = scaleOverride,
    };

    private static string ErrorText(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return JsonSerializer.Serialize(value);
    }

    // ── The seat manager's choice, which every (re)connect uses ────────────────

    [Fact]
    public void GeometryFor_UsesTheSeatOverride_AndReportsIt()
    {
        var mgr = NewManager(defaultScale: 150);
        var seat = NewSeat(width: 1920, scaleOverride: 200);

        var geometry = mgr.GeometryFor(seat);

        Assert.Equal(200, geometry.ScaleFactor);
        // Recorded on the seat, which is what GET /api/seats serialises.
        Assert.Equal(200, seat.ScaleFactor);
        Assert.Equal(ScaleFactorSource.Seat, seat.ScaleFactorSource);
    }

    [Fact]
    public void GeometryFor_UsesTheHostDefault_WhenTheSeatHasNone()
    {
        var mgr = NewManager(defaultScale: 150);
        var seat = NewSeat(width: 1920);

        Assert.Equal(150, mgr.GeometryFor(seat).ScaleFactor);
        Assert.Equal(ScaleFactorSource.HostDefault, seat.ScaleFactorSource);
    }

    [Theory]
    [InlineData(1920, 100)]
    [InlineData(2560, 125)]
    [InlineData(3840, 200)]
    public void GeometryFor_FallsBackToTheWidth_WithNoOverrideAnywhere(int width, int expected)
    {
        var seat = NewSeat(width: width);

        Assert.Equal(expected, NewManager().GeometryFor(seat).ScaleFactor);
        Assert.Equal(ScaleFactorSource.Derived, seat.ScaleFactorSource);
    }

    [Fact]
    public void AHostDefaultRdpWouldIgnore_IsNotUsed()
    {
        // Refused at startup with a warning, and seats keep the heuristic rather than failing to
        // provision or being handed a rounded value.
        var options = new MultiSeatOptions { DefaultScaleFactor = 110 };
        Assert.Null(options.UsableDefaultScaleFactor);

        var seat = NewSeat(width: 1920);
        Assert.Equal(100, NewManager(defaultScale: 110).GeometryFor(seat).ScaleFactor);
        Assert.Equal(ScaleFactorSource.Derived, seat.ScaleFactorSource);
    }

    // ── A bad value never gets as far as a session ────────────────────────────

    [Theory]
    [InlineData(110)]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task Provision_RefusesABadScale_BeforeAllocatingAnything(int scale)
    {
        var mgr = NewManager();

        // ArgumentException, not NullReferenceException: the check runs before the account
        // lookup (a null subsystem here), so nothing was allocated and no seat was registered.
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => mgr.ProvisionSeatAsync(
            new SeatRequest { AccountName = "GuestTest", ScaleFactor = scale },
            CancellationToken.None));

        Assert.Contains(scale.ToString(), ex.Message);
        Assert.Empty(mgr.GetAllSeats());
    }

    [Fact]
    public async Task SetScale_RefusesABadScale_BeforeLookingAtTheSeat()
    {
        // The seat does not exist. A check placed after the lookup would throw
        // SeatNotFoundException instead, and one placed after the gate would reach the reconnect.
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => NewManager().SetScaleFactorAsync(
            Guid.NewGuid(), 110, NewPresetStore(), CancellationToken.None));

        Assert.Contains("110", ex.Message);
    }

    [Fact]
    public async Task CreateEndpoint_AnswersBadRequest_ForABadScale()
    {
        // mgr is null: if the endpoint did not refuse the value itself, the call below would
        // throw instead of returning a result.
        var result = await SeatEndpoints.CreateSeatAsync(
            new SeatRequest { AccountName = "GuestTest", ScaleFactor = 110 },
            mgr: null!, presets: null!, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var error = ErrorText(result);
        Assert.Contains("110", error);
        Assert.Contains("100, 125, 150, 175, 200, 250, 300, 400, 500", error);
    }

    [Fact]
    public async Task ScaleEndpoint_AnswersBadRequest_ForABadScale()
    {
        var result = await SeatEndpoints.SetScaleAsync(
            Guid.NewGuid(), new ScaleFactorRequest { ScaleFactor = 90 },
            mgr: null!, presets: null!, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains("90", ErrorText(result));
    }

    [Theory]
    [InlineData(null)]    // no override: the host default or the heuristic
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(500)]
    public void AbsentOrAllowedScales_PassValidation(int? scale)
    {
        Assert.Null(ApiInputValidation.ScaleFactorError(scale));
    }

    // ── Changing the scale of a live seat ─────────────────────────────────────

    [Fact]
    public async Task SetScale_ThatLeavesTheEffectiveScaleAlone_DoesNotReconnect_ButIsSaved()
    {
        // 1920 wide derives 100. Pinning it at 100 changes nothing the player sees, so the seat
        // must not be reconnected (the null Apollo manager would throw if it were), but the
        // override is real — it stops a later resize moving the scale — and has to persist.
        var mgr = NewManager();
        var seat = NewSeat(width: 1920);
        seat.AutoStart = true;
        mgr.RegisterSeatDirect(seat);
        mgr.GeometryFor(seat);
        var presets = NewPresetStore();

        await mgr.SetScaleFactorAsync(seat.Id, 100, presets, CancellationToken.None);

        Assert.Equal(100, seat.ScaleFactorOverride);
        Assert.Equal(ScaleFactorSource.Seat, seat.ScaleFactorSource);
        Assert.Equal(100, NewPresetStore().GetByAccount("GuestTest")!.ScaleFactor);
    }

    [Fact]
    public async Task SetScale_Null_ClearsTheOverride()
    {
        // The override already equals the derived value, so clearing it needs no reconnect and
        // the seat reports the heuristic again.
        var mgr = NewManager();
        var seat = NewSeat(width: 1920, scaleOverride: 100);
        seat.AutoStart = true;
        mgr.RegisterSeatDirect(seat);
        mgr.GeometryFor(seat);
        var presets = NewPresetStore();

        await mgr.SetScaleFactorAsync(seat.Id, null, presets, CancellationToken.None);

        Assert.Null(seat.ScaleFactorOverride);
        Assert.Equal(ScaleFactorSource.Derived, seat.ScaleFactorSource);
        Assert.Null(NewPresetStore().GetByAccount("GuestTest")!.ScaleFactor);
    }

    // ── Persistence through the presets file ─────────────────────────────────

    [Fact]
    public void AnOverride_SurvivesTheRoundTripThroughThePresetFile()
    {
        var seat = NewSeat(width: 1920, scaleOverride: 200);
        seat.Fps = 120;
        seat.NvencPreset = NvencQualityPreset.Quality;
        NewPresetStore().Upsert(SeatPreset.ForAutoStart(seat));

        // A second store instance is what the service does after a restart.
        var reloaded = NewPresetStore().GetByAccount("GuestTest")!;
        Assert.Equal(200, reloaded.ScaleFactor);

        // And autostart turns it back into a request that carries it.
        var request = MultiSeatWorker.RequestFor(reloaded);
        Assert.Equal(200, request.ScaleFactor);
        Assert.Equal(1920, request.Width);
        Assert.Equal(1080, request.Height);
        Assert.Equal(120, request.Fps);
        Assert.Equal(NvencQualityPreset.Quality, request.NvencPreset);
    }

    [Fact]
    public void ThePresetFile_StoresTheOverrideUnderItsOwnKey()
    {
        NewPresetStore().Upsert(SeatPreset.ForAutoStart(NewSeat(scaleOverride: 175)));

        using var doc = JsonDocument.Parse(File.ReadAllText(_presetPath));
        var entry = Assert.Single(doc.RootElement.EnumerateArray().ToList());
        Assert.Equal(175, entry.GetProperty("ScaleFactor").GetInt32());
    }

    [Fact]
    public void APresetFileFromBeforeThisField_LoadsWithNoOverride()
    {
        // The shape the store wrote up to v0.6.11: PascalCase keys, enum as a number, and no
        // ScaleFactor. Those seats must keep the width heuristic, not fail to load.
        File.WriteAllText(_presetPath, """
            [
              {
                "Id": "6f1c2a4e-2b7d-4c55-9d53-2f7c7f0e9a11",
                "AccountName": "GuestTest",
                "Width": 2560,
                "Height": 1440,
                "Fps": 60,
                "AutoStart": true,
                "NvencPreset": 1,
                "CreatedAt": "2026-09-01T12:00:00+00:00"
              }
            ]
            """);

        var preset = NewPresetStore().GetByAccount("GuestTest");

        Assert.NotNull(preset);
        Assert.Equal(2560, preset.Width);
        Assert.Null(preset.ScaleFactor);
        Assert.Null(MultiSeatWorker.RequestFor(preset).ScaleFactor);
    }

    [Fact]
    public void ForAutoStart_CopiesEveryField()
    {
        // Every preset save goes through this. Before it, three call sites each copied the fields
        // by hand, and a field one of them missed would be reset on that path's next save.
        var seat = NewSeat(width: 2560, scaleOverride: 250);
        seat.Height = 1440;
        seat.Fps = 144;
        seat.NvencPreset = NvencQualityPreset.Latency;

        var preset = SeatPreset.ForAutoStart(seat);

        Assert.Equal("GuestTest", preset.AccountName);
        Assert.Equal(2560, preset.Width);
        Assert.Equal(1440, preset.Height);
        Assert.Equal(144, preset.Fps);
        Assert.Equal(NvencQualityPreset.Latency, preset.NvencPreset);
        Assert.Equal(250, preset.ScaleFactor);
        Assert.True(preset.AutoStart);
    }
}
