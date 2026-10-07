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
            Assert.True(lines.Length >= 2, "expected at least one sample plus the summary line");

            // Every line but the last parses as a sample with the index walking up from 0.
            for (var i = 0; i < lines.Length - 1; i++)
            {
                using var doc = JsonDocument.Parse(lines[i]);
                Assert.Equal(i, doc.RootElement.GetProperty("Index").GetInt32());
                Assert.True(doc.RootElement.TryGetProperty("ActiveDisplays", out _));

                var desktops = doc.RootElement.GetProperty("DesktopsInStation");
                Assert.Equal(JsonValueKind.Array, desktops.ValueKind);
                Assert.True(desktops.GetArrayLength() > 0, "expected at least one desktop in the window station");
            }

            using var last = JsonDocument.Parse(lines[^1]);
            Assert.Equal("summary", last.RootElement.GetProperty("Kind").GetString());
            Assert.Equal(lines.Length - 1, last.RootElement.GetProperty("SampleCount").GetInt32());
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
}
