using System.Text;

namespace MultiSeat.Service.Configuration;

/// <summary>
/// Crash-safe replacement for direct <c>File.WriteAllText(target, ...)</c> calls on live
/// configuration/state files.
///
/// A direct overwrite truncates the target first and streams the new content after, so a
/// process crash or power loss in between leaves a torn file that the next reader (Apollo
/// at startup, the service itself after a reboot) parses as corrupt. This helper instead
/// writes the complete content to a temporary file in the SAME directory — same volume,
/// which rename atomicity requires — and then renames it over the target, which NTFS
/// performs as one atomic metadata operation. Readers therefore observe either the
/// previous complete file or the newly completed file, never a partial one.
///
/// What this does NOT establish: power-loss durability of the NEW content. Nothing here
/// calls FlushFileBuffers, so after a sudden power loss the target may still hold the old
/// content. The invariant is atomicity (old xor new), not durability.
///
/// Failure behavior: the temp file is deleted best-effort and the original exception
/// propagates — callers keep their existing error semantics, and a failed replacement
/// never leaves a partial target behind. Temp names are unique per call, so concurrent
/// writers cannot clobber each other's staging files (concurrency between writers is
/// still the callers' job — e.g. the per-seat lifecycle gate).
///
/// ACL note: the renamed file is a NEW file object — it inherits the directory ACL and
/// does NOT carry over ACEs previously granted on the target. Callers whose targets need
/// explicit grants (seat Modify on apps.json/sunshine_state.json) must re-grant AFTER the
/// move, exactly as with a direct write.
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Replace <paramref name="path"/> with <paramref name="contents"/> atomically.
    /// </summary>
    public static void WriteAllText(string path, string contents, Encoding encoding)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath)!;
        var tmp = Path.Combine(dir, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        File.WriteAllText(tmp, contents, encoding);
        try
        {
            File.Move(tmp, fullPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort — never mask the real error */ }
            throw;
        }
    }
}
