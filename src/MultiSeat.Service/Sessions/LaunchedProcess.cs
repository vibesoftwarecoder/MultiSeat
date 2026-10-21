using Microsoft.Win32.SafeHandles;
using MultiSeat.Service.Interop;

namespace MultiSeat.Service.Sessions;

/// <summary>
/// A process launched by <see cref="ProcessInjector"/> whose handle was kept open, so its exit
/// code can still be read after it exits (issue #124).
///
/// The handle is the one CreateProcessAsUser returned, never one reopened by PID later. Windows
/// does not reuse a PID while a handle to the process is open, so this always names the process
/// we launched, and its exit code survives the exit for as long as the handle is held.
/// </summary>
public sealed class LaunchedProcess : IDisposable
{
    private readonly SafeProcessHandle _handle;

    internal LaunchedProcess(int processId, IntPtr processHandle)
    {
        ProcessId = processId;
        _handle = new SafeProcessHandle(processHandle, ownsHandle: true);
    }

    public int ProcessId { get; }

    /// <summary>
    /// The exit code once the process has exited. Null while it is still running, and null when
    /// the code cannot be read (the handle was disposed, or the call failed). Callers treat null
    /// as "unknown", never as a clean exit.
    /// </summary>
    public int? TryGetExitCode()
    {
        var added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            var handle = _handle.DangerousGetHandle();

            // Wait first: GetExitCodeProcess reports STILL_ACTIVE (259) for a running process,
            // and a process can also exit with 259 itself.
            if (Kernel32.WaitForSingleObject(handle, 0) != Kernel32.WAIT_OBJECT_0)
                return null;

            return Kernel32.GetExitCodeProcess(handle, out var code) ? unchecked((int)code) : null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            if (added) _handle.DangerousRelease();
        }
    }

    public void Dispose() => _handle.Dispose();
}
