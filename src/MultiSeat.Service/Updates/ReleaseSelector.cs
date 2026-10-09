namespace MultiSeat.Service.Updates;

/// <summary>
/// Picks the release to compare against, and classifies installed versus latest. Pure logic.
///
/// The newest release is the one with the highest parsed version, never the first in the list
/// and never the most recently created: GitHub's list is ordered by creation date, so a hotfix
/// cut for an older line would otherwise become "latest" and read as a downgrade.
/// </summary>
public static class ReleaseSelector
{
    /// <summary>
    /// Turn GitHub's releases into the compact candidates the state file keeps. Drafts and
    /// pre-releases are dropped here as well as in the client, and so are tags that do not match
    /// the component's grammar (<c>test-*</c>, legacy <c>moonlightvibe-win-*</c>). For ApolloVibe
    /// the <c>sunshine.exe</c> hash is read from the notes; the notes themselves are not kept.
    /// </summary>
    public static List<ReleaseCandidate> BuildCandidates(UpdateComponent component, IEnumerable<GitHubRelease> releases)
    {
        var result = new List<ReleaseCandidate>();
        foreach (var r in releases)
        {
            if (r.Draft || r.PreRelease) continue;
            if (ReleaseVersion.TryParse(component, r.TagName) is null) continue;

            result.Add(new ReleaseCandidate
            {
                Tag = r.TagName,
                PublishedAt = r.PublishedAt,
                TargetCommit = r.TargetCommitish,
                SunshineSha256 = component == UpdateComponent.ApolloVibe
                    ? ApolloHashTable.ExtractSunshineHash(r.Body)
                    : null,
            });
        }
        return result;
    }

    /// <summary>
    /// The candidate with the highest version, or null when none parses. Ties (the same version
    /// under two tags) go to the later publish date, then to the earlier list position, so the
    /// answer does not depend on hash-table or sort stability.
    /// </summary>
    public static ReleaseCandidate? SelectLatest(UpdateComponent component, IEnumerable<ReleaseCandidate> candidates)
    {
        ReleaseCandidate? best = null;
        ReleaseVersion? bestVersion = null;

        foreach (var c in candidates)
        {
            var v = ReleaseVersion.TryParse(component, c.Tag);
            if (v is null) continue;

            if (bestVersion is null)
            {
                best = c;
                bestVersion = v;
                continue;
            }

            var cmp = v.CompareTo(bestVersion);
            if (cmp > 0 || (cmp == 0 && (c.PublishedAt ?? DateTimeOffset.MinValue) > (best!.PublishedAt ?? DateTimeOffset.MinValue)))
            {
                best = c;
                bestVersion = v;
            }
        }
        return best;
    }

    /// <summary>
    /// Compare an installed version with the newest release. No latest means
    /// <see cref="UpdateStatus.Unavailable"/> whatever is installed; an installed version that
    /// could not be identified is <see cref="UpdateStatus.UnknownInstalled"/>, never an update.
    /// Installed greater than latest is <see cref="UpdateStatus.Ahead"/>.
    /// </summary>
    public static UpdateStatus Classify(ReleaseVersion? installed, ReleaseVersion? latest)
    {
        if (latest is null) return UpdateStatus.Unavailable;
        if (installed is null) return UpdateStatus.UnknownInstalled;

        var cmp = installed.CompareTo(latest);
        if (cmp < 0) return UpdateStatus.UpdateAvailable;
        return cmp > 0 ? UpdateStatus.Ahead : UpdateStatus.UpToDate;
    }
}
