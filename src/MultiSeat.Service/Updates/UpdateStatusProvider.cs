using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;

namespace MultiSeat.Service.Updates;

/// <summary>
/// The body of <c>GET /api/system/updates</c>, field for field. Serialized as is by the API
/// (camelCase, enums as strings).
/// </summary>
public sealed record UpdateStatusSnapshot(
    bool Enabled,
    int IntervalHours,
    DateTimeOffset? CheckedAt,
    DateTimeOffset? NextCheckAt,
    string? Error,
    IReadOnlyList<ComponentUpdateStatus> Components);

/// <summary>What the provider keeps per component between checks. Immutable, so a reader never sees half an update.</summary>
internal sealed record ComponentData(
    ReleaseCandidate? Latest,
    string? Baseline,
    DateTimeOffset? CheckedAt,
    string? Error);

/// <summary>Everything the provider needs to render a snapshot, swapped in whole.</summary>
internal sealed record ProviderData(
    IReadOnlyDictionary<UpdateComponent, ComponentData> Components,
    InstalledDetection MultiSeat,
    InstalledDetection? Apollo,
    DateTimeOffset? NextCheckAt,
    string? Error);

/// <summary>
/// Holds the latest update-check result in memory and turns it into the API's answer.
///
/// It never talks to GitHub and never touches the network: it is built from the state file at
/// start and from what <see cref="UpdateCheckService"/> publishes afterwards. The snapshot is
/// composed on every read from immutable data plus the CURRENT options, so switching the option
/// off shows <c>disabled</c> at once and a reader never needs a lock or sees a half-written state.
///
/// Status and <c>announce</c> follow the design (7.2): a component with a known installed
/// version announces only when an update is available; one without (ApolloVibe unidentified,
/// MoonlightVibe always) announces only when the newest release is newer than the baseline
/// recorded when checks first worked. Nothing announces on upToDate, ahead, notInstalled,
/// unavailable or disabled.
///
/// A failed check does NOT switch an announcement off. The last good result stays (a failure
/// never clears it), the status is recomputed from the CURRENT installed version, and the error
/// is returned beside it. So an update that was announced stays announced through an outage and
/// stops once it is installed; a component that never had a good result has no latest release
/// and so never announces.
/// </summary>
public sealed class UpdateStatusProvider
{
    private readonly IOptionsMonitor<MultiSeatOptions> _options;
    private readonly UpdateStateStore _store;
    private readonly Func<string?> _multiSeatVersion;
    private readonly UpdateState _state;
    private volatile ProviderData _data;

    public UpdateStatusProvider(
        IOptionsMonitor<MultiSeatOptions> options,
        UpdateStateStore store,
        Func<string?>? multiSeatVersion = null)
    {
        _options = options;
        _store = store;
        _multiSeatVersion = multiSeatVersion ?? InstalledVersions.ReadRunningInformationalVersion;
        _state = store.Load();
        _data = BuildData(_state, null, null);
    }

    /// <summary>The state object loaded at start. Only <see cref="UpdateCheckService"/> mutates it, one check at a time.</summary>
    internal UpdateState State => _state;

    internal UpdateStateStore Store => _store;

    internal DateTimeOffset? NextCheckAt => _data.NextCheckAt;

    /// <summary>Rebuild the in-memory data from <see cref="State"/>. Called after every change to it.</summary>
    internal void Publish(InstalledDetection? apollo, DateTimeOffset? nextCheckAt, string? error)
    {
        _data = BuildData(_state, apollo ?? _data.Apollo, nextCheckAt, error);
    }

    /// <summary>Change only the next scheduled check time.</summary>
    internal void SetNextCheck(DateTimeOffset? nextCheckAt)
    {
        var d = _data;
        _data = d with { NextCheckAt = nextCheckAt };
    }

    private ProviderData BuildData(UpdateState state, InstalledDetection? apollo, DateTimeOffset? nextCheckAt, string? error = null)
    {
        var map = new Dictionary<UpdateComponent, ComponentData>();
        foreach (var c in Enum.GetValues<UpdateComponent>())
        {
            var repo = state.Repos.TryGetValue(UpdateRepos.Id(c), out var r) ? r : null;
            ReleaseCandidate? latest = null;
            if (repo is not null)
            {
                var best = ReleaseSelector.SelectLatest(c, repo.Candidates);
                if (best is not null)
                    latest = new ReleaseCandidate { Tag = best.Tag, PublishedAt = best.PublishedAt };
            }
            map[c] = new ComponentData(latest, repo?.Baseline, repo?.CheckedAt, repo?.LastError);
        }

        InstalledDetection ms;
        try { ms = InstalledVersions.DetectMultiSeat(_multiSeatVersion()); }
        catch { ms = InstalledVersions.DetectMultiSeat(null); }

        return new ProviderData(map, ms, apollo, nextCheckAt, error);
    }

    /// <summary>The current answer. Reads memory and the options only.</summary>
    public UpdateStatusSnapshot GetSnapshot()
    {
        var opts = _options.CurrentValue;
        var d = _data;
        var hours = UpdateIntervals.ClampHours(opts.UpdateCheckIntervalHours);

        if (!opts.UpdateCheckEnabled)
        {
            return new UpdateStatusSnapshot(false, hours, null, null, null,
                [Compose(UpdateComponent.MultiSeat, d, enabled: false)]);
        }

        var components = Enum.GetValues<UpdateComponent>().Select(c => Compose(c, d, enabled: true)).ToList();
        var checkedAt = components.Where(c => c.CheckedAt is not null).Select(c => c.CheckedAt).DefaultIfEmpty(null).Min();
        var error = d.Error ?? components.Select(c => c.Error).FirstOrDefault(e => e is not null);
        return new UpdateStatusSnapshot(true, hours, checkedAt, d.NextCheckAt, error, components);
    }

    private const string MoonlightNote = "Runs on your other devices. MultiSeat cannot see its version.";

    private static ComponentUpdateStatus Compose(UpdateComponent c, ProviderData d, bool enabled)
    {
        var data = d.Components[c];
        var id = UpdateRepos.Id(c);
        var name = UpdateRepos.DisplayName(c);

        // What is installed here, if it can be known.
        InstalledDetection? det = c switch
        {
            UpdateComponent.MultiSeat => d.MultiSeat,
            UpdateComponent.ApolloVibe => d.Apollo ?? new InstalledDetection(DetectionOutcome.Unknown, null, null, "Not checked yet."),
            _ => null,
        };

        if (!enabled)
        {
            return new ComponentUpdateStatus(id, name, UpdateStatus.Disabled,
                det?.Installed, det is null ? MoonlightNote : det.Installed is null ? det.Note : null, null, false, null, null);
        }

        LatestInfo? latest = null;
        ReleaseVersion? latestVersion = null;
        if (data.Latest is { } l && ReleaseVersion.TryParse(c, l.Tag) is { } lv)
        {
            latestVersion = lv;
            latest = new LatestInfo(lv.Display, l.Tag, l.PublishedAt, UpdateRepos.ReleaseUrl(c, l.Tag));
        }

        UpdateStatus status;
        if (det?.Outcome == DetectionOutcome.NotInstalled) status = UpdateStatus.NotInstalled;
        else if (latestVersion is null) status = UpdateStatus.Unavailable;
        else if (det is null) status = UpdateStatus.LatestOnly;
        else if (det.Outcome == DetectionOutcome.Identified) status = ReleaseSelector.Classify(det.Version, latestVersion);
        else status = UpdateStatus.UnknownInstalled;

        bool announce;
        if (latestVersion is null) announce = false;
        else if (status == UpdateStatus.UpdateAvailable) announce = true;
        else if (status is UpdateStatus.UnknownInstalled or UpdateStatus.LatestOnly)
        {
            var baseline = ReleaseVersion.TryParse(c, data.Baseline);
            announce = baseline is not null && latestVersion.CompareTo(baseline) > 0;
        }
        else announce = false;

        var note = det is null ? MoonlightNote : det.Installed is null ? det.Note : null;
        return new ComponentUpdateStatus(id, name, status, det?.Installed, note,
            latest, announce, data.CheckedAt, data.Error);
    }
}
