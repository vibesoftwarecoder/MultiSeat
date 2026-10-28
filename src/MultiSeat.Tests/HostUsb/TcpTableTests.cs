using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MultiSeat.Service.Interop;
using Xunit;

namespace MultiSeat.Tests.HostUsb;

/// <summary>
/// The system TCP table is how the host Apollo's pipe is tied to the process that serves Moonlight, and
/// how the API learns which session a request came from. The parsing is pinned on hand-built tables,
/// the questions on injected rows (every doubtful case must come out as "unknown"), and the real call on
/// a real loopback connection made by this very process.
/// </summary>
public class TcpTableTests
{
    private static void PutPort(Span<byte> at, ushort port)
    {
        // Network byte order in the low 16 bits of a DWORD.
        at[0] = (byte)(port >> 8);
        at[1] = (byte)(port & 0xFF);
    }

    private static byte[] V4Table(params (uint State, byte[] Local, ushort LocalPort, byte[] Remote, ushort RemotePort, uint Pid)[] rows)
    {
        var table = new byte[4 + rows.Length * 24];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = table.AsSpan(4 + i * 24, 24);
            BinaryPrimitives.WriteUInt32LittleEndian(row, rows[i].State);
            rows[i].Local.CopyTo(row.Slice(4, 4));
            PutPort(row.Slice(8, 4), rows[i].LocalPort);
            rows[i].Remote.CopyTo(row.Slice(12, 4));
            PutPort(row.Slice(16, 4), rows[i].RemotePort);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(20, 4), rows[i].Pid);
        }
        return table;
    }

    [Fact]
    public void ParseV4_ReadsStateAddressesPortsAndPid()
    {
        var table = V4Table((2, new byte[] { 0, 0, 0, 0 }, 47989, new byte[] { 0, 0, 0, 0 }, 0, 1234));
        var rows = TcpTable.ParseV4(table);

        var row = Assert.Single(rows);
        Assert.Equal(2u, row.State);
        Assert.Equal(IPAddress.Any, row.LocalAddress);
        Assert.Equal((ushort)47989, row.LocalPort);
        Assert.Equal((ushort)0, row.RemotePort);
        Assert.Equal(1234u, row.Pid);
    }

    [Fact]
    public void ParseV4_ATruncatedTableGivesWhatFitsAndNeverThrows()
    {
        var table = V4Table(
            (2, new byte[] { 127, 0, 0, 1 }, 1000, new byte[4], 0, 1),
            (2, new byte[] { 127, 0, 0, 1 }, 1001, new byte[4], 0, 2));
        Assert.Single(TcpTable.ParseV4(table.AsSpan(0, 4 + 24 + 10)));
        Assert.Empty(TcpTable.ParseV4(new byte[2]));
        Assert.Empty(TcpTable.ParseV4(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseV6_ReadsTheSameFieldsFromTheWiderRow()
    {
        var table = new byte[4 + 56];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
        var row = table.AsSpan(4, 56);
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(row.Slice(0, 16));
        PutPort(row.Slice(20, 4), 50000);
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(row.Slice(24, 16));
        PutPort(row.Slice(44, 4), 47989);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(48, 4), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(52, 4), 4321);

        var parsed = Assert.Single(TcpTable.ParseV6(table));
        Assert.Equal(IPAddress.IPv6Loopback, parsed.LocalAddress);
        Assert.Equal((ushort)50000, parsed.LocalPort);
        Assert.Equal((ushort)47989, parsed.RemotePort);
        Assert.Equal(5u, parsed.State);
        Assert.Equal(4321u, parsed.Pid);
    }

    private static TcpTable.Row Row(uint state, ushort localPort, ushort remotePort, uint pid, string local = "127.0.0.1", string remote = "127.0.0.1", long created = 0) =>
        new(IPAddress.Parse(local), localPort, IPAddress.Parse(remote), remotePort, state, pid, created);

    [Fact]
    public void ListenerPid_IsTheOneProcessListeningOnThePort()
    {
        var pid = TcpTable.ListenerPid(47989, v6 => v6
            ? Array.Empty<TcpTable.Row>()
            : new[] { Row(TcpTable.StateListen, 47989, 0, 100, "0.0.0.0", "0.0.0.0"), Row(TcpTable.StateListen, 47990, 0, 200) });
        Assert.Equal(100u, pid);
    }

    [Fact]
    public void ListenerPid_AnApolloOnBothStacksIsStillOneProcess()
    {
        var pid = TcpTable.ListenerPid(47989, v6 => new[] { Row(TcpTable.StateListen, 47989, 0, 100, v6 ? "::" : "0.0.0.0", v6 ? "::" : "0.0.0.0") });
        Assert.Equal(100u, pid);
    }

    [Fact]
    public void ListenerPid_TwoDifferentProcessesOnThePortIsUnknown()
    {
        var pid = TcpTable.ListenerPid(47989, v6 => v6
            ? Array.Empty<TcpTable.Row>()
            : new[] { Row(TcpTable.StateListen, 47989, 0, 100, "0.0.0.0", "0.0.0.0"), Row(TcpTable.StateListen, 47989, 0, 666) });
        Assert.Null(pid);
    }

    [Fact]
    public void ListenerPid_NobodyListening_OrOnlyAConnection_IsUnknown()
    {
        Assert.Null(TcpTable.ListenerPid(47989, _ => Array.Empty<TcpTable.Row>()));
        Assert.Null(TcpTable.ListenerPid(47989, _ => new[] { Row(TcpTable.StateEstablished, 47989, 50000, 100) }));
    }

    [Fact]
    public void ClientOwner_IsTheOwnerAndCreationTimeOfTheClientEndOfTheLoopbackConnection()
    {
        var rows = new[]
        {
            Row(TcpTable.StateEstablished, 50000, 9550, 321, created: 5000),   // the client end
            Row(TcpTable.StateEstablished, 9550, 50000, 77, created: 5000),    // the server end
            Row(TcpTable.StateEstablished, 50001, 9550, 999, created: 6000),   // another client
        };
        Assert.Equal(new TcpTable.Owner(321, 5000), TcpTable.ClientOwner(rows, 50000, 9550));
    }

    [Fact]
    public void ClientOwner_DoubtfulRowsAreUnknown()
    {
        // not loopback, not established, nothing matching, no read at all, pid 0, or two owners for one connection
        Assert.Null(TcpTable.ClientOwner(new[] { Row(TcpTable.StateEstablished, 50000, 9550, 5, "192.168.1.5", "192.168.1.9") }, 50000, 9550));
        Assert.Null(TcpTable.ClientOwner(new[] { Row(TcpTable.StateListen, 50000, 9550, 5) }, 50000, 9550));
        Assert.Null(TcpTable.ClientOwner(Array.Empty<TcpTable.Row>(), 50000, 9550));
        Assert.Null(TcpTable.ClientOwner(null, 50000, 9550));
        Assert.Null(TcpTable.ClientOwner(new[] { Row(TcpTable.StateEstablished, 50000, 9550, 0) }, 50000, 9550));
        Assert.Null(TcpTable.ClientOwner(new[] { Row(TcpTable.StateEstablished, 50000, 9550, 5), Row(TcpTable.StateEstablished, 50000, 9550, 6, "::1", "::1") }, 50000, 9550));
    }

    [Fact]
    public void ClientOwner_TheSameProcessOnBothStacks_IsOneOwner_ButTwoCreationTimesAreNot()
    {
        var same = new[] { Row(TcpTable.StateEstablished, 50000, 9550, 5, created: 100), Row(TcpTable.StateEstablished, 50000, 9550, 5, created: 100) };
        Assert.Equal(new TcpTable.Owner(5, 100), TcpTable.ClientOwner(same, 50000, 9550));

        var differ = new[] { Row(TcpTable.StateEstablished, 50000, 9550, 5, created: 100), Row(TcpTable.StateEstablished, 50000, 9550, 5, created: 200) };
        Assert.Null(TcpTable.ClientOwner(differ, 50000, 9550));
    }

    // The owner-module tables carry each connection's creation time. The table struct holds a LARGE_INTEGER,
    // so the row count is followed by 4 bytes of padding and the rows start at offset 8.

    [Fact]
    public void ParseOwnerModuleV4_ReadsTheCreationTimeFromTheWideRow()
    {
        var table = new byte[8 + 160];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
        var row = table.AsSpan(8, 160);
        BinaryPrimitives.WriteUInt32LittleEndian(row, 5);
        new byte[] { 127, 0, 0, 1 }.CopyTo(row.Slice(4, 4));
        PutPort(row.Slice(8, 4), 50000);
        new byte[] { 127, 0, 0, 1 }.CopyTo(row.Slice(12, 4));
        PutPort(row.Slice(16, 4), 9550);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(20, 4), 321);
        BinaryPrimitives.WriteInt64LittleEndian(row.Slice(24, 8), 133_800_000_000_000_000);

        var parsed = Assert.Single(TcpTable.ParseOwnerModuleV4(table));
        Assert.Equal(5u, parsed.State);
        Assert.Equal((ushort)50000, parsed.LocalPort);
        Assert.Equal((ushort)9550, parsed.RemotePort);
        Assert.Equal(321u, parsed.Pid);
        Assert.Equal(133_800_000_000_000_000, parsed.CreateTime);
    }

    [Fact]
    public void ParseOwnerModuleV6_ReadsTheCreationTimeFromTheWideRow()
    {
        var table = new byte[8 + 192];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
        var row = table.AsSpan(8, 192);
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(row.Slice(0, 16));
        PutPort(row.Slice(20, 4), 50000);
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(row.Slice(24, 16));
        PutPort(row.Slice(44, 4), 9550);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(48, 4), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(52, 4), 4321);
        BinaryPrimitives.WriteInt64LittleEndian(row.Slice(56, 8), 133_800_000_000_000_001);

        var parsed = Assert.Single(TcpTable.ParseOwnerModuleV6(table));
        Assert.Equal(IPAddress.IPv6Loopback, parsed.LocalAddress);
        Assert.Equal(4321u, parsed.Pid);
        Assert.Equal(133_800_000_000_000_001, parsed.CreateTime);
    }

    [Fact]
    public void ParseOwnerModule_ATruncatedTableGivesWhatFitsAndNeverThrows()
    {
        var table = new byte[8 + 160 * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 2);
        Assert.Single(TcpTable.ParseOwnerModuleV4(table.AsSpan(0, 8 + 160 + 10)));
        Assert.Empty(TcpTable.ParseOwnerModuleV4(new byte[2]));
        Assert.Empty(TcpTable.ParseOwnerModuleV6(ReadOnlySpan<byte>.Empty));
    }

    // ---- the real table -----------------------------------------------------------------------

    [Fact]
    public async Task Real_AListenerAndAConnectionMadeByThisProcessAreFoundAsThisProcess()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var serverPort = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, serverPort);
            using var accepted = await listener.AcceptTcpClientAsync();
            var clientPort = (ushort)((IPEndPoint)client.Client.LocalEndPoint!).Port;

            var me = (uint)Environment.ProcessId;
            Assert.Equal(me, TcpTable.ListenerPid(serverPort));

            var rows = TcpTable.ReadConnectionsWithTimes();
            Assert.NotNull(rows);
            var owner = TcpTable.ClientOwner(rows, clientPort, serverPort);
            Assert.Equal(me, owner?.Pid);

            // The connection is newer than this process and not in the future.
            var start = Process.GetCurrentProcess().StartTime.ToFileTime();
            Assert.InRange(owner!.Value.Created, start, DateTime.UtcNow.ToFileTime() + TimeSpan.TicksPerSecond);

            // A port nobody uses, and a pair that is not a connection, are unknown.
            Assert.Null(TcpTable.ClientOwner(rows, clientPort, (ushort)(serverPort ^ 1)));
        }
        finally
        {
            listener.Stop();
        }
    }
}
