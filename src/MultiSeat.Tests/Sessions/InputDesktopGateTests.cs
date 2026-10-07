using MultiSeat.Service.Sessions;
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// Issue #96: the gate must not call the desktop ready on one lucky open. The reporter's host
/// opened it, then lost it again about two seconds later, and recovered only after ~121 s.
/// A fake clock drives the loop so no test sleeps.
/// </summary>
public class InputDesktopGateTests
{
    private static InputDesktopGateResult Run(Func<TimeSpan, bool> openAt, double timeoutS, double stableS)
    {
        var t = TimeSpan.Zero;
        return InputDesktopGate.Wait(
            () => openAt(t),
            TimeSpan.FromSeconds(timeoutS), TimeSpan.FromSeconds(stableS),
            sleep: d => t += d, clock: () => t);
    }

    [Fact]
    public void ReadyOnlyAfterTheDesktopStaysOpenForTheStableWindow()
    {
        var r = Run(_ => true, timeoutS: 60, stableS: 3);
        Assert.Equal(InputDesktopGateOutcome.Ready, r.Outcome);
        Assert.Equal(3000, r.ElapsedMs);
    }

    [Fact]
    public void ABriefOpenFollowedByDenialDoesNotCountAsReady()
    {
        // Open for 0-2 s, denied 2-121 s, open again from 121 s: the shape reported in #96.
        var r = Run(t => t.TotalSeconds < 2 || t.TotalSeconds >= 121, timeoutS: 300, stableS: 3);
        Assert.Equal(InputDesktopGateOutcome.Ready, r.Outcome);
        Assert.True(r.ElapsedMs >= 124_000, $"ready too early: {r.ElapsedMs}");
    }

    [Fact]
    public void TimesOutWhenTheDesktopNeverOpens()
    {
        var r = Run(_ => false, timeoutS: 10, stableS: 3);
        Assert.Equal(InputDesktopGateOutcome.TimedOut, r.Outcome);
        Assert.True(r.ElapsedMs >= 10_000);
    }

    [Fact]
    public void ZeroStableWindowIsReadyOnFirstOpen()
    {
        var r = Run(_ => true, timeoutS: 10, stableS: 0);
        Assert.Equal(InputDesktopGateOutcome.Ready, r.Outcome);
        Assert.Equal(1, r.Attempts);
    }

    [Theory]
    [InlineData("ready 3000 13", true)]
    [InlineData("timeout 180000 721", false)]
    public void ParsesTheHelpersResultFile(string text, bool ready)
        => Assert.Equal(ready, InputDesktopGate.ParseResult(text)!.Outcome == InputDesktopGateOutcome.Ready);

    [Theory]
    [InlineData("")]
    [InlineData("ready")]
    [InlineData("maybe 1 2")]
    public void RejectsMalformedResults(string text)
        => Assert.Null(InputDesktopGate.ParseResult(text));
}
