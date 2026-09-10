namespace MultiSeat.Shared.Models;

/// <summary>
/// Value object that uniquely identifies a process instance, protecting against PID reuse.
///
/// A process ID alone is not an identity: Windows recycles PIDs, so a PID captured when a seat
/// was provisioned may name a completely different process once the original exits. Pairing the
/// PID with the OS-reported start time distinguishes the original from any later occupant.
///
/// INVARIANT: two <see cref="ProcessIdentity"/> values with the same PID but different
/// <see cref="StartedAt"/> represent different process instances.
///
/// ⚠️ <see cref="StartedAt"/> is the time the OPERATING SYSTEM reports the process started
/// (<c>Process.StartTime</c>), never the time we happened to record it. A wall-clock stamp taken
/// at launch looks similar and is useless here — it cannot disagree with a recycled PID's real
/// start time, so a check built on one silently always passes.
///
/// Ported from @Dani6ca-T's MultiSeat-Extended as the minimal primitive, without the process
/// tracking subsystem built around it there. See issue #29.
/// </summary>
public readonly record struct ProcessIdentity : IComparable<ProcessIdentity>
{
    /// <summary>The operating system process ID.</summary>
    public int ProcessId { get; init; }

    /// <summary>
    /// The UTC time the OS reports the process started. If a PID exists but its start time
    /// differs from this, the PID was reused by a different process.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    public ProcessIdentity(int processId, DateTimeOffset startedAt)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), processId,
                "Process ID must be positive.");
        ProcessId = processId;
        StartedAt = startedAt;
    }

    /// <summary>
    /// True when the given PID and start time name this exact instance. Used to detect PID
    /// reuse: the PID existing is not enough, the start time has to agree too.
    /// </summary>
    public bool Matches(int pid, DateTimeOffset startTime) =>
        ProcessId == pid && StartedAt == startTime;

    public int CompareTo(ProcessIdentity other) =>
        ProcessId.CompareTo(other.ProcessId);

    public override string ToString() => $"PID {ProcessId} @ {StartedAt:O}";
}
