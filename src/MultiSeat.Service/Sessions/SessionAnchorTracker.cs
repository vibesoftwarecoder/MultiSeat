using System.Collections.Concurrent;

namespace MultiSeat.Service.Sessions;

/// <summary>
/// One running session anchor, as the tracker sees it. The real one wraps the process that
/// <see cref="SessionLauncher"/> starts with CreateProcessAsUser; tests use a fake.
/// </summary>
internal interface ISessionAnchor : IDisposable
{
    bool HasExited { get; }

    /// <summary>When this incarnation of the anchor was started.</summary>
    DateTime LaunchedUtc { get; }

    /// <summary>The exit code once it has exited, or null when it cannot be read.</summary>
    int? ExitCode { get; }

    /// <summary>End the anchor and wait briefly for it to go.</summary>
    void Kill();
}

/// <summary>What the health check learns when a tracked anchor is found to have exited.</summary>
internal readonly record struct ExitedSessionAnchor(TimeSpan Lifetime, int? ExitCode, bool ShouldRelaunch);

/// <summary>
/// The anchors of every seat session, keyed by session id.
///
/// The anchor is <c>timeout.exe /t 99999 /nobreak</c>, and 99999 seconds is the largest value
/// timeout.exe accepts - 27.8 hours. So every anchor exits on its own a little over a day after
/// its session was created. Before this class, the health check noticed that every 5 seconds,
/// logged a Warning each time and never replaced the anchor: 1,735 warnings in one day for one
/// session, in a 20 MB circular Application log that then lost everything older.
///
/// The rule here is that an exited anchor is reported ONCE per incarnation. <see cref="TakeIfExited"/>
/// removes the entry as it reports it, so the next check finds nothing to report until a new
/// anchor is tracked. That holds whether or not the caller manages to relaunch, which is what
/// makes a log storm impossible rather than merely unlikely. The health check first asks
/// <see cref="HasExitedAnchor"/>, which consumes nothing, and takes the anchor only once it holds
/// the seat's lifecycle gate, so a gate wait that times out leaves the anchor for the next check.
///
/// Concurrent because provisioning, teardown and the health-check loop all reach it from
/// different threads.
/// </summary>
internal sealed class SessionAnchorTracker
{
    /// <summary>
    /// An anchor that lived at least this long is relaunched when it exits. One that died sooner
    /// is not: something is ending it (a seat user in Task Manager, a crash, a policy), and
    /// relaunching on every 5-second check would trade a warning storm for a process storm. The
    /// session does not depend on it - the console-side mstsc keeps it Active - so giving up
    /// costs redundancy, not the seat.
    ///
    /// Far below the 27.8-hour expiry, so natural expiry always qualifies; far above the
    /// 5-second check interval, so a relaunch that keeps dying is attempted at most once per
    /// this interval, and only when the previous one survived it.
    /// </summary>
    internal static readonly TimeSpan MinLifetimeForRelaunch = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<int, ISessionAnchor> _anchors = new();

    internal static bool ShouldRelaunch(TimeSpan lifetime) => lifetime >= MinLifetimeForRelaunch;

    public bool IsTracked(int sessionId) => _anchors.ContainsKey(sessionId);

    /// <summary>
    /// Whether the anchor tracked for this session has exited, WITHOUT taking it. Nothing is
    /// removed or disposed, so this reports nothing: the health check calls it before it waits
    /// for the seat's lifecycle gate, and if that wait fails the anchor must still be here for
    /// the next check to find. <see cref="TakeIfExited"/>, under the gate, is what consumes it.
    ///
    /// Safe to call while teardown or a new Track disposes the same anchor on another thread;
    /// an anchor disposed under it counts as gone, not exited.
    /// </summary>
    public bool HasExitedAnchor(int sessionId) =>
        _anchors.TryGetValue(sessionId, out var anchor) && ReadHasExited(anchor);

    // Process.HasExited throws once the Process has been disposed. The tracker disposes an anchor
    // as it removes it, and a reader may still hold the instance it looked up just before. Such
    // an anchor is no longer tracked, so "not exited" is the right answer: there is nothing to
    // report or relaunch.
    private static bool ReadHasExited(ISessionAnchor anchor)
    {
        try
        {
            return anchor.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Track a newly launched anchor. An anchor already tracked for the same session id is
    /// released: it belongs to a session that no longer exists (Windows reuses session ids) or to
    /// an incarnation that has been replaced.
    /// </summary>
    public void Track(int sessionId, ISessionAnchor anchor)
    {
        ISessionAnchor? displaced = null;
        _anchors.AddOrUpdate(sessionId, anchor, (_, existing) =>
        {
            displaced = existing;
            return anchor;
        });

        if (displaced is not null && !ReferenceEquals(displaced, anchor))
            displaced.Dispose();
    }

    /// <summary>
    /// If the anchor tracked for this session has exited, stop tracking it and say how long it
    /// lived. Returns null when nothing is tracked or the anchor is still running.
    ///
    /// Removal is by instance, so two callers racing on the same exit get one report between
    /// them, and an anchor tracked in the meantime is never removed by mistake.
    /// </summary>
    public ExitedSessionAnchor? TakeIfExited(int sessionId, DateTime nowUtc)
    {
        if (!_anchors.TryGetValue(sessionId, out var anchor) || !ReadHasExited(anchor))
            return null;

        if (!_anchors.TryRemove(new KeyValuePair<int, ISessionAnchor>(sessionId, anchor)))
            return null;

        var lifetime = nowUtc - anchor.LaunchedUtc;
        if (lifetime < TimeSpan.Zero) lifetime = TimeSpan.Zero;
        var exitCode = anchor.ExitCode;
        anchor.Dispose();

        return new ExitedSessionAnchor(lifetime, exitCode, ShouldRelaunch(lifetime));
    }

    /// <summary>
    /// Teardown: stop tracking the session's anchor and end it if it is still running. Best
    /// effort - the session logoff that follows ends it anyway.
    /// </summary>
    public void RemoveAndKill(int sessionId)
    {
        if (!_anchors.TryRemove(sessionId, out var anchor))
            return;

        try
        {
            if (!anchor.HasExited)
                anchor.Kill();
        }
        catch { /* best effort */ }
        finally
        {
            anchor.Dispose();
        }
    }
}
