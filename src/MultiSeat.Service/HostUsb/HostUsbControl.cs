using System.Text.Json;
using MultiSeat.Service.Interop;
using MultiSeat.Service.Monitoring;

namespace MultiSeat.Service.HostUsb;

public sealed record UsbForward(string ClientUuid, string ClientName, string Busid, long SinceUnix);

public sealed record UsbClient(string Uuid, string Name, bool UsbPermission);

public sealed record UsbStatus(
    bool Enabled,
    bool Listening,
    int Port,
    bool RequireStream,
    IReadOnlyList<UsbForward> Forwards,
    IReadOnlyList<UsbClient> Clients,
    IReadOnlyList<string> Denied);

public sealed record UsbSetEnabledResult(bool Enabled, bool Listening, int Port, bool Persisted);

public sealed record UsbRevokeResult(int ForwardsCut, bool Denied, bool Known);

public sealed record UsbClearResult(bool Denied, bool Known);

/// <summary>The outcome of one control call: a value, or a fixed reason code (never text from Apollo).</summary>
public sealed record UsbOutcome<T>(T? Value, string? Reason)
{
    public bool Ok => Reason is null;
    public static UsbOutcome<T> Success(T value) => new(value, null);
    public static UsbOutcome<T> Failure(string reason) => new(default, reason);
}

/// <summary>What the dashboard may ask of the host Apollo's USB forwarding.</summary>
public interface IHostUsbControl
{
    Task<UsbOutcome<UsbStatus>> StatusAsync(CancellationToken ct);
    Task<UsbOutcome<UsbSetEnabledResult>> SetEnabledAsync(bool enabled, CancellationToken ct);
    Task<UsbOutcome<UsbRevokeResult>> RevokeAsync(string clientUuid, CancellationToken ct);
    Task<UsbOutcome<UsbClearResult>> ClearRevokeAsync(string clientUuid, CancellationToken ct);
}

/// <summary>
/// Talks to the host's own Apollo through its control pipe. MultiSeat still never edits that Apollo's
/// config: this is a live switch on a tunnel the host owner already allowed in Apollo's own settings,
/// and a change made here lasts only until that Apollo restarts.
///
/// Which Apollo is "the" one is decided here, not by the pipe: the pipe belongs to the process that
/// LISTENS on the host Apollo's main port. If no single process listens there, or the pipe's server is
/// another process, nothing is sent (see <see cref="ApolloUsbControlClient"/>).
/// </summary>
public sealed class HostUsbControl : IHostUsbControl
{
    /// <summary>The leaf of <c>\\.\pipe\ApolloVibe-usb-control-&lt;main port&gt;</c>.</summary>
    public static string PipeLeafFor(int mainPort) => $"ApolloVibe-usb-control-{mainPort}";

    private readonly Func<int?> _mainPort;
    private readonly Func<ushort, uint?> _listenerPid;
    private readonly ApolloUsbControlClient _client;
    private readonly ILogger<HostUsbControl> _logger;
    private readonly SemaphoreSlim _one = new(1, 1);

    public HostUsbControl(HostApolloMonitor monitor, ApolloUsbControlClient client, ILogger<HostUsbControl> logger)
        : this(monitor.TryResolveMainPort, port => TcpTable.ListenerPid(port), client, logger)
    {
    }

    internal HostUsbControl(
        Func<int?> mainPort, Func<ushort, uint?> listenerPid, ApolloUsbControlClient client, ILogger<HostUsbControl> logger)
    {
        _mainPort = mainPort;
        _listenerPid = listenerPid;
        _client = client;
        _logger = logger;
    }

    public async Task<UsbOutcome<UsbStatus>> StatusAsync(CancellationToken ct) =>
        Interpret(await CallAsync("status", null, ct), ParseStatus);

    public async Task<UsbOutcome<UsbSetEnabledResult>> SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        var result = Interpret(await CallAsync("set_enabled", new Dictionary<string, object> { ["enabled"] = enabled }, ct), ParseSetEnabled);
        _logger.LogInformation("Host USB forwarding: set_enabled({Enabled}) -> {Outcome}", enabled, result.Reason ?? "ok");
        return result;
    }

    public async Task<UsbOutcome<UsbRevokeResult>> RevokeAsync(string clientUuid, CancellationToken ct)
    {
        var result = Interpret(await CallAsync("revoke_client", new Dictionary<string, object> { ["client_uuid"] = clientUuid }, ct), ParseRevoke);
        _logger.LogInformation("Host USB forwarding: revoke_client -> {Outcome}", result.Reason ?? "ok");
        return result;
    }

    public async Task<UsbOutcome<UsbClearResult>> ClearRevokeAsync(string clientUuid, CancellationToken ct)
    {
        var result = Interpret(await CallAsync("clear_revoke", new Dictionary<string, object> { ["client_uuid"] = clientUuid }, ct), ParseClear);
        _logger.LogInformation("Host USB forwarding: clear_revoke -> {Outcome}", result.Reason ?? "ok");
        return result;
    }

    // ---- one call --------------------------------------------------------------------------------

    private async Task<UsbOutcome<JsonElement>> CallAsync(string op, Dictionary<string, object>? fields, CancellationToken ct)
    {
        // Apollo serves four connections at once; this service never uses more than one.
        if (!await _one.WaitAsync(TimeSpan.FromSeconds(3), ct))
            return UsbOutcome<JsonElement>.Failure(UsbReasons.Busy);

        try
        {
            var port = _mainPort();
            if (port is null or < 1024 or > 65535)
                return UsbOutcome<JsonElement>.Failure(UsbReasons.NoHostApollo);

            var pid = _listenerPid((ushort)port.Value);
            if (pid is null)
                return UsbOutcome<JsonElement>.Failure(UsbReasons.NoHostApollo);

            var request = new Dictionary<string, object> { ["v"] = 1, ["op"] = op };
            if (fields is not null)
            {
                foreach (var pair in fields)
                    request[pair.Key] = pair.Value;
            }

            var reply = await _client.CallAsync(PipeLeafFor(port.Value), pid.Value, JsonSerializer.Serialize(request), ct);
            return reply.Outcome switch
            {
                UsbPipeOutcome.Ok => UsbOutcome<JsonElement>.Success(reply.Body!.Value),
                UsbPipeOutcome.Unavailable => UsbOutcome<JsonElement>.Failure(UsbReasons.PipeUnavailable),
                UsbPipeOutcome.Busy => UsbOutcome<JsonElement>.Failure(UsbReasons.Busy),
                UsbPipeOutcome.Timeout => UsbOutcome<JsonElement>.Failure(UsbReasons.Timeout),
                UsbPipeOutcome.UntrustedServer => UsbOutcome<JsonElement>.Failure(UsbReasons.UntrustedServer),
                UsbPipeOutcome.ApolloError => UsbOutcome<JsonElement>.Failure(reply.ApolloError!),
                _ => UsbOutcome<JsonElement>.Failure(UsbReasons.BadReply),
            };
        }
        finally
        {
            _one.Release();
        }
    }

    private static UsbOutcome<T> Interpret<T>(UsbOutcome<JsonElement> raw, Func<JsonElement, T?> parse) where T : class
    {
        if (!raw.Ok)
            return UsbOutcome<T>.Failure(raw.Reason!);
        var value = parse(raw.Value);
        return value is null ? UsbOutcome<T>.Failure(UsbReasons.BadReply) : UsbOutcome<T>.Success(value);
    }

    // ---- strict readers: a wrong type or a missing field is a bad reply, never a guess --------------

    private const int MaxListed = 512;
    private const int MaxText = 256;

    private static bool TryBool(JsonElement o, string name, out bool value)
    {
        value = false;
        if (!o.TryGetProperty(name, out var p) || (p.ValueKind != JsonValueKind.True && p.ValueKind != JsonValueKind.False))
            return false;
        value = p.GetBoolean();
        return true;
    }

    private static bool TryInt(JsonElement o, string name, out int value)
    {
        value = 0;
        return o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value);
    }

    private static bool TryLong(JsonElement o, string name, out long value)
    {
        value = 0;
        return o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out value);
    }

    private static bool TryText(JsonElement o, string name, out string value)
    {
        value = "";
        if (!o.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
            return false;
        value = p.GetString() ?? "";
        return value.Length <= MaxText;
    }

    private static bool TryArray(JsonElement o, string name, out JsonElement array)
    {
        array = default;
        if (!o.TryGetProperty(name, out array) || array.ValueKind != JsonValueKind.Array)
            return false;
        return array.GetArrayLength() <= MaxListed;
    }

    internal static UsbStatus? ParseStatus(JsonElement r)
    {
        if (!TryBool(r, "enabled", out var enabled) || !TryBool(r, "listening", out var listening)
            || !TryInt(r, "port", out var port) || !TryBool(r, "require_stream", out var requireStream)
            || !TryArray(r, "forwards", out var forwardsJson) || !TryArray(r, "clients", out var clientsJson)
            || !TryArray(r, "denied", out var deniedJson))
        {
            return null;
        }

        var forwards = new List<UsbForward>();
        foreach (var f in forwardsJson.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object || !TryText(f, "client_uuid", out var uuid)
                || !TryText(f, "client_name", out var name) || !TryText(f, "busid", out var busid)
                || !TryLong(f, "since_unix", out var since))
            {
                return null;
            }
            forwards.Add(new UsbForward(uuid, name, busid, since));
        }

        var clients = new List<UsbClient>();
        foreach (var c in clientsJson.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || !TryText(c, "uuid", out var uuid)
                || !TryText(c, "name", out var name) || !TryBool(c, "usb_permission", out var permission))
            {
                return null;
            }
            clients.Add(new UsbClient(uuid, name, permission));
        }

        var denied = new List<string>();
        foreach (var d in deniedJson.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.String || d.GetString() is not { Length: <= MaxText } text)
                return null;
            denied.Add(text);
        }

        return new UsbStatus(enabled, listening, port, requireStream, forwards, clients, denied);
    }

    internal static UsbSetEnabledResult? ParseSetEnabled(JsonElement r) =>
        TryBool(r, "enabled", out var enabled) && TryBool(r, "listening", out var listening)
        && TryInt(r, "port", out var port) && TryBool(r, "persisted", out var persisted)
            ? new UsbSetEnabledResult(enabled, listening, port, persisted)
            : null;

    internal static UsbRevokeResult? ParseRevoke(JsonElement r) =>
        TryInt(r, "forwards_cut", out var cut) && cut >= 0 && TryBool(r, "denied", out var denied) && TryBool(r, "known", out var known)
            ? new UsbRevokeResult(cut, denied, known)
            : null;

    internal static UsbClearResult? ParseClear(JsonElement r) =>
        TryBool(r, "denied", out var denied) && TryBool(r, "known", out var known)
            ? new UsbClearResult(denied, known)
            : null;
}

/// <summary>The reason codes this API gives: fixed words, so a client can switch on them. Apollo's own codes pass through.</summary>
public static class UsbReasons
{
    public const string NoHostApollo = "no_host_apollo";
    public const string PipeUnavailable = "pipe_unavailable";
    public const string Busy = "busy";
    public const string Timeout = "timeout";
    public const string UntrustedServer = "untrusted_server";
    public const string BadReply = "bad_reply";
}
