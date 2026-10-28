using System.Runtime.InteropServices;

namespace MultiSeat.Service.Interop;

/// <summary>
/// An open handle to the process that owns the client end of a connection. While this object lives the
/// process id cannot be given to another process, so everything read from it is about one process.
/// </summary>
internal interface IOwnerProcess : IDisposable
{
    /// <summary>When the process was created, as 100 ns ticks since 1601 (the FILETIME / LARGE_INTEGER epoch).</summary>
    long CreationTime { get; }

    /// <summary>The process's session id, or null if it cannot be read.</summary>
    uint? SessionId();

    /// <summary>The string SID of the user in the process's token, or null if it cannot be read.</summary>
    string? UserSid();
}

/// <summary>The production <see cref="IOwnerProcess"/>.</summary>
internal sealed partial class OwnerProcess : IOwnerProcess
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    private readonly uint _pid;
    private IntPtr _handle;

    public long CreationTime { get; }

    private OwnerProcess(uint pid, IntPtr handle, long creation)
    {
        _pid = pid;
        _handle = handle;
        CreationTime = creation;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>
    /// Opens the process and keeps it open. Null when it cannot be opened (it has exited, or it is not
    /// ours to look at) and when it has already exited but something still holds a handle to it.
    /// </summary>
    public static IOwnerProcess? Open(uint pid)
    {
        var handle = Kernel32.OpenProcess(ProcessQueryInformation | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        if (!GetProcessTimes(handle, out var creation, out var exit, out _, out _) || exit != 0 || creation <= 0)
        {
            Kernel32.CloseHandle(handle);
            return null;
        }
        return new OwnerProcess(pid, handle, creation);
    }

    public uint? SessionId() => Kernel32.ProcessIdToSessionId(_pid, out var s) ? s : null;

    public string? UserSid()
    {
        if (!AdvApi.OpenProcessToken(_handle, TokenQuery, out var token)) return null;
        using var safe = new SafeTokenHandle(token);
        return AdvApi.TryGetTokenUserSid(safe.DangerousGetHandle())?.Value;
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero) Kernel32.CloseHandle(h);
    }
}

internal static class ConsoleUser
{
    /// <summary>
    /// The SID of the user logged on at the given console session, or null (nobody is logged on, or the
    /// caller is not allowed to ask). Needs the SYSTEM account, which the service is.
    /// </summary>
    public static string? SidOf(uint session)
    {
        if (!WtsApi.WTSQueryUserToken(session, out var token)) return null;
        using var safe = new SafeTokenHandle(token);
        return AdvApi.TryGetTokenUserSid(safe.DangerousGetHandle())?.Value;
    }
}
