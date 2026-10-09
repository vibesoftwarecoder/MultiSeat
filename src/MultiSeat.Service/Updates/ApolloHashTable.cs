using System.Text.RegularExpressions;

namespace MultiSeat.Service.Updates;

/// <summary>
/// The ApolloVibe hash table: the SHA-256 of each published release's <c>sunshine.exe</c>,
/// read from the release notes, so an installed binary can be matched to a release by its bytes.
///
/// A match means the identical file, so it cannot be a false positive; a miss says nothing
/// (a hand-built exe simply is not in the table). The notes are free text written by a person,
/// so parsing is deliberately narrow and a failure to find a hash means "no entry", never a guess.
///
/// The real notes carry a hash for the release ZIP as well, usually right before the exe's:
/// <code>
/// SHA-256 of `apollovibe-windows-x64.zip`:
/// `B39BDC29...`
///
/// SHA-256 of `sunshine.exe`:
/// `7E9FA157...`
/// </code>
/// so the parser anchors on the label <c>sunshine.exe</c> and takes the first 64-hex value AFTER it,
/// stopping at the next file name (so a zip hash that follows is not taken either).
/// </summary>
public static class ApolloHashTable
{
    private const int MaxNotesLength = 512 * 1024;

    private static readonly Regex Label = new(@"sunshine\.exe", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A file name with an extension that a release note would name. Ends the text a label owns.
    private static readonly Regex AnyFileName = new(
        @"[A-Za-z0-9_.\-]+\.(?:zip|exe|msi|7z|dll|json|ps1|txt)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const int MaxLabelReach = 300;
    private const int MaxLabelsTried = 200;
    // "sunshine.exe SHA-256: <hash>" puts the word right after the label; that is part of the label.
    private const int LabelSuffixAllowance = 16;

    private static readonly Regex HashStatement = new(@"sha-?\s?256|hash|checksum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Sha256Hex = new(
        @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The lowercase <c>sunshine.exe</c> SHA-256 named in <paramref name="notes"/>, or null.
    /// Tolerates CRLF, upper or lower case, backticks, the hash on the next line, and a
    /// differently labelled hash for another file in the same text. If several labels each have
    /// a hash, the first wins.
    /// </summary>
    public static string? ExtractSunshineHash(string? notes)
    {
        if (string.IsNullOrEmpty(notes)) return null;
        if (notes.Length > MaxNotesLength) notes = notes[..MaxNotesLength];

        var tried = 0;
        foreach (Match label in Label.Matches(notes))
        {
            // Notes that name sunshine.exe hundreds of times are not release notes.
            if (++tried > MaxLabelsTried) break;

            var start = label.Index + label.Length;

            // The label owns the text after it, up to the first of: the next file name (so a zip
            // hash that follows is not borrowed), the next "SHA-256" or "hash" statement (so a
            // hash for something not named by a file is not borrowed), or 300 characters.
            // Every search is confined to that window, so the work per label does not grow with
            // the length of the notes.
            var windowEnd = Math.Min(notes.Length, start + MaxLabelReach);
            var end = windowEnd;

            var nextFile = AnyFileName.Match(notes, start, windowEnd - start);
            if (nextFile.Success) end = nextFile.Index;

            var statementFrom = start + LabelSuffixAllowance;
            if (statementFrom < windowEnd)
            {
                var nextStatement = HashStatement.Match(notes, statementFrom, windowEnd - statementFrom);
                if (nextStatement.Success && nextStatement.Index < end) end = nextStatement.Index;
            }

            var hash = Sha256Hex.Match(notes, start, end - start);
            if (hash.Success) return hash.Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Hash to the highest-version candidate that published it. If the same bytes appear in two
    /// releases, the later one is reported so an identical binary is never called "behind".
    /// Candidates whose tag does not parse are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, ReleaseCandidate> Build(IEnumerable<ReleaseCandidate> candidates)
    {
        var table = new Dictionary<string, (ReleaseCandidate Candidate, ReleaseVersion Version)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (!IsSha256(c.SunshineSha256)) continue;
            var v = ReleaseVersion.TryParse(UpdateComponent.ApolloVibe, c.Tag);
            if (v is null) continue;

            if (!table.TryGetValue(c.SunshineSha256!, out var existing) || v.CompareTo(existing.Version) > 0)
                table[c.SunshineSha256!] = (c, v);
        }
        return table.ToDictionary(kv => kv.Key, kv => kv.Value.Candidate, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsSha256(string? s) => s is { Length: 64 } && Sha256Hex.IsMatch(s);
}
