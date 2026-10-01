namespace MultiSeat.Shared.Models;

public enum NvencQualityPreset
{
    Latency  = 0,  // P1, no two-pass, no spatial AQ — lowest encode latency
    Balanced = 1,  // P4, quarter-res two-pass, spatial AQ — default
    Quality  = 2,  // P7, full-res two-pass, spatial AQ, higher VBV — best quality
}

public sealed class SeatRequest
{
    public required string AccountName { get; init; }

    private int _width = 1920;
    public int Width
    {
        get => _width;
        init => _width = Math.Clamp(value, 640, 7680);
    }

    private int _height = 1080;
    public int Height
    {
        get => _height;
        init => _height = Math.Clamp(value, 480, 4320);
    }

    private int _fps = 60;
    public int Fps
    {
        get => _fps;
        init => _fps = Math.Clamp(value, 1, 240);
    }

    public string? LaunchApp { get; init; }
    public NvencQualityPreset NvencPreset { get; init; } = NvencQualityPreset.Balanced;

    /// <summary>
    /// Optional DPI scale for the seat's desktop, in percent. Null uses the host default, or the
    /// width heuristic when there is none. Deliberately NOT clamped like the fields above: a
    /// scale RDP does not accept is rejected with an error, because rounding it to a neighbour
    /// would give the user a size they did not ask for without saying so.
    /// </summary>
    public int? ScaleFactor { get; init; }

    /// <summary>
    /// Whether the seat comes back by itself after the service or the PC restarts (issue #87).
    /// Seats live in memory, so without this a restart loses the seat and it has to be created
    /// again. True saves the seat to the autostart presets once it is Ready, exactly as
    /// PUT /api/seats/{id}/autostart does. False removes any saved preset for the account.
    ///
    /// Null, the default, changes nothing: a caller that does not know about this field keeps
    /// the old behaviour, where a preset saved earlier for the account stays as it is. The seat
    /// then reports whether such a preset exists, so the dashboard shows what will really happen
    /// at the next restart.
    /// </summary>
    public bool? AutoStart { get; init; }
}

/// <summary>
/// A new DPI scale for a live seat. Null clears the seat's override, so the host default or the
/// width heuristic applies again.
/// </summary>
public sealed class ScaleFactorRequest
{
    public int? ScaleFactor { get; init; }
}

public sealed class NvencPresetRequest
{
    public NvencQualityPreset Preset { get; init; } = NvencQualityPreset.Balanced;
}

/// <summary>
/// A new desktop size for a live seat. Applied by recreating the seat's RDP session at that
/// size, because nothing inside the session can change it.
/// </summary>
public sealed class ResolutionRequest
{
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed class AutoStartRequest
{
    public bool Enabled { get; init; }
}

public sealed class LaunchAppRequest
{
    public required string ExecutablePath { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
}
