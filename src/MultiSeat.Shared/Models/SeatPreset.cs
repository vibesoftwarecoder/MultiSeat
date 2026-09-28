namespace MultiSeat.Shared.Models;

/// <summary>
/// Persisted seat definition. Survives service restarts.
/// Seats with AutoStart=true are provisioned automatically at service startup.
/// </summary>
public sealed class SeatPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AccountName { get; set; } = string.Empty;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    public bool AutoStart { get; set; } = false;
    public NvencQualityPreset NvencPreset { get; set; } = NvencQualityPreset.Balanced;

    /// <summary>
    /// The seat's DPI scale override, in percent, or null for none. A presets file written
    /// before this field existed has no such key and loads as null, which keeps the old
    /// behaviour for those seats.
    /// </summary>
    public int? ScaleFactor { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The autostart preset for a live seat. Every place that saves a preset goes through this,
    /// so a field added to the preset cannot be dropped by one caller that forgot to copy it —
    /// which would silently reset that setting on the next save.
    /// </summary>
    public static SeatPreset ForAutoStart(SeatInfo seat) => new()
    {
        AccountName = seat.AccountName,
        Width = seat.Width,
        Height = seat.Height,
        Fps = seat.Fps,
        AutoStart = true,
        NvencPreset = seat.NvencPreset,
        ScaleFactor = seat.ScaleFactorOverride,
    };
}
