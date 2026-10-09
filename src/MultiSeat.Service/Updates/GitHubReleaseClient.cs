using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiSeat.Service.Updates;

public enum FetchOutcome
{
    /// <summary>200 and a well-formed list.</summary>
    Ok,
    /// <summary>304: the stored ETag is still current.</summary>
    NotModified,
    /// <summary>403/429 with a rate-limit signal. <see cref="ReleaseFetchResult.RetryNotBefore"/> says when to try again.</summary>
    RateLimited,
    /// <summary>Anything else that went wrong. Recorded, never thrown.</summary>
    Failed,
}

/// <summary>
/// What one fetch produced. <see cref="Error"/> is a short fixed-vocabulary string: no stack
/// trace, no URL, no exception message (those can carry host or proxy names).
/// </summary>
public sealed record ReleaseFetchResult(
    FetchOutcome Outcome,
    IReadOnlyList<GitHubRelease> Releases,
    string? ETag,
    string? Error,
    DateTimeOffset? RetryNotBefore,
    int? HttpStatus)
{
    internal static ReleaseFetchResult Fail(string error, int? status = null) =>
        new(FetchOutcome.Failed, [], null, error, null, status);
}

/// <summary>
/// Asks GitHub's public API for one repository's release list. The only code in the service
/// that would talk to the internet, and nothing starts it yet.
///
/// What it sends: <c>GET https://api.github.com/repos/vibesoftwarecoder/{repo}/releases?per_page=30</c>
/// with <c>Accept</c>, <c>X-GitHub-Api-Version</c>, a generic <c>User-Agent</c> (no version, no host
/// name) and, when known, <c>If-None-Match</c> with the stored ETag verbatim. Never an
/// <c>Authorization</c> header and never cookies: the constructor refuses an <see cref="HttpClient"/>
/// that would add either. The repository names and host are constants, not settings.
///
/// Failure contract: every network, HTTP, size, encoding and JSON failure comes back as a
/// <see cref="ReleaseFetchResult"/>; only cancellation requested by the caller propagates.
/// Redirects are followed by hand (at most 3) and only to https://api.github.com/repos/... or
/// /repositories/...; the supplied client must have automatic redirects off, see
/// <see cref="CreateHandler"/>. No static state.
/// </summary>
public sealed class GitHubReleaseClient
{
    /// <summary>The <see cref="ReleaseFetchResult.Error"/> of a rate-limited fetch; the service tells it apart from other failures by this text.</summary>
    public const string RateLimitedError = "rate limited";

    public const int MaxRedirects = 3;
    public const long MaxBodyBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    private const int MaxTagLength = 128;
    private const int MaxBodyChars = 256 * 1024;
    private const int MaxReleases = 100;
    private const int MaxAssets = 100;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

    // A weak or strong entity tag exactly as HTTP writes it. Anything else is not sent back, which
    // also keeps a corrupted state file from putting control characters into a header.
    private static readonly Regex ETagShape = new("^(W/)?\"[\\x21\\x23-\\x7E]*\"\\z", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;

    public GitHubReleaseClient(HttpClient http, TimeSpan? requestTimeout = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        var d = http.DefaultRequestHeaders;
        if (d.Authorization is not null || d.ProxyAuthorization is not null || d.Contains("Cookie") || d.UserAgent.Count > 0)
            throw new ArgumentException(
                "The HttpClient adds Authorization, Cookie or User-Agent headers by default; the update check must send none of them.",
                nameof(http));

        _http = http;
        _timeout = requestTimeout ?? DefaultRequestTimeout;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// A handler for the HttpClient this class is given: no automatic redirects (they are
    /// followed here, with a host check), no cookies, the system's default proxy and normal
    /// certificate validation.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    /// <summary>
    /// Fetch one repository's releases. Pass the stored ETag (or null) to get a cheap 304.
    /// Never throws except <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<ReleaseFetchResult> FetchAsync(UpdateComponent component, string? etag, CancellationToken ct)
    {
        try
        {
            return await FetchCoreAsync(component, etag, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Whatever slipped past the specific handlers below: still recorded, never thrown.
            return ReleaseFetchResult.Fail("unexpected error (" + ex.GetType().Name + ")");
        }
    }

    private async Task<ReleaseFetchResult> FetchCoreAsync(UpdateComponent component, string? etag, CancellationToken ct)
    {
        var sendEtag = etag is not null && ETagShape.IsMatch(etag) ? etag : null;
        var uri = UpdateRepos.ReleaseListUri(component);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        for (var hop = 0; ; hop++)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(uri.AbsoluteUri)) return ReleaseFetchResult.Fail("redirect loop");

            using var request = BuildRequest(uri, sendEtag);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ReleaseFetchResult.Fail("timeout");
            }
            catch (HttpRequestException ex)
            {
                return ReleaseFetchResult.Fail("network error (" + ex.HttpRequestError + ")");
            }

            using (response)
            {
                var status = (int)response.StatusCode;

                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= MaxRedirects) return ReleaseFetchResult.Fail("too many redirects", status);
                    var next = ResolveRedirect(uri, response);
                    if (next is null) return ReleaseFetchResult.Fail("redirect refused", status);
                    uri = next;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (sendEtag is null) return ReleaseFetchResult.Fail("unexpected 304", status);
                    return new ReleaseFetchResult(FetchOutcome.NotModified, [], ReadETag(response) ?? sendEtag, null, null, status);
                }

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    var notBefore = RateLimitNotBefore(response);
                    if (notBefore is not null)
                        return new ReleaseFetchResult(FetchOutcome.RateLimited, [], null, RateLimitedError, notBefore, status);
                    return ReleaseFetchResult.Fail("HTTP " + status, status);
                }

                if (response.StatusCode != HttpStatusCode.OK)
                    return ReleaseFetchResult.Fail("HTTP " + status, status);

                // 200: read a capped body, then parse it.
                byte[] body;
                try
                {
                    var read = await ReadCappedAsync(response, timeout.Token).ConfigureAwait(false);
                    if (read.Error is not null) return ReleaseFetchResult.Fail(read.Error, status);
                    body = read.Bytes!;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return ReleaseFetchResult.Fail("timeout", status);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    return ReleaseFetchResult.Fail("body read failed", status);
                }

                var parsed = ParseReleases(body, out var parseError);
                return parsed is null
                    ? ReleaseFetchResult.Fail(parseError ?? "invalid response", status)
                    : new ReleaseFetchResult(FetchOutcome.Ok, parsed, ReadETag(response), null, null, status);
            }
        }
    }

    private static HttpRequestMessage BuildRequest(Uri uri, string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.TryAddWithoutValidation("User-Agent", Shared.Constants.UpdateUserAgent);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return request;
    }

    /// <summary>
    /// The target of a redirect, or null when it must be refused. Only https on the API host's
    /// default port, no credentials in the URL, and only the /repos/ and /repositories/ trees.
    /// </summary>
    private static Uri? ResolveRedirect(Uri current, HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null) return null;

        var target = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (target.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.Equals(target.Host, Shared.Constants.UpdateApiHost, StringComparison.OrdinalIgnoreCase)) return null;
        if (!target.IsDefaultPort) return null;
        if (!string.IsNullOrEmpty(target.UserInfo)) return null;

        var path = target.AbsolutePath;
        if (!path.StartsWith("/repos/", StringComparison.Ordinal) &&
            !path.StartsWith("/repositories/", StringComparison.Ordinal))
            return null;

        return target;
    }

    private static string? ReadETag(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("ETag", out var values)) return null;
        var value = values.FirstOrDefault();
        return value is not null && ETagShape.IsMatch(value) ? value : null;
    }

    /// <summary>
    /// When a 403/429 may be retried, or null if the response carries no rate-limit signal.
    /// <c>Retry-After</c> (seconds or an HTTP date) wins; otherwise
    /// <c>X-RateLimit-Remaining: 0</c> with <c>X-RateLimit-Reset</c> (epoch seconds).
    /// Capped at 24 hours so a bad header cannot park the check indefinitely.
    /// </summary>
    private DateTimeOffset? RateLimitNotBefore(HttpResponseMessage response)
    {
        var now = _time.GetUtcNow();
        DateTimeOffset? notBefore = null;

        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            notBefore = now + delta;
        else if (retryAfter?.Date is { } date)
            notBefore = date;
        else if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
                 remaining.FirstOrDefault()?.Trim() == "0" &&
                 response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) &&
                 long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) &&
                 epoch is >= 0 and <= 253402300799)
            notBefore = DateTimeOffset.FromUnixTimeSeconds(epoch);

        if (notBefore is null) return null;
        if (notBefore < now) notBefore = now;
        var latest = now + MaxRetryAfter;
        return notBefore > latest ? latest : notBefore;
    }

    private readonly record struct BodyRead(byte[]? Bytes, string? Error);

    private static async Task<BodyRead> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var declared = response.Content.Headers.ContentLength;
        if (declared is > MaxBodyBytes) return new BodyRead(null, "response too large");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (n == 0) break;
            if (buffer.Length + n > MaxBodyBytes) return new BodyRead(null, "response too large");
            buffer.Write(chunk, 0, n);
        }

        if (declared is { } d && d != buffer.Length) return new BodyRead(null, "truncated response");
        return new BodyRead(buffer.ToArray(), null);
    }

    /// <summary>
    /// Parse GitHub's JSON array. Returns null (with a reason) for anything that is not a
    /// list of release objects. Drafts and pre-releases are skipped, and so is any release whose
    /// flags or tag are not what GitHub documents: when in doubt a release is NOT offered.
    /// </summary>
    internal static List<GitHubRelease>? ParseReleases(byte[] utf8, out string? error)
    {
        error = null;

        string text;
        try
        {
            // A byte order mark is legal at the start of a UTF-8 document but is not part of its text.
            var start = utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF ? 3 : 0;
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(utf8, start, utf8.Length - start);
        }
        catch (DecoderFallbackException)
        {
            error = "invalid UTF-8";
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            error = "invalid JSON";
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                error = "unexpected JSON shape";
                return null;
            }

            var releases = new List<GitHubRelease>();
            var objects = 0;
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                objects++;
                if (releases.Count >= MaxReleases) continue;
                if (ParseRelease(item) is { } release) releases.Add(release);
            }

            // A non-empty array with no objects in it is some other API's answer, not an empty list.
            if (objects == 0 && root.GetArrayLength() > 0)
            {
                error = "unexpected JSON shape";
                return null;
            }
            return releases;
        }
    }

    private static GitHubRelease? ParseRelease(JsonElement e)
    {
        if (!TryBool(e, "draft", out var draft) || !TryBool(e, "prerelease", out var prerelease)) return null;
        if (draft || prerelease) return null;

        var tag = String(e, "tag_name");
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > MaxTagLength) return null;

        DateTimeOffset? published = null;
        if (String(e, "published_at") is { } p &&
            DateTimeOffset.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            published = parsed;

        var target = String(e, "target_commitish");
        if (target is { Length: > MaxTagLength }) target = null;

        var body = String(e, "body");
        if (body is { Length: > MaxBodyChars }) body = body[..MaxBodyChars];

        var assets = new List<GitHubAsset>();
        if (e.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in a.EnumerateArray())
            {
                if (assets.Count >= MaxAssets) break;
                if (asset.ValueKind != JsonValueKind.Object) continue;
                var name = String(asset, "name");
                if (string.IsNullOrEmpty(name) || name.Length > 256) continue;
                var digest = String(asset, "digest");
                assets.Add(new GitHubAsset(name, digest is { Length: <= 128 } ? digest : null));
            }
        }

        return new GitHubRelease(tag, published, target, body, assets, draft, prerelease);
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // The flag must be a real boolean; a missing or odd value makes the release unusable.
    private static bool TryBool(JsonElement e, string name, out bool value)
    {
        value = false;
        if (!e.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.True) { value = true; return true; }
        return v.ValueKind == JsonValueKind.False;
    }
}
