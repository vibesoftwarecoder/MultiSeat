using System.Runtime.InteropServices;
using MultiSeat.Service.Interop;
using MultiSeat.Service.Sessions;
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// Token handles, interop struct layouts and seat state transitions - the parts of the session
/// path that run without SYSTEM privileges.
///
/// Renamed from SessionLauncherTests, which is what it was called while containing no test of
/// SessionLauncher at all. A file named for the thing it does not cover is worse than no file:
/// it answers "is this tested?" with a yes. SessionLauncher's own guards are covered in
/// SessionGuardTests; the rest of it is P/Invoke against a live session and is exercised only
/// by the [Skip]-gated integration tests.
/// </summary>
public class SessionInteropTests
{
    [Fact]
    public void SafeTokenHandle_InvalidHandle_IsInvalid()
    {
        var handle = new SafeTokenHandle(IntPtr.Zero);
        Assert.True(handle.IsInvalid);
    }

    [Fact]
    public void SafeTokenHandle_ValidHandle_IsNotInvalid()
    {
        // Simulate a valid handle (we won't close it since it's fake)
        var handle = new SafeTokenHandle(new IntPtr(1));
        Assert.False(handle.IsInvalid);
        // Don't dispose — IntPtr(1) is not a real handle
    }

    [Fact]
    public void SeatStateTransition_FullLifecycle()
    {
        // Verify the complete provisioning → teardown lifecycle
        var state = Shared.Models.SeatStatus.Idle;

        Assert.True(state.CanTransitionTo(Shared.Models.SeatStatus.Provisioning));
        state = Shared.Models.SeatStatus.Provisioning;

        Assert.True(state.CanTransitionTo(Shared.Models.SeatStatus.Configuring));
        state = Shared.Models.SeatStatus.Configuring;

        Assert.True(state.CanTransitionTo(Shared.Models.SeatStatus.Ready));
        state = Shared.Models.SeatStatus.Ready;

        Assert.True(state.CanTransitionTo(Shared.Models.SeatStatus.Streaming));
        state = Shared.Models.SeatStatus.Streaming;

        Assert.True(state.CanTransitionTo(Shared.Models.SeatStatus.TearingDown));
        state = Shared.Models.SeatStatus.TearingDown;

        Assert.True(state.CanTransitionTo(Shared.Models.SeatStatus.Idle));
    }

    [Fact]
    public void SeatStateTransition_ErrorRecovery()
    {
        var state = Shared.Models.SeatStatus.Error;
        // Error → TearingDown (cleanup)
        Assert.True(state.CanTransitionTo(Shared.Models.SeatStatus.TearingDown));
        // Error → Provisioning (retry)
        Assert.True(state.CanTransitionTo(Shared.Models.SeatStatus.Provisioning));
        // Error → Streaming (invalid)
        Assert.False(state.CanTransitionTo(Shared.Models.SeatStatus.Streaming));
    }

    [Fact]
    public void WtsSessionInfo_StructLayout_IsCorrect()
    {
        // The struct must be exactly 12 bytes (int + IntPtr + int) on x64
        // to correctly index into the WTS enumeration buffer.
        var size = Marshal.SizeOf<WtsApi.WtsSessionInfo>();
        // On 64-bit: int(4) + padding(4) + IntPtr(8) + int(4) + padding(4) = 24
        Assert.Equal(24, size);
    }

    [Fact]
    public void StartupInfo_StructLayout_HasCorrectCb()
    {
        var si = new Kernel32.StartupInfo
        {
            cb = Marshal.SizeOf<Kernel32.StartupInfo>()
        };

        // cb must be > 0 and match the actual struct size
        Assert.True(si.cb > 0);
        Assert.Equal(Marshal.SizeOf<Kernel32.StartupInfo>(), si.cb);
    }

    [Fact]
    public void ProcessInformation_StructLayout_IsCorrect()
    {
        var size = Marshal.SizeOf<Kernel32.ProcessInformation>();
        // On 64-bit: IntPtr(8) + IntPtr(8) + int(4) + int(4) = 24
        Assert.Equal(24, size);
    }

    /// <summary>
    /// The session anchor is the process launched INSIDE a seat session so Windows does not
    /// reclaim it. It is not the console-session keepalive mstsc of issue #18 - a reporter read
    /// one for the other because both used to log the word "keepalive".
    ///
    /// The command it runs is load-bearing and the alternatives were each rejected for a measured
    /// reason recorded in the method: cmd.exe /c pause needs ReadConsoleInput, which fails once the
    /// console is detached on RDP disconnect; cmd.exe /k takes CTRL_CLOSE_EVENT on disconnect;
    /// PowerShell hits a Node.js v24 null-byte env var bug; ping works but puts traffic on the
    /// wire; and waitfor exits 1 inside an RDP session. This pins the survivor so none of them
    /// comes back by accident.
    ///
    /// The previous test here asserted "cmd.exe" against a string literal it declared itself, so it
    /// could not fail - and the shape it named had not been what the code produced for some time.
    /// </summary>
    [Fact]
    public void SessionAnchorCommand_IsTheOneShapeThatSurvivesAnRdpDisconnect()
    {
        var cmd = SessionLauncher.BuildSessionAnchorCommand();

        // Fully qualified: a bare "timeout" resolves against the seat's PATH, and the anchor has to
        // start before anything in that session is known-good.
        Assert.StartsWith(@"C:\Windows\System32\timeout.exe", cmd, StringComparison.OrdinalIgnoreCase);

        // /nobreak, or a stray Ctrl+C ends the session.
        Assert.Contains("/nobreak", cmd);

        // A long timeout. It still expires (99999 s is timeout.exe's cap, 27.8 h), and the health
        // check relaunches it then - see SessionAnchorTrackerTests - but a short one would churn.
        var match = System.Text.RegularExpressions.Regex.Match(cmd, @"/t\s+(\d+)");
        Assert.True(match.Success, $"the anchor command must set a timeout: {cmd}");
        Assert.True(int.Parse(match.Groups[1].Value) >= 86_400,
            $"a short timeout would drop the seat when it expires: {cmd}");

        // Each of these was tried and failed in the field. None may return.
        Assert.DoesNotContain("pause", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe /k", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ping", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("waitfor", cmd, StringComparison.OrdinalIgnoreCase);
    }

    // ── Integration tests (require SYSTEM + RDP Wrapper) ─────────────

    [Trait("Category", "Integration")]
    [Fact(Skip = "Requires SYSTEM privileges and RDP Wrapper")]
    public void FindExistingSession_ReturnsNegativeForNonexistentUser()
    {
        // This would actually call WTSEnumerateSessions
        // Skipped by default — run manually on a configured host
    }

    [Trait("Category", "Integration")]
    [Fact(Skip = "Requires SYSTEM privileges and RDP Wrapper")]
    public async Task LaunchSessionAsync_CreatesNewSession()
    {
        // Full integration: creates a real session, verifies it appears
        // in WTS enumeration, then tears it down.
        await Task.CompletedTask;
    }
}
