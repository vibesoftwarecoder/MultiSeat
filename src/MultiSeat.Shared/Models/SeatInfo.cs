namespace MultiSeat.Shared.Models;

public enum SeatStatus
{
    Idle,
    Provisioning,
    Configuring,
    /// <summary>
    /// Automatic recovery is in progress: the seat's Windows session went Disconnected (a sleep
    /// drops mstsc) and the health check is rebuilding it. Distinct from Provisioning, which is a
    /// seat being created. Without this the dashboard reads Ready/Streaming for the 15-30s the
    /// repair takes, which is the one moment it is least true.
    /// </summary>
    Connecting,
    Ready,
    Streaming,
    TearingDown,
    Error
}

/// <summary>Where a seat's DPI scale factor came from.</summary>
public enum ScaleFactorSource
{
    /// <summary>Derived from the desktop's width — no override was set.</summary>
    Derived,

    /// <summary>The host-wide default, <c>MultiSeat:DefaultScaleFactor</c>.</summary>
    HostDefault,

    /// <summary>The seat's own override.</summary>
    Seat,
}

public sealed class SeatInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string AccountName { get; init; }
    public int SessionId { get; set; } = -1;
    public SeatStatus Status { get; set; } = SeatStatus.Idle;

    // Display
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    public string? DisplayDevicePath { get; set; }

    /// <summary>
    /// Set at provisioning when <see cref="Fps"/> exceeds the effective refresh rate DWM
    /// composes RDP sessions at host-wide (<c>MultiSeatOptions.DwmFrameIntervalMs</c>, one value
    /// shared by every seat). Apollo still advertises the requested fps to the client regardless
    /// — the seat is silently capped below it (issue #70). Null when there is no mismatch.
    /// </summary>
    public string? EffectiveRefreshRateWarning { get; set; }

    /// <summary>
    /// The seat's own DPI scale override, in percent, or null to use the host default or the
    /// width heuristic. This is what persists in the seat's preset (issue #70).
    /// </summary>
    public int? ScaleFactorOverride { get; set; }

    /// <summary>
    /// The DPI scale this seat's session was last connected with — what is actually in effect,
    /// whatever its source. Set by SeatManager each time it resolves the seat's geometry.
    /// </summary>
    public int ScaleFactor { get; set; } = 100;

    /// <summary>Where <see cref="ScaleFactor"/> came from.</summary>
    public ScaleFactorSource ScaleFactorSource { get; set; } = ScaleFactorSource.Derived;

    /// <summary>
    /// The DPI scale the session was last READ running at, in percent — what Windows applied, not
    /// what <see cref="ScaleFactor"/> says was asked for (issue #93). Null when it has not been
    /// read since the session was last launched or reconnected, or could not be read.
    /// </summary>
    public int? AppliedScaleFactor { get; set; }

    /// <summary>When <see cref="AppliedScaleFactor"/> was read.</summary>
    public DateTimeOffset? AppliedScaleCheckedAt { get; set; }

    /// <summary>
    /// True when the session was read running at a different scale than <see cref="ScaleFactor"/>.
    /// False when it matches or has not been read, so a missing reading never raises an alarm.
    /// </summary>
    public bool ScaleMismatch => AppliedScaleFactor is { } applied && applied != ScaleFactor;

    /// <summary>
    /// A plain-language note on the last reading, for the dashboard and the API: why the scale
    /// the session runs at is not the one asked for, that it could not be read, or that the
    /// session's system scale still lags behind until the seat user signs out. Null when the
    /// reading matches in full or there is no reading. Set together with
    /// <see cref="AppliedScaleFactor"/>.
    /// </summary>
    public string? ScaleNote { get; set; }

    // Networking
    public int PortBase { get; set; }
    public int ApolloProcessId { get; set; }

    /// <summary>
    /// The identity — PID plus the OS-reported start time — of the Apollo this seat launched.
    ///
    /// It is the seat's own record of which process it owns, so a client can see it and a kill
    /// has a second source to verify against. <see cref="ApolloProcessId"/> alone is a bare
    /// number Windows is free to have handed to something else, and terminating on that can kill
    /// an unrelated process tree.
    ///
    /// ⚠️ This was first justified as covering "the instance record is gone after a service
    /// restart, but the seat survives". That is NOT true — seats are in-memory only, with no
    /// persistence and no restore, so the seat and the instance record are lost together. The
    /// field is genuinely useful; that particular argument for it was wrong.
    ///
    /// ⛔ It must be rewritten wherever <see cref="ApolloProcessId"/> is, and cleared wherever
    /// that is cleared. A restart that advanced the PID while leaving this pointing at the dead
    /// process made <c>IsAlive</c> report a healthy Apollo as dead and made teardown leak the
    /// live one.
    ///
    /// Null when the start time could not be read. ⛔ Never populate it with a substitute
    /// timestamp: an identity carrying a made-up time can compare equal to a recycled PID by
    /// coincidence, which is worse than having no identity at all.
    /// </summary>
    public ProcessIdentity? ApolloIdentity { get; set; }

    // Emulator netplay — RetroArch host port for this seat (PortBase + offset; 0 = disabled).
    // Seats connect to each other over loopback at 127.0.0.1:<this port>.
    public int RetroArchNetplayPort { get; set; }

    // Audio — game output (audiomode:i:1 makes host devices visible in RDP session)
    public string? AudioGameRenderDeviceId { get; set; }     // session default render → Apollo loopback-captures for audio_sink
    public string? AudioGameRenderFriendlyName { get; set; } // friendly name → Apollo audio_sink

    // Audio — mic routing (Moonlight mic → Steam Streaming Microphone → games)
    public string? AudioCaptureDeviceId { get; set; } // "Microphone (Steam Streaming Microphone)" device ID → session default capture
    public int VacCableIndex { get; set; } = -1;

    // Input
    public int ViGEmControllerIndex { get; set; } = -1;

    // Lifecycle
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadyAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? LaunchApp { get; set; }

    // Preset
    public bool AutoStart { get; set; } = false;
    public NvencQualityPreset NvencPreset { get; set; } = NvencQualityPreset.Balanced;

    // Granular provisioning progress — set at each major step, cleared on Ready/Error
    public string? ProvisioningStep { get; set; }
}
