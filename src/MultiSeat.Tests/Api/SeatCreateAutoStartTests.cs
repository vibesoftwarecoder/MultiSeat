using Microsoft.AspNetCore.Http;
using MultiSeat.Service.Api;
using MultiSeat.Service.Configuration;
using MultiSeat.Shared.Models;
using MultiSeat.Tests.Streaming;   // TestLogger<T>
using Xunit;

namespace MultiSeat.Tests.Api;

/// <summary>
/// Issue #87: a seat can be created with auto-start already on, in the same request, instead of
/// needing a second call once it exists. Provisioning is passed in as a delegate, so these run
/// without a Windows session; what they pin is what the handler does around it.
/// </summary>
public class SeatCreateAutoStartTests : IDisposable
{
    private readonly string _presetPath =
        Path.Combine(Path.GetTempPath(), $"multiseat-create-autostart-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_presetPath); } catch { /* best effort */ }
        try { File.Delete(_presetPath + ".tmp"); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // A second store reads the file again, the way the service does after a restart. Asserting
    // on it proves the preset was written to disk, not just held in memory.
    private SeatPresetStore NewPresetStore() => new(new TestLogger<SeatPresetStore>(), _presetPath);

    private static Func<SeatRequest, CancellationToken, Task<SeatInfo>> ProvisionsReadySeat() =>
        (request, _) => Task.FromResult(new SeatInfo
        {
            Id = Guid.NewGuid(),
            AccountName = request.AccountName,
            Width = request.Width,
            Height = request.Height,
            Fps = request.Fps,
            NvencPreset = request.NvencPreset,
            ScaleFactorOverride = request.ScaleFactor,
            Status = SeatStatus.Ready,
        });

    private static SeatInfo CreatedSeat(IResult result)
    {
        Assert.Equal(StatusCodes.Status201Created,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        return Assert.IsType<SeatInfo>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
    }

    [Fact]
    public async Task AutoStartTrue_SavesThePreset_AndMarksTheSeat()
    {
        var result = await SeatEndpoints.CreateSeatCoreAsync(
            new SeatRequest { AccountName = "GuestTest", Width = 2560, Height = 1440, Fps = 120, AutoStart = true },
            ProvisionsReadySeat(), NewPresetStore(), CancellationToken.None);

        var seat = CreatedSeat(result);
        Assert.True(seat.AutoStart);

        var saved = NewPresetStore().GetByAccount("GuestTest");
        Assert.NotNull(saved);
        Assert.True(saved.AutoStart);
        // The full preset, through the same path as the card's toggle, not a stub.
        Assert.Equal(2560, saved.Width);
        Assert.Equal(1440, saved.Height);
        Assert.Equal(120, saved.Fps);
    }

    [Fact]
    public async Task AutoStartFalse_RemovesAPresetSavedEarlier()
    {
        var store = NewPresetStore();
        store.Upsert(new SeatPreset { AccountName = "GuestTest", AutoStart = true });

        var result = await SeatEndpoints.CreateSeatCoreAsync(
            new SeatRequest { AccountName = "GuestTest", AutoStart = false },
            ProvisionsReadySeat(), store, CancellationToken.None);

        Assert.False(CreatedSeat(result).AutoStart);
        Assert.Null(NewPresetStore().GetByAccount("GuestTest"));
    }

    [Fact]
    public async Task AutoStartLeftOut_KeepsAPresetSavedEarlier_AndReportsIt()
    {
        // An older caller that does not send the field: nothing about the saved preset changes,
        // and the seat says it will come back, because it will.
        var store = NewPresetStore();
        store.Upsert(new SeatPreset { AccountName = "GuestTest", AutoStart = true });

        var result = await SeatEndpoints.CreateSeatCoreAsync(
            new SeatRequest { AccountName = "GuestTest" },
            ProvisionsReadySeat(), store, CancellationToken.None);

        Assert.True(CreatedSeat(result).AutoStart);
        Assert.True(NewPresetStore().GetByAccount("GuestTest")!.AutoStart);
    }

    [Fact]
    public async Task AutoStartLeftOut_WithNoPreset_SavesNothing()
    {
        var result = await SeatEndpoints.CreateSeatCoreAsync(
            new SeatRequest { AccountName = "GuestTest" },
            ProvisionsReadySeat(), NewPresetStore(), CancellationToken.None);

        Assert.False(CreatedSeat(result).AutoStart);
        Assert.Empty(NewPresetStore().GetAll());
    }

    [Fact]
    public async Task AFailedProvision_SavesNoPreset()
    {
        // Otherwise a seat that never came up would be attempted again at every boot.
        var result = await SeatEndpoints.CreateSeatCoreAsync(
            new SeatRequest { AccountName = "GuestTest", AutoStart = true },
            (_, _) => throw new InvalidOperationException("session did not appear"),
            NewPresetStore(), CancellationToken.None);

        Assert.NotEqual(StatusCodes.Status201Created,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(NewPresetStore().GetAll());
    }

    [Fact]
    public void TheCardToggle_AndCreation_ShareOnePath()
    {
        // SetAutoStart is what PUT /api/seats/{id}/autostart calls. Off after on leaves no preset.
        var store = NewPresetStore();
        var seat = new SeatInfo { Id = Guid.NewGuid(), AccountName = "GuestTest", Status = SeatStatus.Ready };

        SeatEndpoints.SetAutoStart(seat, true, store);
        Assert.True(NewPresetStore().GetByAccount("GuestTest")!.AutoStart);

        SeatEndpoints.SetAutoStart(seat, false, store);
        Assert.False(seat.AutoStart);
        Assert.Null(NewPresetStore().GetByAccount("GuestTest"));
    }

    [Fact]
    public void TheRequestField_IsReadFromCamelCaseJson_AndAbsentMeansNull()
    {
        // The dashboard sends camelCase. A field the binder silently ignored would leave every
        // seat created from the form without auto-start, which is this issue all over again.
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        var on = System.Text.Json.JsonSerializer.Deserialize<SeatRequest>(
            """{ "accountName": "GuestTest", "autoStart": true }""", web)!;
        var absent = System.Text.Json.JsonSerializer.Deserialize<SeatRequest>(
            """{ "accountName": "GuestTest" }""", web)!;

        Assert.True(on.AutoStart);
        Assert.Null(absent.AutoStart);
    }
}
