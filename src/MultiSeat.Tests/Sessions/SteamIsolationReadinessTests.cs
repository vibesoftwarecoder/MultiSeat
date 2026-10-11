using System.Security.AccessControl;
using System.Security.Principal;
using MultiSeat.Service.Interop;
using MultiSeat.Service.Sessions;
using Xunit;

namespace MultiSeat.Tests.Sessions;

public sealed class SteamIsolationReadinessTests
{
    [Fact]
    public void SecurityGrantsOnlyHostAdminsAndExactSeatMinimalRights()
    {
        var seat = new SecurityIdentifier("S-1-5-21-100-200-300-1001");
        var security = new RawSecurityDescriptor(SteamIsolationReadiness.BuildSddl(seat));
        Assert.True(security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.Equal(3, security.DiscretionaryAcl!.Count);
        var rule = Assert.IsType<CommonAce>(security.DiscretionaryAcl[2]);
        Assert.Equal(seat, rule.SecurityIdentifier);
        Assert.Equal(0x00100002, rule.AccessMask);
        Assert.Equal(AceQualifier.AccessAllowed, rule.AceQualifier);
    }

    [Fact]
    public async Task RealNamedEventCanBeSignaledByItsIntendedAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var ready = new SteamIsolationReadiness(identity.User!);
        using var opened = EventWaitHandle.OpenExisting(ready.Name);
        Assert.False(opened.WaitOne(0));
        opened.Set();
        await ready.WaitAsync(Kernel32.GetCurrentProcess(), TimeSpan.FromSeconds(1), default);
        Assert.True(opened.WaitOne(0)); // manual reset
    }

    [Fact]
    public async Task WaitsForInitializationInsteadOfAssumingRunningMeansReady()
    {
        var checks = 0;
        await SteamIsolationReadiness.WaitCoreAsync(() => ++checks >= 3, () => null,
            TimeSpan.FromSeconds(2), default);
        Assert.True(checks >= 3);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(9u)]
    public async Task ExitWithoutReadinessFailsEvenWithZeroExitCode(uint code)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SteamIsolationReadiness.WaitCoreAsync(() => false, () => code, TimeSpan.FromSeconds(1), default));
        Assert.Contains($"code {code}", error.Message);
    }

    [Fact]
    public async Task MissingReadinessTimesOut() =>
        await Assert.ThrowsAsync<TimeoutException>(() => SteamIsolationReadiness.WaitCoreAsync(
            () => false, () => null, TimeSpan.Zero, default));

    [Fact]
    public async Task CancellationInterruptsReadinessWait()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SteamIsolationReadiness.WaitCoreAsync(
            () => false, () => null, TimeSpan.FromSeconds(30), cancellation.Token));
    }
}
