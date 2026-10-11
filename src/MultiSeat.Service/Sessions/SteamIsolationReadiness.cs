using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MultiSeat.Service.Interop;

namespace MultiSeat.Service.Sessions;

/// <summary>A service-owned event shared only with the exact seat account and host administrators.</summary>
internal sealed class SteamIsolationReadiness : IDisposable
{
    private readonly EventWaitHandle _event;
    internal string Name { get; }

    internal SteamIsolationReadiness(SecurityIdentifier seatSid)
    {
        Name = @"Global\MultiSeatSteamReady-" + Guid.NewGuid().ToString("N");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(BuildSddl(seatSid), 1,
                out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create Steam readiness event security.");
        using (descriptor)
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor.DangerousGetHandle(),
                InheritHandle = false
            };
            var handle = CreateEventW(ref attributes, true, false, Name);
            var error = Marshal.GetLastWin32Error();
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new Win32Exception(error, "Cannot create Steam readiness event.");
            }
            if (error == 183) // A fresh GUID must never reuse an existing object.
            {
                handle.Dispose();
                throw new InvalidOperationException("Steam readiness event name collision.");
            }
            try
            {
                _event = new EventWaitHandle(false, EventResetMode.ManualReset);
                var originalHandle = _event.SafeWaitHandle;
                _event.SafeWaitHandle = handle;
                originalHandle.Dispose();
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }

    internal static string BuildSddl(SecurityIdentifier sid) =>
        $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00100002;;;{sid.Value})";

    internal Task WaitAsync(IntPtr processHandle, TimeSpan timeout, CancellationToken ct) =>
        WaitCoreAsync(() => _event.WaitOne(0), () =>
        {
            var status = Kernel32.WaitForSingleObject(processHandle, 0);
            if (status == Kernel32.WAIT_OBJECT_0)
            {
                if (!Kernel32.GetExitCodeProcess(processHandle, out var code))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read Steam launcher exit code.");
                return code;
            }
            if (status != Kernel32.WAIT_TIMEOUT)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot wait for Steam launcher.");
            return null;
        }, timeout, ct);

    internal static async Task WaitCoreAsync(Func<bool> isReady, Func<uint?> exitCode,
        TimeSpan timeout, CancellationToken ct)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (isReady()) return;
            if (exitCode() is { } code)
                throw new InvalidOperationException($"Steam isolation launcher exited before initialization succeeded (code {code}).");
            if (elapsed.Elapsed >= timeout)
                throw new TimeoutException("Steam isolation initialization did not signal readiness before the timeout.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }

    public void Dispose() => _event.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    private sealed class SafeLocalMemoryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeLocalMemoryHandle() : base(true) { }
        protected override bool ReleaseHandle() => LocalFree(handle) == IntPtr.Zero;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string descriptor, uint revision, out SafeLocalMemoryHandle result, out uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEventW(ref SecurityAttributes attributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
