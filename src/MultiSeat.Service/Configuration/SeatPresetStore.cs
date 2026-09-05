using System.Text;
using System.Text.Json;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Configuration;

/// <summary>
/// Persists seat presets to C:\ProgramData\MultiSeat\seat-presets.json.
/// Thread-safe; survives service restarts.
/// </summary>
public sealed class SeatPresetStore
{
    private readonly string _filePath;
    private readonly ILogger<SeatPresetStore> _logger;
    private readonly object _lock = new();
    private List<SeatPreset> _presets = [];

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    /// <param name="filePath">
    /// Where the presets live. Defaults to the ProgramData location the service uses; tests
    /// pass a temp path, because the real file holds the user's autostart seats and a test that
    /// wrote to it would delete them.
    /// </param>
    public SeatPresetStore(ILogger<SeatPresetStore> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? DefaultFilePath;
        Load();
    }

    internal const string DefaultFilePath = @"C:\ProgramData\MultiSeat\seat-presets.json";

    public IReadOnlyList<SeatPreset> GetAll()
    {
        lock (_lock) return [.. _presets];
    }

    public IReadOnlyList<SeatPreset> GetAutoStart() =>
        GetAll().Where(p => p.AutoStart).ToList();

    public SeatPreset? GetByAccount(string accountName) =>
        GetAll().FirstOrDefault(p =>
            p.AccountName.Equals(accountName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Create or update a preset (matched by AccountName).
    /// </summary>
    public SeatPreset Upsert(SeatPreset preset)
    {
        lock (_lock)
        {
            var idx = _presets.FindIndex(p =>
                p.AccountName.Equals(preset.AccountName, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0)
            {
                // Preserve original Id and CreatedAt
                preset.Id = _presets[idx].Id;
                preset.CreatedAt = _presets[idx].CreatedAt;
                _presets[idx] = preset;
            }
            else
            {
                _presets.Add(preset);
            }

            Save();
        }
        return preset;
    }

    public bool DeleteByAccount(string accountName)
    {
        lock (_lock)
        {
            var removed = _presets.RemoveAll(p =>
                p.AccountName.Equals(accountName, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            string json;
            try
            {
                json = File.ReadAllText(_filePath);
            }
            catch (Exception ex)
            {
                // The bytes could not be read at all (locked, denied, transient I/O).
                // That is NOT proof of corruption, so the file is left alone.
                _logger.LogWarning(ex,
                    "Could not read seat presets from {Path} — starting empty", _filePath);
                _presets = [];
                return;
            }

            List<SeatPreset>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<List<SeatPreset>>(json, _json);
            }
            catch (JsonException ex)
            {
                // Readable but not valid JSON: definitely corrupt. Move the original bytes
                // aside before recovering, otherwise the next Save would silently destroy
                // the evidence of what the presets used to be.
                QuarantineCorruptFile();
                _logger.LogWarning(ex,
                    "Seat presets at {Path} are corrupt and were quarantined — starting empty",
                    _filePath);
                _presets = [];
                return;
            }

            _presets = parsed ?? [];
            _logger.LogInformation("Loaded {Count} seat preset(s) from {Path}",
                _presets.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load seat presets from {Path} — starting empty", _filePath);
            _presets = [];
        }
    }

    /// <summary>
    /// Move a proven-corrupt presets file aside next to the original, preserving its exact
    /// bytes for diagnosis. The name carries a UTC timestamp (human-sortable) plus a random
    /// suffix so repeated corruptions never overwrite previous evidence — and the move never
    /// overwrites an existing artifact. Same directory, so no cross-volume move is involved.
    ///
    /// Best-effort by design: presets are autostart convenience, and refusing startup over
    /// them would be worse than the corruption. If the move fails the original stays and
    /// recovery proceeds empty in memory, exactly as before.
    /// </summary>
    private void QuarantineCorruptFile()
    {
        try
        {
            var dest = _filePath
                + ".corrupt-"
                + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
                + "-"
                + Guid.NewGuid().ToString("N")[..8];
            File.Move(_filePath, dest);
            _logger.LogWarning("Quarantined corrupt seat presets {Path} → {Quarantine}",
                _filePath, dest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not quarantine corrupt seat presets at {Path} — evidence left in place",
                _filePath);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            // Atomic write-then-rename so a crash mid-write can't truncate the existing file.
            // BOM-less UTF-8, exactly as the previous direct write produced.
            AtomicFile.WriteAllText(_filePath,
                JsonSerializer.Serialize(_presets, _json), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save seat presets to {Path}", _filePath);
        }
    }
}
