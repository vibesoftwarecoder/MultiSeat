using System.Net;
using MultiSeat.Service.Api;
using MultiSeat.Service.Interop;
using Xunit;

namespace MultiSeat.Tests.HostUsb;

/// <summary>
/// The rule that lets only the person at the host use USB forwarding: the request must come over
/// loopback from a process of the user logged on at the active console session, and not from one of
/// MultiSeat's seat accounts. Every doubt must refuse.
/// </summary>
public class ConsoleCallerCheckTests
{
    private const uint ConsoleSession = 1;
    private const string ConsoleUser = "S-1-5-21-1-1-1-1001";
    private const string SeatUser = "S-1-5-21-1-1-1-1002";
    private const long ConnectionCreated = 1_000_000;

    private sealed class FakeOwner : IOwnerProcess
    {
        public long CreationTime { get; init; } = ConnectionCreated - 10;
        public uint? Session { get; init; } = ConsoleSession;
        public string? User { get; init; } = ConsoleUser;
        public bool Disposed { get; private set; }
        public uint? SessionId() => Session;
        public string? UserSid() => User;
        public void Dispose() => Disposed = true;
    }

    /// <summary>What the TCP table says about the client end of the connection: its owner and when it was created.</summary>
    private static TcpTable.Owner? Found(uint pid = 4242, long created = ConnectionCreated) => new(pid, created);

    private sealed class Setup
    {
        public Func<ushort, ushort, TcpTable.Owner?>[] Reads { get; set; } = [(_, _) => Found()];
        public int ReadCount;
        public FakeOwner? Owner { get; set; } = new();
        public uint Console { get; set; } = ConsoleSession;
        public string? ConsoleUserSid { get; set; } = ConsoleUser;
        public string[] SeatSids { get; set; } = [SeatUser];
        public Func<uint>? ConsoleSessionProvider { get; set; }
        public uint? OpenedPid;
        public int Opened;

        public ConsoleCallerCheck Build() => new(
            (clientPort, serverPort) => Reads[Math.Min(ReadCount++, Reads.Length - 1)](clientPort, serverPort),
            pid => { OpenedPid = pid; Opened++; return Owner; },
            () => ConsoleSessionProvider?.Invoke() ?? Console,
            _ => ConsoleUserSid,
            () => SeatSids);
    }

    private static CallerVerdict Run(Setup s, IPAddress? remote = null, int remotePort = 50000, int localPort = 9550) =>
        s.Build().Check(remote ?? IPAddress.Loopback, remotePort, localPort);

    [Fact]
    public void TheConsoleUserInTheConsoleSession_IsAllowed()
    {
        Assert.Equal(CallerVerdict.Allowed, Run(new Setup()));
        Assert.Equal(CallerVerdict.Allowed, Run(new Setup(), IPAddress.IPv6Loopback));
        Assert.Equal(CallerVerdict.Allowed, Run(new Setup(), IPAddress.Loopback.MapToIPv6()));
    }

    [Fact]
    public void ARequestFromASeatsSession_IsRefused()
    {
        Assert.Equal(CallerVerdict.NotConsoleSession, Run(new Setup { Owner = new FakeOwner { Session = 3 } }));
    }

    [Fact]
    public void ARequestFromTheNetwork_IsRefusedWithoutLookingAnythingUp()
    {
        var s = new Setup();
        var check = s.Build();

        Assert.Equal(CallerVerdict.NotLoopback, check.Check(IPAddress.Parse("192.168.1.20"), 50000, 9550));
        Assert.Equal(CallerVerdict.NotLoopback, check.Check(null, 50000, 9550));
        Assert.Equal(0, s.ReadCount);
    }

    [Theory]
    [InlineData(0, 9550)]
    [InlineData(50000, 0)]
    [InlineData(70000, 9550)]
    [InlineData(-1, 9550)]
    public void ImpossiblePorts_AreUnknown(int remotePort, int localPort)
    {
        Assert.Equal(CallerVerdict.UnknownCaller, Run(new Setup(), remotePort: remotePort, localPort: localPort));
    }

    [Fact]
    public void WhenTheCallerCannotBeFound_ItIsRefused()
    {
        Assert.Equal(CallerVerdict.UnknownCaller, Run(new Setup { Reads = [(_, _) => null] }));
        Assert.Equal(CallerVerdict.UnknownCaller, Run(new Setup { Reads = [(_, _) => Found(pid: 0)] }));
        Assert.Equal(CallerVerdict.UnknownCaller, Run(new Setup { Owner = null }));
        Assert.Equal(CallerVerdict.UnknownCaller, Run(new Setup { Owner = new FakeOwner { Session = null } }));
        Assert.Equal(CallerVerdict.UnknownCaller, Run(new Setup { Owner = new FakeOwner { User = null } }));
        Assert.Equal(CallerVerdict.UnknownCaller, Run(new Setup { ConsoleUserSid = null }));
    }

    [Theory]
    [InlineData(0u)]            // the services session is not a person
    [InlineData(0xFFFFFFFFu)]   // nobody is at the console
    public void WhenThereIsNoConsoleSession_NothingMatches(uint console)
    {
        Assert.Equal(CallerVerdict.UnknownCaller, Run(new Setup { Console = console, Owner = new FakeOwner { Session = console } }));
    }

    [Fact]
    public void ACallerInTheServicesSession_IsNeverTheConsole()
    {
        Assert.Equal(CallerVerdict.NotConsoleSession, Run(new Setup { Owner = new FakeOwner { Session = 0 } }));
    }

    [Fact]
    public void TheLookupUsesTheClientsPortThenTheServersPort()
    {
        (ushort Client, ushort Server)? seen = null;
        var s = new Setup { Reads = [(c, sv) => { seen = (c, sv); return Found(pid: 77); }] };
        Run(s, remotePort: 51234, localPort: 9550);
        Assert.Equal(((ushort)51234, (ushort)9550), seen);
        Assert.Equal(77u, s.OpenedPid);
    }

    // ── who the caller is, not just where ─────────────────────────────

    [Fact]
    public void ADifferentUserInTheConsoleSession_IsRefused()
    {
        var s = new Setup { Owner = new FakeOwner { User = "S-1-5-21-1-1-1-1500" } };
        Assert.Equal(CallerVerdict.NotConsoleUser, Run(s));
    }

    [Fact]
    public void ASeatAccount_IsRefused_EvenWhenItsSessionIsTheConsole()
    {
        // A seat user who moves their own session to the console with tscon is the console user, but a seat account.
        var s = new Setup { ConsoleUserSid = SeatUser, Owner = new FakeOwner { User = SeatUser } };
        Assert.Equal(CallerVerdict.NotConsoleUser, Run(s));
    }

    [Fact]
    public void TheSeatAccountComparison_IgnoresCase()
    {
        var s = new Setup { ConsoleUserSid = SeatUser, Owner = new FakeOwner { User = SeatUser }, SeatSids = [SeatUser.ToLowerInvariant()] };
        Assert.Equal(CallerVerdict.NotConsoleUser, Run(s));
    }

    [Theory]
    [InlineData("S-1-5-18")]                       // SYSTEM
    [InlineData("S-1-5-19")]                       // LocalService
    [InlineData("S-1-5-20")]                       // NetworkService
    [InlineData("S-1-5-80-1-2-3-4-5")]             // a per-service SID
    public void AServiceAccount_IsNeverThePersonAtTheConsole(string sid)
    {
        var s = new Setup { ConsoleUserSid = sid, Owner = new FakeOwner { User = sid } };
        Assert.Equal(CallerVerdict.NotConsoleUser, Run(s));
    }

    [Fact]
    public void IfTheSeatAccountsCannotBeListed_NothingExtraIsRefused()
    {
        var check = new ConsoleCallerCheck(
            (_, _) => Found(), _ => new FakeOwner(), () => ConsoleSession, _ => ConsoleUser,
            () => throw new InvalidOperationException("no list"));
        Assert.Equal(CallerVerdict.Allowed, check.Check(IPAddress.Loopback, 50000, 9550));
    }

    // ── a process id can be given to another process ──────────────────

    [Fact]
    public void AnOwnerCreatedAfterTheConnection_IsARecycledId_AndIsRefused()
    {
        var s = new Setup { Owner = new FakeOwner { CreationTime = ConnectionCreated + 1 } };
        Assert.Equal(CallerVerdict.UnknownCaller, Run(s));
    }

    [Fact]
    public void AnOwnerCreatedAtTheSameTickOrBefore_IsAllowed()
    {
        Assert.Equal(CallerVerdict.Allowed, Run(new Setup { Owner = new FakeOwner { CreationTime = ConnectionCreated } }));
        Assert.Equal(CallerVerdict.Allowed, Run(new Setup { Owner = new FakeOwner { CreationTime = 1 } }));
    }

    [Fact]
    public void ZeroTimestamps_AreRefused()
    {
        Assert.Equal(CallerVerdict.UnknownCaller, Run(new Setup { Owner = new FakeOwner { CreationTime = 0 } }));
        Assert.Equal(CallerVerdict.UnknownCaller, Run(new Setup { Reads = [(_, _) => Found(created: 0)] }));
    }

    [Fact]
    public void TheOwnerHandle_IsHeldAcrossTheSecondRead_AndReleasedAfterwards()
    {
        var s = new Setup();
        var disposedAtSecondRead = (bool?)null;
        s.Reads =
        [
            (_, _) => Found(),
            (_, _) => { disposedAtSecondRead = s.Owner!.Disposed; return Found(); },
        ];

        Assert.Equal(CallerVerdict.Allowed, Run(s));
        Assert.False(disposedAtSecondRead);
        Assert.True(s.Owner!.Disposed);
    }

    [Fact]
    public void TheHandleIsReleased_AlsoWhenTheCallerIsRefused()
    {
        var s = new Setup { Owner = new FakeOwner { Session = 3 } };
        Run(s);
        Assert.True(s.Owner!.Disposed);
    }

    [Fact]
    public void IfTheTableNamesAnotherOwnerTheSecondTime_ItIsRefused()
    {
        var s = new Setup
        {
            Reads = [(_, _) => Found(pid: 4242), (_, _) => Found(pid: 9999)],
        };
        Assert.Equal(CallerVerdict.UnknownCaller, Run(s));
    }

    [Fact]
    public void IfTheConnectionIsNewerTheSecondTime_ItIsRefused()
    {
        var s = new Setup
        {
            Reads = [(_, _) => Found(created: ConnectionCreated), (_, _) => Found(created: ConnectionCreated + 500)],
        };
        Assert.Equal(CallerVerdict.UnknownCaller, Run(s));
    }

    [Fact]
    public void IfTheSecondReadFails_ItIsRefused()
    {
        var s = new Setup { Reads = [(_, _) => Found(), (_, _) => null] };
        Assert.Equal(CallerVerdict.UnknownCaller, Run(s));
    }

    [Fact]
    public void IfTheConsoleMovesDuringTheCheck_ItIsRefused()
    {
        var calls = 0;
        var s = new Setup { ConsoleSessionProvider = () => calls++ < 1 ? ConsoleSession : 2u };
        Assert.Equal(CallerVerdict.UnknownCaller, Run(s));
    }
}
