using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MultiSeat.Service.Interop;

/// <summary>
/// P/Invoke for the few named-pipe calls the host Apollo control client needs. The pipe is opened
/// with CreateFile directly, not through NamedPipeClientStream's own connect, because the contract
/// with Apollo is stated in terms of the exact open flags and the exact Win32 error that came back
/// (file not found is "no pipe", pipe busy is "retry, bounded"), and the managed connect hides both.
/// </summary>
internal static partial class PipeApi
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    /// <summary>
    /// Flags of the OPEN call, not a step after it. Together they make the server's token for this
    /// client an identification-level token: the server can learn who connected but can never act
    /// as them.
    /// </summary>
    public const uint SECURITY_SQOS_PRESENT = 0x00100000;
    public const uint SECURITY_IDENTIFICATION = 0x00010000;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_PIPE_BUSY = 231;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WaitNamedPipe(string name, uint timeoutMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
