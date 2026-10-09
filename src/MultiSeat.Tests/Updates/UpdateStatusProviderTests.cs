using MultiSeat.Service.Configuration;
using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

/// <summary>Status and announce, as the design (7.2) defines them, and the provider's thread safety.</summary>
public class UpdateStatusProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "multiseat-upd-" + Guid.NewGuid().ToString("N"));

    public UpdateStatusProviderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (UpdateStatusProvider P, FakeOptionsMonitor O) Make(bool enabled = true, string ms = "0.6.18+abc1234")
    {
        var o = new FakeOptionsMonitor(new MultiSeatOptions { UpdateCheckEnabled = enabled });
        var store = new UpdateStateStore(Path.Combine(_dir, "u.json"), (_, _) => true);
        return (new UpdateStatusProvider(o, store, () => ms), o);
    }

    private static ReleaseCandidate Tag(string t) => new() { Tag = t, PublishedAt = new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero) };

    private static void Seed(UpdateStatusProvider p, UpdateComponent c, string? latestTag, string? baseline = null, string? error = null)
    {
        var r = p.State.GetOrAdd(c);
        r.Candidates = latestTag is null ? [] : [Tag(latestTag)];
        r.Baseline = baseline;
        r.LastError = error;
        r.CheckedAt = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    }

    private static InstalledDetection UnknownApollo() =>
        new(DetectionOutcome.Unknown, null, null, "Built locally or not a published release (no hash match).");

    private static ComponentUpdateStatus Of(UpdateStatusProvider p, string id) => p.GetSnapshot().Components.Single(c => c.Id == id);

    // ── Disabled ─────────────────────────────────────────────────────

    [Fact]
    public void Disabled_ListsOnlyTheInstalledMultiSeat_WithNothingFromGitHub()
    {
        var (p, _) = Make(enabled: false);
        Seed(p, UpdateComponent.MultiSeat, "v0.9.0", baseline: "0.6.0"); // a stale cache must not leak through
        p.Publish(null, null, null);

        var s = p.GetSnapshot();

        Assert.False(s.Enabled);
        var only = Assert.Single(s.Components);
        Assert.Equal("multiseat", only.Id);
        Assert.Equal(UpdateStatus.Disabled, only.Status);
        Assert.Equal("0.6.18", only.Installed!.Version);
        Assert.Equal("0.6.18 (abc1234)", only.Installed.Display);
        Assert.Null(only.Latest);
        Assert.False(only.Announce);
        Assert.Null(s.CheckedAt);
    }

    // ── Known installed version ──────────────────────────────────────

    [Theory]
    [InlineData("0.6.18+abc1234", "v0.6.19", UpdateStatus.UpdateAvailable, true)]
    [InlineData("0.6.19+abc1234", "v0.6.19", UpdateStatus.UpToDate, false)]
    [InlineData("0.6.20+abc1234", "v0.6.19", UpdateStatus.Ahead, false)]
    [InlineData("0.6.9", "v0.6.19", UpdateStatus.UpdateAvailable, true)]   // numeric, not string, order
    public void MultiSeat_StatusAndAnnounce_FollowTheVersionComparison(string installed, string latest, UpdateStatus status, bool announce)
    {
        var (p, _) = Make(ms: installed);
        Seed(p, UpdateComponent.MultiSeat, latest);
        p.Publish(null, null, null);

        var c = Of(p, "multiseat");

        Assert.Equal(status, c.Status);
        Assert.Equal(announce, c.Announce);
    }

    [Fact]
    public void ACheckErrorAfterAGoodResult_KeepsAnnouncingTheKnownUpdate_AndShowsTheError()
    {
        var (p, _) = Make();
        Seed(p, UpdateComponent.MultiSeat, "v0.6.19", error: "network error (ConnectionError)");
        p.Publish(null, null, null);

        var c = Of(p, "multiseat");

        Assert.Equal(UpdateStatus.UpdateAvailable, c.Status);
        Assert.True(c.Announce);
        Assert.Equal("0.6.19", c.Latest!.Version);
        Assert.Equal("network error (ConnectionError)", c.Error);
        Assert.Equal("network error (ConnectionError)", p.GetSnapshot().Error);
    }

    [Fact]
    public void ACheckErrorWithNoGoodResultYet_NeverAnnounces()
    {
        var (p, _) = Make();
        Seed(p, UpdateComponent.MultiSeat, null, error: "HTTP 500");
        Seed(p, UpdateComponent.ApolloVibe, null, baseline: "2026.6.1-ms6", error: "HTTP 500");
        p.Publish(UnknownApollo(), null, null);

        var ms = Of(p, "multiseat");
        var apollo = Of(p, "apollovibe");

        Assert.Null(ms.Latest);
        Assert.False(ms.Announce);
        Assert.False(apollo.Announce);
        Assert.Equal("HTTP 500", ms.Error);
    }

    [Fact]
    public void OnceTheUpdateIsInstalled_TheAnnouncementStops_EvenWhileChecksKeepFailing()
    {
        // The installed version is read from the running assembly; after the user updates, the
        // service restarts and reads the new one. The cache and its error survive the restart.
        var (before, _) = Make(ms: "0.6.18+abc1234");
        Seed(before, UpdateComponent.MultiSeat, "v0.6.19", error: "network error (ConnectionError)");
        before.Publish(null, null, null);
        Assert.True(Of(before, "multiseat").Announce);

        var (after, _) = Make(ms: "0.6.19+def5678");
        Seed(after, UpdateComponent.MultiSeat, "v0.6.19", error: "network error (ConnectionError)");
        after.Publish(null, null, null);

        var c = Of(after, "multiseat");
        Assert.Equal(UpdateStatus.UpToDate, c.Status);
        Assert.False(c.Announce);
        Assert.NotNull(c.Error);
    }

    [Theory]
    [InlineData("0.6.19+abc", "v0.6.19", UpdateStatus.UpToDate)]
    [InlineData("0.6.20+abc", "v0.6.19", UpdateStatus.Ahead)]
    public void AStaleError_NeverMakesAnUpToDateOrAheadComponentAnnounce(string installed, string latest, UpdateStatus status)
    {
        var (p, _) = Make(ms: installed);
        Seed(p, UpdateComponent.MultiSeat, latest, error: "rate limited");
        p.Publish(null, null, null);

        var c = Of(p, "multiseat");

        Assert.Equal(status, c.Status);
        Assert.False(c.Announce);
    }

    [Fact]
    public void NoDataYet_IsUnavailable_AndNeverAnnounces()
    {
        var (p, _) = Make();

        Assert.All(p.GetSnapshot().Components, c =>
        {
            Assert.Equal(UpdateStatus.Unavailable, c.Status);
            Assert.False(c.Announce);
            Assert.Null(c.Latest);
        });
    }

    // ── No known installed version: the baseline ─────────────────────

    [Fact]
    public void Unidentified_Apollo_DoesNotAnnounce_AReleaseThatWasAlreadyOutWhenChecksStarted()
    {
        var (p, _) = Make();
        Seed(p, UpdateComponent.ApolloVibe, "v2026.6.1-ms6", baseline: "2026.6.1-ms6");
        p.Publish(UnknownApollo(), null, null);

        var c = Of(p, "apollovibe");

        Assert.Equal(UpdateStatus.UnknownInstalled, c.Status);
        Assert.False(c.Announce);
        Assert.Contains("Built locally", c.InstalledNote);
        Assert.Null(c.Installed);
    }

    [Fact]
    public void Unidentified_Apollo_Announces_OnlyWhenTheLatestAdvancesBeyondTheBaseline()
    {
        var (p, _) = Make();
        Seed(p, UpdateComponent.ApolloVibe, "v2026.6.1-ms7", baseline: "2026.6.1-ms6");
        p.Publish(UnknownApollo(), null, null);

        Assert.True(Of(p, "apollovibe").Announce);
    }

    [Fact]
    public void WithoutABaseline_NothingIsAnnounced()
    {
        var (p, _) = Make();
        Seed(p, UpdateComponent.ApolloVibe, "v2026.6.1-ms7", baseline: null);
        Seed(p, UpdateComponent.MoonlightVibe, "v6.3.9", baseline: null);
        p.Publish(UnknownApollo(), null, null);

        Assert.False(Of(p, "apollovibe").Announce);
        Assert.False(Of(p, "moonlightvibe").Announce);
    }

    [Fact]
    public void MoonlightVibe_IsLatestOnly_WithTheHonestNote_AndAnnouncesOnlyBeyondTheBaseline()
    {
        var (p, _) = Make();
        Seed(p, UpdateComponent.MoonlightVibe, "v6.3.9", baseline: "6.3.9");
        p.Publish(null, null, null);
        var same = Of(p, "moonlightvibe");
        Assert.Equal(UpdateStatus.LatestOnly, same.Status);
        Assert.False(same.Announce);
        Assert.Null(same.Installed);
        Assert.Contains("cannot see", same.InstalledNote);

        Seed(p, UpdateComponent.MoonlightVibe, "v6.3.10", baseline: "6.3.9");
        p.Publish(null, null, null);
        Assert.True(Of(p, "moonlightvibe").Announce); // 6.3.10 > 6.3.9 numerically
    }

    [Fact]
    public void ApolloVibe_NotInstalled_NeverAnnounces()
    {
        var (p, _) = Make();
        Seed(p, UpdateComponent.ApolloVibe, "v2026.6.1-ms7", baseline: "2026.6.1-ms6");
        p.Publish(new InstalledDetection(DetectionOutcome.NotInstalled, null, null, "Not found at X."), null, null);

        var c = Of(p, "apollovibe");

        Assert.Equal(UpdateStatus.NotInstalled, c.Status);
        Assert.False(c.Announce);
    }

    [Fact]
    public void AnIdentifiedApollo_IsComparedLikeAnyKnownVersion()
    {
        var (p, _) = Make();
        Seed(p, UpdateComponent.ApolloVibe, "v2026.6.1-ms10");
        var v = ReleaseVersion.TryParse(UpdateComponent.ApolloVibe, "v2026.6.1-ms9")!;
        p.Publish(new InstalledDetection(DetectionOutcome.Identified, v,
            new InstalledInfo(v.Display, v.Display, InstalledSource.ReleaseHash, null), null), null, null);

        var c = Of(p, "apollovibe");

        Assert.Equal(UpdateStatus.UpdateAvailable, c.Status); // ms10 > ms9, not the string order
        Assert.True(c.Announce);
        Assert.Equal("release-hash", System.Text.Json.JsonSerializer.Serialize(c.Installed!.Source).Trim('"'));
    }

    // ── What reaches the dashboard ───────────────────────────────────

    [Fact]
    public void ATagThatIsNotAVersion_IsNeverShown()
    {
        var (p, _) = Make();
        Seed(p, UpdateComponent.MultiSeat, "\"><script>alert(1)</script>");
        p.Publish(null, null, null);

        var c = Of(p, "multiseat");

        Assert.Null(c.Latest);
        Assert.Equal(UpdateStatus.Unavailable, c.Status);
    }

    [Fact]
    public void TheReleaseUrl_IsBuiltFromTheRepositoryAndTheTag()
    {
        var (p, _) = Make();
        Seed(p, UpdateComponent.MultiSeat, "v0.6.19");
        p.Publish(null, null, null);

        Assert.Equal("https://github.com/vibesoftwarecoder/MultiSeat/releases/tag/v0.6.19", Of(p, "multiseat").Latest!.ReleaseUrl);
    }

    [Fact]
    public void TheOptionIsReadOnEverySnapshot_SoSwitchingOffTakesEffectAtOnce()
    {
        var (p, o) = Make();
        Seed(p, UpdateComponent.MultiSeat, "v0.6.19");
        p.Publish(null, null, null);
        Assert.Equal(3, p.GetSnapshot().Components.Count);

        o.Set(new MultiSeatOptions { UpdateCheckEnabled = false });

        Assert.Equal(UpdateStatus.Disabled, Assert.Single(p.GetSnapshot().Components).Status);
    }

    // ── Through the real service: the baseline is recorded, then news is news ──

    [Fact(Timeout = 60_000)]
    public async Task ThroughTheService_FirstEnableDoesNotAnnounce_ALaterReleaseDoes()
    {
        using var rig = new ServiceRig(enabled: true, random: () => 0.5);
        await rig.StartAsync();
        await rig.RunAsync(TimeSpan.FromMinutes(20)); // first round: the releases that were already out

        var first = rig.Provider.GetSnapshot();
        // Installed MultiSeat 0.6.18 is known to be behind: that announces. The two whose installed
        // version is unknown must stay quiet about releases that were already out.
        Assert.True(first.Components.Single(c => c.Id == "multiseat").Announce);
        Assert.False(first.Components.Single(c => c.Id == "apollovibe").Announce);
        Assert.False(first.Components.Single(c => c.Id == "moonlightvibe").Announce);
        Assert.Equal(UpdateStatus.UpdateAvailable, first.Components.Single(c => c.Id == "multiseat").Status); // installed 0.6.18 < 0.6.19

        rig.Handler.Respond = (r, _, _) => Task.FromResult(CountingHandler.Releases(r, "v0.6.19", "v0.6.20"));
        await rig.RunAsync(TimeSpan.FromHours(13));

        var later = rig.Provider.GetSnapshot().Components.ToDictionary(c => c.Id);
        Assert.True(later["multiseat"].Announce);
        Assert.True(later["apollovibe"].Announce);      // unidentified install, but a release newer than the baseline
        Assert.True(later["moonlightvibe"].Announce);
        Assert.Equal("0.6.20", later["multiseat"].Latest!.Version);
    }

    // ── Thread safety ────────────────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentPublishAndRead_NeverTearsASnapshot()
    {
        var (p, _) = Make();
        var stop = false;
        var reads = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var deadline = Environment.TickCount64 + 60_000;

        // The overlap is proved, not hoped for: the writer waits until the readers have read, and
        // keeps publishing until they have read 2000 snapshots (60 s limit, then a clear failure).
        var writer = Task.Run(() =>
        {
            while (Volatile.Read(ref reads) < 1)
            {
                if (Environment.TickCount64 > deadline) { failures.Add("the reader threads never ran"); stop = true; return; }
                Thread.Yield();
            }
            var floor = Volatile.Read(ref reads) + 2000;
            for (var i = 0; (i < 3000 || Volatile.Read(ref reads) < floor) && failures.IsEmpty; i++)
            {
                if (Environment.TickCount64 > deadline) { failures.Add($"no overlap: only {reads} reads during {i} publishes"); break; }
                Seed(p, UpdateComponent.MultiSeat, i % 2 == 0 ? "v0.6.19" : "v0.6.20");
                p.Publish(i % 3 == 0 ? UnknownApollo() : null, DateTimeOffset.UnixEpoch.AddSeconds(i), null);
            }
            stop = true;
        });
        var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            while (!Volatile.Read(ref stop) && failures.IsEmpty)
            {
                try
                {
                    var s = p.GetSnapshot();
                    Interlocked.Increment(ref reads);
                    if (s.Components.Count != 3) failures.Add("count " + s.Components.Count);
                    if (s.Components.Any(c => c.Latest is { } l && l.Tag.Length == 0)) failures.Add("torn latest");
                }
                catch (Exception ex) { failures.Add(ex.GetType().Name); }
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer));
        Assert.Empty(failures);
    }
}
