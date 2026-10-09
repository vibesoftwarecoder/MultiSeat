using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

/// <summary>
/// The update-check cache. It is disposable, so the contract is: whatever is wrong with the file,
/// loading yields an empty state and never throws; saving is atomic and restricted; a failed
/// save never throws either.
/// </summary>
public class UpdateStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "multiseat-updstate-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "update-check.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private readonly List<string> _warnings = [];
    private readonly List<string> _restricted = [];

    private UpdateStateStore Store(string? path = null, bool restrictSucceeds = true) =>
        new(path ?? FilePath,
            (p, _) => { _restricted.Add(p); return restrictSucceeds; },
            _warnings.Add);

    private static string Sha(char c) => new(c, 64);

    private static UpdateState Sample()
    {
        var s = new UpdateState();
        var apollo = s.GetOrAdd(UpdateComponent.ApolloVibe);
        apollo.ETag = "W/\"bcd412d8\"";
        apollo.CheckedAt = new DateTimeOffset(2026, 10, 9, 14, 2, 11, TimeSpan.Zero);
        apollo.Baseline = "2026.6.1-ms6";
        apollo.LastError = "HTTP 503";
        apollo.Backoff = new BackoffState { ConsecutiveFailures = 2, NotBefore = new DateTimeOffset(2026, 10, 9, 15, 0, 0, TimeSpan.Zero) };
        apollo.Candidates =
        [
            new ReleaseCandidate { Tag = "v2026.6.1-ms6", PublishedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), TargetCommit = "681e36a885fa2adc6232152fa099db80bdc0a976", SunshineSha256 = Sha('a') },
            new ReleaseCandidate { Tag = "v2026.6.1-ms5", TargetCommit = "master" },
        ];
        s.GetOrAdd(UpdateComponent.MultiSeat).ETag = "\"strong\"";
        s.ApolloExeHash = new InstalledExeHash { Path = @"C:\Program Files\ApolloVibe\sunshine.exe", Size = 23_456_789, MtimeUtc = new DateTime(2026, 10, 1, 12, 30, 0, 123, DateTimeKind.Utc).AddTicks(4567), Sha256 = Sha('b') };
        return s;
    }

    // ── Round trip ────────────────────────────────────────────────────

    [Fact]
    public void SaveThenLoad_RoundTripsEverything()
    {
        var store = Store();
        var original = Sample();

        Assert.True(store.Save(original));
        var loaded = store.Load();

        var a = loaded.Repos["apollovibe"];
        Assert.Equal("W/\"bcd412d8\"", a.ETag);
        Assert.Equal(original.Repos["apollovibe"].CheckedAt, a.CheckedAt);
        Assert.Equal("2026.6.1-ms6", a.Baseline);
        Assert.Equal("HTTP 503", a.LastError);
        Assert.Equal(2, a.Backoff.ConsecutiveFailures);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 15, 0, 0, TimeSpan.Zero), a.Backoff.NotBefore);
        Assert.Equal(2, a.Candidates.Count);
        Assert.Equal("v2026.6.1-ms6", a.Candidates[0].Tag);
        Assert.Equal("681e36a885fa2adc6232152fa099db80bdc0a976", a.Candidates[0].TargetCommit);
        Assert.Equal(Sha('a'), a.Candidates[0].SunshineSha256);
        Assert.Null(a.Candidates[1].SunshineSha256);
        Assert.Equal("\"strong\"", loaded.Repos["multiseat"].ETag);
        Assert.False(loaded.Repos.ContainsKey("moonlightvibe"));

        var h = loaded.ApolloExeHash!;
        Assert.Equal(original.ApolloExeHash!.Path, h.Path);
        Assert.Equal(23_456_789, h.Size);
        Assert.Equal(original.ApolloExeHash.MtimeUtc.Ticks, h.MtimeUtc.Ticks); // sub-millisecond precision survives
        Assert.Equal(Sha('b'), h.Sha256);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Save_CreatesAMissingDirectory()
    {
        Assert.False(Directory.Exists(_dir));

        Assert.True(Store().Save(new UpdateState()));

        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Save_WritesCamelCaseJsonWithoutABom_AndTheSchema()
    {
        Store().Save(Sample());

        var bytes = File.ReadAllBytes(FilePath);
        Assert.NotEqual(0xEF, bytes[0]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"schema\": 1", text);
        Assert.Contains("\"apolloExeHash\"", text);
        Assert.Contains("\"eTag\"", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_AlwaysStampsTheCurrentSchema()
    {
        var store = Store();
        var s = Sample();
        s.Schema = 99;

        store.Save(s);

        Assert.Equal(UpdateState.CurrentSchema, store.Load().Schema);
    }

    // ── Atomic write and permissions ──────────────────────────────────

    [Fact]
    public void Save_LeavesNoTempFileBehind_AndReplacesTheOldFile()
    {
        var store = Store();
        store.Save(Sample());
        var second = new UpdateState();
        second.GetOrAdd(UpdateComponent.MoonlightVibe).ETag = "\"second\"";

        store.Save(second);

        Assert.Equal(["update-check.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
        var loaded = store.Load();
        Assert.Equal("\"second\"", loaded.Repos["moonlightvibe"].ETag);
        Assert.False(loaded.Repos.ContainsKey("apollovibe"));
    }

    [Fact]
    public async Task Save_IsAtomic_AReaderNeverSeesAHalfWrittenFile()
    {
        // A direct File.WriteAllText truncates first and streams after, so a reader that opens the
        // file in between sees a short or empty file. A rename never does. The state is made big
        // (about 3 MB) so the window of a direct write is wide.
        var big = new UpdateState();
        var repo = big.GetOrAdd(UpdateComponent.ApolloVibe);
        for (var i = 0; i < 20_000; i++)
            repo.Candidates.Add(new ReleaseCandidate { Tag = $"v2026.6.1-ms{i}", TargetCommit = new string('a', 40), SunshineSha256 = Sha('a') });
        var store = Store();
        Assert.True(store.Save(big));
        var fullLength = new FileInfo(FilePath).Length;

        using var stop = new CancellationTokenSource();
        var torn = 0;
        var reads = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    reads++;
                    if (ms.Length != fullLength) torn++;
                }
                catch (IOException) { /* the file was being replaced at that instant: not a torn read */ }
                catch (UnauthorizedAccessException) { }
            }
        });

        for (var i = 0; i < 40; i++) store.Save(big);
        stop.Cancel();
        await reader;

        Assert.True(reads > 0);
        Assert.Equal(0, torn);
    }

    [Fact]
    public void Save_RestrictsTheFile_AfterEveryWrite()
    {
        var store = Store();

        store.Save(Sample());
        store.Save(Sample());

        // Each write is a rename that creates a new file object, so each needs its own restriction.
        Assert.Equal([FilePath, FilePath], _restricted);
    }

    [Fact]
    public void Save_FailureToRestrict_StillSucceeds_AndIsReported()
    {
        var warned = new List<string>();
        var store = new UpdateStateStore(FilePath, (_, onError) => { onError(new UnauthorizedAccessException("no")); return false; }, warned.Add);

        Assert.True(store.Save(Sample()));

        Assert.Contains(warned, w => w.Contains("restrict permissions", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_WithTheRealHelper_LeavesOnlySystemAndAdministrators()
    {
        var store = new UpdateStateStore(FilePath); // default: SecureFile.TryRestrictToSystemAndAdmins

        Assert.True(store.Save(Sample()));

        var acl = new FileInfo(FilePath).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var sids = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                      .Select(r => (SecurityIdentifier)r.IdentityReference).Distinct().ToList();
        Assert.Contains(system, sids);
        Assert.Contains(admins, sids);
        Assert.Equal(2, sids.Count);

        // Cleanup needs admin rights once the ACL is tight; Dispose is best effort.
    }

    [Fact]
    public void Save_WhenTheTargetCannotBeWritten_ReturnsFalse_AndDoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(FilePath); // a directory where the file should be
        var store = Store();

        Assert.False(store.Save(Sample()));
        Assert.Contains(_warnings, w => w.Contains("could not be written"));
        Assert.Empty(Directory.GetFiles(_dir)); // and the temp file was cleaned up
    }

    [Fact]
    public void Save_WhenTheDirectoryCannotBeCreated_ReturnsFalse()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var store = Store(Path.Combine(blocker, "sub", "update-check.json"));

        Assert.False(store.Save(Sample()));
    }

    [Fact]
    public void Save_ThatThrowsFromTheWarningCallback_IsStillSwallowed()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(FilePath);
        var store = new UpdateStateStore(FilePath, (_, _) => true, _ => throw new InvalidOperationException("logger broke"));

        Assert.False(store.Save(Sample()));
    }

    // ── A bad file is ignored, never an error ─────────────────────────

    private UpdateState LoadFromText(string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, text, new UTF8Encoding(false));
        return Store().Load();
    }

    private static void AssertEmpty(UpdateState s)
    {
        Assert.Empty(s.Repos);
        Assert.Null(s.ApolloExeHash);
        Assert.Equal(UpdateState.CurrentSchema, s.Schema);
    }

    [Fact]
    public void Load_MissingFile_IsEmpty_WithoutAWarning()
    {
        AssertEmpty(Store().Load());
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Load_MissingDirectory_IsEmpty()
    {
        AssertEmpty(Store(Path.Combine(_dir, "a", "b", "c.json")).Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("{\"schema\": 1")]
    [InlineData("not json at all")]
    [InlineData("<html></html>")]
    [InlineData("\0\0\0\0")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"schema\"")]
    public void Load_CorruptJson_IsIgnored(string text)
    {
        AssertEmpty(LoadFromText(text));
        Assert.NotEmpty(_warnings);
    }

    [Fact]
    public void Load_TruncatedRealFile_IsIgnored()
    {
        Store().Save(Sample());
        var full = File.ReadAllText(FilePath);

        AssertEmpty(LoadFromText(full[..(full.Length / 2)]));
    }

    [Fact]
    public void Load_BinaryGarbage_IsIgnored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 % 251)).ToArray());

        AssertEmpty(Store().Load());
    }

    [Theory]
    [InlineData("{}")]                                            // no schema
    [InlineData("{\"schema\": 2}")]                               // a newer file is not ours to interpret
    [InlineData("{\"schema\": 0}")]
    [InlineData("{\"schema\": -1}")]
    [InlineData("{\"schema\": 1.5}")]
    [InlineData("{\"schema\": \"1\"}")]
    [InlineData("{\"schema\": null}")]
    [InlineData("{\"schema\": 99, \"repos\": {\"multiseat\": {\"eTag\": \"\\\"x\\\"\"}}}")]
    public void Load_WrongOrFutureSchema_IsIgnored_EvenWhenTheRestLooksValid(string text)
    {
        AssertEmpty(LoadFromText(text));
        Assert.Contains(_warnings, w => w.Contains("schema"));
    }

    [Theory]
    [InlineData("{\"schema\":1,\"repos\":[]}")]
    [InlineData("{\"schema\":1,\"repos\":\"x\"}")]
    [InlineData("{\"schema\":1,\"repos\":{\"multiseat\":\"x\"}}")]
    [InlineData("{\"schema\":1,\"repos\":{\"multiseat\":{\"candidates\":\"x\"}}}")]
    [InlineData("{\"schema\":1,\"repos\":{\"multiseat\":{\"candidates\":[1,2]}}}")]
    [InlineData("{\"schema\":1,\"repos\":{\"multiseat\":{\"checkedAt\":\"yesterday\"}}}")]
    [InlineData("{\"schema\":1,\"repos\":{\"multiseat\":{\"checkedAt\":12}}}")]
    [InlineData("{\"schema\":1,\"repos\":{\"multiseat\":{\"backoff\":{\"consecutiveFailures\":\"many\"}}}}")]
    [InlineData("{\"schema\":1,\"apolloExeHash\":\"x\"}")]
    [InlineData("{\"schema\":1,\"apolloExeHash\":{\"size\":\"big\"}}")]
    [InlineData("{\"schema\":1,\"apolloExeHash\":{\"mtimeUtc\":\"never\"}}")]
    public void Load_WrongTypes_AreIgnored(string text)
    {
        AssertEmpty(LoadFromText(text));
    }

    [Fact]
    public void Load_UnknownFields_AreTolerated_AndKnownOnesKept()
    {
        var s = LoadFromText("""
            { "schema": 1, "futureTopLevel": { "x": [1,2,3] },
              "repos": { "multiseat": { "eTag": "\"abc\"", "somethingNew": true, "candidates": [ { "tag": "v0.6.19", "extra": 1 } ] },
                         "someFutureComponent": { "eTag": "\"zzz\"" } } }
            """);

        Assert.Equal("\"abc\"", s.Repos["multiseat"].ETag);
        Assert.Equal("v0.6.19", Assert.Single(s.Repos["multiseat"].Candidates).Tag);
        Assert.False(s.Repos.ContainsKey("someFutureComponent"));
    }

    [Fact]
    public void Load_TrailingCommasAndExtraWhitespace_AreTolerated()
    {
        var s = LoadFromText("{\n \"schema\": 1,\n \"repos\": { \"multiseat\": { \"eTag\": \"\\\"a\\\"\", }, },\n}\n");

        Assert.Equal("\"a\"", s.Repos["multiseat"].ETag);
    }

    [Fact]
    public void Load_AFileOverTheSizeLimit_IsIgnoredWithoutBeingParsed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"schema\":1,\"pad\":\"" + new string('x', 5 * 1024 * 1024) + "\"}");

        AssertEmpty(Store().Load());
        Assert.Contains(_warnings, w => w.Contains("too large"));
    }

    [Fact]
    public void Load_DeeplyNestedJson_IsIgnored()
    {
        AssertEmpty(LoadFromText("{\"schema\":1,\"x\":" + new string('[', 3000) + new string(']', 3000) + "}"));
    }

    [Fact]
    public void Load_ALockedFile_IsIgnored()
    {
        Store().Save(Sample());
        using var hold = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        AssertEmpty(Store().Load());
    }

    // ── What is kept from a damaged-but-parseable file ────────────────

    [Fact]
    public void Load_DropsAnETagThatCouldInjectAHeader()
    {
        var s = LoadFromText("""{ "schema": 1, "repos": { "multiseat": { "eTag": "\"a\"\r\nAuthorization: Bearer x" }, "apollovibe": { "eTag": "W/\"ok\"" } } }""");

        Assert.Null(s.Repos["multiseat"].ETag);
        Assert.Equal("W/\"ok\"", s.Repos["apollovibe"].ETag);
    }

    [Fact]
    public void Load_DropsBadCandidatesAndBadHashes_KeepsGoodOnes()
    {
        var s = LoadFromText($$"""
            { "schema": 1, "repos": { "apollovibe": { "candidates": [
                { "tag": "v2026.6.1-ms6", "sunshineSha256": "{{Sha('C')}}" },
                { "tag": "", "sunshineSha256": "{{Sha('a')}}" },
                { "tag": "v2026.6.1-ms5", "sunshineSha256": "not-a-hash" },
                null,
                { "tag": "{{new string('t', 600)}}" } ] } },
              "apolloExeHash": { "path": "C:\\x\\sunshine.exe", "size": 5, "mtimeUtc": "2026-10-01T00:00:00Z", "sha256": "short" } }
            """);

        var c = s.Repos["apollovibe"].Candidates;
        Assert.Equal(["v2026.6.1-ms6", "v2026.6.1-ms5"], c.Select(x => x.Tag));
        Assert.Equal(Sha('c'), c[0].SunshineSha256); // normalised to lower case
        Assert.Null(c[1].SunshineSha256);
        Assert.Null(s.ApolloExeHash); // a hash that is not 64 hex digits is not a cache entry
    }

    [Fact]
    public void Load_CapsTheNumberOfCandidates_AndOverlongStrings()
    {
        var many = string.Join(",", Enumerable.Range(0, 300).Select(i => $"{{\"tag\":\"v0.0.{i}\"}}"));
        var s = LoadFromText($$"""{ "schema": 1, "repos": { "multiseat": { "lastError": "{{new string('e', 5000)}}", "candidates": [{{many}}] } } }""");

        Assert.Equal(100, s.Repos["multiseat"].Candidates.Count);
        Assert.Equal(512, s.Repos["multiseat"].LastError!.Length);
    }

    [Fact]
    public void Load_ClampsTheFailureCount()
    {
        var s = LoadFromText("""{ "schema": 1, "repos": { "multiseat": { "backoff": { "consecutiveFailures": 2000000000 } }, "apollovibe": { "backoff": { "consecutiveFailures": -4 } } } }""");

        Assert.Equal(1000, s.Repos["multiseat"].Backoff.ConsecutiveFailures);
        Assert.Equal(0, s.Repos["apollovibe"].Backoff.ConsecutiveFailures);
    }

    // ── What the file must not contain ────────────────────────────────

    [Fact]
    public void SavedFile_HoldsNoReleaseNotesNoUrlsNoSecretsNoHostName()
    {
        var s = Sample();
        Store().Save(s);

        var text = File.ReadAllText(FilePath);

        Assert.DoesNotContain("body", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("://", text);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, text.Replace("Program Files", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_RejectsAnEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new UpdateStateStore(""));
        Assert.Throws<ArgumentException>(() => new UpdateStateStore("   "));
    }

    [Fact]
    public async Task ConcurrentSaves_AllSucceed_AndLeaveOneCompleteFile()
    {
        var store = Store();
        var results = new System.Collections.Concurrent.ConcurrentBag<bool>();
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 15; n++)
            {
                var s = Sample();
                s.GetOrAdd(UpdateComponent.MultiSeat).Baseline = $"writer-{i}-{n}";
                results.Add(store.Save(s));
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(120, results.Count);
        Assert.DoesNotContain(false, results);
        Assert.Single(Directory.GetFiles(_dir));
        Assert.StartsWith("writer-", store.Load().Repos["multiseat"].Baseline);
        Assert.Equal("W/\"bcd412d8\"", store.Load().Repos["apollovibe"].ETag);
    }
}
