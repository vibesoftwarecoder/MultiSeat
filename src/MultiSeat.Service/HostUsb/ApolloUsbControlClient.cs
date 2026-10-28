using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MultiSeat.Service.Interop;

namespace MultiSeat.Service.HostUsb;

public enum UsbPipeOutcome
{
    /// <summary>A reply that passed every check.</summary>
    Ok,
    /// <summary>No pipe by that name (control is off in Apollo, or Apollo is old, or not up).</summary>
    Unavailable,
    /// <summary>All instances busy, or the server closed the connection with no reply (its connection cap).</summary>
    Busy,
    /// <summary>The server did not answer in time.</summary>
    Timeout,
    /// <summary>The pipe is not owned by SYSTEM or Administrators, is not served by the Apollo we expect, or refused us.</summary>
    UntrustedServer,
    /// <summary>The reply was not a valid version 1 reply.</summary>
    BadReply,
    /// <summary>Apollo answered with ok:false and one of its fixed error codes.</summary>
    ApolloError,
}

public sealed record UsbPipeReply(UsbPipeOutcome Outcome, JsonElement? Body = null, string? ApolloError = null);

/// <summary>
/// The client side of the host Apollo's USB control pipe, written to Apollo's CLIENT CONTRACT
/// (usb_control_pipe.h in ApolloVibe, "Control pipe" in its docs/configuration.md).
///
/// The pipe's access list keeps ordinary users out of Apollo. It does nothing for US: while Apollo is
/// stopped, any local user can create a pipe with the same name, and this service runs as SYSTEM, so
/// without the checks below it would send a fake server a request and believe the answer. Before a
/// single byte is sent, the client therefore:
///   1. opens the pipe with SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION (flags of the open call),
///      so whoever serves it can never act as SYSTEM;
///   2. requires the pipe's owner to be SYSTEM or Administrators;
///   3. requires the serving process to be the one the caller names (the process that listens on
///      Apollo's main port).
/// Every wait is bounded. A server that closes without answering means "busy" (Apollo serves at most
/// four connections), not an answer.
/// </summary>
public sealed class ApolloUsbControlClient
{
    public sealed class Options
    {
        /// <summary>Total time spent getting past ERROR_PIPE_BUSY.</summary>
        public TimeSpan ConnectBudget { get; init; } = TimeSpan.FromMilliseconds(2500);

        /// <summary>One WaitNamedPipe slice inside the budget.</summary>
        public TimeSpan WaitSlice { get; init; } = TimeSpan.FromMilliseconds(500);

        /// <summary>Writing the request, and separately reading the reply.</summary>
        public TimeSpan IoTimeout { get; init; } = TimeSpan.FromSeconds(5);

        public int MaxReplyBytes { get; init; } = 256 * 1024;

        /// <summary>Production accepts SYSTEM and Administrators only. Tests widen it.</summary>
        public Func<SecurityIdentifier, bool> OwnerAcceptable { get; init; } = IsTrustedOwner;
    }

    private static readonly HashSet<string> ApolloErrorCodes = new(StringComparer.Ordinal)
    {
        "bad_request", "too_large", "bad_version", "unknown_op", "bad_field", "timeout",
        "port_refused", "start_failed", "shutting_down", "deny_list_full", "internal",
    };

    private readonly Options _options;

    public ApolloUsbControlClient() : this(new Options()) { }

    public ApolloUsbControlClient(Options options) => _options = options;

    /// <summary>S-1-5-18 (SYSTEM) or S-1-5-32-544 (BUILTIN\Administrators).</summary>
    public static bool IsTrustedOwner(SecurityIdentifier sid) =>
        sid.Value is "S-1-5-18" or "S-1-5-32-544";

    /// <param name="pipeLeaf">The pipe name after <c>\\.\pipe\</c>.</param>
    /// <param name="expectedServerPid">The process that must be serving it.</param>
    /// <param name="requestLine">One JSON object, without the newline.</param>
    public async Task<UsbPipeReply> CallAsync(
        string pipeLeaf, uint expectedServerPid, string requestLine, CancellationToken ct)
    {
        var path = @"\\.\pipe\" + pipeLeaf;

        var opened = Open(path, out var handle);
        if (opened != UsbPipeOutcome.Ok)
            return new UsbPipeReply(opened);

        using var pipe = new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, handle!);

        // Who is on the other end? Both answers before anything is sent.
        if (!ServerIsTrusted(pipe, handle!, expectedServerPid))
            return new UsbPipeReply(UsbPipeOutcome.UntrustedServer);

        try
        {
            var request = Encoding.UTF8.GetBytes(requestLine + "\n");

            using (var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                writeCts.CancelAfter(_options.IoTimeout);
                try
                {
                    await pipe.WriteAsync(request, writeCts.Token);
                    await pipe.FlushAsync(writeCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new UsbPipeReply(UsbPipeOutcome.Timeout);
                }
                catch (IOException)
                {
                    // The server went away mid-request: its cap, or it is stopping.
                    return new UsbPipeReply(UsbPipeOutcome.Busy);
                }
            }

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(_options.IoTimeout);
            return await ReadReplyAsync(pipe, readCts.Token, ct);
        }
        catch (ObjectDisposedException)
        {
            return new UsbPipeReply(UsbPipeOutcome.Busy);
        }
    }

    /// <summary>
    /// Opens the pipe with the identification flags. ERROR_PIPE_BUSY gets bounded WaitNamedPipe slices
    /// inside <see cref="Options.ConnectBudget"/>; anything else decides the outcome at once.
    /// </summary>
    private UsbPipeOutcome Open(string path, out SafePipeHandle? handle)
    {
        handle = null;
        var started = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            var raw = PipeApi.CreateFile(
                path,
                PipeApi.GENERIC_READ | PipeApi.GENERIC_WRITE,
                0,
                IntPtr.Zero,
                PipeApi.OPEN_EXISTING,
                PipeApi.FILE_FLAG_OVERLAPPED | PipeApi.SECURITY_SQOS_PRESENT | PipeApi.SECURITY_IDENTIFICATION,
                IntPtr.Zero);

            if (raw != new IntPtr(-1))
            {
                handle = new SafePipeHandle(raw, ownsHandle: true);
                return UsbPipeOutcome.Ok;
            }

            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (error == PipeApi.ERROR_PIPE_BUSY)
            {
                var left = _options.ConnectBudget - started.Elapsed;
                if (left <= TimeSpan.Zero)
                    return UsbPipeOutcome.Busy;
                var slice = left < _options.WaitSlice ? left : _options.WaitSlice;
                PipeApi.WaitNamedPipe(path, (uint)Math.Max(1, (int)slice.TotalMilliseconds));
                continue;
            }

            // The access list lets SYSTEM in. Being refused means the pipe is not the one Apollo makes.
            return error == PipeApi.ERROR_ACCESS_DENIED ? UsbPipeOutcome.UntrustedServer : UsbPipeOutcome.Unavailable;
        }
    }

    private bool ServerIsTrusted(NamedPipeClientStream pipe, SafePipeHandle handle, uint expectedServerPid)
    {
        try
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || !_options.OwnerAcceptable(owner))
                return false;

            if (!PipeApi.GetNamedPipeServerProcessId(handle, out var serverPid))
                return false;

            return expectedServerPid != 0 && serverPid == expectedServerPid;
        }
        catch (Exception)
        {
            return false; // any doubt is a refusal
        }
    }

    private async Task<UsbPipeReply> ReadReplyAsync(
        NamedPipeClientStream pipe, CancellationToken token, CancellationToken callerToken)
    {
        var received = new List<byte>(1024);
        var buffer = new byte[4096];

        try
        {
            while (true)
            {
                var got = await pipe.ReadAsync(buffer, token);
                if (got == 0)
                {
                    // Closed. With nothing read that is the connection cap (or a stopping server).
                    return received.Count == 0
                        ? new UsbPipeReply(UsbPipeOutcome.Busy)
                        : new UsbPipeReply(UsbPipeOutcome.BadReply);
                }

                var newline = Array.IndexOf(buffer, (byte)'\n', 0, got);
                var take = newline >= 0 ? newline : got;
                if (received.Count + take > _options.MaxReplyBytes)
                    return new UsbPipeReply(UsbPipeOutcome.BadReply);
                received.AddRange(new ArraySegment<byte>(buffer, 0, take));

                if (newline >= 0)
                    return ParseReply(received.ToArray());
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return new UsbPipeReply(UsbPipeOutcome.Timeout);
        }
        catch (IOException)
        {
            return new UsbPipeReply(UsbPipeOutcome.Busy);
        }
    }

    /// <summary>A version 1 reply: an object with <c>v</c> = 1 and a boolean <c>ok</c>; ok:false carries one of Apollo's fixed codes.</summary>
    internal static UsbPipeReply ParseReply(byte[] line)
    {
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(line);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != 1
                || !root.TryGetProperty("ok", out var ok)
                || (ok.ValueKind != JsonValueKind.True && ok.ValueKind != JsonValueKind.False))
            {
                return new UsbPipeReply(UsbPipeOutcome.BadReply);
            }

            if (ok.ValueKind == JsonValueKind.True)
                return new UsbPipeReply(UsbPipeOutcome.Ok, root.Clone());

            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() is { } code
                && ApolloErrorCodes.Contains(code))
            {
                return new UsbPipeReply(UsbPipeOutcome.ApolloError, null, code);
            }
            return new UsbPipeReply(UsbPipeOutcome.BadReply);
        }
        catch (Exception)
        {
            return new UsbPipeReply(UsbPipeOutcome.BadReply);
        }
    }
}
