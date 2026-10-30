using System.Net;
using MultiSeat.Service.Accounts;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Interop;
using MultiSeat.Service.Sessions;

namespace MultiSeat.Service.Api;

public enum CallerVerdict
{
    /// <summary>The request came over loopback from the user logged on at the active console session, in a process of that session.</summary>
    Allowed,
    /// <summary>Not a loopback connection (the dashboard was reached over the network).</summary>
    NotLoopback,
    /// <summary>The calling process, its session or its user could not be determined. Refused: unknown is not "yes".</summary>
    UnknownCaller,
    /// <summary>The caller runs in another session, for example a seat's.</summary>
    NotConsoleSession,
    /// <summary>The caller is in the console session but is not the person logged on there: a service account, another user, or a seat account.</summary>
    NotConsoleUser,
}

/// <summary>
/// Decides whether a request came from the person sitting at the host: a process of the user logged on at
/// the active CONSOLE session. The dashboard API has no key on this host by the owner's choice and every
/// seat user can reach it on loopback, so without this a seat user could read the host's USB status or
/// switch its USB tunnel on through MultiSeat's SYSTEM service.
///
/// How: the request's source port identifies the client end of the loopback connection; the system's TCP
/// table names the process that owns that end and when the connection was created.
///   - The owner process is opened and the handle is KEPT OPEN for the whole check, so its id cannot be
///     given to another process meanwhile. The table keeps naming the process that CREATED a socket after
///     that process has exited; if its id was then reused, the process that now has it is younger than the
///     connection. So the check refuses when the owner was created AFTER the connection, and when either
///     timestamp is zero. The comparison is strict: a tolerance could only let a reused id through.
///   - The table is read again once the handle is held and must name the same owner and connection, and
///     the console session must not have moved.
///   - The owner's session must be the active console session. Its token user must be the user logged on
///     at that session, must not be SYSTEM or another service account, and must not be a MultiSeat seat
///     account. A seat user who moves their own session to the console with tscon is the console user,
///     but is a seat account, so is still refused.
/// Every doubt refuses.
///
/// What it does not stop: another program of the console user, which is the same trust the console user
/// already has over the host. A console-user program that hands a connected socket to a program in another
/// session is not detected, because the table names the creator. A dashboard reached over the network
/// fails (not loopback); that case would need a key and is not offered.
/// </summary>
public sealed class ConsoleCallerCheck
{
    private const uint NoSession = 0xFFFFFFFF;

    private readonly Func<ushort, ushort, TcpTable.Owner?> _clientOwner;
    private readonly Func<uint, IOwnerProcess?> _openOwner;
    private readonly Func<uint> _consoleSession;
    private readonly Func<uint, string?> _consoleUserSid;
    private readonly Func<IReadOnlyCollection<string>> _seatSids;

    public ConsoleCallerCheck(AccountManager accounts, SeatManager seats, SeatPresetStore presets)
        : this(
            (clientPort, serverPort) => TcpTable.ClientOwner(TcpTable.ReadConnectionsWithTimes(), clientPort, serverPort),
            OwnerProcess.Open,
            Kernel32.WTSGetActiveConsoleSessionId,
            ConsoleUser.SidOf,
            () => SeatSidsOf(accounts, seats, presets))
    {
    }

    internal ConsoleCallerCheck(
        Func<ushort, ushort, TcpTable.Owner?> clientOwner, Func<uint, IOwnerProcess?> openOwner,
        Func<uint> consoleSession, Func<uint, string?> consoleUserSid, Func<IReadOnlyCollection<string>> seatSids)
    {
        _clientOwner = clientOwner;
        _openOwner = openOwner;
        _consoleSession = consoleSession;
        _consoleUserSid = consoleUserSid;
        _seatSids = seatSids;
    }

    /// <summary>
    /// The SIDs of the accounts that are, or can become, a seat: accounts MultiSeat created, the account of
    /// every live seat, and the account of every saved seat preset. NOT every account MultiSeat has stored:
    /// <c>AccountManager</c> also holds ordinary accounts that were only LINKED (<c>IsManaged</c> false), and
    /// the person at the console is often one of them. Counting those refused the console user from the
    /// panel meant for them. A linked account that does host a seat still counts, through its seat or preset.
    /// A name that cannot be resolved is skipped.
    /// </summary>
    internal static IReadOnlyCollection<string> SeatSidsOf(AccountManager accounts, SeatManager seats, SeatPresetStore presets) =>
        SeatSidsFrom(
            accounts.ListManagedAccounts().Select(a => (a.Sid, a.Username, a.IsManaged)),
            seats.GetAllSeats().Select(s => s.AccountName),
            presets.GetAll().Select(p => p.AccountName),
            name => AccountManager.TryResolveSid(name, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));

    /// <summary>The rule behind <see cref="SeatSidsOf"/>, with its inputs passed in so a test can drive it.</summary>
    internal static IReadOnlyCollection<string> SeatSidsFrom(
        IEnumerable<(string? Sid, string Username, bool IsManaged)> stored,
        IEnumerable<string> liveSeatAccounts, IEnumerable<string> presetAccounts, Func<string, string?> resolve)
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? knownSid, string username)
        {
            var sid = !string.IsNullOrEmpty(knownSid) ? knownSid
                : string.IsNullOrWhiteSpace(username) ? null : resolve(username);
            if (!string.IsNullOrEmpty(sid)) sids.Add(sid);
        }

        foreach (var (sid, username, isManaged) in stored)
            if (isManaged) Add(sid, username);
        foreach (var name in liveSeatAccounts) Add(null, name);
        foreach (var name in presetAccounts) Add(null, name);
        return sids;
    }

    public CallerVerdict Check(IPAddress? remote, int remotePort, int localPort)
    {
        if (remote is null || !IPAddress.IsLoopback(remote))
            return CallerVerdict.NotLoopback;

        if (remotePort is < 1 or > 65535 || localPort is < 1 or > 65535)
            return CallerVerdict.UnknownCaller;

        var first = _clientOwner((ushort)remotePort, (ushort)localPort);
        if (first is not { Pid: not 0 } owner)
            return CallerVerdict.UnknownCaller;

        // Held to the end of the check: the id cannot be reused while this handle is open.
        using var process = _openOwner(owner.Pid);
        if (process is null)
            return CallerVerdict.UnknownCaller;

        // A reused id belongs to a process younger than the connection. Strict, no tolerance.
        if (process.CreationTime <= 0 || owner.Created <= 0 || process.CreationTime > owner.Created)
            return CallerVerdict.UnknownCaller;

        var session = process.SessionId();
        var console = _consoleSession();
        var consoleUser = console == 0 || console == NoSession ? null : _consoleUserSid(console);
        var user = process.UserSid();

        // Ask again now that the handle is held: the table must name the same owner and connection, and
        // the answers about the session must not have moved.
        if (_clientOwner((ushort)remotePort, (ushort)localPort) != first)
            return CallerVerdict.UnknownCaller;
        if (process.SessionId() != session || _consoleSession() != console)
            return CallerVerdict.UnknownCaller;

        // Session 0 is the services session and 0xFFFFFFFF means nobody is at the console: neither is a
        // person at the host, so with either as the console nothing can match.
        if (session is null || console == 0 || console == NoSession || consoleUser is null || user is null)
            return CallerVerdict.UnknownCaller;

        if (session != console)
            return CallerVerdict.NotConsoleSession;

        if (!string.Equals(user, consoleUser, StringComparison.OrdinalIgnoreCase)
            || IsServiceAccount(user) || IsServiceAccount(consoleUser) || IsSeatAccount(user))
            return CallerVerdict.NotConsoleUser;

        return CallerVerdict.Allowed;
    }

    /// <summary>SYSTEM, LocalService, NetworkService and per-service SIDs are not "the person at the console".</summary>
    private static bool IsServiceAccount(string sid) =>
        sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" || sid.StartsWith("S-1-5-80-", StringComparison.Ordinal);

    private bool IsSeatAccount(string sid)
    {
        try { return _seatSids().Contains(sid, StringComparer.OrdinalIgnoreCase); }
        catch (Exception) { return false; } // the set could not be obtained: nothing extra is refused
    }
}
