using System.Diagnostics;
using MultiSeat.Service.Interop;
using Xunit;

namespace MultiSeat.Tests.Interop;

/// <summary>
/// <see cref="DesktopSwitchHook"/>. A real desktop switch cannot be produced here without moving
/// the console's input desktop, so the callback body is driven directly; the real hook is
/// checked for a clean start and stop only. Whether Windows delivers Winlogon switches to a
/// seat session's non-SYSTEM process is answered by a live probe run, not by these tests.
/// </summary>
public class DesktopSwitchHookTests
{
    [Fact]
    public void OnlyDesktopSwitchEventsAreCounted()
    {
        using var hook = DesktopSwitchHook.CreateDetachedForTests();
        hook.OnNativeEvent(eventType: 0x0003 /* EVENT_SYSTEM_FOREGROUND */, eventTimeMs: 1);
        Assert.Equal(0, hook.Wait(TimeSpan.Zero));
        Assert.Equal(0, hook.TotalEvents);

        hook.OnNativeEvent(User32.EVENT_SYSTEM_DESKTOPSWITCH, 2);
        hook.OnNativeEvent(User32.EVENT_SYSTEM_DESKTOPSWITCH, 3);
        Assert.Equal(2, hook.Wait(TimeSpan.Zero));
        Assert.Equal(0, hook.Wait(TimeSpan.Zero)); // consumed
        Assert.Equal(2, hook.TotalEvents);
    }

    [Fact]
    public void WaitReturnsAtOnceWhenAnEventIsAlreadyPending()
    {
        using var hook = DesktopSwitchHook.CreateDetachedForTests();
        hook.OnNativeEvent(User32.EVENT_SYSTEM_DESKTOPSWITCH, 1);

        var sw = Stopwatch.StartNew();
        var n = hook.Wait(TimeSpan.FromSeconds(10));
        Assert.Equal(1, n);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"blocked for {sw.Elapsed}");
    }

    [Fact]
    public void WaitWakesWhenAnEventArrivesFromAnotherThread()
    {
        using var hook = DesktopSwitchHook.CreateDetachedForTests();
        var t = Task.Run(() => { Thread.Sleep(100); hook.OnNativeEvent(User32.EVENT_SYSTEM_DESKTOPSWITCH, 1); });

        var sw = Stopwatch.StartNew();
        var n = hook.Wait(TimeSpan.FromSeconds(10));
        t.Wait();
        Assert.Equal(1, n);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"did not wake: {sw.Elapsed}");
    }

    [Fact]
    public void WaitTimesOutWithZeroWhenNothingHappens()
    {
        using var hook = DesktopSwitchHook.CreateDetachedForTests();
        var sw = Stopwatch.StartNew();
        Assert.Equal(0, hook.Wait(TimeSpan.FromMilliseconds(150)));
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void TheListenerSeesTheEventTimeAndItsExceptionsDoNotEscape()
    {
        var seen = new List<uint>();
        using var hook = DesktopSwitchHook.CreateDetachedForTests(t =>
        {
            seen.Add(t);
            throw new InvalidOperationException("listener bug");
        });

        hook.OnNativeEvent(User32.EVENT_SYSTEM_DESKTOPSWITCH, 12345); // must not throw
        Assert.Equal(new uint[] { 12345 }, seen);
        Assert.Equal(1, hook.Wait(TimeSpan.Zero)); // still counted
    }

    [Fact]
    public void TheRealHookStartsAndStopsCleanly()
    {
        var sw = Stopwatch.StartNew();
        var hook = DesktopSwitchHook.Start();
        if (!hook.Installed) Assert.NotEqual(0, hook.InstallError);

        GC.Collect();
        GC.WaitForPendingFinalizers(); // the native delegate must survive this while installed
        GC.Collect();

        hook.Dispose();
        hook.Dispose(); // idempotent
        Assert.False(hook.Installed);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"start/stop took {sw.Elapsed}");
        Assert.Equal(0, hook.TotalEvents); // nothing here moves the input desktop
    }

    [Fact]
    public void AnUninstalledHookDegradesToAPlainSleep()
    {
        // A hook that is not installed must still be a working wait. Simulated by disposing a
        // real one: Installed goes false and Wait must then just sleep and report nothing.
        var hook = DesktopSwitchHook.Start();
        hook.Dispose();

        var sw = Stopwatch.StartNew();
        Assert.Equal(0, hook.Wait(TimeSpan.FromMilliseconds(120)));
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(90));
    }
}
