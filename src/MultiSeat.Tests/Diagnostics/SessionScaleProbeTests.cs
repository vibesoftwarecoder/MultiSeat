using System.Text.Json;
using MultiSeat.Service.Diagnostics;
using MultiSeat.Service.Display;
using Xunit;

namespace MultiSeat.Tests.Diagnostics;

/// <summary>
/// Issue #93: the scale a seat reports is the scale MultiSeat asked for, not the one Windows
/// applied. SessionScaleProbe reads the applied one from inside the session. Its Win32 half can
/// only be exercised against whatever desktop the test runs on, so the live test here is a
/// cross-check between independent observations (as the input-desktop probe's is): it fails when
/// the reader and the display configuration disagree, which a reader that returned a constant
/// would not survive on a scaled desktop. The decision half is pure and is tested with
/// constructed readings, including the case that matters: a session whose primary monitor is not
/// the display mstsc scaled.
/// </summary>
public class SessionScaleProbeTests
{
    private static MonitorScale Monitor(
        string name, string adapter, bool primary, int effective, int? stored = null) =>
        new(name, adapter, primary, (uint)(effective * 96 / 100), effective, stored, 1920, 1080);

    private static SessionScaleObservation Observation(params MonitorScale[] monitors) =>
        new(96, 100, monitors, Error: null);

    // ── Live: two independent observations of the same desktop must agree ──────

    [Fact]
    public void ReaderAgreesWithTheDisplayConfigurationOnThisDesktop()
    {
        var observation = SessionScaleProbe.Observe();

        // A desktop with no monitors (a headless agent) has nothing to compare, but it must
        // SAY so rather than return an empty "reading" that evaluates as a match.
        if (observation.Monitors.Length == 0)
        {
            Assert.NotNull(observation.Error);
            Assert.Equal(ScaleVerdict.Unknown, SessionScaleProbe.Evaluate(100, observation));
            return;
        }

        Assert.Null(observation.Error);
        Assert.Single(observation.Monitors, m => m.Primary);

        // Source 1: the DPI a per-monitor-aware process is given. Source 2: the scale the display
        // configuration stores for that display. Two parts of Windows describing one setting.
        foreach (var monitor in observation.Monitors)
        {
            Assert.Contains(monitor.EffectivePercent, SessionScaleProbe.DpiScaleSteps);
            if (monitor.StoredPercent is { } stored)
                Assert.True(stored == monitor.EffectivePercent,
                    $"{monitor.GdiName}: display configuration stores {stored}% but apps are given {monitor.EffectivePercent}%");
        }
        Assert.Empty(observation.Disagreements);

        // Source 3: a separate enumeration of the same displays. Every monitor the reader found
        // must be an active path in the display topology.
        var activePaths = DisplayEnumeratorHelper.EnumerateAllPaths()
            .Where(p => p.Active)
            .Select(p => p.GdiName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in observation.Monitors)
            Assert.Contains(monitor.GdiName, activePaths);

        // The verdict is a function of that reading: asking for what it reports matches, asking
        // for anything else does not.
        var applied = observation.AppliedPercent!.Value;
        Assert.Equal(ScaleVerdict.Match, SessionScaleProbe.Evaluate(applied, observation));
        Assert.Equal(ScaleVerdict.Mismatch, SessionScaleProbe.Evaluate(applied == 100 ? 200 : 100, observation));
    }

    [Fact]
    public void HelperFileHoldsTheSameReadingTheProcessTakes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scale-probe-test-{Guid.NewGuid():N}.json");
        try
        {
            var exit = SessionScaleProbe.RunAndWriteToFile(path);
            var direct = SessionScaleProbe.Observe();

            var parsed = SessionScaleProbe.ParseResult(File.ReadAllText(path));
            Assert.NotNull(parsed);

            // The exit code says whether it is a reading, and so does the file.
            Assert.Equal(exit == 0, parsed!.Error is null);
            Assert.Equal(direct.Monitors.Select(m => (m.GdiName, m.EffectivePercent)),
                         parsed.Monitors.Select(m => (m.GdiName, m.EffectivePercent)));
            Assert.Equal(direct.AppliedPercent, parsed.AppliedPercent);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ParseResultRefusesGarbageInsteadOfInventingAReading()
    {
        Assert.Null(SessionScaleProbe.ParseResult("not json"));
        Assert.Equal(ScaleVerdict.Unknown, SessionScaleProbe.Evaluate(100, null));
    }

    // ── Pure: the arithmetic and the decision ──────────────────────────────────

    [Theory]
    [InlineData(96u, 100)]
    [InlineData(120u, 125)]
    [InlineData(144u, 150)]
    [InlineData(168u, 175)]
    [InlineData(192u, 200)]
    [InlineData(240u, 250)]
    [InlineData(288u, 300)]
    [InlineData(384u, 400)]
    [InlineData(480u, 500)]
    public void DpiConvertsToThePercentageSettingsShows(uint dpi, int expected) =>
        Assert.Equal(expected, SessionScaleProbe.PercentFromDpi(dpi));

    [Theory]
    // Relative to the recommended scale: with a recommended 125% the minimum is one step below it.
    [InlineData(-1, 0, 125)]    // current = recommended
    [InlineData(-1, 1, 150)]    // one step above it
    [InlineData(-1, -1, 100)]   // one step below it
    [InlineData(0, 0, 100)]     // recommended is the first entry
    [InlineData(-4, 0, 200)]    // recommended 200%
    [InlineData(-4, 3, 300)]   // three steps above 200%
    public void RelativeScaleCountsStepsFromTheRecommendedScale(int min, int cur, int expected) =>
        Assert.Equal(expected, SessionScaleProbe.PercentFromRelativeScale(min, cur));

    [Theory]
    [InlineData(0, -1)]
    [InlineData(-1, -3)]
    [InlineData(0, 99)]
    public void RelativeScaleOutsideTheListIsNoReadingRatherThanAWrongOne(int min, int cur) =>
        Assert.Null(SessionScaleProbe.PercentFromRelativeScale(min, cur));

    [Fact]
    public void TheVerdictIsAboutTheRdpDisplayNotWhicheverIsPrimary()
    {
        // After display isolation Apollo's virtual display is primary at 100% while the RDP
        // display, the one mstsc scaled, sits at 200%. Judging on the primary would call a
        // working session broken and recreate it.
        var reading = Observation(
            Monitor(@"\\.\DISPLAY30", "SudoMaker Virtual Display Adapter", primary: true, effective: 100),
            Monitor(@"\\.\DISPLAY31", "Microsoft Remote Display Adapter", primary: false, effective: 200));

        Assert.Equal(200, reading.AppliedPercent);
        Assert.Equal(ScaleVerdict.Match, SessionScaleProbe.Evaluate(200, reading));
        Assert.Equal(ScaleVerdict.Mismatch, SessionScaleProbe.Evaluate(100, reading));
    }

    [Fact]
    public void WithoutAnRdpDisplayThePrimaryMonitorIsTheOneJudged()
    {
        var reading = Observation(
            Monitor(@"\\.\DISPLAY1", "Some GPU", primary: true, effective: 150),
            Monitor(@"\\.\DISPLAY2", "Some GPU", primary: false, effective: 100));

        Assert.Equal(150, reading.AppliedPercent);
    }

    [Fact]
    public void AReadingWithAnErrorNeverEvaluatesAsAMatchOrAMismatch()
    {
        var failed = new SessionScaleObservation(0, 0, [Monitor("x", "", true, 100)], "boom");

        Assert.Null(failed.AppliedPercent);
        Assert.Equal(ScaleVerdict.Unknown, SessionScaleProbe.Evaluate(100, failed));
        Assert.Equal(ScaleVerdict.Unknown, SessionScaleProbe.Evaluate(200, failed));
    }

    [Fact]
    public void StoredAndEffectiveScalesThatDifferAreReportedAndMarkedInTheLog()
    {
        // The display configuration says 200% while apps render at 100%: a scale that was set
        // but not taken up. That is the shape of the reported bug, and it must be visible.
        var reading = Observation(
            Monitor(@"\\.\DISPLAY1", "Microsoft Remote Display Adapter", true, effective: 100, stored: 200),
            Monitor(@"\\.\DISPLAY2", "Other", false, effective: 125, stored: 125));

        var bad = Assert.Single(reading.Disagreements);
        Assert.Equal(@"\\.\DISPLAY1", bad.GdiName);
        Assert.Contains("DIFFERS", reading.Describe());
        Assert.DoesNotContain("DIFFERS", Observation(Monitor("a", "b", true, 100, stored: 100)).Describe());
    }

    [Fact]
    public void ObservationSurvivesTheJsonRoundTripTheHelperUses()
    {
        var reading = Observation(
            Monitor(@"\\.\DISPLAY31", "Microsoft Remote Display Adapter", true, effective: 175, stored: 175));

        var back = SessionScaleProbe.ParseResult(JsonSerializer.Serialize(reading));

        Assert.NotNull(back);
        Assert.Equal(175, back!.AppliedPercent);
        Assert.Equal(175, back.Monitors[0].StoredPercent);
        Assert.True(back.Monitors[0].IsRdpDisplay);
    }
}
