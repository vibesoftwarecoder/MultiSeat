using System.Net;
using System.Reflection;
using System.Text;
using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

/// <summary>
/// The GitHub client against a fake HttpMessageHandler (the pattern of ApolloReadinessTests).
/// No test here opens a socket: the only handler ever used to send is the fake.
/// </summary>
public class GitHubReleaseClientTests
{
    private const UpdateComponent MS = UpdateComponent.MultiSeat;

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Seen(Uri Uri, Dictionary<string, string> Headers);

    private sealed class Handler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Snapshot now: the request is disposed after the call.
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            lock (Requests) Requests.Add(new Seen(request.RequestUri!, headers));
            return await respond(request, Requests.Count, ct);
        }
    }

    private static (GitHubReleaseClient Client, Handler Handler) Make(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null)
    {
        var handler = new Handler(respond);
        var client = new GitHubReleaseClient(new HttpClient(handler), timeout, new FixedTime(Now));
        return (client, handler);
    }

    private static (GitHubReleaseClient Client, Handler Handler) Make(Func<HttpResponseMessage> respond, TimeSpan? timeout = null) =>
        Make((_, _, _) => Task.FromResult(respond()), timeout);

    private static string One(string tag = "v0.6.19", string extra = "") =>
        $$"""
        {"tag_name":"{{tag}}","draft":false,"prerelease":false,"published_at":"2026-10-09T10:20:56Z",
         "target_commitish":"master","body":"notes","assets":[{"name":"multiseat-windows-x64.zip","digest":"sha256:abc"}]{{extra}}}
        """;

    private static HttpResponseMessage Ok(string json, string? etag = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null) r.Headers.TryAddWithoutValidation("ETag", etag);
        return r;
    }

    private static HttpResponseMessage Redirect(string location, HttpStatusCode code = HttpStatusCode.MovedPermanently)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.TryAddWithoutValidation("Location", location);
        return r;
    }

    // ── Success and the exact request ─────────────────────────────────

    [Fact]
    public async Task Ok_ParsesReleases_AndKeepsTheETagVerbatim()
    {
        var (client, _) = Make(() => Ok("[" + One() + "]", "W/\"bcd412d8\""));

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Ok, r.Outcome);
        Assert.Equal("W/\"bcd412d8\"", r.ETag);
        Assert.Null(r.Error);
        var release = Assert.Single(r.Releases);
        Assert.Equal("v0.6.19", release.TagName);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 10, 20, 56, TimeSpan.Zero), release.PublishedAt);
        Assert.Equal("master", release.TargetCommitish);
        Assert.Equal("notes", release.Body);
        var asset = Assert.Single(release.Assets);
        Assert.Equal(new GitHubAsset("multiseat-windows-x64.zip", "sha256:abc"), asset);
    }

    [Theory]
    [InlineData(UpdateComponent.MultiSeat, "MultiSeat")]
    [InlineData(UpdateComponent.ApolloVibe, "ApolloVibe")]
    [InlineData(UpdateComponent.MoonlightVibe, "MoonlightVibe")]
    public async Task Request_HasTheExactHeaders_AndOnlyTheAllowListedUrl(UpdateComponent c, string repo)
    {
        var (client, handler) = Make(() => Ok("[]"));

        await client.FetchAsync(c, null, default);

        var seen = Assert.Single(handler.Requests);
        Assert.Equal("https", seen.Uri.Scheme);
        Assert.Equal("api.github.com", seen.Uri.Host);
        Assert.Equal(443, seen.Uri.Port);
        Assert.Equal($"/repos/vibesoftwarecoder/{repo}/releases", seen.Uri.AbsolutePath);
        Assert.Equal("?per_page=30", seen.Uri.Query);

        Assert.Equal("application/vnd.github+json", seen.Headers["Accept"]);
        Assert.Equal("2022-11-28", seen.Headers["X-GitHub-Api-Version"]);
        // Generic: no version number, no host name. GitHub refuses a request with no User-Agent.
        Assert.Equal("MultiSeat-update-check", seen.Headers["User-Agent"]);
        Assert.False(seen.Headers.ContainsKey("If-None-Match"));
        // Nothing that identifies the caller or authenticates it.
        Assert.False(seen.Headers.ContainsKey("Authorization"));
        Assert.False(seen.Headers.ContainsKey("Proxy-Authorization"));
        Assert.False(seen.Headers.ContainsKey("Cookie"));
        Assert.False(seen.Headers.ContainsKey("Referer"));
        Assert.False(seen.Headers.ContainsKey("From"));
        Assert.Equal(["Accept", "User-Agent", "X-GitHub-Api-Version"], seen.Headers.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("W/\"bcd412d8\"")]
    [InlineData("\"strong-etag\"")]
    [InlineData("W/\"\"")]
    public async Task StoredETag_IsSentBackVerbatim(string etag)
    {
        var (client, handler) = Make(() => Ok("[]"));

        await client.FetchAsync(MS, etag, default);

        Assert.Equal(etag, Assert.Single(handler.Requests).Headers["If-None-Match"]);
    }

    [Theory]
    [InlineData("no-quotes")]
    [InlineData("\"unterminated")]
    [InlineData("\"a\r\nX-Injected: 1\"")]
    [InlineData("W/\"a\"\"b\"")]
    [InlineData("")]
    public async Task MalformedStoredETag_IsNotSent(string etag)
    {
        var (client, handler) = Make(() => Ok("[]"));

        await client.FetchAsync(MS, etag, default);

        Assert.False(Assert.Single(handler.Requests).Headers.ContainsKey("If-None-Match"));
    }

    [Fact]
    public async Task Ok_IgnoresAMalformedResponseETag()
    {
        var (client, _) = Make(() => Ok("[]", "not-an-etag"));

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Ok, r.Outcome);
        Assert.Null(r.ETag);
    }

    // ── 304 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task NotModified_KeepsTheETag_AndCarriesNoReleases()
    {
        var (client, handler) = Make(() => new HttpResponseMessage(HttpStatusCode.NotModified));

        var r = await client.FetchAsync(MS, "W/\"abc\"", default);

        Assert.Equal(FetchOutcome.NotModified, r.Outcome);
        Assert.Equal("W/\"abc\"", r.ETag);
        Assert.Empty(r.Releases);
        Assert.Null(r.Error);
        Assert.Equal(304, r.HttpStatus);
        Assert.Equal("W/\"abc\"", handler.Requests[0].Headers["If-None-Match"]);
    }

    [Fact]
    public async Task NotModified_WhenNoETagWasSent_IsAFailure()
    {
        var (client, _) = Make(() => new HttpResponseMessage(HttpStatusCode.NotModified));

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
    }

    // ── Rate limits ───────────────────────────────────────────────────

    [Fact]
    public async Task Forbidden_WithRemainingZero_UsesTheResetTime()
    {
        var reset = Now.AddMinutes(37);
        var (client, _) = Make(() =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            r.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
            return r;
        });

        var res = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.RateLimited, res.Outcome);
        Assert.Equal(reset, res.RetryNotBefore);
        Assert.Equal(403, res.HttpStatus);
        Assert.Empty(res.Releases);
    }

    [Fact]
    public async Task TooManyRequests_WithRetryAfterSeconds_WaitsThatLong()
    {
        var (client, _) = Make(() =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.TryAddWithoutValidation("Retry-After", "120");
            return r;
        });

        var res = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.RateLimited, res.Outcome);
        Assert.Equal(Now.AddSeconds(120), res.RetryNotBefore);
    }

    [Fact]
    public async Task Forbidden_WithRetryAfterDate_WaitsUntilThen()
    {
        var when = Now.AddMinutes(5);
        var (client, _) = Make(() =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.TryAddWithoutValidation("Retry-After", when.ToString("R"));
            return r;
        });

        var res = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.RateLimited, res.Outcome);
        Assert.Equal(when, res.RetryNotBefore);
    }

    [Fact]
    public async Task RetryAfter_BeatsTheResetHeader()
    {
        var (client, _) = Make(() =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.TryAddWithoutValidation("Retry-After", "60");
            r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            r.Headers.TryAddWithoutValidation("X-RateLimit-Reset", Now.AddHours(3).ToUnixTimeSeconds().ToString());
            return r;
        });

        var res = await client.FetchAsync(MS, null, default);

        Assert.Equal(Now.AddSeconds(60), res.RetryNotBefore);
    }

    [Fact]
    public async Task RateLimitWait_IsCappedAtADay_AndNeverInThePast()
    {
        var (far, _) = Make(() =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.TryAddWithoutValidation("Retry-After", "99999999");
            return r;
        });
        var (past, _) = Make(() =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            r.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1");
            return r;
        });

        Assert.Equal(Now.AddHours(24), (await far.FetchAsync(MS, null, default)).RetryNotBefore);
        Assert.Equal(Now, (await past.FetchAsync(MS, null, default)).RetryNotBefore);
    }

    [Theory]
    [InlineData(403, null)]
    [InlineData(403, "5")]
    [InlineData(429, null)]
    public async Task Forbidden_OrTooMany_WithoutARateLimitSignal_IsAPlainFailure(int status, string? remaining)
    {
        var (client, _) = Make(() =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)status);
            if (remaining is not null) r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
            return r;
        });

        var res = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, res.Outcome);
        Assert.Null(res.RetryNotBefore);
        Assert.Equal($"HTTP {status}", res.Error);
    }

    // ── Every failure is a result, none is an exception ───────────────

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(404)]
    [InlineData(401)]
    [InlineData(410)]
    [InlineData(204)]
    public async Task OtherStatuses_AreRecordedAsFailures(int status)
    {
        var (client, _) = Make(() => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("x") });

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal($"HTTP {status}", r.Error);
        Assert.Equal(status, r.HttpStatus);
        Assert.Empty(r.Releases);
        Assert.Null(r.ETag);
    }

    public static IEnumerable<object[]> ThrowingHandlers()
    {
        yield return [new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known", new System.Net.Sockets.SocketException(11001)), "network error (NameResolutionError)"];
        yield return [new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established", null), "network error (SecureConnectionError)"];
        yield return [new HttpRequestException(HttpRequestError.ConnectionError, "refused", null), "network error (ConnectionError)"];
        yield return [new IOException("The response ended prematurely"), "unexpected error (IOException)"];
        yield return [new InvalidOperationException("boom"), "unexpected error (InvalidOperationException)"];
        yield return [new NotSupportedException("boom"), "unexpected error (NotSupportedException)"];
        yield return [new TimeoutException(), "unexpected error (TimeoutException)"];
    }

    [Theory]
    [MemberData(nameof(ThrowingHandlers))]
    public async Task ExceptionsFromTheNetworkStack_NeverEscape(Exception thrown, string expectedError)
    {
        var (client, _) = Make((_, _, _) => throw thrown);

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal(expectedError, r.Error);
        // The message of the exception (which can carry host or proxy names) is not recorded.
        Assert.DoesNotContain(thrown.Message, r.Error);
    }

    [Fact]
    public async Task HungServer_TimesOut_AsAResult()
    {
        var (client, _) = Make(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("[]");
        }, timeout: TimeSpan.FromMilliseconds(150));

        var r = await WithinAsync(client.FetchAsync(MS, null, default));

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal("timeout", r.Error);
    }

    [Fact]
    public async Task BodyThatNeverFinishes_TimesOut_AsAResult()
    {
        var (client, _) = Make(() =>
        {
            var content = new StreamContent(new HangingStream());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }, timeout: TimeSpan.FromMilliseconds(150));

        var r = await WithinAsync(client.FetchAsync(MS, null, default));

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal("timeout", r.Error);
    }

    [Fact]
    public async Task CallerCancellation_Propagates_AndIsNotRecorded()
    {
        using var cts = new CancellationTokenSource();
        var (client, _) = Make(async (_, _, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("[]");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync(MS, null, cts.Token));
    }

    [Fact]
    public async Task AlreadyCancelledCaller_SendsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (client, handler) = Make(() => Ok("[]"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync(MS, null, cts.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TruncatedBody_ThatThrowsMidRead_IsAFailure()
    {
        var (client, _) = Make(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingStream(Encoding.UTF8.GetBytes("[{\"tag_name\":\"v1"), new IOException("premature EOF"))),
        });

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal("body read failed", r.Error);
    }

    [Fact]
    public async Task TruncatedBody_ThatEndsEarlyAgainstContentLength_IsAFailure()
    {
        var full = "[" + One() + "]";
        var content = new StringContent(full[..(full.Length - 10)]);
        content.Headers.ContentLength = full.Length;
        var (client, _) = Make(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal("truncated response", r.Error);
    }

    [Fact]
    public async Task TruncatedJson_WithoutALength_IsAFailure()
    {
        var (client, _) = Make(() => Ok("[" + One()));

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal("invalid JSON", r.Error);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body><h1>Unicorn!</h1> This page is taking too long to load.</body></html>", "invalid JSON")]
    [InlineData("", "invalid JSON")]
    [InlineData("not json", "invalid JSON")]
    [InlineData("{}", "unexpected JSON shape")]
    [InlineData("{\"message\":\"Not Found\",\"documentation_url\":\"https://docs.github.com\"}", "unexpected JSON shape")]
    [InlineData("\"a string\"", "unexpected JSON shape")]
    [InlineData("null", "unexpected JSON shape")]
    [InlineData("42", "unexpected JSON shape")]
    [InlineData("[1,2,3]", "unexpected JSON shape")]
    [InlineData("[\"v0.6.19\"]", "unexpected JSON shape")]
    [InlineData("[[]]", "unexpected JSON shape")]
    public async Task WrongBodies_AreRecordedFailures(string body, string expectedError)
    {
        var (client, _) = Make(() => Ok(body));

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal(expectedError, r.Error);
        Assert.Empty(r.Releases);
    }

    [Fact]
    public async Task InvalidUtf8_IsAFailure()
    {
        // 0xFF is never valid in UTF-8. Valid JSON structure around it.
        var bytes = Encoding.ASCII.GetBytes("[{\"tag_name\":\"v1.0.0\",\"draft\":false,\"prerelease\":false,\"body\":\"x").Concat(new byte[] { 0xFF, 0xFE })
            .Concat(Encoding.ASCII.GetBytes("y\"}]")).ToArray();
        var (client, _) = Make(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal("invalid UTF-8", r.Error);
    }

    [Fact]
    public async Task Utf8Bom_IsTolerated()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[" + One() + "]")).ToArray();
        var (client, _) = Make(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Ok, r.Outcome);
        Assert.Single(r.Releases);
    }

    // ── The 2 MB cap ──────────────────────────────────────────────────

    [Fact]
    public async Task BodyOverTheCap_IsRefused_WithoutBeingParsed()
    {
        // 5 MB of valid-looking JSON, no Content-Length: the stream is cut at the cap.
        var big = "[" + One() + "," + string.Join(",", Enumerable.Repeat(One("v0.0.1", ",\"pad\":\"" + new string('x', 1000) + "\""), 5000)) + "]";
        Assert.True(Encoding.UTF8.GetByteCount(big) > 4 * 1024 * 1024);
        var (client, _) = Make(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PlainStream(Encoding.UTF8.GetBytes(big))) });

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal("response too large", r.Error);
    }

    [Fact]
    public async Task DeclaredLengthOverTheCap_IsRefused_WithoutReadingTheBody()
    {
        var neverRead = new ThrowingStream([], new InvalidOperationException("body must not be read"));
        var content = new StreamContent(neverRead);
        content.Headers.ContentLength = 10L * 1024 * 1024;
        var (client, _) = Make(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal("response too large", r.Error);
        Assert.False(neverRead.WasRead);
    }

    [Fact]
    public async Task BodyExactlyAtTheCap_IsAccepted_OneByteOverIsNot()
    {
        static string Padded(int totalBytes)
        {
            const string head = "[{\"tag_name\":\"v0.6.19\",\"draft\":false,\"prerelease\":false,\"body\":\"";
            const string tail = "\"}]";
            return head + new string('x', totalBytes - head.Length - tail.Length) + tail;
        }

        var atCap = Padded((int)GitHubReleaseClient.MaxBodyBytes);
        var overCap = Padded((int)GitHubReleaseClient.MaxBodyBytes + 1);
        Assert.Equal(GitHubReleaseClient.MaxBodyBytes, Encoding.UTF8.GetByteCount(atCap));

        var (a, _) = Make(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PlainStream(Encoding.UTF8.GetBytes(atCap))) });
        var (b, _) = Make(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PlainStream(Encoding.UTF8.GetBytes(overCap))) });

        Assert.Equal(FetchOutcome.Ok, (await a.FetchAsync(MS, null, default)).Outcome);
        Assert.Equal("response too large", (await b.FetchAsync(MS, null, default)).Error);
    }

    // ── Redirects ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Redirect_ToTheApiHost_IsFollowed(HttpStatusCode code)
    {
        var (client, handler) = Make((_, n, _) => Task.FromResult(n == 1
            ? Redirect("https://api.github.com/repositories/12345/releases?per_page=30", code)
            : Ok("[" + One() + "]", "W/\"new\"")));

        var r = await client.FetchAsync(MS, "W/\"old\"", default);

        Assert.Equal(FetchOutcome.Ok, r.Outcome);
        Assert.Equal("W/\"new\"", r.ETag);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://api.github.com/repositories/12345/releases?per_page=30", handler.Requests[1].Uri.AbsoluteUri);
        // The second hop is still the same anonymous, generic request.
        Assert.Equal("MultiSeat-update-check", handler.Requests[1].Headers["User-Agent"]);
        Assert.False(handler.Requests[1].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Redirect_WithARelativeLocation_StaysOnTheApiHost()
    {
        var (client, handler) = Make((_, n, _) => Task.FromResult(n == 1
            ? Redirect("/repositories/777/releases?per_page=30")
            : Ok("[]")));

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Ok, r.Outcome);
        Assert.Equal("https://api.github.com/repositories/777/releases?per_page=30", handler.Requests[1].Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://github.com/vibesoftwarecoder/MultiSeat/releases")]
    [InlineData("https://evil.example/repos/vibesoftwarecoder/MultiSeat/releases")]
    [InlineData("https://api.github.com.evil.example/repos/x/y/releases")]
    [InlineData("https://api.github.com@evil.example/repos/x/y/releases")]
    [InlineData("https://user:pw@api.github.com/repos/x/y/releases")]
    [InlineData("http://api.github.com/repos/x/y/releases")]
    [InlineData("https://api.github.com:8443/repos/x/y/releases")]
    [InlineData("https://api.github.com/user")]
    [InlineData("https://api.github.com/")]
    [InlineData("https://api.github.com/reposX/y")]
    [InlineData("ftp://api.github.com/repos/x/y/releases")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("//evil.example/repos/x/y/releases")]
    [InlineData("javascript:alert(1)")]
    public async Task Redirect_ToAnythingElse_IsRefused_AndNeverRequested(string location)
    {
        var (client, handler) = Make(() => Redirect(location));

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal("redirect refused", r.Error);
        Assert.Single(handler.Requests); // the refused target was never contacted
    }

    [Fact]
    public async Task Redirect_WithoutALocation_IsRefused()
    {
        var (client, _) = Make(() => new HttpResponseMessage(HttpStatusCode.MovedPermanently));

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal("redirect refused", r.Error);
    }

    [Fact]
    public async Task ThreeRedirects_AreFollowed_TheFourthIsRefused()
    {
        string Hop(int n) => $"https://api.github.com/repositories/{n}/releases?per_page=30";

        var (three, threeHandler) = Make((_, n, _) => Task.FromResult(n <= 3 ? Redirect(Hop(n)) : Ok("[]")));
        var (four, fourHandler) = Make((_, n, _) => Task.FromResult(n <= 4 ? Redirect(Hop(n)) : Ok("[]")));

        var ok = await three.FetchAsync(MS, null, default);
        var refused = await four.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Ok, ok.Outcome);
        Assert.Equal(4, threeHandler.Requests.Count);
        Assert.Equal(FetchOutcome.Failed, refused.Outcome);
        Assert.Equal("too many redirects", refused.Error);
        Assert.Equal(4, fourHandler.Requests.Count); // the fifth request was never made
    }

    [Fact]
    public async Task RedirectLoop_IsRefused()
    {
        var (client, handler) = Make(() => Redirect("https://api.github.com/repositories/1/releases?per_page=30"));

        var r = await client.FetchAsync(MS, null, default);

        Assert.Equal(FetchOutcome.Failed, r.Outcome);
        Assert.Equal("redirect loop", r.Error);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ── Parsing ───────────────────────────────────────────────────────

    private static List<GitHubRelease> Parse(string json, out string? error) =>
        GitHubReleaseClient.ParseReleases(Encoding.UTF8.GetBytes(json), out error) ?? [];

    [Fact]
    public void Parse_SkipsDraftsAndPreReleases()
    {
        var json = "[" +
            One("v0.6.19") + "," +
            One("v0.7.0").Replace("\"draft\":false", "\"draft\":true") + "," +
            One("v0.8.0").Replace("\"prerelease\":false", "\"prerelease\":true") + "]";

        var releases = Parse(json, out var error);

        Assert.Null(error);
        Assert.Equal(["v0.6.19"], releases.Select(r => r.TagName));
    }

    [Theory]
    [InlineData("\"draft\":\"false\"")]
    [InlineData("\"draft\":null")]
    [InlineData("\"draft\":0")]
    public void Parse_SkipsAReleaseWhoseFlagsAreNotBooleans(string badDraft)
    {
        var json = "[" + One("v1.0.0").Replace("\"draft\":false", badDraft) + "," + One("v1.0.1") + "]";

        Assert.Equal(["v1.0.1"], Parse(json, out _).Select(r => r.TagName));
    }

    [Fact]
    public void Parse_SkipsAReleaseWithMissingFlags_OrMissingOrHugeTag()
    {
        var noFlags = "{\"tag_name\":\"v1.0.0\"}";
        var noTag = "{\"draft\":false,\"prerelease\":false}";
        var nullTag = "{\"tag_name\":null,\"draft\":false,\"prerelease\":false}";
        var intTag = "{\"tag_name\":5,\"draft\":false,\"prerelease\":false}";
        var hugeTag = One(new string('v', 500));

        var releases = Parse($"[{noFlags},{noTag},{nullTag},{intTag},{hugeTag},{One("v1.0.2")}]", out var error);

        Assert.Null(error);
        Assert.Equal(["v1.0.2"], releases.Select(r => r.TagName));
    }

    [Fact]
    public void Parse_ToleratesOddFields_WithoutLosingTheRelease()
    {
        var json = """
            [{"tag_name":"v1.0.0","draft":false,"prerelease":false,
              "published_at":"not a date","target_commitish":12,"body":null,
              "assets":[{"name":"ok.zip","digest":null},{"name":5},"junk",{"name":""},{"name":"b.zip","digest":"sha256:1"}],
              "unknown_future_field":{"nested":[1,2,3]}}]
            """;

        var r = Assert.Single(Parse(json, out var error));

        Assert.Null(error);
        Assert.Null(r.PublishedAt);
        Assert.Null(r.TargetCommitish);
        Assert.Null(r.Body);
        Assert.Equal([new GitHubAsset("ok.zip", null), new GitHubAsset("b.zip", "sha256:1")], r.Assets);
    }

    [Fact]
    public void Parse_AssetsMissingOrWrongType_YieldsNoAssets()
    {
        var json = "[" + One("v1.0.0") + ",{\"tag_name\":\"v1.0.1\",\"draft\":false,\"prerelease\":false,\"assets\":\"none\"}]";

        var releases = Parse(json, out _);

        Assert.Equal(2, releases.Count);
        Assert.Empty(releases[1].Assets);
    }

    [Fact]
    public void Parse_EmptyArray_IsAnEmptyListNotAnError()
    {
        var releases = GitHubReleaseClient.ParseReleases("[]"u8.ToArray(), out var error);

        Assert.NotNull(releases);
        Assert.Empty(releases!);
        Assert.Null(error);
    }

    [Fact]
    public void Parse_NonObjectElementsAreSkipped_WhenAtLeastOneObjectIsPresent()
    {
        var releases = Parse("[1,\"x\"," + One("v1.0.0") + ",null]", out var error);

        Assert.Null(error);
        Assert.Single(releases);
    }

    [Fact]
    public void Parse_DeeplyNestedJson_IsAFailureNotACrash()
    {
        var deep = new string('[', 5000) + new string(']', 5000);

        Parse(deep, out var error);

        Assert.Equal("invalid JSON", error);
    }

    [Fact]
    public void Parse_OverlongBody_IsTruncatedNotKeptWhole()
    {
        var json = "[{\"tag_name\":\"v1.0.0\",\"draft\":false,\"prerelease\":false,\"body\":\"" + new string('a', 1_000_000) + "\"}]";

        var r = Assert.Single(Parse(json, out _));

        Assert.True(r.Body!.Length <= 256 * 1024);
    }

    [Fact]
    public void Parse_KeepsAtMostAHundredReleases()
    {
        var json = "[" + string.Join(",", Enumerable.Range(0, 250).Select(i => One($"v0.0.{i}"))) + "]";

        Assert.Equal(100, Parse(json, out _).Count);
    }

    [Fact]
    public void Parse_TheRealApolloVibeResponse()
    {
        var bytes = File.ReadAllBytes(Fixtures.Path("github-releases-apollovibe.json"));

        var releases = GitHubReleaseClient.ParseReleases(bytes, out var error)!;

        Assert.Null(error);
        // 16 releases in the response; the five test-* / debug-* ones are pre-releases and are dropped.
        Assert.Equal(11, releases.Count);
        Assert.DoesNotContain(releases, r => r.TagName.StartsWith("test-") || r.TagName.StartsWith("debug-"));
        var ms6 = releases.Single(r => r.TagName == "v2026.6.1-ms6");
        Assert.StartsWith("681e36a8", ms6.TargetCommitish);
        Assert.Contains("SHA-256 of `sunshine.exe`", ms6.Body);
        var zip = Assert.Single(ms6.Assets);
        Assert.Equal("apollovibe-windows-x64.zip", zip.Name);
        Assert.StartsWith("sha256:b39bdc29", zip.Digest);
    }

    [Fact]
    public void TheRealApolloVibeResponse_SelectsMs6_AndKeepsTheExeHashNotTheZipHash()
    {
        var bytes = File.ReadAllBytes(Fixtures.Path("github-releases-apollovibe.json"));
        var releases = GitHubReleaseClient.ParseReleases(bytes, out var error)!;
        Assert.Null(error);

        var candidates = ReleaseSelector.BuildCandidates(UpdateComponent.ApolloVibe, releases);
        var latest = ReleaseSelector.SelectLatest(UpdateComponent.ApolloVibe, candidates)!;

        Assert.Equal("v2026.6.1-ms6", latest.Tag);
        Assert.DoesNotContain(candidates, c => c.Tag.StartsWith("test-") || c.Tag.StartsWith("debug-"));
        Assert.Contains(candidates, c => c.Tag == "v2026.4.30-multiseat.1");
        // ms6's notes name the zip hash first and the exe hash second; the candidate keeps the exe's.
        Assert.Equal("7e9fa1572579879fa98716c74753da7ec070f0c8209fe5177f2d73cd0c1b33cc", latest.SunshineSha256);
        Assert.StartsWith("681e36a8", latest.TargetCommit);
        // ms3 to ms5 each have their own, and the legacy releases have none.
        Assert.Equal(4, candidates.Count(c => c.SunshineSha256 is not null));
    }

    // ── Construction, handler, state ──────────────────────────────────

    [Fact]
    public void Constructor_RefusesAClientThatWouldAddCredentialsOrAnIdentity()
    {
        var withAuth = new HttpClient();
        withAuth.DefaultRequestHeaders.Authorization = new("Bearer", "x");
        var withUa = new HttpClient();
        withUa.DefaultRequestHeaders.UserAgent.ParseAdd("MultiSeat/0.6.19 (HOST-NAME)");
        var withCookie = new HttpClient();
        withCookie.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "a=b");
        var withProxyAuth = new HttpClient();
        withProxyAuth.DefaultRequestHeaders.ProxyAuthorization = new("Basic", "x");

        Assert.Throws<ArgumentException>(() => new GitHubReleaseClient(withAuth));
        Assert.Throws<ArgumentException>(() => new GitHubReleaseClient(withUa));
        Assert.Throws<ArgumentException>(() => new GitHubReleaseClient(withCookie));
        Assert.Throws<ArgumentException>(() => new GitHubReleaseClient(withProxyAuth));
        Assert.Throws<ArgumentNullException>(() => new GitHubReleaseClient(null!));
    }

    [Fact]
    public void CreateHandler_DisablesAutoRedirectAndCookies()
    {
        using var handler = GitHubReleaseClient.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate, handler.AutomaticDecompression);
        // Normal certificate validation: nothing was overridden.
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void Client_HasNoMutableStaticState()
    {
        const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var f in typeof(GitHubReleaseClient).GetFields(all))
            Assert.True(f.IsInitOnly || f.IsLiteral, $"static field {f.Name} is mutable");
        Assert.DoesNotContain(typeof(GitHubReleaseClient).GetProperties(all), p => p.SetMethod is not null);
    }

    [Fact]
    public async Task TwoClients_DoNotShareState()
    {
        var (a, _) = Make(() => Ok("[" + One("v1.0.0") + "]", "W/\"a\""));
        var (b, _) = Make(() => Ok("[" + One("v2.0.0") + "]", "W/\"b\""));

        var ra = await a.FetchAsync(MS, null, default);
        var rb = await b.FetchAsync(MS, null, default);

        Assert.Equal("W/\"a\"", ra.ETag);
        Assert.Equal("W/\"b\"", rb.ETag);
        Assert.Equal("v2.0.0", rb.Releases.Single().TagName);
    }

    // A fetch that should have timed out must FAIL the test, not hang the run.
    private static async Task<T> WithinAsync<T>(Task<T> task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(ReferenceEquals(finished, task), "the fetch did not finish: the timeout is not working");
        return await task;
    }

    // ── Helper streams ────────────────────────────────────────────────

    private sealed class ThrowingStream(byte[] prefix, Exception thrown) : Stream
    {
        private int _pos;
        public bool WasRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            if (_pos >= prefix.Length) throw thrown;
            var n = Math.Min(count, prefix.Length - _pos);
            Array.Copy(prefix, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PlainStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
