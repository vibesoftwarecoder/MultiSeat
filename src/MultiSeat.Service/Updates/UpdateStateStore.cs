using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MultiSeat.Service.Configuration;

namespace MultiSeat.Service.Updates;

/// <summary>Backoff after failed checks, kept so a restart does not reset it.</summary>
public sealed class BackoffState
{
    public int ConsecutiveFailures { get; set; }

    /// <summary>No request before this time. Null means "not held back".</summary>
    public DateTimeOffset? NotBefore { get; set; }
}

/// <summary>What the cache remembers about one repository.</summary>
public sealed class RepoState
{
    /// <summary>The ETag of the last 200, verbatim (including a leading <c>W/</c>), sent back as <c>If-None-Match</c>.</summary>
    public string? ETag { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }

    public List<ReleaseCandidate> Candidates { get; set; } = [];

    /// <summary>
    /// The newest version seen when the feature first worked, for components whose installed
    /// version is unknown: only a release newer than this is worth a notice.
    /// </summary>
    public string? Baseline { get; set; }

    public BackoffState Backoff { get; set; } = new();

    /// <summary>The last failure, short and fixed-vocabulary. Null after a success.</summary>
    public string? LastError { get; set; }
}

/// <summary>Cache of one hashed <c>sunshine.exe</c>, valid while path, size and modification time are unchanged.</summary>
public sealed class InstalledExeHash
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime MtimeUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// The whole contents of <c>update-check.json</c>. Holds no secrets and no host or user data
/// beyond the path of the file that was hashed.
/// </summary>
public sealed class UpdateState
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Keyed by <see cref="UpdateRepos.Id"/>: <c>multiseat</c>, <c>apollovibe</c>, <c>moonlightvibe</c>.</summary>
    public Dictionary<string, RepoState> Repos { get; set; } = new(StringComparer.Ordinal);

    public InstalledExeHash? ApolloExeHash { get; set; }

    public RepoState GetOrAdd(UpdateComponent component)
    {
        var id = UpdateRepos.Id(component);
        if (!Repos.TryGetValue(id, out var repo))
            Repos[id] = repo = new RepoState();
        return repo;
    }
}

/// <summary>
/// Reads and writes the update-check cache at a path it is given
/// (<c>Constants.DefaultUpdateStatePath</c> in production). The cache is disposable: anything
/// wrong with the file means "start empty", never an error, and a failed write is reported and
/// swallowed so the check keeps running with what it has in memory.
///
/// Writes go through <see cref="AtomicFile"/> (a reader sees the old or the new file, never half)
/// and are then restricted to SYSTEM and Administrators the way <c>api-key.txt</c> is. The
/// restriction is applied after every write because the rename creates a new file object that
/// inherits the folder's ACL.
/// </summary>
public sealed class UpdateStateStore
{
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxCandidatesPerRepo = 100;
    private const int MaxStringLength = 512;

    private static readonly Regex ETagShape = new("^(W/)?\"[\\x21\\x23-\\x7E]*\"\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Hex64 = new("^[0-9A-Fa-f]{64}\\z", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        MaxDepth = 16,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly Func<string, Action<Exception>, bool> _restrict;
    private readonly Action<string>? _warn;
    private readonly object _writeLock = new();

    /// <param name="path">Full path of the JSON file; its folder is created on the first save.</param>
    /// <param name="restrictAcl">Applies the permissions; defaults to <c>SecureFile.TryRestrictToSystemAndAdmins</c>. Injected so tests can see it called.</param>
    /// <param name="warn">Receives one short line when a load or save problem is swallowed.</param>
    public UpdateStateStore(string path, Func<string, Action<Exception>, bool>? restrictAcl = null, Action<string>? warn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _restrict = restrictAcl ?? Storage.SecureFile.TryRestrictToSystemAndAdmins;
        _warn = warn;
    }

    public string Path => _path;

    /// <summary>
    /// The stored state, or an empty one when the file is missing, unreadable, corrupt, holds the
    /// wrong types, or carries a schema other than the current one (including a newer one: it
    /// is not ours to interpret). Never throws.
    /// </summary>
    public UpdateState Load()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) return new UpdateState();
            if (info.Length > MaxFileBytes)
            {
                Warn("update-check.json is too large; ignoring it");
                return new UpdateState();
            }

            var bytes = File.ReadAllBytes(_path);
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("schema", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var version) ||
                version != UpdateState.CurrentSchema)
            {
                Warn("update-check.json has an unknown schema; ignoring it");
                return new UpdateState();
            }

            var state = doc.RootElement.Deserialize<UpdateState>(JsonOptions);
            return state is null ? new UpdateState() : Sanitize(state);
        }
        catch (Exception ex)
        {
            Warn("update-check.json could not be read (" + ex.GetType().Name + "); starting empty");
            return new UpdateState();
        }
    }

    /// <summary>
    /// Write <paramref name="state"/> atomically and restrict the file. Returns false (after a
    /// warning) if anything fails; never throws.
    /// </summary>
    public bool Save(UpdateState state)
    {
        try
        {
            state.Schema = UpdateState.CurrentSchema;
            var json = JsonSerializer.Serialize(state, JsonOptions);

            lock (_writeLock)
            {
                var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                AtomicFile.WriteAllText(_path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                _restrict(_path, ex => Warn("could not restrict permissions on update-check.json (" + ex.GetType().Name + ")"));
            }
            return true;
        }
        catch (Exception ex)
        {
            Warn("update-check.json could not be written (" + ex.GetType().Name + ")");
            return false;
        }
    }

    // Deserialized data is untrusted too (a hand-edited or damaged file): keep only what is the
    // right shape, so later code never meets a header-injecting ETag, a 10 MB string or a
    // component id it does not know.
    private static UpdateState Sanitize(UpdateState state)
    {
        var clean = new UpdateState();
        foreach (var component in Enum.GetValues<UpdateComponent>())
        {
            if (!state.Repos.TryGetValue(UpdateRepos.Id(component), out var repo) || repo is null) continue;

            var r = clean.GetOrAdd(component);
            r.ETag = repo.ETag is not null && ETagShape.IsMatch(repo.ETag) ? repo.ETag : null;
            r.CheckedAt = repo.CheckedAt;
            r.Baseline = Cap(repo.Baseline);
            r.LastError = Cap(repo.LastError);
            r.Backoff = new BackoffState
            {
                ConsecutiveFailures = Math.Clamp(repo.Backoff?.ConsecutiveFailures ?? 0, 0, 1000),
                NotBefore = repo.Backoff?.NotBefore,
            };

            foreach (var c in (repo.Candidates ?? []).Where(c => c is not null).Take(MaxCandidatesPerRepo))
            {
                if (string.IsNullOrWhiteSpace(c.Tag) || c.Tag.Length > MaxStringLength) continue;
                r.Candidates.Add(new ReleaseCandidate
                {
                    Tag = c.Tag,
                    PublishedAt = c.PublishedAt,
                    TargetCommit = Cap(c.TargetCommit),
                    SunshineSha256 = c.SunshineSha256 is not null && Hex64.IsMatch(c.SunshineSha256)
                        ? c.SunshineSha256.ToLowerInvariant() : null,
                });
            }
        }

        var h = state.ApolloExeHash;
        if (h is not null && !string.IsNullOrWhiteSpace(h.Path) && h.Path.Length <= 1024 &&
            h.Size >= 0 && Hex64.IsMatch(h.Sha256 ?? string.Empty))
        {
            clean.ApolloExeHash = new InstalledExeHash
            {
                Path = h.Path,
                Size = h.Size,
                MtimeUtc = h.MtimeUtc,
                Sha256 = h.Sha256!.ToLowerInvariant(),
            };
        }
        return clean;
    }

    private static string? Cap(string? s) => s is null ? null : s.Length <= MaxStringLength ? s : s[..MaxStringLength];

    private void Warn(string message)
    {
        try { _warn?.Invoke(message); }
        catch { /* a logger must never break the cache */ }
    }
}
