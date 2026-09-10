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

    // Networking
    public int PortBase { get; set; }
    public int ApolloProcessId { get; set; }

    /// <summary>
    /// The identity — PID plus the OS-reported start time — of the Apollo this seat launched.
    ///
    /// ⭐ This is what makes a kill safe when <c>ApolloManager</c>'s in-memory instance record is
    /// gone, which is exactly the state after a service restart. Without it the only survivor is
    /// <see cref="ApolloProcessId"/>, a bare number Windows is free to have handed to something
    /// else in the meantime, and terminating on that alone can kill an unrelated process tree.
    /// PR B narrowed that path to a process-name check; carrying the identity here closes it.
    ///
    /// Null when the start time could not be read at launch. ⛔ Never populate it with a
    /// substitute timestamp: an identity carrying a made-up time can compare equal to a recycled
    /// PID by coincidence, which is worse than having no identity at all.
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
