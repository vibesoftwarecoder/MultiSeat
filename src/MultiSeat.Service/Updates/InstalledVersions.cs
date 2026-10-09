using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiSeat.Service.Updates;

/// <summary>What an ApolloVibe check found, plus the (possibly refreshed) hash cache to store.</summary>
public sealed record ApolloDetectionResult(InstalledDetection Detection, InstalledExeHash? HashCache);

/// <summary>
/// Works out which release of each component is installed on this host. Local only: nothing
/// here touches the network, and the hash of <c>sunshine.exe</c> is never sent anywhere.
///
/// MultiSeat reads its own assembly. ApolloVibe cannot: its exe reports <c>2026.6.1</c> for ms3
/// through ms6 (the <c>-msN</c> is never compiled in) and <c>/serverinfo</c> reports a constant,
/// so it is identified by the bytes of <c>sunshine.exe</c>. MoonlightVibe runs on other devices
/// and has no detector at all.
/// </summary>
public static class InstalledVersions
{
    private const long MaxMarkerBytes = 64 * 1024;

    // "2026.6.1", "2026.6.1.681e36a8", "2026.6.1.dirty", "2026.6.1.681e36a8.dirty". The hash is at
    // least 7 hex digits so a numeric fourth part (a Windows file version "2026.6.1.0") is not one.
    private static readonly Regex PeVersion = new(
        @"^([0-9]+\.[0-9]+\.[0-9]+)(?:\.([0-9A-Fa-f]{7,40}))?(?:\.dirty)?\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex FullSha = new("^[0-9A-Fa-f]{40}\\z", RegexOptions.CultureInvariant);

    // ── MultiSeat ────────────────────────────────────────────────────

    /// <summary>
    /// The informational version of the RUNNING service assembly (for example
    /// <c>0.6.19+b7e6d9539</c>), the same attribute <c>--config</c> prints. The running binary is
    /// what matters: after an upgrade that has not restarted the service, it is the old one.
    /// </summary>
    public static string? ReadRunningInformationalVersion() =>
        typeof(InstalledVersions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>
    /// Parse a MultiSeat informational version. <c>1.0.0</c> is what a build without
    /// <c>version.txt</c> reports and matches no release, so it is "unknown", as is anything
    /// that does not parse.
    /// </summary>
    public static InstalledDetection DetectMultiSeat(string? informationalVersion)
    {
        var v = ReleaseVersion.TryParse(UpdateComponent.MultiSeat, informationalVersion);
        if (v is null)
            return new InstalledDetection(DetectionOutcome.Unknown, null, null,
                "MultiSeat's own version could not be read.");

        if (v.Display == "1.0.0")
            return new InstalledDetection(DetectionOutcome.Unknown, null, null,
                "This MultiSeat build carries the placeholder version 1.0.0, so it cannot be matched to a release.");

        var shortBuild = v.Build is { Length: > 0 } b ? b[..Math.Min(7, b.Length)] : null;
        var display = shortBuild is null ? v.Display : $"{v.Display} ({shortBuild})";
        return new InstalledDetection(DetectionOutcome.Identified, v,
            new InstalledInfo(v.Display, display, InstalledSource.Assembly, null), null);
    }

    // ── ApolloVibe ───────────────────────────────────────────────────

    /// <summary>
    /// Identify the installed ApolloVibe, in this order (first hit wins):
    /// not installed; marker <c>release.json</c> whose recorded hash equals the real one;
    /// a marker that no longer matches (<c>marker-modified</c>); SHA-256 equal to a release's;
    /// same source commit as a release (<c>commit-match</c>, never an exact release); else unknown.
    /// Never throws: a file that cannot be read or hashed is "unknown" with a note.
    /// </summary>
    /// <param name="exePath">The caller passes <c>MultiSeat:ApolloExePath</c>.</param>
    /// <param name="releases">Parsed candidates, with <see cref="ReleaseCandidate.SunshineSha256"/> and <see cref="ReleaseCandidate.TargetCommit"/>.</param>
    /// <param name="cachedHash">The stored hash cache, reused while path, size and mtime match.</param>
    /// <param name="readProductVersion">Reads the PE ProductVersion; injected for tests. Null uses <see cref="FileVersionInfo"/>.</param>
    public static ApolloDetectionResult DetectApollo(
        string exePath,
        IReadOnlyCollection<ReleaseCandidate> releases,
        InstalledExeHash? cachedHash,
        Func<string, string?>? readProductVersion = null)
    {
        try
        {
            return DetectApolloCore(exePath, releases, cachedHash, readProductVersion ?? ReadProductVersion);
        }
        catch (Exception ex)
        {
            return new ApolloDetectionResult(
                new InstalledDetection(DetectionOutcome.Unknown, null, null,
                    "Could not check the installed ApolloVibe (" + ex.GetType().Name + ")."),
                cachedHash);
        }
    }

    private static ApolloDetectionResult DetectApolloCore(
        string exePath, IReadOnlyCollection<ReleaseCandidate> releases, InstalledExeHash? cachedHash,
        Func<string, string?> readProductVersion)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return new ApolloDetectionResult(
                new InstalledDetection(DetectionOutcome.NotInstalled, null, null, "Not found at " + exePath + "."),
                cachedHash);
        }

        var hashResult = GetHash(exePath, cachedHash);
        if (hashResult.Hash is null)
        {
            return new ApolloDetectionResult(
                new InstalledDetection(DetectionOutcome.Unknown, null, null,
                    "Could not read sunshine.exe to identify it (" + hashResult.Error + ")."),
                cachedHash);
        }

        var hash = hashResult.Hash;
        var cache = hashResult.Cache;

        // 1 and 2: the marker. Its tag is trusted only while the exe still hashes to what it recorded.
        ReleaseVersion? modified = null;
        var marker = ReadMarker(exePath);
        if (marker is not null)
        {
            var tagVersion = ReleaseVersion.TryParse(UpdateComponent.ApolloVibe, marker.Tag);
            if (tagVersion is not null)
            {
                if (string.Equals(marker.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                {
                    return Done(DetectionOutcome.Identified, tagVersion, InstalledSource.Marker, null, cache);
                }
                // The marker is refuted, not discarded: exact release bytes or a source-commit match
                // below say more about THIS binary than a stale marker does, so they are tried first.
                modified = tagVersion;
            }
        }

        // 3: exact bytes published in a release's notes.
        var table = ApolloHashTable.Build(releases);
        if (table.TryGetValue(hash, out var byHash) &&
            ReleaseVersion.TryParse(UpdateComponent.ApolloVibe, byHash.Tag) is { } hashVersion)
        {
            return Done(DetectionOutcome.Identified, hashVersion, InstalledSource.ReleaseHash, null, cache);
        }

        // 4: same source commit. Says nothing about the binary, so it is labelled and never "release-hash".
        var pe = readProductVersion(exePath);
        var commit = ParsePeCommit(pe);
        if (commit is not null)
        {
            ReleaseVersion? best = null;
            foreach (var r in releases)
            {
                if (r.TargetCommit is null || !FullSha.IsMatch(r.TargetCommit)) continue;
                if (!r.TargetCommit.StartsWith(commit, StringComparison.OrdinalIgnoreCase)) continue;
                var v = ReleaseVersion.TryParse(UpdateComponent.ApolloVibe, r.Tag);
                if (v is not null && (best is null || v.CompareTo(best) > 0)) best = v;
            }
            if (best is not null)
            {
                return Done(DetectionOutcome.Identified, best, InstalledSource.CommitMatch,
                    $"Built from the same source commit as {best.Display}. The exe itself is not that release.", cache);
            }
        }

        // 5: the marker names a release but the exe is no longer that file and nothing else identifies it.
        if (modified is not null)
        {
            return Done(DetectionOutcome.Identified, modified, InstalledSource.MarkerModified,
                $"Based on {modified.Display}, but sunshine.exe was replaced after install.", cache);
        }

        // 6: installed, but not tied to anything published. The PE "2026.6.1" is deliberately not
        // used: it is the same for ms3 to ms6.
        return new ApolloDetectionResult(
            new InstalledDetection(DetectionOutcome.Unknown, null, null,
                "Built locally or not a published release (no hash match)."),
            cache);
    }

    private static ApolloDetectionResult Done(
        DetectionOutcome outcome, ReleaseVersion version, InstalledSource source, string? note, InstalledExeHash? cache) =>
        new(new InstalledDetection(outcome, version,
                new InstalledInfo(version.Display, version.Display, source, note), note),
            cache);

    /// <summary>The commit prefix in a PE ProductVersion like <c>2026.6.1.681e36a8.dirty</c>, or null when it has none.</summary>
    internal static string? ParsePeCommit(string? productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion)) return null;
        var m = PeVersion.Match(productVersion.Trim());
        return m.Success && m.Groups[2].Success ? m.Groups[2].Value : null;
    }

    private static string? ReadProductVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductVersion; }
        catch { return null; }
    }

    private sealed record Marker(string Tag, string Sha256);

    // release.json beside sunshine.exe: {"tag": "...", "commit": "...", "sunshineSha256": "..."}.
    // Read as untrusted: size-capped, tolerant of extra fields, ignored when malformed.
    private static Marker? ReadMarker(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            if (dir is null) return null;
            var path = Path.Combine(dir, Shared.Constants.ApolloReleaseMarkerFileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxMarkerBytes) return null;

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var tag = Str(doc.RootElement, "tag");
            var sha = Str(doc.RootElement, "sunshineSha256");
            if (tag is null || !ApolloHashTable.IsSha256(sha)) return null;
            return new Marker(tag, sha!);
        }
        catch
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private readonly record struct HashResult(string? Hash, InstalledExeHash? Cache, string? Error);

    /// <summary>
    /// SHA-256 of the exe, from the cache when path, size and mtime are unchanged. The file is
    /// opened with <c>FileShare.ReadWrite | FileShare.Delete</c> because a running Apollo holds it.
    /// </summary>
    private static HashResult GetHash(string path, InstalledExeHash? cached)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var before = new FileInfo(full);
            var size = before.Length;
            var mtime = before.LastWriteTimeUtc;

            if (cached is not null &&
                string.Equals(cached.Path, full, StringComparison.OrdinalIgnoreCase) &&
                cached.Size == size && cached.MtimeUtc == mtime &&
                ApolloHashTable.IsSha256(cached.Sha256))
            {
                return new HashResult(cached.Sha256.ToLowerInvariant(), cached, null);
            }

            string hex;
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, bufferSize: 81920))
            {
                hex = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }

            // If the file changed while it was being read, the hash is still the best answer for
            // this check but must not be cached against stats it no longer matches.
            var after = new FileInfo(full);
            var stable = after.Length == size && after.LastWriteTimeUtc == mtime;
            var cache = stable ? new InstalledExeHash { Path = full, Size = size, MtimeUtc = mtime, Sha256 = hex } : null;
            return new HashResult(hex, cache, null);
        }
        catch (Exception ex)
        {
            return new HashResult(null, null, ex.GetType().Name);
        }
    }
}
