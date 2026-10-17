using System.Text.Json;

namespace MultiSeat.Service.SteamShim;

/// <summary>
/// One thing the installer changed, with what was there before. <see cref="Kind"/> is "shortcut" or
/// "registry". For a shortcut, <see cref="Location"/> is the file and <see cref="Shortcut"/> its
/// previous contents. For a registry value, <see cref="Location"/> is <c>Software\...|ValueName</c>
/// under the user whose SID is <see cref="Sid"/>; <see cref="Value"/> is the previous text, or null
/// with <see cref="Existed"/> false when the value was not there at all.
/// </summary>
internal sealed record ShimBackupEntry(
    string Kind, string Location, string? Sid, bool Existed, string? Value, ShortcutInfo? Shortcut)
{
    internal string Key => $"{Kind}|{Sid}|{Location}".ToLowerInvariant();
}

/// <summary>
/// The record that makes every shim edit undoable (<c>C:\ProgramData\MultiSeat\steam-shim-backup.json</c>).
/// The first original seen for a location is kept for good: applying the shim again to something it
/// already changed must never replace the real original with its own edit.
/// </summary>
internal sealed class SteamShimBackup
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, ShimBackupEntry> _entries = new();

    internal SteamShimBackup(string path)
    {
        _path = path;
        if (!File.Exists(path)) return;
        var loaded = JsonSerializer.Deserialize<List<ShimBackupEntry>>(File.ReadAllText(path), Json) ?? [];
        foreach (var entry in loaded) _entries[entry.Key] = entry;
    }

    internal IReadOnlyList<ShimBackupEntry> Entries
    {
        get { lock (_lock) return _entries.Values.ToList(); }
    }

    /// <summary>Records <paramref name="entry"/> unless that location already has an original.</summary>
    internal bool TryRecord(ShimBackupEntry entry)
    {
        lock (_lock)
        {
            if (!_entries.TryAdd(entry.Key, entry)) return false;
            Save();
            return true;
        }
    }

    internal void Forget(ShimBackupEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Remove(entry.Key)) Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries.Values.ToList(), Json));
        File.Move(temp, _path, overwrite: true);
    }
}
