using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace MultiSeat.Service.Interop;

/// <summary>
/// Who owns a TCP endpoint, from the system's extended TCP table (IPv4 and IPv6).
///
/// Two questions are asked of it and both fail CLOSED, meaning "unknown" whenever the table does not
/// give exactly one answer:
///   - which single process listens on a port (so the host Apollo's control pipe can be tied to the
///     Apollo that really serves Moonlight, not to whoever created a pipe with the same name);
///   - which single process owns the client end of a loopback connection, and when that connection was
///     created (so the API can tell which session a request came from, and notice a process id that has
///     been given to a different process since).
/// </summary>
internal static partial class TcpTable
{
    private const uint AF_INET = 2;
    private const uint AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    private const int TCP_TABLE_OWNER_MODULE_CONNECTIONS = 7;
    private const uint NO_ERROR = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    public const uint StateListen = 2;
    public const uint StateEstablished = 5;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint addressFamily, int tableClass, uint reserved);

    /// <summary>
    /// One connection or listener. <paramref name="CreateTime"/> is when the connection was created, in
    /// 100 ns ticks since 1601 (the epoch of a process's creation time); it is 0 for rows read from a
    /// table that does not carry it.
    /// </summary>
    internal readonly record struct Row(
        IPAddress LocalAddress, ushort LocalPort,
        IPAddress RemoteAddress, ushort RemotePort,
        uint State, uint Pid, long CreateTime = 0);

    /// <summary>The process that owns the client end of a connection, and when that connection was created.</summary>
    internal readonly record struct Owner(uint Pid, long Created);

    /// <summary>The raw bytes of one of the system tables. Null when the call fails.</summary>
    private static byte[]? ReadBytes(uint family, int tableClass)
    {
        uint size = 0;
        // The table can grow between the size query and the read; try a few times.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal((int)Math.Max(size, 4));
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, family, tableClass, 0);
                if (result == NO_ERROR)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(buffer, bytes, 0, (int)size);
                    return bytes;
                }
                if (result != ERROR_INSUFFICIENT_BUFFER)
                    return null;
                size += 4096; // headroom for rows added meanwhile
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    /// <summary>Reads one of the system tables. An empty list when the call fails.</summary>
    internal static IReadOnlyList<Row> Read(bool ipv6, bool listenersOnly)
    {
        var tableClass = listenersOnly ? TCP_TABLE_OWNER_PID_LISTENER : TCP_TABLE_OWNER_PID_CONNECTIONS;
        var bytes = ReadBytes(ipv6 ? AF_INET6 : AF_INET, tableClass);
        if (bytes is null) return Array.Empty<Row>();
        return ipv6 ? ParseV6(bytes) : ParseV4(bytes);
    }

    /// <summary>
    /// Every connection, both address families, each with the time it was created. Null when either table
    /// cannot be read: a failed read is never reported as "no connections".
    /// </summary>
    internal static IReadOnlyList<Row>? ReadConnectionsWithTimes()
    {
        var v4 = ReadBytes(AF_INET, TCP_TABLE_OWNER_MODULE_CONNECTIONS);
        var v6 = ReadBytes(AF_INET6, TCP_TABLE_OWNER_MODULE_CONNECTIONS);
        if (v4 is null || v6 is null) return null;
        return ParseOwnerModuleV4(v4).Concat(ParseOwnerModuleV6(v6)).ToArray();
    }

    /// <summary>A port as stored in the table: the low 16 bits of a DWORD, in network byte order.</summary>
    private static ushort DecodePort(uint value) =>
        BinaryPrimitives.ReverseEndianness((ushort)(value & 0xFFFF));

    /// <summary>MIB_TCPTABLE_OWNER_PID: a count, then 24-byte rows.</summary>
    internal static IReadOnlyList<Row> ParseV4(ReadOnlySpan<byte> table)
    {
        const int rowSize = 24;
        var rows = new List<Row>();
        if (table.Length < 4) return rows;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (var i = 0; i < count; i++)
        {
            var at = 4 + i * rowSize;
            if (at + rowSize > table.Length) break;
            var row = table.Slice(at, rowSize);
            rows.Add(new Row(
                new IPAddress(row.Slice(4, 4)),
                DecodePort(BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(8, 4))),
                new IPAddress(row.Slice(12, 4)),
                DecodePort(BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4))),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(0, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(20, 4))));
        }
        return rows;
    }

    /// <summary>MIB_TCP6TABLE_OWNER_PID: a count, then 56-byte rows.</summary>
    internal static IReadOnlyList<Row> ParseV6(ReadOnlySpan<byte> table)
    {
        const int rowSize = 56;
        var rows = new List<Row>();
        if (table.Length < 4) return rows;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (var i = 0; i < count; i++)
        {
            var at = 4 + i * rowSize;
            if (at + rowSize > table.Length) break;
            var row = table.Slice(at, rowSize);
            rows.Add(new Row(
                new IPAddress(row.Slice(0, 16)),
                DecodePort(BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(20, 4))),
                new IPAddress(row.Slice(24, 16)),
                DecodePort(BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(44, 4))),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(48, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(52, 4))));
        }
        return rows;
    }

    // The owner-module rows hold a LARGE_INTEGER, so the table struct is 8-byte aligned: the DWORD row
    // count is followed by 4 bytes of padding and the rows start at offset 8.
    private const int OwnerModuleHeader = 8;

    /// <summary>
    /// MIB_TCPTABLE_OWNER_MODULE with 160-byte rows: state, laddr, lport, raddr, rport, owning pid (six
    /// DWORDs), liCreateTimestamp (8 bytes), then OwningModuleInfo (16 ULONGLONGs) that is not read.
    /// </summary>
    internal static IReadOnlyList<Row> ParseOwnerModuleV4(ReadOnlySpan<byte> table)
    {
        const int rowSize = 160;
        var rows = new List<Row>();
        if (table.Length < 4) return rows;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (var i = 0; i < count; i++)
        {
            var at = OwnerModuleHeader + i * rowSize;
            if (at + rowSize > table.Length) break;
            var row = table.Slice(at, rowSize);
            rows.Add(new Row(
                new IPAddress(row.Slice(4, 4)),
                DecodePort(BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(8, 4))),
                new IPAddress(row.Slice(12, 4)),
                DecodePort(BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4))),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(0, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(20, 4)),
                BinaryPrimitives.ReadInt64LittleEndian(row.Slice(24, 8))));
        }
        return rows;
    }

    /// <summary>
    /// MIB_TCP6TABLE_OWNER_MODULE with 192-byte rows: laddr[16], lscope, lport, raddr[16], rscope, rport,
    /// state, owning pid, liCreateTimestamp (8 bytes at offset 56), then OwningModuleInfo that is not read.
    /// </summary>
    internal static IReadOnlyList<Row> ParseOwnerModuleV6(ReadOnlySpan<byte> table)
    {
        const int rowSize = 192;
        var rows = new List<Row>();
        if (table.Length < 4) return rows;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (var i = 0; i < count; i++)
        {
            var at = OwnerModuleHeader + i * rowSize;
            if (at + rowSize > table.Length) break;
            var row = table.Slice(at, rowSize);
            rows.Add(new Row(
                new IPAddress(row.Slice(0, 16)),
                DecodePort(BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(20, 4))),
                new IPAddress(row.Slice(24, 16)),
                DecodePort(BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(44, 4))),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(48, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(52, 4)),
                BinaryPrimitives.ReadInt64LittleEndian(row.Slice(56, 8))));
        }
        return rows;
    }

    /// <summary>
    /// The one process that listens on <paramref name="port"/>. Nothing when no process does, or when
    /// more than one does (an Apollo on the wildcard address and a different process on one specific
    /// address is a situation to refuse, not to guess about).
    /// </summary>
    internal static uint? ListenerPid(ushort port, Func<bool, IReadOnlyList<Row>>? readListeners = null)
    {
        readListeners ??= ipv6 => Read(ipv6, listenersOnly: true);
        var pids = new HashSet<uint>();
        foreach (var row in readListeners(false).Concat(readListeners(true)))
        {
            if (row.State == StateListen && row.LocalPort == port)
                pids.Add(row.Pid);
        }
        return pids.Count == 1 ? pids.First() : null;
    }

    /// <summary>
    /// The one process that owns the CLIENT end of a loopback connection, and when the connection was
    /// created: the row whose local port is <paramref name="clientPort"/> and whose remote port is
    /// <paramref name="serverPort"/>, with both addresses on loopback. Nothing when there is no such row,
    /// the table could not be read (<paramref name="rows"/> is null), the owner is process 0, or the rows
    /// disagree about the owner or the creation time. The same process on both address stacks is one owner.
    /// </summary>
    internal static Owner? ClientOwner(IReadOnlyList<Row>? rows, ushort clientPort, ushort serverPort)
    {
        if (rows is null) return null;

        var owners = new HashSet<Owner>();
        foreach (var row in rows)
        {
            if (row.State == StateEstablished
                && row.LocalPort == clientPort
                && row.RemotePort == serverPort
                && IPAddress.IsLoopback(row.LocalAddress)
                && IPAddress.IsLoopback(row.RemoteAddress))
            {
                if (row.Pid == 0) return null;
                owners.Add(new Owner(row.Pid, row.CreateTime));
            }
        }
        return owners.Count == 1 ? owners.First() : null;
    }
}
