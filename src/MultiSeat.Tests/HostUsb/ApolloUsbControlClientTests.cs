using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using MultiSeat.Service.HostUsb;
using Xunit;

namespace MultiSeat.Tests.HostUsb;

/// <summary>
/// The pipe client against REAL named pipes served by the test. The server here owns the pipe as the
/// current user (set explicitly in its security descriptor), so the production rule "SYSTEM or
/// Administrators only" refuses it whether the tests run elevated or not; tests that need the client
/// to go on widen the owner rule to this user.
/// </summary>
public class ApolloUsbControlClientTests
{
    internal static SecurityIdentifier Me => WindowsIdentity.GetCurrent().User!;

    internal static string UniqueLeaf() => $"MultiSeatTest-{Environment.ProcessId}-{Guid.NewGuid():N}";

    internal static ApolloUsbControlClient.Options Lenient(
        TimeSpan? io = null, TimeSpan? connect = null, int? maxReply = null) => new()
    {
        OwnerAcceptable = sid => sid == Me,
        IoTimeout = io ?? TimeSpan.FromSeconds(5),
        ConnectBudget = connect ?? TimeSpan.FromMilliseconds(2500),
        MaxReplyBytes = maxReply ?? 256 * 1024,
    };

    /// <summary>A pipe server owned by the current user, with one scripted conversation per connection.</summary>
    internal sealed class FakeServer : IAsyncDisposable
    {
        public string Leaf { get; }
        public List<string> Received { get; } = new();
        private readonly NamedPipeServerStream _pipe;
        private readonly Task _run;

        public FakeServer(string leaf, Func<NamedPipeServerStream, string?, Task> script, bool readRequest = true, int maxInstances = 1)
        {
            Leaf = leaf;
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(Me, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.SetOwner(Me);
            _pipe = NamedPipeServerStreamAcl.Create(
                leaf, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);

            _run = Task.Run(async () =>
            {
                try
                {
                    await _pipe.WaitForConnectionAsync();
                    string? line = null;
                    if (readRequest)
                    {
                        line = await ReadLineAsync(_pipe, TimeSpan.FromSeconds(2));
                        lock (Received) { if (line is not null) Received.Add(line); }
                    }
                    await script(_pipe, line);
                }
                catch (Exception)
                {
                    // the client went away: the script is over
                }
            });
        }

        /// <summary>The token level the server sees when it acts as the client, after the request was read.</summary>
        public static int? ReadTokenLevel(NamedPipeServerStream pipe)
        {
            int? level = null;
            pipe.RunAsClient(() =>
            {
                if (OpenThreadToken(GetCurrentThread(), 0x0008 /* TOKEN_QUERY */, true, out var token))
                {
                    if (GetTokenInformation(token, 9 /* TokenImpersonationLevel */, out int value, sizeof(int), out _))
                        level = value;
                    CloseHandle(token);
                }
            });
            return level;
        }

        public async ValueTask DisposeAsync()
        {
            _pipe.Dispose();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* ended */ }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }

    internal static async Task<string?> ReadLineAsync(Stream stream, TimeSpan limit)
    {
        using var cts = new CancellationTokenSource(limit);
        var bytes = new List<byte>();
        var one = new byte[1];
        try
        {
            while (await stream.ReadAsync(one, cts.Token) == 1)
            {
                if (one[0] == (byte)'\n')
                    return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(one[0]);
            }
        }
        catch (OperationCanceledException) { }
        return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
    }

    internal static Task Reply(Stream pipe, string line) =>
        pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).AsTask();

    private static readonly string OkReply = """{"v":1,"ok":true,"enabled":true}""";

    private static ApolloUsbControlClient Client(ApolloUsbControlClient.Options options) => new(options);

    // ── the happy path ───────────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task ATrustedServer_GetsTheRequestAndItsReplyComesBack()
    {
        await using var server = new FakeServer(UniqueLeaf(), async (pipe, _) =>
        {
            await Reply(pipe, OkReply);
            await ReadLineAsync(pipe, TimeSpan.FromSeconds(1)); // Apollo waits for the client to finish
        });

        var reply = await Client(Lenient()).CallAsync(server.Leaf, (uint)Environment.ProcessId, """{"v":1,"op":"status"}""", CancellationToken.None);

        Assert.Equal(UsbPipeOutcome.Ok, reply.Outcome);
        Assert.True(reply.Body!.Value.GetProperty("enabled").GetBoolean());
        Assert.Equal("""{"v":1,"op":"status"}""", Assert.Single(server.Received));
    }

    // ── who is on the other end: nothing is sent unless both checks pass ─

    [Fact(Timeout = 30_000)]
    public async Task AServerOwnedByAnOrdinaryUser_IsRefusedBeforeAnythingIsSent()
    {
        if (Me.Value == "S-1-5-18") return; // SYSTEM cannot stand in for an ordinary user
        await using var server = new FakeServer(UniqueLeaf(), (_, _) => Task.CompletedTask);

        // The production owner rule, not the lenient one.
        var reply = await new ApolloUsbControlClient().CallAsync(server.Leaf, (uint)Environment.ProcessId, """{"v":1,"op":"status"}""", CancellationToken.None);

        Assert.Equal(UsbPipeOutcome.UntrustedServer, reply.Outcome);
        Assert.Empty(server.Received);
    }

    [Fact(Timeout = 30_000)]
    public async Task AServerThatIsNotTheProcessWeExpect_IsRefusedBeforeAnythingIsSent()
    {
        // Each server serves one connection, so each expectation gets its own.
        await using var wrongServer = new FakeServer(UniqueLeaf(), (_, _) => Task.CompletedTask);
        await using var noneServer = new FakeServer(UniqueLeaf(), (_, _) => Task.CompletedTask);

        var wrong = await Client(Lenient()).CallAsync(wrongServer.Leaf, (uint)Environment.ProcessId + 4, """{"v":1,"op":"status"}""", CancellationToken.None);
        var none = await Client(Lenient()).CallAsync(noneServer.Leaf, 0, """{"v":1,"op":"status"}""", CancellationToken.None);

        Assert.Equal(UsbPipeOutcome.UntrustedServer, wrong.Outcome);
        Assert.Equal(UsbPipeOutcome.UntrustedServer, none.Outcome);
        Assert.Empty(wrongServer.Received);
        Assert.Empty(noneServer.Received);
    }

    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-32-544", true)]
    [InlineData("S-1-5-32-545", false)]   // Users
    [InlineData("S-1-1-0", false)]        // Everyone
    [InlineData("S-1-5-11", false)]       // Authenticated Users
    [InlineData("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", false)] // TrustedInstaller
    public void TheOwnerRule_IsSystemAndAdministratorsOnly(string sid, bool trusted)
    {
        Assert.Equal(trusted, ApolloUsbControlClient.IsTrustedOwner(new SecurityIdentifier(sid)));
    }

    [Fact(Timeout = 30_000)]
    public async Task ThePipeIsOpenedAtIdentificationLevel_SoTheServerCannotActAsUs()
    {
        // The server acts as the client after reading the request. The token it gets is identification-only:
        // it can see who connected but could never do anything as them.
        int? level = null;
        await using var server = new FakeServer(UniqueLeaf(), async (pipe, _) =>
        {
            level = FakeServer.ReadTokenLevel(pipe);
            await Reply(pipe, OkReply);
        });

        var reply = await Client(Lenient()).CallAsync(server.Leaf, (uint)Environment.ProcessId, """{"v":1,"op":"status"}""", CancellationToken.None);

        Assert.Equal(UsbPipeOutcome.Ok, reply.Outcome);
        Assert.Equal(1, level); // SecurityIdentification
    }

    // ── what can go wrong with the connection ────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task NoPipe_IsUnavailable_AndQuick()
    {
        var started = DateTime.UtcNow;
        var reply = await Client(Lenient()).CallAsync(UniqueLeaf(), (uint)Environment.ProcessId, """{"v":1,"op":"status"}""", CancellationToken.None);

        Assert.Equal(UsbPipeOutcome.Unavailable, reply.Outcome);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact(Timeout = 30_000)]
    public async Task AServerThatClosesWithoutAReply_MeansBusy()
    {
        await using var server = new FakeServer(UniqueLeaf(), (pipe, _) => { pipe.Dispose(); return Task.CompletedTask; });
        var reply = await Client(Lenient()).CallAsync(server.Leaf, (uint)Environment.ProcessId, """{"v":1,"op":"status"}""", CancellationToken.None);
        Assert.Equal(UsbPipeOutcome.Busy, reply.Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task AServerThatNeverAnswers_TimesOutAtTheDeadline()
    {
        await using var server = new FakeServer(UniqueLeaf(), async (_, _) => await Task.Delay(TimeSpan.FromSeconds(20)));
        var started = DateTime.UtcNow;

        var reply = await Client(Lenient(io: TimeSpan.FromMilliseconds(400))).CallAsync(server.Leaf, (uint)Environment.ProcessId, """{"v":1,"op":"status"}""", CancellationToken.None);

        Assert.Equal(UsbPipeOutcome.Timeout, reply.Outcome);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Fact(Timeout = 30_000)]
    public async Task APipeWithAllInstancesBusy_GivesUpAfterTheBudget()
    {
        // One instance, already taken by somebody else.
        var leaf = UniqueLeaf();
        await using var server = new FakeServer(leaf, async (_, _) => await Task.Delay(TimeSpan.FromSeconds(20)), readRequest: false);
        using var occupant = new NamedPipeClientStream(".", leaf, PipeDirection.InOut, PipeOptions.Asynchronous);
        await occupant.ConnectAsync(2000);

        var started = DateTime.UtcNow;
        var reply = await Client(Lenient(connect: TimeSpan.FromMilliseconds(700))).CallAsync(leaf, (uint)Environment.ProcessId, """{"v":1,"op":"status"}""", CancellationToken.None);

        Assert.Equal(UsbPipeOutcome.Busy, reply.Outcome);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Fact(Timeout = 30_000)]
    public async Task ACallerWhoCancels_GetsACancellation_NotATimeout()
    {
        await using var server = new FakeServer(UniqueLeaf(), async (_, _) => await Task.Delay(TimeSpan.FromSeconds(20)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(Lenient()).CallAsync(server.Leaf, (uint)Environment.ProcessId, """{"v":1,"op":"status"}""", cts.Token));
    }

    // ── what the reply may look like ─────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task AReplyBiggerThanTheCap_IsBad()
    {
        await using var server = new FakeServer(UniqueLeaf(), async (pipe, _) =>
        {
            await pipe.WriteAsync(new byte[200_000].Select(_ => (byte)'x').ToArray());
        });
        var reply = await Client(Lenient(maxReply: 64 * 1024)).CallAsync(server.Leaf, (uint)Environment.ProcessId, """{"v":1,"op":"status"}""", CancellationToken.None);
        Assert.Equal(UsbPipeOutcome.BadReply, reply.Outcome);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"ok":true}""")]                       // no version
    [InlineData("""{"v":2,"ok":true}""")]                  // wrong version
    [InlineData("""{"v":"1","ok":true}""")]                // version of the wrong type
    [InlineData("""{"v":1}""")]                            // no ok
    [InlineData("""{"v":1,"ok":"yes"}""")]                 // ok of the wrong type
    [InlineData("""{"v":1,"ok":false}""")]                 // failure with no code
    [InlineData("""{"v":1,"ok":false,"error":"rm -rf"}""")] // a code Apollo does not have
    [InlineData("""{"v":1,"ok":false,"error":5}""")]
    public void AReplyThatIsNotAVersionOneAnswer_IsBad(string line)
    {
        Assert.Equal(UsbPipeOutcome.BadReply, ApolloUsbControlClient.ParseReply(Encoding.UTF8.GetBytes(line)).Outcome);
    }

    [Fact]
    public void AReplyThatIsNotUtf8_IsBad()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"v\":1,\"ok\":true,\"x\":\"").Concat(new byte[] { 0xFF, 0xFE }).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
        Assert.Equal(UsbPipeOutcome.BadReply, ApolloUsbControlClient.ParseReply(bytes).Outcome);
    }

    [Theory]
    [InlineData("bad_request")]
    [InlineData("port_refused")]
    [InlineData("start_failed")]
    [InlineData("shutting_down")]
    [InlineData("deny_list_full")]
    [InlineData("internal")]
    public void AnApolloErrorCode_PassesThrough(string code)
    {
        var reply = ApolloUsbControlClient.ParseReply(Encoding.UTF8.GetBytes($$"""{"v":1,"ok":false,"error":"{{code}}"}"""));
        Assert.Equal(UsbPipeOutcome.ApolloError, reply.Outcome);
        Assert.Equal(code, reply.ApolloError);
    }
}
