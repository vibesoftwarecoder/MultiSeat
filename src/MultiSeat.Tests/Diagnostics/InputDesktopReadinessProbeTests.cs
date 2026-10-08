using System.Text.Json;
using MultiSeat.Service.Diagnostics;
using Xunit;

namespace MultiSeat.Tests.Diagnostics;

/// <summary>
/// The probe's Win32 calls (OpenInputDesktop, QueryDisplayConfig) are not meaningfully assertable
/// off a real seat session — a CI agent's own desktop answers them, not a seat's — so these pin
/// what IS true anywhere: every run produces one well-formed JSON line per sample plus a summary
/// line, and nothing in the loop throws when a sample comes back empty-handed (CaptureEndpoint-
/// InspectorTests and KeepaliveDesktopTests draw the same line for the same reason).
/// </summary>
public class InputDesktopReadinessProbeTests
{
    [Fact]
    public void TakeSampleNeverThrowsAndProducesAWellFormedRecord()
    {
        // Whatever this build agent's desktop answers, the record shape must hold: a sample is
        // either a successful open with no error, or a failed one that actually carries a Win32
        // error code — never both, and never neither.
        var sample = InputDesktopReadinessProbe.TakeSample(DateTime.UtcNow, 0);

        Assert.Equal(0, sample.Index);
        Assert.True(sample.ElapsedMs >= 0);
        if (sample.OpenInputDesktopSucceeded)
        {
            Assert.Equal(0, sample.Win32Error);
        }
        else
        {
            Assert.NotEqual(0, sample.Win32Error);
            Assert.Null(sample.DesktopName);
        }
        Assert.NotNull(sample.ActiveDisplays);

        // Direct observation, not inference: this build agent's own window station has at least
        // one desktop (it is running on one right now), and the shape holds for every entry -
        // "denied" and "has windows" are mutually exclusive, never both and never neither.
        Assert.NotNull(sample.DesktopsInStation);
        Assert.NotEmpty(sample.DesktopsInStation);
        foreach (var desktop in sample.DesktopsInStation)
        {
            Assert.False(string.IsNullOrEmpty(desktop.DesktopName));
            if (desktop.EnumerationSucceeded)
            {
                Assert.False(desktop.EnumerationDenied);
                Assert.Null(desktop.Win32ErrorIfDenied);
                Assert.NotNull(desktop.Windows);
            }
            else
            {
                Assert.True(desktop.EnumerationDenied);
                Assert.NotNull(desktop.Win32ErrorIfDenied);
                Assert.Empty(desktop.Windows);
            }
        }

        // Cross-check the two observations against each other: if OpenInputDesktop just
        // succeeded and named a desktop, that exact name must also appear in the independent
        // window-station enumeration - they are two different Win32 calls describing the same
        // real object, and they had better agree.
        if (sample.OpenInputDesktopSucceeded && sample.DesktopName is { } openedName)
        {
            Assert.Contains(sample.DesktopsInStation, d => d.DesktopName == openedName);
        }
    }

    [Fact]
    public void RunAndWriteToFileProducesOneLinePerSamplePlusASummary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idrp-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            var exit = InputDesktopReadinessProbe.RunAndWriteToFile(path, seconds: 1);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(path));

            var lines = File.ReadAllLines(path);
            Assert.True(lines.Length >= 3, "expected the hook line, at least one sample, and the summary line");

            var docs = lines.Select(l => JsonDocument.Parse(l)).ToList();
            try
            {
                string Kind(JsonDocument d) => d.RootElement.GetProperty("Kind").GetString()!;

                // First line says whether the desktop-switch hook installed; last is the summary.
                Assert.Equal("hook", Kind(docs[0]));
                Assert.Equal("summary", Kind(docs[^1]));
                var hookInstalled = docs[0].RootElement.GetProperty("Installed").GetBoolean();

                // Samples walk up from index 0, in order, with the desktop listing present.
                var samples = docs.Where(d => Kind(d) == "sample").ToList();
                Assert.NotEmpty(samples);
                for (var i = 0; i < samples.Count; i++)
                {
                    Assert.Equal(i, samples[i].RootElement.GetProperty("Index").GetInt32());
                    Assert.True(samples[i].RootElement.TryGetProperty("ActiveDisplays", out _));

                    var desktops = samples[i].RootElement.GetProperty("DesktopsInStation");
                    Assert.Equal(JsonValueKind.Array, desktops.ValueKind);
                    Assert.True(desktops.GetArrayLength() > 0, "expected at least one desktop in the window station");
                }

                // Every other line is a desktop-switch event; the summary must agree with the file.
                var events = docs.Where(d => Kind(d) == "desktop-switch").ToList();
                Assert.Equal(docs.Count, 2 + samples.Count + events.Count);
                var summary = docs[^1].RootElement;
                Assert.Equal(samples.Count, summary.GetProperty("SampleCount").GetInt32());
                Assert.Equal(events.Count, summary.GetProperty("EventCount").GetInt32());
                Assert.Equal(hookInstalled, summary.GetProperty("HookInstalled").GetBoolean());
            }
            finally
            {
                docs.ForEach(d => d.Dispose());
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void SampleIntervalIsFineEnoughToCatchATransitionWithoutFloodingTheFile()
    {
        // The reporter's own fixed-delay testing used whole seconds (0/1/3/5/10s); sampling any
        // coarser than that would miss exactly the kind of transition this probe exists to catch.
        Assert.True(InputDesktopReadinessProbe.SampleInterval <= TimeSpan.FromSeconds(1));
        Assert.True(InputDesktopReadinessProbe.SampleInterval >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void ReadInputDesktopAgreesWithTheSampleAndWithTheStationEnumeration()
    {
        // Two routes to the same fact: the helper the hook thread uses, and the full sample.
        // On a quiet agent the input desktop does not move between the two calls, so they must
        // agree; and if it opened, its name must be one the window-station enumeration lists.
        var direct = InputDesktopReadinessProbe.ReadInputDesktop();
        var sample = InputDesktopReadinessProbe.TakeSample(DateTime.UtcNow, 0);

        if (direct.Opened != sample.OpenInputDesktopSucceeded) return; // desktop moved between calls
        Assert.Equal(sample.DesktopName, direct.Name);
        Assert.Equal(sample.Win32Error, direct.Win32Error);
        if (direct.Opened)
            Assert.Contains(InputDesktopReadinessProbe.EnumerateDesktopsAndWindows(), d => d.DesktopName == direct.Name);
    }

    private static ProbeEventRecorder NewRecorder(
        List<string> lines, List<DateTime> clock,
        Func<(bool, int, string?)> input, Func<DesktopObservation[]> desktops)
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var i = 0;
        clock.AddRange([start.AddMilliseconds(1500), start.AddMilliseconds(2750)]);
        return new ProbeEventRecorder(lines.Add, start, () => clock[i++], input, desktops);
    }

    [Fact]
    public void ARecordedSwitchCarriesTheTimestampTheInputDesktopAndTheWindowsOnIt()
    {
        var lines = new List<string>();
        var windows = new[]
        {
            new DesktopObservation("Default", false, 5, []),
            new DesktopObservation("Winlogon", true, null, [new DesktopWindow(4242, "consent", "Credential Dialog Xaml Host")]),
        };
        // Access denied to the input desktop: the case this probe exists to explain.
        var rec = NewRecorder(lines, [], () => (false, 5, null), () => windows);

        rec.OnSwitch(eventTimeMs: 987654);

        Assert.Equal(1, rec.Count);
        var line = Assert.Single(lines);
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        Assert.Equal("desktop-switch", r.GetProperty("Kind").GetString());
        Assert.Equal(0, r.GetProperty("EventIndex").GetInt32());
        Assert.Equal(1500, r.GetProperty("ElapsedMs").GetDouble());
        Assert.Equal(987654u, r.GetProperty("EventTimeMs").GetUInt32());
        Assert.False(r.GetProperty("OpenInputDesktopSucceeded").GetBoolean());
        Assert.Equal(5, r.GetProperty("Win32Error").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("InputDesktopName").ValueKind);
        var d = r.GetProperty("DesktopsInStation");
        Assert.Equal(2, d.GetArrayLength());
        Assert.Equal("Winlogon", d[1].GetProperty("DesktopName").GetString());
        Assert.Equal("consent", d[1].GetProperty("Windows")[0].GetProperty("ProcessName").GetString());
    }

    [Fact]
    public void SuccessiveSwitchesGetIncreasingIndexesAndTheOpenedDesktopName()
    {
        var lines = new List<string>();
        var rec = NewRecorder(lines, [], () => (true, 0, "Winlogon"), () => []);

        rec.OnSwitch(1);
        rec.OnSwitch(2);

        Assert.Equal(2, rec.Count);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal(1, second.RootElement.GetProperty("EventIndex").GetInt32());
        Assert.Equal(2750, second.RootElement.GetProperty("ElapsedMs").GetDouble());
        Assert.Equal("Winlogon", second.RootElement.GetProperty("InputDesktopName").GetString());
    }

    [Fact]
    public void ARecorderNeverThrowsFromInsideTheCallbackAndStillWritesWhatItCan()
    {
        var lines = new List<string>();
        var rec = NewRecorder(lines, [], () => (true, 0, "Default"),
            () => throw new InvalidOperationException("enumeration blew up"));

        rec.OnSwitch(1); // must not throw

        var line = Assert.Single(lines); // the event is still on record, with an empty listing
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(0, doc.RootElement.GetProperty("DesktopsInStation").GetArrayLength());

        var broken = NewRecorder([], [], () => throw new InvalidOperationException("no desktop"), () => []);
        broken.OnSwitch(2); // must not throw either
    }
}
