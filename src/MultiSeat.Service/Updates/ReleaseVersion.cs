using System.Globalization;
using System.Text.RegularExpressions;

namespace MultiSeat.Service.Updates;

/// <summary>
/// A release version parsed under a closed, per-component grammar and compared as a numeric
/// tuple. Anything that does not match the grammar is "not a version" (<see cref="TryParse"/>
/// returns null); nothing here throws on garbage input.
///
/// Why not strings or a SemVer library: <c>0.6.9</c> sorts above <c>0.6.19</c> as a string, and
/// under SemVer the pre-release identifiers <c>ms10</c> and <c>ms9</c> compare as text, which puts
/// ms10 BELOW ms9. ApolloVibe's <c>-msN</c> and the legacy <c>-multiseat.N</c> are therefore parsed
/// to an integer and compared as the fourth tuple element. Only MultiSeat has real SemVer
/// pre-releases (<c>0.7.0-rc1</c> sorts below <c>0.7.0</c>). Build metadata (<c>+sha</c>) is
/// kept for display and ignored when comparing.
///
/// Grammars (after trimming, an optional lowercase <c>v</c>):
///   MultiSeat      major.minor.patch[-prerelease][+build]
///   ApolloVibe     year.monthday.patch-ms{N}  or  year.monthday.patch-multiseat.{N}
///   MoonlightVibe  2 to 4 dot-separated numbers, missing parts count as 0
/// Numbers are parsed as <see cref="long"/>; a number that overflows rejects the whole version.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private const int MaxLength = 128;
    private const int ComparedParts = 4;

    private static readonly Regex MultiSeatGrammar = new(
        @"^v?([0-9]+)\.([0-9]+)\.([0-9]+)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex ApolloGrammar = new(
        @"^v?([0-9]+)\.([0-9]+)\.([0-9]+)-(ms|multiseat\.)([0-9]+)\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex MoonlightGrammar = new(
        @"^v?([0-9]+(?:\.[0-9]+){1,3})\z",
        RegexOptions.CultureInvariant);

    private readonly long[] _parts;
    private readonly string[] _preRelease;

    private ReleaseVersion(UpdateComponent component, long[] parts, string display, string[] preRelease, string? build)
    {
        Component = component;
        _parts = parts;
        Display = display;
        _preRelease = preRelease;
        Build = build;
    }

    public UpdateComponent Component { get; }

    /// <summary>The version without a <c>v</c> prefix or build metadata, as shown to a person: <c>0.6.19</c>, <c>2026.6.1-ms6</c>, <c>6.3.9</c>.</summary>
    public string Display { get; }

    /// <summary>MultiSeat build metadata after <c>+</c> (usually a commit), or null. Never compared.</summary>
    public string? Build { get; }

    /// <summary>True for a MultiSeat pre-release such as <c>0.7.0-rc1</c>.</summary>
    public bool IsPreRelease => _preRelease.Length > 0;

    /// <summary>The numeric key, padded to four elements.</summary>
    public IReadOnlyList<long> Key => _parts;

    /// <summary>Parse <paramref name="text"/> under the grammar of <paramref name="component"/>, or return null.</summary>
    public static ReleaseVersion? TryParse(UpdateComponent component, string? text)
    {
        if (text is null) return null;
        var s = text.Trim();
        if (s.Length == 0 || s.Length > MaxLength) return null;

        return component switch
        {
            UpdateComponent.MultiSeat => ParseMultiSeat(s),
            UpdateComponent.ApolloVibe => ParseApollo(s),
            UpdateComponent.MoonlightVibe => ParseMoonlight(s),
            _ => null,
        };
    }

    private static ReleaseVersion? ParseMultiSeat(string s)
    {
        var m = MultiSeatGrammar.Match(s);
        if (!m.Success) return null;

        if (!TryNumbers(m, 1, 3, out var nums)) return null;

        string[] pre = [];
        if (m.Groups[4].Success)
        {
            pre = m.Groups[4].Value.Split('.');
            // "1.0.0-" and "1.0.0-a..b": an empty identifier is not a pre-release.
            if (pre.Any(p => p.Length == 0)) return null;
        }

        var build = m.Groups[5].Success ? m.Groups[5].Value : null;
        var display = $"{nums[0]}.{nums[1]}.{nums[2]}" + (pre.Length > 0 ? "-" + string.Join('.', pre) : "");
        return new ReleaseVersion(UpdateComponent.MultiSeat, Pad(nums), display, pre, build);
    }

    private static ReleaseVersion? ParseApollo(string s)
    {
        var m = ApolloGrammar.Match(s);
        if (!m.Success) return null;
        if (!TryNumbers(m, 1, 3, out var nums)) return null;
        if (!long.TryParse(m.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;

        // Both spellings feed N; the legacy one keeps its own spelling for display.
        var suffix = m.Groups[4].Value == "ms" ? $"ms{n}" : $"multiseat.{n}";
        return new ReleaseVersion(UpdateComponent.ApolloVibe, [nums[0], nums[1], nums[2], n],
            $"{nums[0]}.{nums[1]}.{nums[2]}-{suffix}", [], null);
    }

    private static ReleaseVersion? ParseMoonlight(string s)
    {
        var m = MoonlightGrammar.Match(s);
        if (!m.Success) return null;

        var pieces = m.Groups[1].Value.Split('.');
        var nums = new long[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (!long.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return null;
        }
        return new ReleaseVersion(UpdateComponent.MoonlightVibe, Pad(nums), string.Join('.', nums), [], null);
    }

    private static bool TryNumbers(Match m, int firstGroup, int count, out long[] nums)
    {
        nums = new long[count];
        for (var i = 0; i < count; i++)
        {
            if (!long.TryParse(m.Groups[firstGroup + i].Value, NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                return false;
        }
        return true;
    }

    private static long[] Pad(long[] nums)
    {
        var padded = new long[ComparedParts];
        Array.Copy(nums, padded, Math.Min(nums.Length, ComparedParts));
        return padded;
    }

    /// <summary>
    /// Numeric tuple order; a MultiSeat pre-release sorts below the same triple. Versions of
    /// different components are not comparable and throw <see cref="ArgumentException"/>: that is
    /// a programming error, not bad input.
    /// </summary>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        if (other.Component != Component)
            throw new ArgumentException("Versions of different components cannot be compared.", nameof(other));

        for (var i = 0; i < ComparedParts; i++)
        {
            var c = _parts[i].CompareTo(other._parts[i]);
            if (c != 0) return c;
        }
        return ComparePreRelease(_preRelease, other._preRelease);
    }

    // SemVer section 11: no pre-release outranks any pre-release; otherwise identifier by
    // identifier, numbers numerically and below text, text in ASCII order, fewer fields lower.
    private static int ComparePreRelease(string[] a, string[] b)
    {
        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0) return 1;
        if (b.Length == 0) return -1;

        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNum = IsDigits(a[i]);
            var bNum = IsDigits(b[i]);
            int c;
            if (aNum && bNum) c = CompareDigitStrings(a[i], b[i]);
            else if (aNum) c = -1;
            else if (bNum) c = 1;
            else c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    private static bool IsDigits(string s) => s.All(ch => ch is >= '0' and <= '9');

    // Digit strings can be longer than a long, so compare by magnitude without converting.
    private static int CompareDigitStrings(string a, string b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
    }

    public bool Equals(ReleaseVersion? other) =>
        other is not null && other.Component == Component && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is ReleaseVersion v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(Component, _parts[0], _parts[1], _parts[2], _parts[3],
        string.Join('.', _preRelease.Select(p => IsDigits(p) ? p.TrimStart('0') : p)));

    public override string ToString() => Display;

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
}
