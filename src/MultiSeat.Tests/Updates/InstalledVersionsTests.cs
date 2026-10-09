using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

/// <summary>
/// Installed-version detection. Everything runs against temp files: the real C:\Program Files
/// tree, the services and the host's state are never touched, and no test makes a network call.
/// </summary>
public class InstalledVersionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "multiseat-installed-" + Guid.NewGuid().ToString("N"));
    private string ExePath => Path.Combine(_dir, "sunshine.exe");

    public InstalledVersionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTime Mtime = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private void WriteExe(byte[] bytes, DateTime? mtime = null)
    {
        File.WriteAllBytes(ExePath, bytes);
        File.SetLastWriteTimeUtc(ExePath, mtime ?? Mtime);
    }

    private static byte[] Bytes(string seed) => Encoding.UTF8.GetBytes("MZ fake sunshine " + seed).Concat(new byte[4096]).ToArray();

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private const string Ms6Commit = "681e36a885fa2adc6232152fa099db80bdc0a976";

    private static ReleaseCandidate Cand(string tag, string? sha = null, string? commit = null) =>
        new() { Tag = tag, SunshineSha256 = sha, TargetCommit = commit };

    private static Func<string, string?> Pe(string? version) => _ => version;

    private ApolloDetectionResult Detect(IReadOnlyCollection<ReleaseCandidate> releases, InstalledExeHash? cache = null, string? pe = "2026.6.1") =>
        InstalledVersions.DetectApollo(ExePath, releases, cache, Pe(pe));

    // ── MultiSeat ─────────────────────────────────────────────────────

    [Fact]
    public void MultiSeat_ParsesTheInformationalVersion_AndKeepsTheCommitForDisplay()
    {
        var d = InstalledVersions.DetectMultiSeat("0.6.19+b7e6d9539");

        Assert.Equal(DetectionOutcome.Identified, d.Outcome);
        Assert.Equal("0.6.19", d.Version!.Display);
        Assert.Equal(new InstalledInfo("0.6.19", "0.6.19 (b7e6d95)", InstalledSource.Assembly, null), d.Installed);
    }

    [Theory]
    [InlineData("0.6.19", "0.6.19")]
    [InlineData("v0.6.19", "0.6.19")]
    [InlineData("0.7.0-rc1+abc", "0.7.0-rc1 (abc)")]
    [InlineData("0.6.19+abc", "0.6.19 (abc)")]
    public void MultiSeat_DisplayShapes(string informational, string display)
    {
        Assert.Equal(display, InstalledVersions.DetectMultiSeat(informational).Installed!.Display);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0+b7e6d9539")]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0.6")]
    public void MultiSeat_PlaceholderOrGarbage_IsUnknown_NeverAnException_NeverAnUpdate(string? informational)
    {
        var d = InstalledVersions.DetectMultiSeat(informational);

        Assert.Equal(DetectionOutcome.Unknown, d.Outcome);
        Assert.Null(d.Version);
        Assert.Null(d.Installed);
        Assert.False(string.IsNullOrWhiteSpace(d.Note));
        Assert.Equal(UpdateStatus.UnknownInstalled, ReleaseSelector.Classify(d.Version, ReleaseVersion.TryParse(UpdateComponent.MultiSeat, "v0.6.19")));
    }

    [Fact]
    public void MultiSeat_OneDotZeroDotZero_OnlyCountsAsPlaceholderWithoutAPreRelease()
    {
        Assert.Equal(DetectionOutcome.Identified, InstalledVersions.DetectMultiSeat("1.0.0-rc1").Outcome);
    }

    [Fact]
    public void RunningVersion_IsTheServiceAssembliesInformationalVersion()
    {
        var expected = typeof(MultiSeatOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(expected, InstalledVersions.ReadRunningInformationalVersion());
        Assert.False(string.IsNullOrEmpty(expected));
    }

    // ── ApolloVibe: not installed, unknown ────────────────────────────

    [Fact]
    public void Apollo_MissingFile_IsNotInstalled()
    {
        var r = Detect([Cand("v2026.6.1-ms6", H('a'))]);

        Assert.Equal(DetectionOutcome.NotInstalled, r.Detection.Outcome);
        Assert.Null(r.Detection.Installed);
        Assert.Contains(ExePath, r.Detection.Note);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Apollo_EmptyPath_IsNotInstalled(string path)
    {
        var r = InstalledVersions.DetectApollo(path, [], null);

        Assert.Equal(DetectionOutcome.NotInstalled, r.Detection.Outcome);
    }

    [Fact]
    public void Apollo_APathThatIsADirectory_IsNotInstalled()
    {
        var r = InstalledVersions.DetectApollo(_dir, [], null);

        Assert.Equal(DetectionOutcome.NotInstalled, r.Detection.Outcome);
    }

    [Fact]
    public void Apollo_NoMatchAnywhere_IsUnknown_AndSaysWhy()
    {
        WriteExe(Bytes("locally built"));

        var r = Detect([Cand("v2026.6.1-ms6", H('a'), Ms6Commit)]);

        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome);
        Assert.Null(r.Detection.Version);
        Assert.Null(r.Detection.Installed);
        Assert.Contains("no hash match", r.Detection.Note);
    }

    // ── release-hash ──────────────────────────────────────────────────

    [Fact]
    public void Apollo_HashMatchesARelease_IsReleaseHash_AndTheRightRelease()
    {
        var bytes = Bytes("ms5");
        WriteExe(bytes);

        var r = Detect([Cand("v2026.6.1-ms6", H('a')), Cand("v2026.6.1-ms5", Sha(bytes)), Cand("v2026.6.1-ms4", H('b'))]);

        Assert.Equal(DetectionOutcome.Identified, r.Detection.Outcome);
        Assert.Equal("2026.6.1-ms5", r.Detection.Version!.Display);
        Assert.Equal(InstalledSource.ReleaseHash, r.Detection.Installed!.Source);
        Assert.Equal("2026.6.1-ms5", r.Detection.Installed.Version);
        // It is ms5, not ms6: a real update is available.
        Assert.Equal(UpdateStatus.UpdateAvailable, ReleaseSelector.Classify(r.Detection.Version, ReleaseVersion.TryParse(UpdateComponent.ApolloVibe, "v2026.6.1-ms6")));
    }

    [Fact]
    public void Apollo_HashMatchIsCaseInsensitive()
    {
        var bytes = Bytes("upper");
        WriteExe(bytes);

        var r = Detect([Cand("v2026.6.1-ms6", Sha(bytes).ToUpperInvariant())]);

        Assert.Equal(InstalledSource.ReleaseHash, r.Detection.Installed!.Source);
    }

    [Fact]
    public void Apollo_OneChangedByte_IsNoLongerAMatch()
    {
        var bytes = Bytes("ms6");
        var candidates = new[] { Cand("v2026.6.1-ms6", Sha(bytes)) };
        WriteExe(bytes);
        Assert.Equal(InstalledSource.ReleaseHash, Detect(candidates).Detection.Installed!.Source);

        var changed = (byte[])bytes.Clone();
        changed[10] ^= 0x01;
        WriteExe(changed, Mtime.AddMinutes(1));

        var r = Detect(candidates);
        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome);
    }

    [Fact]
    public void Apollo_RealNotesParsedEndToEnd_ThenMatchedAgainstAFileWithThatHash()
    {
        // Take the real ms6 notes, swap the printed exe hash for the hash of a test file, run the
        // real parser and the real matcher on it.
        var bytes = Bytes("end to end");
        WriteExe(bytes);
        var notes = Fixtures.Notes("apollovibe-ms6.json").Replace("7E9FA1572579879FA98716C74753DA7EC070F0C8209FE5177F2D73CD0C1B33CC", Sha(bytes).ToUpperInvariant());
        var releases = ReleaseSelector.BuildCandidates(UpdateComponent.ApolloVibe,
            [new GitHubRelease("v2026.6.1-ms6", null, "master", notes, [], false, false)]);

        var r = Detect(releases);

        Assert.Equal(InstalledSource.ReleaseHash, r.Detection.Installed!.Source);
        Assert.Equal("2026.6.1-ms6", r.Detection.Installed.Version);
    }

    [Fact]
    public void Apollo_AFileThatMatchesTheZipHashOfARelease_IsNotAMatch()
    {
        // A hash that only ever appeared as the zip's hash must not identify the exe.
        var bytes = Bytes("zip-like");
        WriteExe(bytes);
        var notes = $"SHA-256 of `apollovibe-windows-x64.zip`:\n`{Sha(bytes)}`\n\nSHA-256 of `sunshine.exe`:\n`{H('a')}`\n";
        var releases = ReleaseSelector.BuildCandidates(UpdateComponent.ApolloVibe,
            [new GitHubRelease("v2026.6.1-ms6", null, "master", notes, [], false, false)]);

        Assert.Equal(DetectionOutcome.Unknown, Detect(releases).Detection.Outcome);
    }

    // ── Locked files and hashing failure ──────────────────────────────

    [Fact]
    public void Apollo_RunningProcessHoldsTheFileOpenForWriting_StillHashes()
    {
        var bytes = Bytes("running");
        WriteExe(bytes);
        // How a running exe looks to us: open, shared for read, write and delete.
        using var holder = new FileStream(ExePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        var r = Detect([Cand("v2026.6.1-ms6", Sha(bytes))]);

        Assert.Equal(InstalledSource.ReleaseHash, r.Detection.Installed!.Source);
    }

    [Fact]
    public void Apollo_AFileOpenedWithoutSharing_IsUnknownWithANote_NotAnException()
    {
        WriteExe(Bytes("exclusive"));
        using var holder = new FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var r = Detect([Cand("v2026.6.1-ms6", H('a'))]);

        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome);
        Assert.Contains("Could not read sunshine.exe", r.Detection.Note);
        Assert.Null(r.Detection.Installed);
    }

    [Fact]
    public void Apollo_HashingFailure_KeepsTheOldCacheEntry()
    {
        WriteExe(Bytes("exclusive-2"));
        var old = new InstalledExeHash { Path = ExePath, Size = 1, MtimeUtc = Mtime, Sha256 = H('c') };
        using var holder = new FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var r = Detect([], old);

        Assert.Same(old, r.HashCache);
    }

    [Fact]
    public void Apollo_AThrowingProductVersionReader_IsUnknown_NotAnException()
    {
        WriteExe(Bytes("pe-throws"));

        var r = InstalledVersions.DetectApollo(ExePath, [Cand("v2026.6.1-ms6", H('a'), Ms6Commit)], null,
            _ => throw new InvalidOperationException("pe reader broke"));

        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome);
        Assert.Contains("InvalidOperationException", r.Detection.Note);
    }

    // ── Cache ─────────────────────────────────────────────────────────

    [Fact]
    public void Apollo_FirstRun_ReturnsACacheEntryDescribingTheFile()
    {
        var bytes = Bytes("cache me");
        WriteExe(bytes);

        var r = Detect([]);

        var c = r.HashCache!;
        Assert.Equal(Path.GetFullPath(ExePath), c.Path);
        Assert.Equal(bytes.Length, c.Size);
        Assert.Equal(Mtime, c.MtimeUtc);
        Assert.Equal(Sha(bytes), c.Sha256);
    }

    [Fact]
    public void Apollo_UnchangedFile_ReusesTheCache_WithoutReadingTheFileAgain()
    {
        var bytes = Bytes("reuse");
        WriteExe(bytes);
        // A cache entry for the same path, size and mtime, but carrying a hash the file does NOT
        // have. If the detector re-hashed, it would not match the release below; if it trusts the
        // cache (as designed), it does. That makes the reuse observable.
        var planted = new InstalledExeHash { Path = Path.GetFullPath(ExePath), Size = bytes.Length, MtimeUtc = Mtime, Sha256 = H('d') };

        var r = Detect([Cand("v2026.6.1-ms6", H('d'))], planted);

        Assert.Equal(InstalledSource.ReleaseHash, r.Detection.Installed!.Source);
        Assert.Same(planted, r.HashCache);
    }

    [Fact]
    public void Apollo_CacheEntryIsReused_EvenIfThePathCaseDiffers()
    {
        var bytes = Bytes("case");
        WriteExe(bytes);
        var planted = new InstalledExeHash { Path = Path.GetFullPath(ExePath).ToUpperInvariant(), Size = bytes.Length, MtimeUtc = Mtime, Sha256 = H('d') };

        var r = Detect([Cand("v2026.6.1-ms6", H('d'))], planted);

        Assert.Equal(InstalledSource.ReleaseHash, r.Detection.Installed!.Source);
    }

    [Fact]
    public void Apollo_ChangedSize_InvalidatesTheCache()
    {
        var bytes = Bytes("size");
        WriteExe(bytes);
        var stale = new InstalledExeHash { Path = Path.GetFullPath(ExePath), Size = bytes.Length - 1, MtimeUtc = Mtime, Sha256 = H('d') };

        var r = Detect([Cand("v2026.6.1-ms6", H('d'))], stale);

        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome); // the stale hash was not trusted
        Assert.Equal(Sha(bytes), r.HashCache!.Sha256);
        Assert.Equal(bytes.Length, r.HashCache.Size);
    }

    [Fact]
    public void Apollo_ChangedMtime_InvalidatesTheCache()
    {
        var bytes = Bytes("mtime");
        WriteExe(bytes);
        var stale = new InstalledExeHash { Path = Path.GetFullPath(ExePath), Size = bytes.Length, MtimeUtc = Mtime.AddSeconds(-1), Sha256 = H('d') };

        var r = Detect([Cand("v2026.6.1-ms6", H('d'))], stale);

        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome);
        Assert.Equal(Sha(bytes), r.HashCache!.Sha256);
        Assert.Equal(Mtime, r.HashCache.MtimeUtc);
    }

    [Fact]
    public void Apollo_CacheForAnotherPath_IsNotUsed()
    {
        var bytes = Bytes("path");
        WriteExe(bytes);
        var other = new InstalledExeHash { Path = Path.Combine(_dir, "other", "sunshine.exe"), Size = bytes.Length, MtimeUtc = Mtime, Sha256 = H('d') };

        var r = Detect([Cand("v2026.6.1-ms6", H('d'))], other);

        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome);
        Assert.Equal(Path.GetFullPath(ExePath), r.HashCache!.Path);
    }

    [Fact]
    public void Apollo_AMalformedCacheHash_IsNotUsed()
    {
        var bytes = Bytes("bad-cache");
        WriteExe(bytes);
        var bad = new InstalledExeHash { Path = Path.GetFullPath(ExePath), Size = bytes.Length, MtimeUtc = Mtime, Sha256 = "nope" };

        var r = Detect([Cand("v2026.6.1-ms6", Sha(bytes))], bad);

        Assert.Equal(InstalledSource.ReleaseHash, r.Detection.Installed!.Source);
        Assert.Equal(Sha(bytes), r.HashCache!.Sha256);
    }

    // ── Marker ────────────────────────────────────────────────────────

    private void WriteMarker(string json) => File.WriteAllText(Path.Combine(_dir, "release.json"), json);

    private static string MarkerJson(string tag, string sha) =>
        JsonSerializer.Serialize(new { tag, commit = "abc1234", sunshineSha256 = sha, futureField = 1 });

    [Fact]
    public void Marker_WhoseHashEqualsTheRealExe_IsTrusted_EvenWithNoReleaseInTheTable()
    {
        var bytes = Bytes("ms7");
        WriteExe(bytes);
        WriteMarker(MarkerJson("v2026.6.1-ms7", Sha(bytes).ToUpperInvariant()));

        var r = Detect([Cand("v2026.6.1-ms6", H('a'))]);

        Assert.Equal(DetectionOutcome.Identified, r.Detection.Outcome);
        Assert.Equal(InstalledSource.Marker, r.Detection.Installed!.Source);
        Assert.Equal("2026.6.1-ms7", r.Detection.Installed.Version);
    }

    [Fact]
    public void Marker_WhoseHashDiffers_IsNeverTrustedAsAMarker_ItIsMarkerModified()
    {
        WriteExe(Bytes("replaced after install"));
        WriteMarker(MarkerJson("v2026.6.1-ms7", H('a')));

        var r = Detect([]);

        Assert.Equal(InstalledSource.MarkerModified, r.Detection.Installed!.Source);
        Assert.NotEqual(InstalledSource.Marker, r.Detection.Installed.Source);
        Assert.Equal("2026.6.1-ms7", r.Detection.Version!.Display);
        Assert.Contains("replaced", r.Detection.Installed.Note);
    }

    [Fact]
    public void Marker_Refuted_ButTheExeIsExactlyAnotherRelease_ReportsThatRelease()
    {
        // The marker says ms7; the file is byte for byte ms5. The bytes are the stronger evidence.
        var ms5 = Bytes("ms5 swapped in");
        WriteExe(ms5);
        WriteMarker(MarkerJson("v2026.6.1-ms7", H('a')));

        var r = Detect([Cand("v2026.6.1-ms5", Sha(ms5))]);

        Assert.Equal(InstalledSource.ReleaseHash, r.Detection.Installed!.Source);
        Assert.Equal("2026.6.1-ms5", r.Detection.Installed.Version);
    }

    [Fact]
    public void Marker_Refuted_ButTheSourceCommitMatches_ReportsCommitMatch()
    {
        WriteExe(Bytes("rebuilt from ms6 source"));
        WriteMarker(MarkerJson("v2026.6.1-ms7", H('a')));

        var r = Detect([Cand("v2026.6.1-ms6", H('b'), Ms6Commit)], pe: "2026.6.1.681e36a8.dirty");

        Assert.Equal(InstalledSource.CommitMatch, r.Detection.Installed!.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"tag\":5,\"sunshineSha256\":\"x\"}")]
    [InlineData("{\"tag\":\"v2026.6.1-ms7\"}")]
    [InlineData("{\"tag\":\"v2026.6.1-ms7\",\"sunshineSha256\":\"short\"}")]
    [InlineData("{\"tag\":\"not-a-tag\",\"sunshineSha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")]
    public void Marker_Malformed_IsIgnored_AndTheRestOfTheOrderApplies(string json)
    {
        var bytes = Bytes("ms6 with bad marker");
        WriteExe(bytes);
        WriteMarker(json);

        var r = Detect([Cand("v2026.6.1-ms6", Sha(bytes))]);

        Assert.Equal(InstalledSource.ReleaseHash, r.Detection.Installed!.Source);
    }

    [Fact]
    public void Marker_Oversized_IsIgnored()
    {
        var bytes = Bytes("big marker");
        WriteExe(bytes);
        WriteMarker(MarkerJson("v2026.6.1-ms7", Sha(bytes)).Replace("}", ",\"pad\":\"" + new string('x', 100_000) + "\"}"));

        var r = Detect([]);

        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome);
    }

    [Fact]
    public void Marker_NextToADifferentExe_IsOnlyReadFromTheExesFolder()
    {
        var elsewhere = Path.Combine(_dir, "other");
        Directory.CreateDirectory(elsewhere);
        var bytes = Bytes("other folder");
        File.WriteAllBytes(Path.Combine(elsewhere, "sunshine.exe"), bytes);
        WriteMarker(MarkerJson("v2026.6.1-ms7", Sha(bytes))); // in _dir, not in "other"

        var r = InstalledVersions.DetectApollo(Path.Combine(elsewhere, "sunshine.exe"), [], null, Pe("2026.6.1"));

        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome);
    }

    // ── Commit match ──────────────────────────────────────────────────

    [Fact]
    public void CommitMatch_IsReportedAsCommitMatch_NeverAsTheRelease()
    {
        WriteExe(Bytes("built from ms6 source, not the release"));

        var r = Detect([Cand("v2026.6.1-ms6", H('a'), Ms6Commit), Cand("v2026.6.1-ms5", H('b'), "master")], pe: "2026.6.1.681e36a8.dirty");

        Assert.Equal(DetectionOutcome.Identified, r.Detection.Outcome);
        Assert.Equal(InstalledSource.CommitMatch, r.Detection.Installed!.Source);
        Assert.NotEqual(InstalledSource.ReleaseHash, r.Detection.Installed.Source);
        Assert.NotEqual(InstalledSource.Marker, r.Detection.Installed.Source);
        Assert.Contains("same source commit", r.Detection.Installed.Note);
        Assert.Contains("not that release", r.Detection.Installed.Note);
        Assert.Equal("2026.6.1-ms6", r.Detection.Version!.Display);
    }

    [Theory]
    [InlineData("2026.6.1.681e36a8")]
    [InlineData("2026.6.1.681E36A8.dirty")]
    [InlineData("2026.6.1.681e36a885fa2adc6232152fa099db80bdc0a976")]
    [InlineData("2026.6.1.681e36a")]
    public void CommitMatch_AcceptsTheShapesOfThePeVersion(string pe)
    {
        WriteExe(Bytes("pe shapes " + pe));

        var r = Detect([Cand("v2026.6.1-ms6", H('a'), Ms6Commit)], pe: pe);

        Assert.Equal(InstalledSource.CommitMatch, r.Detection.Installed!.Source);
    }

    [Theory]
    [InlineData("2026.6.1")]                // no commit in the version at all
    [InlineData("2026.6.1.dirty")]
    [InlineData("2026.6.1.0")]              // a numeric 4th part is not a commit
    [InlineData("2026.6.1.12345")]
    [InlineData("2026.6.1.681e36")]         // under 7 digits is too short to mean anything
    [InlineData("2026.6.1.deadbeef")]       // a commit nobody released
    [InlineData("2026.6.1.681e36a8.rc1")]
    [InlineData("")]
    [InlineData(null)]
    public void CommitMatch_NotPossible_IsUnknown(string? pe)
    {
        WriteExe(Bytes("no commit " + pe));

        var r = Detect([Cand("v2026.6.1-ms6", H('a'), Ms6Commit)], pe: pe);

        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome);
    }

    [Fact]
    public void CommitMatch_IgnoresReleasesWhoseTargetIsNotAFullSha()
    {
        WriteExe(Bytes("branch names"));

        // target_commitish is often a branch name; a branch that happens to start with the hex digits
        // must not count, and neither must an abbreviated sha.
        var r = Detect([Cand("v2026.6.1-ms6", H('a'), "681e36a8-branch"), Cand("v2026.6.1-ms5", H('b'), "681e36a8"), Cand("v2026.6.1-ms4", H('c'), Ms6Commit[..39])],
            pe: "2026.6.1.681e36a8");

        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome);
    }

    [Fact]
    public void CommitMatch_SeveralReleasesFromOneCommit_PicksTheHighest()
    {
        WriteExe(Bytes("two tags one commit"));

        var r = Detect([Cand("v2026.6.1-ms9", H('a'), Ms6Commit), Cand("v2026.6.1-ms10", H('b'), Ms6Commit), Cand("v2026.6.1-ms2", H('c'), Ms6Commit)],
            pe: "2026.6.1.681e36a8");

        Assert.Equal("2026.6.1-ms10", r.Detection.Version!.Display);
    }

    [Fact]
    public void ReleaseHash_BeatsCommitMatch()
    {
        var bytes = Bytes("exact bytes of ms5");
        WriteExe(bytes);

        var r = Detect([Cand("v2026.6.1-ms5", Sha(bytes), "master"), Cand("v2026.6.1-ms6", H('a'), Ms6Commit)], pe: "2026.6.1.681e36a8");

        Assert.Equal(InstalledSource.ReleaseHash, r.Detection.Installed!.Source);
        Assert.Equal("2026.6.1-ms5", r.Detection.Version!.Display);
    }

    // ── The PE version is not a release version ───────────────────────

    [Fact]
    public void ThePeVersionAlone_NeverIdentifiesARelease()
    {
        // ms3 to ms6 all carry the PE version 2026.6.1. An exe that says so and matches nothing
        // else must not be treated as any of them, in particular not as the latest.
        WriteExe(Bytes("pe says 2026.6.1"));
        var all = new[] { Cand("v2026.6.1-ms3", H('1')), Cand("v2026.6.1-ms4", H('2')), Cand("v2026.6.1-ms5", H('3')), Cand("v2026.6.1-ms6", H('4')) };

        var r = Detect(all, pe: "2026.6.1");

        Assert.Equal(DetectionOutcome.Unknown, r.Detection.Outcome);
        Assert.Equal(UpdateStatus.UnknownInstalled, ReleaseSelector.Classify(r.Detection.Version, ReleaseVersion.TryParse(UpdateComponent.ApolloVibe, "v2026.6.1-ms6")));
    }

    [Theory]
    [InlineData("2026.6.1", null)]
    [InlineData("2026.6.1.681e36a8.dirty", "681e36a8")]
    [InlineData("2026.6.1.681e36a8", "681e36a8")]
    [InlineData("  2026.6.1.AbCdEf0  ", "AbCdEf0")]
    [InlineData("2026.6.1.dirty", null)]
    [InlineData("7.1.431.-1", null)]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParsePeCommit(string? pe, string? expected)
    {
        Assert.Equal(expected, InstalledVersions.ParsePeCommit(pe));
    }

    [Fact]
    public void RealPeVersionReader_ReadsAnExeWithoutThrowing()
    {
        // The default reader on a real PE file (the test host itself), plus on a non-PE file.
        var host = Environment.ProcessPath!;
        File.Copy(host, ExePath);
        var notPe = Path.Combine(_dir, "plain", "sunshine.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(notPe)!);
        File.WriteAllText(notPe, "not a pe");

        var real = InstalledVersions.DetectApollo(ExePath, [], null);
        var plain = InstalledVersions.DetectApollo(notPe, [], null);

        Assert.Equal(DetectionOutcome.Unknown, real.Detection.Outcome);
        Assert.Equal(DetectionOutcome.Unknown, plain.Detection.Outcome);
    }

    [Fact]
    public void Detection_UsesTheGivenPath_NotTheDefaultInstallLocation()
    {
        // A host whose MultiSeat:ApolloExePath points somewhere else is judged by that file only.
        WriteExe(Bytes("custom location"));

        var r = Detect([]);

        Assert.NotEqual(DetectionOutcome.NotInstalled, r.Detection.Outcome);
        Assert.NotEqual(Path.GetFullPath(MultiSeat.Shared.Constants.DefaultApolloPath), r.HashCache!.Path);
    }

    private static string H(char c) => new(c, 64);
}
