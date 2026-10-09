using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

/// <summary>
/// The version grammar and comparison. Every row here is a way a naive string or SemVer compare
/// gets a real tag wrong; the numbers in the comments are the cases of the update-check design
/// (section 3.2).
/// </summary>
public class ReleaseVersionTests
{
    private const UpdateComponent MS = UpdateComponent.MultiSeat;
    private const UpdateComponent AV = UpdateComponent.ApolloVibe;
    private const UpdateComponent MV = UpdateComponent.MoonlightVibe;

    private static ReleaseVersion V(UpdateComponent c, string s) =>
        ReleaseVersion.TryParse(c, s) ?? throw new Xunit.Sdk.XunitException($"'{s}' did not parse as {c}");

    // expected: sign of Compare(a, b)
    [Theory]
    // 1: 0.6.9 is BELOW 0.6.19 (a string compare says the opposite)
    [InlineData(MS, "0.6.9", "0.6.19", -1)]
    [InlineData(MS, "0.9.0", "0.10.0", -1)]
    [InlineData(MS, "1.0.0", "0.99.99", 1)]
    // 2: the v prefix and the +sha build do not matter
    [InlineData(MS, "v0.6.19", "0.6.19+b7e6d9539", 0)]
    [InlineData(MS, "v0.6.19", "v0.6.19", 0)]
    // 3: ms10 is above ms9 (SemVer would compare the identifiers as text)
    [InlineData(AV, "v2026.6.1-ms10", "v2026.6.1-ms9", 1)]
    [InlineData(AV, "v2026.6.1-ms2", "v2026.6.1-ms10", -1)]
    [InlineData(AV, "v2026.6.1-ms99", "v2026.6.1-ms100", -1)]
    // 5: the date triple outranks N
    [InlineData(AV, "v2026.7.1-ms1", "v2026.6.1-ms9", 1)]
    [InlineData(AV, "v2026.6.2-ms1", "v2026.6.1-ms9", 1)]
    [InlineData(AV, "v2027.1.1-ms1", "v2026.12.31-ms50", 1)]
    // legacy spelling feeds N too
    [InlineData(AV, "v2026.6.1-multiseat.1", "v2026.6.1-ms2", -1)]
    [InlineData(AV, "v2026.6.1-ms1", "v2026.6.1-multiseat.1", 0)]
    [InlineData(AV, "v2026.4.30-multiseat.3", "v2026.4.30-multiseat.10", -1)]
    [InlineData(AV, "v2026.5.15-multiseat.1", "v2026.4.30-multiseat.4", 1)]
    // 6: a MultiSeat pre-release sorts below the same triple
    [InlineData(MS, "0.7.0-rc1", "0.7.0", -1)]
    [InlineData(MS, "0.7.0-rc1", "0.6.99", 1)]
    [InlineData(MS, "0.7.0-rc1", "0.7.0-rc2", -1)]
    [InlineData(MS, "0.7.0-rc.2", "0.7.0-rc.10", -1)]
    [InlineData(MS, "0.7.0-alpha", "0.7.0-beta", -1)]
    [InlineData(MS, "0.7.0-alpha", "0.7.0-alpha.1", -1)]
    [InlineData(MS, "0.7.0-1", "0.7.0-alpha", -1)]
    [InlineData(MS, "0.7.0-2", "0.7.0-10", -1)]
    // 7: equal versions with different build metadata are equal
    [InlineData(MS, "0.6.19+aaaaaaaaa", "0.6.19+bbbbbbbbb", 0)]
    // MoonlightVibe: numeric tuple of 2 to 4 parts, missing parts are 0
    [InlineData(MV, "v6.3.9", "v6.3.10", -1)]
    [InlineData(MV, "6.3", "6.3.0", 0)]
    [InlineData(MV, "6.3.0.0", "6.3", 0)]
    [InlineData(MV, "6.3.9.1", "6.3.9", 1)]
    [InlineData(MV, "6.10.0", "6.9.9", 1)]
    // 10: leading zeros are numbers, big numbers are fine until they overflow a long
    [InlineData(MS, "0.06.19", "0.6.19", 0)]
    [InlineData(MS, "v1.99999999999.0", "v1.2.0", 1)]
    public void Compare_IsNumericTupleOrder(UpdateComponent c, string a, string b, int expected)
    {
        var va = V(c, a);
        var vb = V(c, b);

        Assert.Equal(expected, Math.Sign(va.CompareTo(vb)));
        Assert.Equal(-expected, Math.Sign(vb.CompareTo(va)));
        Assert.Equal(expected == 0, va.Equals(vb));
        Assert.Equal(expected < 0, va < vb);
        Assert.Equal(expected > 0, va > vb);
        if (expected == 0) Assert.Equal(va.GetHashCode(), vb.GetHashCode());
    }

    // 4, 9 and the rest of the grammar: these are "not a version", never an exception
    [Theory]
    // 4: a bare ApolloVibe CalVer is not a real tag; it is ignored rather than ranked wrongly
    [InlineData(AV, "v2026.6.1")]
    [InlineData(AV, "2026.6.1")]
    [InlineData(AV, "v2026.6.1-ms")]
    [InlineData(AV, "v2026.6.1-msX")]
    [InlineData(AV, "v2026.6.1-rc1")]
    [InlineData(AV, "v2026.6.1-ms6-extra")]
    [InlineData(AV, "v2026.6-ms6")]
    // pre-release test builds
    [InlineData(AV, "test-no-priority-hack-2026-09-06")]
    [InlineData(AV, "debug-probe-instrumented-2026-09-06")]
    // 9: legacy and mismatched MoonlightVibe tags
    [InlineData(MV, "moonlightvibe-win-6.3.1-efabf3")]
    [InlineData(MV, "moonlightvibe-mac-6.2.2-14707a")]
    [InlineData(MV, "v6.1.0-multiseat.1")]
    [InlineData(MV, "v6")]
    [InlineData(MV, "v6.3.9.1.2")]
    [InlineData(MV, "v6.3.x")]
    [InlineData(MV, "v6..3")]
    // MultiSeat
    [InlineData(MS, "0.6")]
    [InlineData(MS, "0.6.19.1")]
    [InlineData(MS, "v")]
    [InlineData(MS, "V0.6.19")]
    [InlineData(MS, "vv0.6.19")]
    [InlineData(MS, "0.6.19-")]
    [InlineData(MS, "0.6.19-a..b")]
    [InlineData(MS, "0.6.19+")]
    [InlineData(MS, "0.6.19 rc1")]
    [InlineData(MS, "0.6.19\n+x")]
    [InlineData(MS, "0.6.-1")]
    [InlineData(MS, "-0.6.1")]
    [InlineData(MS, "0.6.1e3")]
    // 10: overflow of a long, in every position
    [InlineData(MS, "99999999999999999999.0.0")]
    [InlineData(MS, "0.0.9223372036854775808")]
    [InlineData(AV, "v2026.6.1-ms99999999999999999999")]
    [InlineData(MV, "6.99999999999999999999")]
    // not ASCII digits: \d would have accepted these
    [InlineData(MS, "٣.٢.١")]
    // empty, whitespace, null, junk
    [InlineData(MS, "")]
    [InlineData(MS, "   ")]
    [InlineData(MS, "garbage")]
    [InlineData(MS, "latest")]
    [InlineData(MS, null)]
    [InlineData(AV, null)]
    [InlineData(MV, null)]
    public void TryParse_OutsideTheGrammar_ReturnsNullAndDoesNotThrow(UpdateComponent c, string? text)
    {
        Assert.Null(ReleaseVersion.TryParse(c, text));
    }

    [Fact]
    public void TryParse_ExtremelyLongInput_ReturnsNull()
    {
        Assert.Null(ReleaseVersion.TryParse(MS, "0.0." + new string('9', 100_000)));
        Assert.Null(ReleaseVersion.TryParse(MS, "0.0.1-" + new string('a', 100_000)));
    }

    [Fact]
    public void TryParse_LongestValidNumber_Parses()
    {
        var v = V(MS, "0.0." + long.MaxValue);
        Assert.Equal(long.MaxValue, v.Key[2]);
        Assert.True(V(MS, "0.0." + long.MaxValue) > V(MS, "0.0.9223372036854775806"));
    }

    [Theory]
    [InlineData(MS, "  v0.6.19\r\n", "0.6.19")]
    [InlineData(MS, "0.6.19+b7e6d9539", "0.6.19")]
    [InlineData(MS, "v0.7.0-rc1", "0.7.0-rc1")]
    [InlineData(MS, "0.7.0-rc.1+abc.def", "0.7.0-rc.1")]
    [InlineData(AV, "v2026.6.1-ms6", "2026.6.1-ms6")]
    [InlineData(AV, "v2026.4.30-multiseat.3", "2026.4.30-multiseat.3")]
    [InlineData(MV, "v6.3.9", "6.3.9")]
    [InlineData(MV, "6.3", "6.3")]
    public void Display_DropsPrefixAndBuild(UpdateComponent c, string tag, string expected)
    {
        Assert.Equal(expected, V(c, tag).Display);
    }

    [Fact]
    public void Build_KeepsTheCommitForDisplay()
    {
        var v = V(MS, "0.6.19+b7e6d9539");
        Assert.Equal("b7e6d9539", v.Build);
        Assert.Null(V(MS, "0.6.19").Build);
    }

    [Fact]
    public void IsPreRelease_OnlyForMultiSeatSuffix()
    {
        Assert.True(V(MS, "0.7.0-rc1").IsPreRelease);
        Assert.False(V(MS, "0.7.0").IsPreRelease);
        Assert.False(V(AV, "v2026.6.1-ms6").IsPreRelease);
    }

    [Fact]
    public void Compare_AcrossComponents_IsAProgrammingError()
    {
        Assert.Throws<ArgumentException>(() => V(MS, "1.0.0").CompareTo(V(MV, "1.0.0")));
        Assert.False(V(MS, "1.0.0").Equals(V(MV, "1.0.0")));
    }

    [Fact]
    public void Compare_WithNull_IsGreater()
    {
        Assert.True(V(MS, "1.0.0").CompareTo(null) > 0);
    }

    [Fact]
    public void SortingARealTagList_PutsTheHighestLast()
    {
        // The order GitHub lists them in is creation order, not version order.
        var tags = new[] { "v2026.6.1-ms6", "v2026.6.1-ms10", "v2026.6.1-ms9", "v2026.6.1-multiseat.1", "v2026.5.15-multiseat.1", "v2026.7.1-ms1" };
        var sorted = tags.Select(t => V(AV, t)).OrderBy(v => v).Select(v => v.Display).ToArray();
        Assert.Equal(
            ["2026.5.15-multiseat.1", "2026.6.1-multiseat.1", "2026.6.1-ms6", "2026.6.1-ms9", "2026.6.1-ms10", "2026.7.1-ms1"],
            sorted);
    }
}
