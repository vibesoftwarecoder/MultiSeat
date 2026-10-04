using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Input;
using Xunit;

namespace MultiSeat.Tests.Input;

/// <summary>
/// <see cref="HidHideConfigurator.ReconcileExcludedPhysicalPads"/> — the inverse of the per-seat
/// jail: confine an operator-named PHYSICAL controller to the CONSOLE's own session instead of a
/// seat's, so it is invisible to every seat (issue #92, RageMC: a real controller plugged into the
/// main PC was detected inside a seat's game).
///
/// The pure matching and decision logic is tested directly, the same way
/// <c>AnchorRelaunchOrderTests</c> drives <c>SessionHealthCheck.RelaunchExitedAnchorAsync</c> with
/// fakes rather than a live session — there is no HidHideCLI.exe on a build agent, so anything
/// that would actually shell out to it (writing a rule, reading the live blacklist) is outside
/// what can be verified here, exactly as it already is for <c>CloakForSession</c> and
/// <c>PreWriteRules</c>, which have no direct unit coverage either.
/// </summary>
public class HidHideExcludedPadTests
{
    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        { }
    }

    private static HidHideDevice Pad(string hid, string xusb) => new()
    {
        DeviceInstancePath = hid,
        BaseContainerDeviceInstancePath = xusb,
        FriendlyName = "Controller (XBOX 360 For Windows)",
    };

    // A real, existing file so HidHideCli.IsAvailable (a plain File.Exists check) reads true
    // without ever actually invoking anything through cmd.exe — every test here either never
    // reaches a CLI call (the no-op / skip paths) or only exercises code before one.
    private static readonly string ExistingHarmlessPath = typeof(HidHideExcludedPadTests).Assembly.Location;

    private static HidHideConfigurator MakeConfigurator(
        MultiSeatOptions options, Func<int> consoleSessionIdProvider) =>
        new(new NullLogger<HidHideConfigurator>(), Options.Create(options), consoleSessionIdProvider);

    // ── Matching: identity against either node (AttributePadTo's pattern, no emulated filter) ──

    [Fact]
    public void MatchConfiguredPhysicalPad_MatchesViaTheHidNode()
    {
        var pad = Pad(@"HID\VID_045E&PID_028E&IG_00\3&8968588&0&0000", @"USB\VID_045E&PID_028E\01");

        var match = HidHideConfigurator.MatchConfiguredPhysicalPad(
            @"HID\VID_045E&PID_028E&IG_00\3&8968588&0&0000", [pad]);

        Assert.Same(pad, match);
    }

    [Fact]
    public void MatchConfiguredPhysicalPad_MatchesViaTheXusbNode()
    {
        // This is the node XInput actually reads — configuring it must work exactly as well as
        // configuring the HID node.
        var pad = Pad(@"HID\VID_045E&PID_028E&IG_00\3&8968588&0&0000", @"USB\VID_045E&PID_028E\01");

        var match = HidHideConfigurator.MatchConfiguredPhysicalPad(@"USB\VID_045E&PID_028E\01", [pad]);

        Assert.Same(pad, match);
    }

    [Fact]
    public void MatchConfiguredPhysicalPad_IsCaseInsensitive()
    {
        var pad = Pad(@"HID\VID_045E&PID_028E&IG_00\3&8968588&0&0000", @"USB\VID_045E&PID_028E\01");

        var match = HidHideConfigurator.MatchConfiguredPhysicalPad(@"usb\vid_045e&pid_028e\01", [pad]);

        Assert.Same(pad, match);
    }

    [Fact]
    public void MatchConfiguredPhysicalPad_ReturnsNullWhenNoPresentDeviceMatches()
    {
        var pad = Pad(@"HID\VID_045E&PID_028E&IG_00\3&8968588&0&0000", @"USB\VID_045E&PID_028E\01");

        var match = HidHideConfigurator.MatchConfiguredPhysicalPad(@"USB\VID_1234&PID_5678\01", [pad]);

        Assert.Null(match);
    }

    [Fact]
    public void MatchConfiguredPhysicalPad_DoesNotRequireAnEmulatedPad()
    {
        // Unlike AttributePadTo's identity leg, this path is FOR physical controllers on purpose
        // — there is no IsEmulatedPad filter to pass. A device whose parent is a real USB bus
        // (not ROOT\...) must still match.
        var physical = Pad(@"HID\VID_054C&PID_09CC\7&1234&0&0000", @"HID\VID_054C&PID_09CC\7&1234&0&0000");

        var match = HidHideConfigurator.MatchConfiguredPhysicalPad(
            @"HID\VID_054C&PID_09CC\7&1234&0&0000", [physical]);

        Assert.Same(physical, match);
    }

    // ── Planning: the pure decision behind ReconcileOneExcludedPad ──────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(5)]
    public void PlanExcludedPad_DeviceAbsent_IsNeverWritten(int? lastSessionId)
    {
        // The key behavior under test, and the one that differs from the seat's PreWriteRules:
        // an absent device never produces a write, however it was last confined.
        var plan = HidHideConfigurator.PlanExcludedPad(device: null, lastSessionId, consoleSessionId: 9);

        Assert.Equal(HidHideConfigurator.ExcludedPadPlan.DeviceNotPresent, plan);
    }

    [Fact]
    public void PlanExcludedPad_PresentWithNoPriorRule_WritesNew()
    {
        var pad = Pad(@"HID\...", @"USB\...");

        var plan = HidHideConfigurator.PlanExcludedPad(pad, lastConfinedSessionId: null, consoleSessionId: 7);

        Assert.Equal(HidHideConfigurator.ExcludedPadPlan.WriteNew, plan);
    }

    [Fact]
    public void PlanExcludedPad_PresentAndAlreadyConfinedToTheLiveSession_DoesNothing()
    {
        var pad = Pad(@"HID\...", @"USB\...");

        var plan = HidHideConfigurator.PlanExcludedPad(pad, lastConfinedSessionId: 7, consoleSessionId: 7);

        Assert.Equal(HidHideConfigurator.ExcludedPadPlan.AlreadyCurrent, plan);
    }

    [Fact]
    public void PlanExcludedPad_ConsoleSessionChanged_RewritesForTheNewSession()
    {
        // Fast user switch / logoff-logon: the console session that was correct a tick ago no
        // longer is, even though the controller never moved.
        var pad = Pad(@"HID\...", @"USB\...");

        var plan = HidHideConfigurator.PlanExcludedPad(pad, lastConfinedSessionId: 5, consoleSessionId: 7);

        Assert.Equal(HidHideConfigurator.ExcludedPadPlan.RewriteForNewSession, plan);
    }

    [Fact]
    public void WriteNewPlan_WouldProduceRulesForBothNodes()
    {
        // What ReconcileOneExcludedPad does once PlanExcludedPad says WriteNew: build rules via
        // the exact same ConfineAll the seat path uses, so both the HID and the XUSB node are
        // covered — a rule on only one leaves the pad visible to XInput in every seat.
        var pad = Pad(@"HID\VID_045E&PID_028E&IG_00\3&8968588&0&0000", @"USB\VID_045E&PID_028E\01");

        var plan = HidHideConfigurator.PlanExcludedPad(pad, lastConfinedSessionId: null, consoleSessionId: 7);
        Assert.Equal(HidHideConfigurator.ExcludedPadPlan.WriteNew, plan);

        var rules = HidHideSessionJail.ConfineAll(pad, 7);

        Assert.Equal(2, rules.Count);
        Assert.Contains(@"HID\VID_045E&PID_028E&IG_00\3&8968588&0&0000!7", rules);
        Assert.Contains(@"USB\VID_045E&PID_028E\01!7", rules);
    }

    // ── ResetOnStartup must sweep our rules exactly like a seat's ───────────────────────────

    [Fact]
    public void ExcludedPadRules_AreRecognisedByResetOnStartupsStaleRuleFilter()
    {
        // ResetOnStartup only releases entries where HidHideSessionJail.Split(...).SessionId is
        // not null (see HidHideConfigurator.ResetOnStartup). Our rules are built with the exact
        // same HidHideSessionJail.Confine/ConfineAll the seat path uses, so this is the same
        // guarantee, not a parallel one that could drift.
        var pad = Pad(@"HID\VID_045E&PID_028E&IG_00\3&8968588&0&0000", @"USB\VID_045E&PID_028E\01");

        foreach (var rule in HidHideSessionJail.ConfineAll(pad, 7))
        {
            var (_, sessionId) = HidHideSessionJail.Split(rule);
            Assert.NotNull(sessionId);
        }
    }

    // ── The feature as a whole: empty config is a complete no-op ────────────────────────────

    [Fact]
    public void ReconcileExcludedPhysicalPads_EmptyConfig_NeverResolvesTheConsoleSession()
    {
        var options = new MultiSeatOptions
        {
            ExcludedPhysicalPadDevicePaths = [], // default — the feature must be inert
            HidHideCliPath = ExistingHarmlessPath,
        };

        var calls = 0;
        var configurator = MakeConfigurator(options, () => { calls++; return 5; });

        configurator.ReconcileExcludedPhysicalPads();

        Assert.Equal(0, calls);
    }

    [Fact]
    public void ReconcileExcludedPhysicalPads_CliUnavailable_NeverResolvesTheConsoleSessionEither()
    {
        var options = new MultiSeatOptions
        {
            ExcludedPhysicalPadDevicePaths = [@"USB\VID_045E&PID_028E\01"],
            HidHideCliPath = @"C:\nonexistent\HidHideCLI.exe", // File.Exists is false
        };

        var calls = 0;
        var configurator = MakeConfigurator(options, () => { calls++; return 5; });

        configurator.ReconcileExcludedPhysicalPads(); // must not throw, must not touch a CLI

        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReconcileExcludedPhysicalPads_NoActiveConsoleSession_NeverTouchesTheCli(int sentinel)
    {
        // WTSGetActiveConsoleSessionId can report "no session" (e.g. nobody logged in at the
        // console, or the 0xFFFFFFFF/-1 sentinel). HidHideSessionJail.Confine throws for
        // sessionId <= 0, so this has to be caught before the device list is even read, not
        // after a device happens to not match.
        //
        // There is no fake HidHideCLI.exe to inject here, so "never touches the CLI" is proven
        // by timing rather than a call count: HidHideCli enforces an ~800ms minimum gap and
        // shells out to cmd.exe for every real Read/Write. A guard that skipped this logging
        // step but still called ListGamingDevices() would make this test take proportionally
        // longer — measured at ~950ms when this guard was deliberately removed during mutation
        // testing, against ~1-5ms when it is in place.
        var options = new MultiSeatOptions
        {
            ExcludedPhysicalPadDevicePaths = [@"USB\VID_045E&PID_028E\01"],
            HidHideCliPath = ExistingHarmlessPath, // reads "available" without ever being invoked
        };

        var configurator = MakeConfigurator(options, () => sentinel);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ex = Record.Exception(() => configurator.ReconcileExcludedPhysicalPads());
        stopwatch.Stop();

        Assert.Null(ex);
        Assert.True(stopwatch.ElapsedMilliseconds < 400,
            $"took {stopwatch.ElapsedMilliseconds}ms — HidHide's CLI gate (800ms) implies it was invoked");
    }
}
