using MultiSeat.Service.Configuration;
using MultiSeat.Shared.Models;
using MultiSeat.Tests.Streaming;   // TestLogger<T> lives alongside the streaming tests
using Xunit;

namespace MultiSeat.Tests.Configuration;

/// <summary>
/// Seat presets are what survive a service restart: which accounts exist as seats, their size, and
/// which ones auto-start. Every failure here is quiet — a preset that does not persist means a seat
/// simply does not come back after a reboot, and nobody is told why.
///
/// The store writes to a fixed ProgramData path in production. These pass a temp path instead:
/// the real file holds the user's autostart seats, and a test that wrote to it would delete them.
/// </summary>
public class SeatPresetStoreTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"multiseat-presets-{Guid.NewGuid():N}.json");

    private SeatPresetStore NewStore() =>
        new(new TestLogger<SeatPresetStore>(), _path);

    private static SeatPreset Preset(string account, bool autoStart = false, int width = 1920) => new()
    {
        AccountName = account,
        Width = width,
        Height = 1080,
        Fps = 60,
        AutoStart = autoStart,
    };

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
        try { File.Delete(_path + ".tmp"); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── Persistence ───────────────────────────────────────────────────

    [Fact]
    public void APresetSurvivesARestart()
    {
        // The whole point of the file. A second store instance is what the service does after a
        // reboot; if this does not hold, autostart seats silently stop coming back.
        NewStore().Upsert(Preset("Gaming", autoStart: true));

        var reloaded = NewStore().GetByAccount("Gaming");

        Assert.NotNull(reloaded);
        Assert.True(reloaded.AutoStart);
        Assert.Equal(1920, reloaded.Width);
    }

    [Fact]
    public void NoFileYet_IsNotAnError()
    {
        var store = NewStore();          // nothing on disk

        Assert.Empty(store.GetAll());
        Assert.Empty(store.GetAutoStart());
        Assert.Null(store.GetByAccount("Gaming"));
    }

    [Fact]
    public void CorruptFile_StartsEmptyRatherThanThrowing()
    {
        // A half-written or hand-edited file must not stop the service from starting. Losing the
        // presets is bad; failing to boot is worse.
        File.WriteAllText(_path, "{ this is not valid json");

        var store = NewStore();

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void NoTempFileIsLeftBehind()
    {
        // Save writes to .tmp then renames, so a crash mid-write cannot truncate the real file.
        // A leftover .tmp means the rename did not happen and the write went nowhere.
        NewStore().Upsert(Preset("Gaming"));

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    // ── Upsert ────────────────────────────────────────────────────────

    [Fact]
    public void UpsertingAnExistingAccount_UpdatesInPlaceAndKeepsIdentity()
    {
        // Id and CreatedAt are deliberately preserved across an update. Regenerating the Id would
        // silently orphan anything holding it, and CreatedAt is the only record of when a seat was
        // first set up.
        var store = NewStore();
        var first = store.Upsert(Preset("Gaming", width: 1920));
        var originalId = first.Id;
        var originalCreated = first.CreatedAt;

        var updated = store.Upsert(Preset("Gaming", width: 2560));

        Assert.Single(store.GetAll());
        Assert.Equal(originalId, updated.Id);
        Assert.Equal(originalCreated, updated.CreatedAt);
        Assert.Equal(2560, store.GetByAccount("Gaming")!.Width);
    }

    [Theory]
    [InlineData("GAMING")]
    [InlineData("gaming")]
    [InlineData("GaMiNg")]
    public void AccountMatchingIgnoresCase(string variant)
    {
        // Windows account names are case-insensitive. If this matched exactly, "Gaming" and
        // "gaming" would become two presets for one account and autostart would launch it twice.
        var store = NewStore();
        store.Upsert(Preset("Gaming"));

        store.Upsert(Preset(variant, width: 1280));

        Assert.Single(store.GetAll());
        Assert.Equal(1280, store.GetByAccount("Gaming")!.Width);
        Assert.NotNull(store.GetByAccount(variant));
    }

    [Fact]
    public void SeparateAccountsAreKeptSeparate()
    {
        var store = NewStore();
        store.Upsert(Preset("Gaming"));
        store.Upsert(Preset("Seat2"));

        Assert.Equal(2, store.GetAll().Count);
    }

    // ── Reads ─────────────────────────────────────────────────────────

    [Fact]
    public void GetAutoStart_ReturnsOnlyTheAutoStartOnes()
    {
        var store = NewStore();
        store.Upsert(Preset("Gaming", autoStart: true));
        store.Upsert(Preset("Seat2", autoStart: false));

        var auto = store.GetAutoStart();

        Assert.Single(auto);
        Assert.Equal("Gaming", auto[0].AccountName);
    }

    [Fact]
    public void GetAllReturnsASnapshot_NotTheLiveList()
    {
        // GetAll copies under the lock. If it handed back the live list, a caller iterating it
        // while another thread provisioned a seat would throw mid-enumeration.
        var store = NewStore();
        store.Upsert(Preset("Gaming"));

        var snapshot = store.GetAll();
        store.Upsert(Preset("Seat2"));      // mutate the store behind the snapshot's back

        Assert.Single(snapshot);            // the snapshot did not move
        Assert.Equal(2, store.GetAll().Count);

        // And it is not a mutable List handed out by reference, so a caller cannot edit the
        // store's own collection without going through Upsert/Delete (and therefore Save).
        Assert.IsNotType<List<SeatPreset>>(snapshot);
    }

    // ── Delete ────────────────────────────────────────────────────────

    [Fact]
    public void DeleteByAccount_RemovesAndPersists()
    {
        var store = NewStore();
        store.Upsert(Preset("Gaming"));

        Assert.True(store.DeleteByAccount("gaming"));   // case-insensitive here too
        Assert.Empty(store.GetAll());
        Assert.Empty(NewStore().GetAll());              // and it stayed deleted on disk
    }

    [Fact]
    public void DeletingSomethingThatIsNotThere_ReportsFalse()
    {
        var store = NewStore();
        store.Upsert(Preset("Gaming"));

        Assert.False(store.DeleteByAccount("NeverExisted"));
        Assert.Single(store.GetAll());
    }

    // ── Corruption quarantine (G10) ─────────────────────────────────────
    //
    // A malformed seat-presets.json used to load as empty with the corrupt bytes left in
    // place — indistinguishable from "no presets yet" — and the next Save then overwrote
    // the evidence. Load now moves proven-corrupt bytes aside before recovering empty.

    [Fact]
    public void MalformedFile_IsQuarantined_AndRecoversEmpty()
    {
        const string corrupt = "{ this is not valid json";
        File.WriteAllText(_path, corrupt);
        try
        {
            var store = NewStore();

            Assert.Empty(store.GetAll());                       // documented recovery
            Assert.False(File.Exists(_path));                   // corrupt bytes gone from the live path
            var quarantine = Assert.Single(QuarantineArtifacts());
            Assert.Equal(corrupt, File.ReadAllText(quarantine)); // original bytes preserved verbatim
        }
        finally
        {
            DeleteQuarantineArtifacts();
        }
    }

    [Fact]
    public void SecondCorruption_DoesNotOverwriteFirstQuarantine()
    {
        const string first = "{ corrupt one";
        const string second = "{ corrupt two";
        File.WriteAllText(_path, first);
        try
        {
            NewStore();
            File.WriteAllText(_path, second);
            NewStore();

            var artifacts = QuarantineArtifacts();
            Assert.Equal(2, artifacts.Count);
            var bodies = artifacts.Select(File.ReadAllText).ToList();
            Assert.Contains(first, bodies);
            Assert.Contains(second, bodies);
        }
        finally
        {
            DeleteQuarantineArtifacts();
        }
    }

    [Fact]
    public void LockedFile_IsNotQuarantined()
    {
        // An unreadable file is NOT proof of corruption (sharing violation, transient I/O):
        // it must be left alone. An exclusive lock makes the read fail deterministically.
        File.WriteAllText(_path, "[");
        try
        {
            using var lockHandle = File.Open(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var store = NewStore();

            Assert.Empty(store.GetAll());               // same in-memory recovery as before
            Assert.True(File.Exists(_path));            // but the file was not moved
            Assert.Empty(QuarantineArtifacts());
        }
        finally
        {
            DeleteQuarantineArtifacts();
        }
    }

    [Fact]
    public void SaveAfterQuarantine_WritesFreshState_AndKeepsEvidence()
    {
        const string corrupt = "{ this is not valid json";
        File.WriteAllText(_path, corrupt);
        try
        {
            NewStore().Upsert(Preset("Gaming", autoStart: true));

            var quarantine = Assert.Single(QuarantineArtifacts());
            Assert.Equal(corrupt, File.ReadAllText(quarantine)); // evidence intact
            var reloaded = NewStore().GetByAccount("Gaming");    // fresh state round-trips
            Assert.NotNull(reloaded);
            Assert.True(reloaded.AutoStart);
        }
        finally
        {
            DeleteQuarantineArtifacts();
        }
    }

    private List<string> QuarantineArtifacts() =>
        Directory.EnumerateFiles(
            Path.GetDirectoryName(_path)!,
            Path.GetFileName(_path) + ".corrupt-*").ToList();

    private void DeleteQuarantineArtifacts()
    {
        foreach (var artifact in QuarantineArtifacts())
            try { File.Delete(artifact); } catch { /* best effort */ }
    }
}
