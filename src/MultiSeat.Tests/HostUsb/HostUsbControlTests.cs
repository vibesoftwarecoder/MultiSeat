using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MultiSeat.Service.HostUsb;
using Xunit;
using static MultiSeat.Tests.HostUsb.ApolloUsbControlClientTests;

namespace MultiSeat.Tests.HostUsb;

/// <summary>
/// What the dashboard's calls turn into: which Apollo is "the" Apollo, which process must be serving
/// its pipe, what is sent, and how each kind of answer or failure is reported. Real pipes again, so the
/// client's checks run for real.
/// </summary>
public class HostUsbControlTests
{
    private static readonly Random Dice = new();

    /// <summary>A main port no real Apollo uses, so the pipe name cannot collide with an installed one.</summary>
    private static int FreshPort() => 20000 + Dice.Next(0, 9000);

    private static HostUsbControl Control(
        int? port, uint? listenerPid, ApolloUsbControlClient.Options? options = null) =>
        new(() => port, _ => listenerPid, new ApolloUsbControlClient(options ?? Lenient()), NullLogger<HostUsbControl>.Instance);

    private const string FullStatus = """
        {"v":1,"ok":true,"enabled":true,"listening":true,"port":47996,"require_stream":false,
         "forwards":[{"client_uuid":"AB-1","client_name":"Player One","busid":"1-2","since_unix":1700000000}],
         "clients":[{"uuid":"AB-1","name":"Player One","usb_permission":true},{"uuid":"CD-2","name":"Player Two","usb_permission":false}],
         "denied":["EF-3"]}
        """;

    private static Task Answer(Stream pipe, string json) => Reply(pipe, json.ReplaceLineEndings(""));

    // ── which Apollo ─────────────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task WithoutAStandaloneApollo_NothingIsContacted()
    {
        var result = await Control(port: null, listenerPid: 1).StatusAsync(CancellationToken.None);
        Assert.Equal(UsbReasons.NoHostApollo, result.Reason);
    }

    [Fact(Timeout = 30_000)]
    public async Task WhenNoSingleProcessListensOnTheMainPort_NothingIsContacted()
    {
        var port = FreshPort();
        await using var server = new FakeServer(HostUsbControl.PipeLeafFor(port), (_, _) => Task.CompletedTask);

        var result = await Control(port, listenerPid: null).StatusAsync(CancellationToken.None);

        Assert.Equal(UsbReasons.NoHostApollo, result.Reason);
        Assert.Empty(server.Received);
    }

    [Fact(Timeout = 30_000)]
    public async Task ThePipeIsNamedAfterTheMainPort_AndItsServerMustBeTheListener()
    {
        var port = FreshPort();
        await using var server = new FakeServer(HostUsbControl.PipeLeafFor(port), (_, _) => Task.CompletedTask);

        // The pipe exists and is lenient about its owner, but its server is not the process listening on the port.
        var result = await Control(port, listenerPid: (uint)Environment.ProcessId + 4).StatusAsync(CancellationToken.None);

        Assert.Equal(UsbReasons.UntrustedServer, result.Reason);
        Assert.Empty(server.Received);
    }

    [Fact(Timeout = 30_000)]
    public async Task NoPipe_MeansControlIsNotSwitchedOnInApollo()
    {
        var result = await Control(FreshPort(), (uint)Environment.ProcessId).StatusAsync(CancellationToken.None);
        Assert.Equal(UsbReasons.PipeUnavailable, result.Reason);
    }

    [Fact(Timeout = 30_000)]
    public async Task APipeOwnedByAnOrdinaryUser_IsReportedAsUntrusted()
    {
        if (Me.Value == "S-1-5-18") return;
        var port = FreshPort();
        await using var server = new FakeServer(HostUsbControl.PipeLeafFor(port), (_, _) => Task.CompletedTask);

        var result = await Control(port, (uint)Environment.ProcessId, new ApolloUsbControlClient.Options()).StatusAsync(CancellationToken.None);

        Assert.Equal(UsbReasons.UntrustedServer, result.Reason);
        Assert.Empty(server.Received);
    }

    // ── what is sent, what comes back ────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task Status_AsksForStatus_AndReadsEveryField()
    {
        var port = FreshPort();
        await using var server = new FakeServer(HostUsbControl.PipeLeafFor(port), (pipe, _) => Answer(pipe, FullStatus));

        var result = await Control(port, (uint)Environment.ProcessId).StatusAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal("""{"v":1,"op":"status"}""", Assert.Single(server.Received));
        var status = result.Value!;
        Assert.True(status.Enabled);
        Assert.True(status.Listening);
        Assert.Equal(47996, status.Port);
        Assert.False(status.RequireStream);
        Assert.Equal(new UsbForward("AB-1", "Player One", "1-2", 1700000000), Assert.Single(status.Forwards));
        Assert.Equal(2, status.Clients.Count);
        Assert.True(status.Clients[0].UsbPermission);
        Assert.False(status.Clients[1].UsbPermission);
        Assert.Equal(new[] { "EF-3" }, status.Denied);
    }

    [Fact(Timeout = 30_000)]
    public async Task SetEnabled_SendsTheBoolean_AndSaysItIsNotPersisted()
    {
        var port = FreshPort();
        await using var server = new FakeServer(HostUsbControl.PipeLeafFor(port),
            (pipe, _) => Answer(pipe, """{"v":1,"ok":true,"enabled":true,"listening":true,"port":47996,"persisted":false}"""));

        var result = await Control(port, (uint)Environment.ProcessId).SetEnabledAsync(true, CancellationToken.None);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal("""{"v":1,"op":"set_enabled","enabled":true}""", Assert.Single(server.Received));
        Assert.Equal(new UsbSetEnabledResult(true, true, 47996, false), result.Value);
    }

    [Fact(Timeout = 30_000)]
    public async Task Revoke_AndClear_SendTheClientUuid()
    {
        var port = FreshPort();
        await using var revokeServer = new FakeServer(HostUsbControl.PipeLeafFor(port),
            (pipe, _) => Answer(pipe, """{"v":1,"ok":true,"forwards_cut":2,"denied":true,"known":true}"""));
        var revoked = await Control(port, (uint)Environment.ProcessId).RevokeAsync("AB-1", CancellationToken.None);
        Assert.Equal("""{"v":1,"op":"revoke_client","client_uuid":"AB-1"}""", Assert.Single(revokeServer.Received));
        Assert.Equal(new UsbRevokeResult(2, true, true), revoked.Value);

        var port2 = FreshPort();
        await using var clearServer = new FakeServer(HostUsbControl.PipeLeafFor(port2),
            (pipe, _) => Answer(pipe, """{"v":1,"ok":true,"denied":false,"known":false}"""));
        var cleared = await Control(port2, (uint)Environment.ProcessId).ClearRevokeAsync("ZZ-9", CancellationToken.None);
        Assert.Equal("""{"v":1,"op":"clear_revoke","client_uuid":"ZZ-9"}""", Assert.Single(clearServer.Received));
        Assert.Equal(new UsbClearResult(false, false), cleared.Value);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnApolloErrorCode_IsReportedAsItIs()
    {
        var port = FreshPort();
        await using var server = new FakeServer(HostUsbControl.PipeLeafFor(port),
            (pipe, _) => Answer(pipe, """{"v":1,"ok":false,"error":"start_failed"}"""));

        var result = await Control(port, (uint)Environment.ProcessId).SetEnabledAsync(true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("start_failed", result.Reason);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnAnswerOfTheWrongShape_IsABadReply_NotAGuess()
    {
        var port = FreshPort();
        await using var server = new FakeServer(HostUsbControl.PipeLeafFor(port),
            (pipe, _) => Answer(pipe, """{"v":1,"ok":true,"enabled":"yes"}"""));
        var result = await Control(port, (uint)Environment.ProcessId).StatusAsync(CancellationToken.None);
        Assert.Equal(UsbReasons.BadReply, result.Reason);
    }

    // ── the strict readers ───────────────────────────────────────────

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ParseStatus_ReadsAValidReply()
    {
        Assert.NotNull(HostUsbControl.ParseStatus(Json(FullStatus)));
    }

    [Theory]
    [InlineData("""{"v":1,"ok":true}""")]
    [InlineData("""{"enabled":1,"listening":true,"port":1,"require_stream":true,"forwards":[],"clients":[],"denied":[]}""")]
    [InlineData("""{"enabled":true,"listening":true,"port":"1","require_stream":true,"forwards":[],"clients":[],"denied":[]}""")]
    [InlineData("""{"enabled":true,"listening":true,"port":1,"require_stream":true,"forwards":{},"clients":[],"denied":[]}""")]
    [InlineData("""{"enabled":true,"listening":true,"port":1,"require_stream":true,"forwards":[1],"clients":[],"denied":[]}""")]
    [InlineData("""{"enabled":true,"listening":true,"port":1,"require_stream":true,"forwards":[],"clients":[{"uuid":"a","name":"b"}],"denied":[]}""")]
    [InlineData("""{"enabled":true,"listening":true,"port":1,"require_stream":true,"forwards":[],"clients":[],"denied":[5]}""")]
    public void ParseStatus_RefusesAnythingElse(string json)
    {
        Assert.Null(HostUsbControl.ParseStatus(Json(json)));
    }

    [Fact]
    public void ParseStatus_RefusesHugeListsAndLongText()
    {
        var many = string.Join(",", Enumerable.Repeat("""{"uuid":"a","name":"b","usb_permission":true}""", 513));
        Assert.Null(HostUsbControl.ParseStatus(Json($$"""{"enabled":true,"listening":true,"port":1,"require_stream":true,"forwards":[],"clients":[{{many}}],"denied":[]}""")));

        var longName = new string('x', 300);
        Assert.Null(HostUsbControl.ParseStatus(Json($$"""{"enabled":true,"listening":true,"port":1,"require_stream":true,"forwards":[],"clients":[{"uuid":"a","name":"{{longName}}","usb_permission":true}],"denied":[]}""")));
    }

    [Fact]
    public void TheOtherReaders_AreStrictToo()
    {
        Assert.Null(HostUsbControl.ParseSetEnabled(Json("""{"enabled":true,"listening":true,"port":1}""")));
        Assert.Null(HostUsbControl.ParseRevoke(Json("""{"forwards_cut":-1,"denied":true,"known":true}""")));
        Assert.Null(HostUsbControl.ParseRevoke(Json("""{"forwards_cut":1,"denied":true}""")));
        Assert.Null(HostUsbControl.ParseClear(Json("""{"denied":false}""")));
    }

    // ── one conversation at a time ───────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task ASecondCallWhileOneIsInFlight_WaitsThenReportsBusy()
    {
        var port = FreshPort();
        await using var server = new FakeServer(HostUsbControl.PipeLeafFor(port), async (_, _) => await Task.Delay(TimeSpan.FromSeconds(20)));
        var control = Control(port, (uint)Environment.ProcessId, Lenient(io: TimeSpan.FromSeconds(15)));
        using var cancelFirst = new CancellationTokenSource();

        var first = control.StatusAsync(cancelFirst.Token);
        await Task.Delay(300); // the first is now holding the gate

        var started = DateTime.UtcNow;
        var second = await control.StatusAsync(CancellationToken.None);

        Assert.Equal(UsbReasons.Busy, second.Reason);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(8));

        cancelFirst.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }
}
