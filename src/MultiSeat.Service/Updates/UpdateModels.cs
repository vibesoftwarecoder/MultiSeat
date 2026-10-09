using System.Text.Json.Serialization;

namespace MultiSeat.Service.Updates;

/// <summary>The three projects the update check knows about. Closed on purpose: see <see cref="UpdateRepos"/>.</summary>
public enum UpdateComponent
{
    MultiSeat,
    ApolloVibe,
    MoonlightVibe,
}

/// <summary>
/// What the dashboard is told about one component. The member names are spelled out because the
/// API's global enum converter writes C# names as they are (PascalCase), and the dashboard
/// contract is <c>upToDate</c>, <c>updateAvailable</c> and so on.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateStatus>))]
public enum UpdateStatus
{
    /// <summary>Installed equals the newest published release.</summary>
    [JsonStringEnumMemberName("upToDate")] UpToDate,
    /// <summary>A newer published release exists.</summary>
    [JsonStringEnumMemberName("updateAvailable")] UpdateAvailable,
    /// <summary>Installed is newer than the newest published release (a local build before release).</summary>
    [JsonStringEnumMemberName("ahead")] Ahead,
    /// <summary>The component is installed but its release could not be identified. Never an alarm.</summary>
    [JsonStringEnumMemberName("unknownInstalled")] UnknownInstalled,
    /// <summary>The component is not installed where MultiSeat looks for it.</summary>
    [JsonStringEnumMemberName("notInstalled")] NotInstalled,
    /// <summary>MoonlightVibe: the host cannot see the client version, so only the latest release is shown.</summary>
    [JsonStringEnumMemberName("latestOnly")] LatestOnly,
    /// <summary>No data yet, or no release tag parsed.</summary>
    [JsonStringEnumMemberName("unavailable")] Unavailable,
    /// <summary>Update checks are off.</summary>
    [JsonStringEnumMemberName("disabled")] Disabled,
}

/// <summary>How an installed version was learned. Serialized as <c>assembly</c>, <c>marker</c>, <c>marker-modified</c>, <c>release-hash</c>, <c>commit-match</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InstalledSource>))]
public enum InstalledSource
{
    /// <summary>MultiSeat's own running assembly.</summary>
    [JsonStringEnumMemberName("assembly")] Assembly,
    /// <summary>A <c>release.json</c> marker whose recorded hash equals the real <c>sunshine.exe</c>.</summary>
    [JsonStringEnumMemberName("marker")] Marker,
    /// <summary>A marker exists but <c>sunshine.exe</c> no longer matches it: the binary was replaced.</summary>
    [JsonStringEnumMemberName("marker-modified")] MarkerModified,
    /// <summary>The SHA-256 of <c>sunshine.exe</c> equals the one published in a release's notes.</summary>
    [JsonStringEnumMemberName("release-hash")] ReleaseHash,
    /// <summary>Built from the same source commit as a release. Says nothing about the binary itself.</summary>
    [JsonStringEnumMemberName("commit-match")] CommitMatch,
}

/// <summary>The installed side of one component, in the shape of the API's <c>installed</c> object.</summary>
public sealed record InstalledInfo(string? Version, string? Display, InstalledSource Source, string? Note);

/// <summary>The newest release, in the shape of the API's <c>latest</c> object. <see cref="ReleaseUrl"/> is built by us, never taken from GitHub.</summary>
public sealed record LatestInfo(string Version, string Tag, DateTimeOffset? PublishedAt, string ReleaseUrl);

/// <summary>
/// One component of <c>GET /api/system/updates</c>, field for field. Nothing in it comes
/// straight from GitHub's JSON: versions are re-rendered from the parsed tag and the URL is built
/// from the repository constant, so a hostile release cannot put text on the dashboard.
/// </summary>
public sealed record ComponentUpdateStatus(
    string Id,
    string Name,
    UpdateStatus Status,
    InstalledInfo? Installed,
    string? InstalledNote,
    LatestInfo? Latest,
    bool Announce,
    DateTimeOffset? CheckedAt,
    string? Error);

/// <summary>Whether an installed component could be identified.</summary>
public enum DetectionOutcome
{
    /// <summary><see cref="InstalledDetection.Version"/> is set and can be compared with the latest release.</summary>
    Identified,
    /// <summary>It is installed but could not be tied to a release. Maps to <see cref="UpdateStatus.UnknownInstalled"/>.</summary>
    Unknown,
    /// <summary>Not there. Maps to <see cref="UpdateStatus.NotInstalled"/>.</summary>
    NotInstalled,
}

/// <summary>What an installed-version detector found.</summary>
public sealed record InstalledDetection(
    DetectionOutcome Outcome,
    ReleaseVersion? Version,
    InstalledInfo? Installed,
    string? Note);

/// <summary>
/// A release as the state file keeps it: only what later steps need. No release notes and no
/// URL from GitHub. <see cref="SunshineSha256"/> is the ApolloVibe <c>sunshine.exe</c> hash found
/// in the notes.
/// </summary>
public sealed class ReleaseCandidate
{
    public string Tag { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
    public string? TargetCommit { get; set; }
    public string? SunshineSha256 { get; set; }
}

/// <summary>One asset of a GitHub release. <see cref="Digest"/> is GitHub's <c>sha256:...</c> for the asset (the zip, not the files inside it).</summary>
public sealed record GitHubAsset(string Name, string? Digest);

/// <summary>
/// The parts of a GitHub release this feature reads. <see cref="Body"/> is needed only to find
/// the ApolloVibe <c>sunshine.exe</c> hash and is never forwarded to the dashboard or the state
/// file. Every string is untrusted text from the internet.
/// </summary>
public sealed record GitHubRelease(
    string TagName,
    DateTimeOffset? PublishedAt,
    string? TargetCommitish,
    string? Body,
    IReadOnlyList<GitHubAsset> Assets,
    bool Draft,
    bool PreRelease);

/// <summary>Repository names and URLs. Constants only: see Constants.cs for why nothing here is configurable.</summary>
public static class UpdateRepos
{
    public static string Id(UpdateComponent c) => c switch
    {
        UpdateComponent.MultiSeat => "multiseat",
        UpdateComponent.ApolloVibe => "apollovibe",
        UpdateComponent.MoonlightVibe => "moonlightvibe",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static string DisplayName(UpdateComponent c) => c switch
    {
        UpdateComponent.MultiSeat => "MultiSeat",
        UpdateComponent.ApolloVibe => "ApolloVibe",
        UpdateComponent.MoonlightVibe => "MoonlightVibe",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static string Repo(UpdateComponent c) => c switch
    {
        UpdateComponent.MultiSeat => Shared.Constants.UpdateRepoMultiSeat,
        UpdateComponent.ApolloVibe => Shared.Constants.UpdateRepoApolloVibe,
        UpdateComponent.MoonlightVibe => Shared.Constants.UpdateRepoMoonlightVibe,
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>The one URL the client ever requests for a component.</summary>
    public static Uri ReleaseListUri(UpdateComponent c) =>
        new($"https://{Shared.Constants.UpdateApiHost}/repos/{Shared.Constants.UpdateRepoOwner}/{Repo(c)}/releases?per_page=30");

    /// <summary>
    /// Link to a release page, built from the repository constant and the tag, URL-encoded.
    /// GitHub's own <c>html_url</c> is never used.
    /// </summary>
    public static string ReleaseUrl(UpdateComponent c, string tag) =>
        $"https://github.com/{Shared.Constants.UpdateRepoOwner}/{Repo(c)}/releases/tag/{Uri.EscapeDataString(tag)}";
}

/// <summary>Clamping for <c>UpdateCheckIntervalHours</c>, applied where the value is used.</summary>
public static class UpdateIntervals
{
    public static int ClampHours(int hours) =>
        Math.Clamp(hours, Shared.Constants.MinUpdateCheckIntervalHours, Shared.Constants.MaxUpdateCheckIntervalHours);

    public static TimeSpan ToInterval(int hours) => TimeSpan.FromHours(ClampHours(hours));
}
