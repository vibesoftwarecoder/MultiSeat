using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

/// <summary>
/// The release-notes parser behind ApolloVibe identification. The fixtures apollovibe-ms3..ms6.json
/// are the real notes of the published releases, fetched with `gh release view --json`.
/// </summary>
public class ApolloHashTableTests
{
    // The sunshine.exe hashes as the real notes print them (upper case), lower-cased here.
    // ms6's notes also carry the hash of the zip, FIRST: b39bdc29...ad5c. That must not be returned.
    private const string Ms3 = "75467310fcabb262e47d707df79c105415d3a5ebcd7ce3f5225180ca14bc9056";
    private const string Ms4 = "ccc693f69f0f7db12ab10fe22858b064b4db699a035753bf1a8dad628ddf3bff";
    private const string Ms5 = "95083822664bca479c895b935b5a83efe0d2960986360cb773e8794cd02cccdf";
    private const string Ms6 = "7e9fa1572579879fa98716c74753da7ec070f0c8209fe5177f2d73cd0c1b33cc";
    private const string Ms6Zip = "b39bdc299988a712fbf5dad68c0847e05e65baf733b55194297c6b2c9374ad5c";

    private static string H(char c) => new(c, 64);

    [Theory]
    [InlineData("apollovibe-ms3.json", Ms3)]
    [InlineData("apollovibe-ms4.json", Ms4)]
    [InlineData("apollovibe-ms5.json", Ms5)]
    [InlineData("apollovibe-ms6.json", Ms6)]
    public void RealReleaseNotes_YieldTheSunshineExeHash(string fixture, string expected)
    {
        Assert.Equal(expected, ApolloHashTable.ExtractSunshineHash(Fixtures.Notes(fixture)));
    }

    [Fact]
    public void RealMs6Notes_NameTheZipFirst_AndTheParserStillTakesTheExe()
    {
        var notes = Fixtures.Notes("apollovibe-ms6.json");

        // The fixture really has both, zip first: otherwise this test would prove nothing.
        Assert.Contains(Ms6Zip, notes, StringComparison.OrdinalIgnoreCase);
        Assert.True(notes.IndexOf(Ms6Zip, StringComparison.OrdinalIgnoreCase) < notes.IndexOf(Ms6, StringComparison.OrdinalIgnoreCase));

        var hash = ApolloHashTable.ExtractSunshineHash(notes);

        Assert.NotEqual(Ms6Zip, hash);
        Assert.Equal(Ms6, hash);
    }

    [Fact]
    public void RealNotes_ToleratedWithCrlfLineEndings()
    {
        var crlf = Fixtures.Notes("apollovibe-ms6.json").Replace("\r\n", "\n").Replace("\n", "\r\n");

        Assert.Equal(Ms6, ApolloHashTable.ExtractSunshineHash(crlf));
    }

    [Fact]
    public void RealNotes_ToleratedInUpperAndLowerCase()
    {
        var notes = Fixtures.Notes("apollovibe-ms6.json");

        Assert.Equal(Ms6, ApolloHashTable.ExtractSunshineHash(notes.ToUpperInvariant()));
        Assert.Equal(Ms6, ApolloHashTable.ExtractSunshineHash(notes.ToLowerInvariant()));
    }

    [Fact]
    public void RealNotes_ThatLackAHash_GiveNoEntry()
    {
        // ms6's notes with the exe's hash line removed: only the zip's hash is left.
        var notes = Fixtures.Notes("apollovibe-ms6.json");
        var cut = notes[..notes.IndexOf("SHA-256 of `sunshine.exe`", StringComparison.Ordinal)];
        Assert.Contains(Ms6Zip, cut, StringComparison.OrdinalIgnoreCase);

        Assert.Null(ApolloHashTable.ExtractSunshineHash(cut));
    }

    [Fact]
    public void NotesWithNoHashAtAll_GiveNoEntry()
    {
        // ms3 to ms6 have all four fixtures; strip every hash from the longest one.
        var notes = System.Text.RegularExpressions.Regex.Replace(Fixtures.Notes("apollovibe-ms6.json"), "[0-9A-Fa-f]{64}", "<removed>");

        Assert.Null(ApolloHashTable.ExtractSunshineHash(notes));
    }

    // ── Shapes of the label and the hash ──────────────────────────────

    [Theory]
    [InlineData("SHA-256 of `sunshine.exe`:\n`{0}`\n")]
    [InlineData("SHA-256 of `sunshine.exe`:\r\n`{0}`\r\n")]
    [InlineData("SHA-256 of sunshine.exe: {0}")]
    [InlineData("sunshine.exe SHA-256: {0}")]
    [InlineData("sunshine.exe (SHA-256): {0}")]
    [InlineData("sunshine.exe: {0}")]
    [InlineData("**sunshine.exe** `{0}`")]
    [InlineData("SHA256 of SUNSHINE.EXE\n\n{0}")]
    [InlineData("- sunshine.exe | {0} |")]
    public void LabelledHash_IsFound_InCommonShapes(string template)
    {
        var hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        Assert.Equal(hash, ApolloHashTable.ExtractSunshineHash(string.Format(template, hash)));
        Assert.Equal(hash, ApolloHashTable.ExtractSunshineHash(string.Format(template, hash.ToUpperInvariant())));
    }

    [Theory]
    // two hashes on one line, exe first and zip first
    [InlineData("sunshine.exe: {E}, apollovibe-windows-x64.zip: {Z}")]
    [InlineData("apollovibe-windows-x64.zip: {Z}, sunshine.exe: {E}")]
    [InlineData("zip {Z} | sunshine.exe {E}")]
    [InlineData("sunshine.exe {E} | zip SHA-256 {Z}")]
    [InlineData("SHA-256 of `apollovibe-windows-x64.zip`:\n`{Z}`\n\nSHA-256 of `sunshine.exe`:\n`{E}`\n")]
    [InlineData("Verify the download. SHA-256 of `sunshine.exe`:\n`{E}`\n\nSHA-256 of `apollovibe-windows-x64.zip`:\n`{Z}`\n")]
    [InlineData("The sunshine.exe in this release changed.\n\nSHA-256 of `apollovibe-windows-x64.zip`: {Z}\nSHA-256 of `sunshine.exe`: {E}")]
    public void TwoHashes_TheExeOneIsChosen_NotTheZip(string template)
    {
        var e = H('e');
        var z = H('2');

        var text = template.Replace("{E}", e).Replace("{Z}", z);

        Assert.Equal(e, ApolloHashTable.ExtractSunshineHash(text));
    }

    [Fact]
    public void AMentionOfTheExeWithoutAHash_DoesNotBorrowTheZipHashThatFollows()
    {
        var z = H('2');

        // "sunshine.exe" appears in prose, then a hash for something that is not named by a file.
        Assert.Null(ApolloHashTable.ExtractSunshineHash($"sunshine.exe changed in this release. SHA-256 of the release zip: {z}"));
        Assert.Null(ApolloHashTable.ExtractSunshineHash($"sunshine.exe now waits for the desktop.\n\nChecksum: {z}"));
        // and one named by its file
        Assert.Null(ApolloHashTable.ExtractSunshineHash($"sunshine.exe is rebuilt.\nSHA-256 of `apollovibe-windows-x64.zip`: {z}"));
    }

    [Fact]
    public void AProseMentionBeforeTheRealLabel_IsSkipped()
    {
        var e = H('e');
        var notes = $"This build keeps sunshine.exe small.\n\nSHA-256 of `sunshine.exe`:\n`{e}`";

        Assert.Equal(e, ApolloHashTable.ExtractSunshineHash(notes));
    }

    [Fact]
    public void ALabelWithoutAnythingAfterIt_GivesNoEntry()
    {
        Assert.Null(ApolloHashTable.ExtractSunshineHash("see sunshine.exe"));
        Assert.Null(ApolloHashTable.ExtractSunshineHash("sunshine.exe"));
        Assert.Null(ApolloHashTable.ExtractSunshineHash("sunshine.exe:"));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(32)]
    [InlineData(128)]
    public void ARunOfHexThatIsNot64Long_IsNotAHash(int length)
    {
        Assert.Null(ApolloHashTable.ExtractSunshineHash($"SHA-256 of `sunshine.exe`:\n`{new string('a', length)}`"));
    }

    [Fact]
    public void NonHexCharacters_AreNotAHash()
    {
        Assert.Null(ApolloHashTable.ExtractSunshineHash("sunshine.exe: " + new string('g', 64)));
        Assert.Null(ApolloHashTable.ExtractSunshineHash("sunshine.exe: " + new string('a', 63) + "z"));
    }

    [Fact]
    public void ALabelFarFromTheHash_IsNotTrusted()
    {
        var far = "SHA-256 of sunshine.exe is below.\n" + new string('\n', 400) + H('a');

        Assert.Null(ApolloHashTable.ExtractSunshineHash(far));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just some prose")]
    public void NothingToParse_GivesNoEntry(string? notes)
    {
        Assert.Null(ApolloHashTable.ExtractSunshineHash(notes));
    }

    [Fact]
    public void HugeNotes_AreHandledQuickly_AndDoNotThrow()
    {
        // 2.8 MB of nothing but the label: every label has to be examined without rescanning the rest.
        var notes = string.Concat(Enumerable.Repeat("sunshine.exe and more text. ", 100_000));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(ApolloHashTable.ExtractSunshineHash(notes));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
    }

    // ── The table ─────────────────────────────────────────────────────

    private static ReleaseCandidate Cand(string tag, string? sha) => new() { Tag = tag, SunshineSha256 = sha };

    [Fact]
    public void Build_MapsEachHashToItsRelease_CaseInsensitively()
    {
        var table = ApolloHashTable.Build([Cand("v2026.6.1-ms5", H('a')), Cand("v2026.6.1-ms6", H('b'))]);

        Assert.Equal("v2026.6.1-ms5", table[H('a')].Tag);
        Assert.Equal("v2026.6.1-ms6", table[new string('B', 64)].Tag);
        Assert.False(table.ContainsKey(H('c')));
    }

    [Fact]
    public void Build_SameBytesInTwoReleases_ReportsTheHigherVersion_InAnyOrder()
    {
        var a = ApolloHashTable.Build([Cand("v2026.6.1-ms5", H('a')), Cand("v2026.6.1-ms6", H('a'))]);
        var b = ApolloHashTable.Build([Cand("v2026.6.1-ms6", H('a')), Cand("v2026.6.1-ms5", H('a'))]);
        var c = ApolloHashTable.Build([Cand("v2026.6.1-ms9", H('a')), Cand("v2026.6.1-ms10", H('a'))]);

        Assert.Equal("v2026.6.1-ms6", a[H('a')].Tag);
        Assert.Equal("v2026.6.1-ms6", b[H('a')].Tag);
        Assert.Equal("v2026.6.1-ms10", c[H('a')].Tag);
    }

    [Fact]
    public void Build_SkipsCandidatesWithoutAUsableHashOrTag()
    {
        var table = ApolloHashTable.Build(
        [
            Cand("v2026.6.1-ms6", null),
            Cand("v2026.6.1-ms5", "short"),
            Cand("v2026.6.1-ms4", new string('z', 64)),
            Cand("not-a-version", H('a')),
            Cand("v2026.6.1-ms3", H('d')),
        ]);

        Assert.Equal([H('d')], table.Keys);
    }

    [Fact]
    public void Build_FromTheRealReleases_HasOneEntryPerRelease()
    {
        var releases = new[] { "apollovibe-ms3.json", "apollovibe-ms4.json", "apollovibe-ms5.json", "apollovibe-ms6.json" }
            .Select((f, i) => Cand($"v2026.6.1-ms{i + 3}", ApolloHashTable.ExtractSunshineHash(Fixtures.Notes(f))))
            .ToList();

        var table = ApolloHashTable.Build(releases);

        Assert.Equal(4, table.Count);
        Assert.Equal("v2026.6.1-ms6", table[Ms6].Tag);
        Assert.Equal("v2026.6.1-ms3", table[Ms3].Tag);
        Assert.False(table.ContainsKey(Ms6Zip)); // the zip's hash identifies nothing installed
    }

    [Fact]
    public void IsSha256_AcceptsOnly64HexDigits()
    {
        Assert.True(ApolloHashTable.IsSha256(H('a')));
        Assert.True(ApolloHashTable.IsSha256(H('F')));
        Assert.False(ApolloHashTable.IsSha256(null));
        Assert.False(ApolloHashTable.IsSha256(""));
        Assert.False(ApolloHashTable.IsSha256(new string('a', 63)));
        Assert.False(ApolloHashTable.IsSha256(new string('a', 64) + "\n"));
        Assert.False(ApolloHashTable.IsSha256(new string('g', 64)));
    }
}
