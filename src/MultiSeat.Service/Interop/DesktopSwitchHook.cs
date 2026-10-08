using System.Runtime.InteropServices;

namespace MultiSeat.Service.Interop;

/// <summary>
/// A source of "the input desktop changed" signals that a waiting loop can block on instead of
/// sleeping. The gate takes this interface so tests can drive it with a fake clock and scripted
/// events, and so a hook that never fires degrades to plain polling.
/// </summary>
internal interface IDesktopSwitchSignal
{
    /// <summary>
    /// Blocks for at most <paramref name="maxWait"/>. Returns early when a desktop switch is
    /// signalled. Returns how many switches happened since the previous call (0 on a plain
    /// timeout). A zero <paramref name="maxWait"/> only collects what is already pending.
    /// </summary>
    int Wait(TimeSpan maxWait);
}

/// <summary>
/// <c>SetWinEventHook(EVENT_SYSTEM_DESKTOPSWITCH)</c> on its own thread (issue #96).
///
/// WHY A DEDICATED THREAD: an out-of-context WinEvent hook is delivered through the message queue
/// of the thread that installed it, so that thread has to run a message loop for as long as the
/// hook lives. The hook is installed, pumped and removed on that one thread; no other thread ever
/// touches the hook handle. The callback does no Win32 work of its own beyond counting, signalling
/// and (optionally) calling <c>onSwitch</c>, which runs on the hook thread.
///
/// LIFETIME: <see cref="Dispose"/> posts WM_QUIT to the hook thread, which then unhooks and exits;
/// Dispose joins it for a bounded time. The thread is a background thread, so even if that join
/// were to time out the process can still exit, and Windows removes the hook with the process.
/// The native delegate is held in a field for the whole life of the hook: if it were collected,
/// the next event would call freed memory.
///
/// FAILURE MODE: if the hook cannot be installed (<see cref="Installed"/> is false) or never
/// fires, <see cref="Wait"/> simply sleeps for <c>maxWait</c> and reports 0 events. A caller
/// that also polls therefore behaves exactly as it did before the hook existed.
///
/// UNVERIFIED (to be answered by a live run, see the readiness probe): whether Windows raises
/// this event to a non-SYSTEM process in an RDP session for Winlogon / Secure Desktop switches.
/// </summary>
internal sealed class DesktopSwitchHook : IDesktopSwitchSignal, IDisposable
{
    private readonly Action<uint>? _onSwitch;
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private User32.WinEventProc? _proc; // pinned for the hook's life by being a field
    private Thread? _thread;
    private uint _threadId;
    private int _pending;
    private int _total;
    private int _disposed;

    /// <summary>True once the hook is installed. False if SetWinEventHook failed.</summary>
    public bool Installed { get; private set; }

    /// <summary>The Win32 error when the install failed, else 0.</summary>
    public int InstallError { get; private set; }

    /// <summary>Every desktop-switch event seen since the hook started.</summary>
    public int TotalEvents => Volatile.Read(ref _total);

    private DesktopSwitchHook(Action<uint>? onSwitch) => _onSwitch = onSwitch;

    /// <summary>
    /// Starts the hook thread and waits (bounded) for it to report whether the install worked.
    /// Never throws: a failure shows up as <see cref="Installed"/> = false.
    /// </summary>
    /// <param name="onSwitch">Optional. Called on the hook thread with the event's own
    /// timestamp (<c>dwmsEventTime</c>, ms since boot) for each desktop switch. Exceptions are
    /// swallowed.</param>
    public static DesktopSwitchHook Start(Action<uint>? onSwitch = null)
    {
        var hook = new DesktopSwitchHook(onSwitch);
        try
        {
            hook._thread = new Thread(hook.ThreadMain) { IsBackground = true, Name = "DesktopSwitchHook" };
            hook._thread.Start();
            hook._ready.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Installed stays false; callers poll.
        }
        return hook;
    }

    /// <summary>
    /// Creates an inert instance for tests: no thread, no native hook. Events are injected with
    /// <see cref="OnNativeEvent"/>.
    /// </summary>
    internal static DesktopSwitchHook CreateDetachedForTests(Action<uint>? onSwitch = null)
    {
        var hook = new DesktopSwitchHook(onSwitch) { Installed = true };
        return hook;
    }

    public int Wait(TimeSpan maxWait)
    {
        if (maxWait <= TimeSpan.Zero)
            return Interlocked.Exchange(ref _pending, 0);

        if (!Installed)
        {
            Thread.Sleep(maxWait);
            return 0;
        }

        _signal.WaitOne(maxWait);
        return Interlocked.Exchange(ref _pending, 0);
    }

    /// <summary>The WinEvent callback body. Runs on the hook thread; must never throw.</summary>
    internal void OnNativeEvent(uint eventType, uint eventTimeMs)
    {
        if (eventType != User32.EVENT_SYSTEM_DESKTOPSWITCH) return;
        try
        {
            Interlocked.Increment(ref _total);
            Interlocked.Increment(ref _pending);
            _signal.Set();
            _onSwitch?.Invoke(eventTimeMs);
        }
        catch
        {
            // A callback must not unwind into the OS.
        }
    }

    private void ThreadMain()
    {
        var hHook = IntPtr.Zero;
        try
        {
            // Touch the message queue first so it exists before anyone can PostThreadMessage to us.
            User32.PeekMessageW(out _, IntPtr.Zero, 0, 0, User32.PM_NOREMOVE);
            _threadId = User32.GetCurrentThreadId();

            _proc = (_, evt, _, _, _, _, time) => OnNativeEvent(evt, time);
            hHook = User32.SetWinEventHook(
                User32.EVENT_SYSTEM_DESKTOPSWITCH, User32.EVENT_SYSTEM_DESKTOPSWITCH,
                IntPtr.Zero, _proc, 0, 0, User32.WINEVENT_OUTOFCONTEXT);
            if (hHook == IntPtr.Zero)
            {
                InstallError = Marshal.GetLastWin32Error();
                return;
            }

            Installed = true;
            _ready.Set();

            // Message loop: returns 0 on WM_QUIT (posted by Dispose), -1 on error.
            while (User32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessageW(ref msg);
            }
        }
        catch
        {
            // Fall through to cleanup; Installed may already be true, which is harmless.
        }
        finally
        {
            try
            {
                if (hHook != IntPtr.Zero) User32.UnhookWinEvent(hHook);
                Installed = false;
                _ready.Set(); // also releases Start() on the failure path
            }
            catch (ObjectDisposedException) { /* Dispose gave up waiting for us; nothing left to signal */ }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        var t = _thread;
        var joined = true;
        if (t is { IsAlive: true } && _threadId != 0)
        {
            User32.PostThreadMessageW(_threadId, User32.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            joined = t.Join(TimeSpan.FromSeconds(2));
        }
        Installed = false;
        // If the thread did not exit in time it may still touch these; leave them to the GC.
        if (!joined) return;
        _signal.Dispose();
        _ready.Dispose();
    }
}
