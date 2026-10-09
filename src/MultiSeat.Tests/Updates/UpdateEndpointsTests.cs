using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;
using MultiSeat.Service.Api;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

/// <summary>
/// The three update routes over real HTTP on a loopback port, through the API's real auth
/// middleware and JSON options (ApiServer.UseApiKeyAuth / ConfigureApiJson) and the real route
/// mapping (UpdateEndpoints.Map). ApiServer.Build itself needs the whole host (seats, RDP, ...),
/// so it is not constructed here. The GitHub side is the counting fake handler.
/// </summary>
public class UpdateEndpointsTests
{
    /// <summary>A loopback web host with only the update routes mapped.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public ServiceRig Rig { get; }
        public ApiAuthState Auth { get; }
        public HttpClient Http { get; }
        public string SettingsPath { get; }
        private readonly WebApplication _app;

        private Harness(ServiceRig rig, ApiAuthState auth, WebApplication app, HttpClient http, string settingsPath)
        { Rig = rig; Auth = auth; _app = app; Http = http; SettingsPath = settingsPath; }

        public static async Task<Harness> StartAsync(bool enabled, string? apiKey = null, Func<double>? random = null)
        {
            var rig = new ServiceRig(enabled, random: random, multiSeatVersion: "0.6.18+ebcd253", apolloPresent: false);
            var auth = new ApiAuthState(apiKey is not null, apiKey ?? "");
            var settings = Path.Combine(rig.Dir, "appsettings.local.json");

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.ConfigureHttpJsonOptions(o => ApiServer.ConfigureApiJson(o.SerializerOptions));
            builder.Services.AddSingleton(rig.Provider);
            builder.Services.AddSingleton(rig.Service);
            var app = builder.Build();
            ApiServer.UseApiKeyAuth(app, auth);
            UpdateEndpoints.Map(app, settings);
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Harness(rig, auth, app, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) }, settings);
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            Rig.Dispose();
        }
    }

    private static HttpRequestMessage Req(HttpMethod m, string path, string? key = null, string? body = null)
    {
        var r = new HttpRequestMessage(m, path);
        if (key is not null) r.Headers.Add("X-MultiSeat-Key", key);
        if (body is not null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return r;
    }

    // ── GET ──────────────────────────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task Get_WhenOff_ListsOnlyMultiSeat_AndMakesNoCall()
    {
        await using var h = await Harness.StartAsync(enabled: false);

        var resp = await h.Http.GetAsync("/api/system/updates");
        var json = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(
            """{"enabled":false,"intervalHours":12,"checkedAt":null,"nextCheckAt":null,"error":null,"components":[""" +
            """{"id":"multiseat","name":"MultiSeat","status":"disabled","installed":{"version":"0.6.18","display":"0.6.18 (ebcd253)","source":"assembly","note":null},"installedNote":null,"latest":null,"announce":false,"checkedAt":null,"error":null}]}""",
            json);
        Assert.Equal(0, h.Rig.Handler.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task Get_WhenOnWithAnEmptyCache_ReadsMemoryOnly()
    {
        await using var h = await Harness.StartAsync(enabled: true);

        for (var i = 0; i < 5; i++)
        {
            var resp = await h.Http.GetAsync("/api/system/updates");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(3, doc.RootElement.GetProperty("components").GetArrayLength());
        }

        Assert.Equal(0, h.Rig.Handler.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task Check_ThenGet_ReturnTheSameShape_CamelCase_WithEnumStrings()
    {
        await using var h = await Harness.StartAsync(enabled: true);

        var post = await h.Http.SendAsync(Req(HttpMethod.Post, "/api/system/updates/check"));
        var postJson = await post.Content.ReadAsStringAsync();
        var get = await h.Http.GetStringAsync("/api/system/updates");

        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Assert.Equal(get, postJson);

        // The exact answer for three fixed releases, installed MultiSeat 0.6.18, no ApolloVibe on disk.
        var t = "2026-10-09T10:20:56+00:00";
        var checkedAt = "2026-10-09T00:00:00+00:00";
        var expected =
            """{"enabled":true,"intervalHours":12,"checkedAt":"__C__","nextCheckAt":"__N__","error":null,"components":[""" +
            """{"id":"multiseat","name":"MultiSeat","status":"updateAvailable","installed":{"version":"0.6.18","display":"0.6.18 (ebcd253)","source":"assembly","note":null},"installedNote":null,"latest":{"version":"0.6.19","tag":"v0.6.19","publishedAt":"__T__","releaseUrl":"https://github.com/vibesoftwarecoder/MultiSeat/releases/tag/v0.6.19"},"announce":true,"checkedAt":"__C__","error":null},""" +
            """{"id":"apollovibe","name":"ApolloVibe","status":"notInstalled","installed":null,"installedNote":"Not found at __A__.","latest":{"version":"2026.6.1-ms19","tag":"v2026.6.1-ms19","publishedAt":"__T__","releaseUrl":"https://github.com/vibesoftwarecoder/ApolloVibe/releases/tag/v2026.6.1-ms19"},"announce":false,"checkedAt":"__C__","error":null},""" +
            """{"id":"moonlightvibe","name":"MoonlightVibe","status":"latestOnly","installed":null,"installedNote":"Runs on your other devices. MultiSeat cannot see its version.","latest":{"version":"6.3.19","tag":"v6.3.19","publishedAt":"__T__","releaseUrl":"https://github.com/vibesoftwarecoder/MoonlightVibe/releases/tag/v6.3.19"},"announce":false,"checkedAt":"__C__","error":null}]}""";
        using var doc = JsonDocument.Parse(get);
        var next = doc.RootElement.GetProperty("nextCheckAt").GetString()!;
        var apollo = JsonEncodedText.Encode(h.Rig.ApolloExe).ToString();
        expected = expected.Replace("__C__", checkedAt).Replace("__N__", next).Replace("__T__", t).Replace("__A__", apollo);
        Assert.Equal(expected, get);
    }

    [Fact(Timeout = 60_000)]
    public async Task HostileGitHubText_NeverReachesTheResponse()
    {
        await using var h = await Harness.StartAsync(enabled: true);
        h.Rig.Handler.Respond = (_, _, _) => Task.FromResult(CountingHandler.Json("""
            [
              {"tag_name":"v0.6.19","draft":false,"prerelease":false,"published_at":"2026-10-09T10:20:56Z",
               "html_url":"javascript:alert(document.cookie)","target_commitish":"<img src=x onerror=alert(1)>",
               "name":"<script>steal()</script>","body":"<script>alert('body')</script> https://evil.example/x",
               "assets":[{"name":"<b>a</b>","digest":"sha256:evil","browser_download_url":"https://evil.example/a.zip"}]},
              {"tag_name":"\"><script>alert('tag')</script>","draft":false,"prerelease":false,"published_at":"2026-10-10T10:20:56Z",
               "html_url":"https://evil.example/tag","body":"x","assets":[]},
              {"tag_name":"v9.9.9","draft":false,"prerelease":false,"published_at":"2026-10-10T10:20:56Z",
               "html_url":"https://github.com/someone-else/MultiSeat/releases/tag/v9.9.9","body":"x","assets":[]}
            ]
            """));

        await h.Http.SendAsync(Req(HttpMethod.Post, "/api/system/updates/check"));
        var json = await h.Http.GetStringAsync("/api/system/updates");

        foreach (var bad in new[] { "javascript:", "<script", "alert(", "evil.example", "onerror", "steal", "someone-else", "<b>", "sha256" })
            Assert.DoesNotContain(bad, json, StringComparison.OrdinalIgnoreCase);
        // The one legitimate link is the one built here, from the repository constant and the tag.
        Assert.Contains("https://github.com/vibesoftwarecoder/MultiSeat/releases/tag/v9.9.9", json);
    }

    // ── POST check ───────────────────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task Check_WhenOff_Is409_WithTheFixedMessage_AndNoCall()
    {
        await using var h = await Harness.StartAsync(enabled: false);

        var resp = await h.Http.SendAsync(Req(HttpMethod.Post, "/api/system/updates/check"));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("""{"error":"Update checks are off"}""", await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, h.Rig.Handler.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task Check_InsideTheCooldown_Is429_WithRetryAfter()
    {
        await using var h = await Harness.StartAsync(enabled: true);
        var first = await h.Http.SendAsync(Req(HttpMethod.Post, "/api/system/updates/check"));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(3, h.Rig.Handler.Count);

        var second = await h.Http.SendAsync(Req(HttpMethod.Post, "/api/system/updates/check"));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("60", second.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
        Assert.Equal(3, h.Rig.Handler.Count);
    }

    // ── Auth ─────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Routes() =>
    [
        ["GET", "/api/system/updates", null!],
        ["POST", "/api/system/updates/check", null!],
        ["POST", "/api/system/updates/settings", """{"enabled":true}"""],
    ];

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(Routes))]
    public async Task WithAuthOn_EveryUpdateRoute_Is401WithoutTheKey_AndDoesNothing(string method, string path, string? body)
    {
        await using var h = await Harness.StartAsync(enabled: true, apiKey: "secret-key");

        var wrong = await h.Http.SendAsync(Req(new HttpMethod(method), path, key: "wrong", body: body));
        var none = await h.Http.SendAsync(Req(new HttpMethod(method), path, body: body));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(0, h.Rig.Handler.Count);            // an unauthenticated check must not reach GitHub
        Assert.False(File.Exists(h.SettingsPath));        // nor write the settings
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(Routes))]
    public async Task WithAuthOn_TheKeyOpensEveryUpdateRoute(string method, string path, string? body)
    {
        await using var h = await Harness.StartAsync(enabled: true, apiKey: "secret-key");

        var resp = await h.Http.SendAsync(Req(new HttpMethod(method), path, key: "secret-key", body: body));

        Assert.True((int)resp.StatusCode is 200 or 202, $"{method} {path} -> {(int)resp.StatusCode}");
    }

    // ── POST settings over HTTP ──────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task Settings_WritesTheLocalFile_AndAnswersWithTheNewState()
    {
        await using var h = await Harness.StartAsync(enabled: false);

        var resp = await h.Http.SendAsync(Req(HttpMethod.Post, "/api/system/updates/settings", body: """{"enabled":true}"""));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
        using var file = JsonDocument.Parse(File.ReadAllText(h.SettingsPath));
        Assert.True(file.RootElement.GetProperty("MultiSeat").GetProperty("UpdateCheckEnabled").GetBoolean());
    }

    [Theory(Timeout = 60_000)]
    [InlineData("""{"enabled":true,"intervalHours":1}""")]
    [InlineData("""{"enabled":true,"ApiKey":"x"}""")]
    [InlineData("""{"intervalHours":1}""")]
    [InlineData("""{"enabled":"true"}""")]
    [InlineData("""{"enabled":1}""")]
    [InlineData("""{"enabled":null}""")]
    [InlineData("""{"enabled":true,"enabled":false}""")]
    [InlineData("""{"Enabled":true}""")]
    [InlineData("""[true]""")]
    [InlineData("""true""")]
    [InlineData("""{}""")]
    [InlineData("""{"enabled":""")]
    [InlineData("")]
    public async Task Settings_RejectsAnythingButExactlyEnabled(string body)
    {
        await using var h = await Harness.StartAsync(enabled: false);

        var resp = await h.Http.SendAsync(Req(HttpMethod.Post, "/api/system/updates/settings", body: body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(File.Exists(h.SettingsPath));
    }

    [Fact(Timeout = 60_000)]
    public async Task Settings_RejectsAnOversizedBody()
    {
        await using var h = await Harness.StartAsync(enabled: false);
        var body = "{\"enabled\":true,\"pad\":\"" + new string('x', 5000) + "\"}";

        var resp = await h.Http.SendAsync(Req(HttpMethod.Post, "/api/system/updates/settings", body: body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(File.Exists(h.SettingsPath));
    }

    // ── Persistence, called directly with temp files ─────────────────

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "multiseat-updset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static Task<IResult> Post(string body, string path) =>
        UpdateEndpoints.SetEnabledAsync(new MemoryStream(Encoding.UTF8.GetBytes(body)), path, NullLogger.Instance, default);

    [Fact]
    public async Task Persist_WritesTheLocalFile_AndNeverTouchesAppsettingsJson()
    {
        var dir = TempDir();
        try
        {
            var shipped = Path.Combine(dir, "appsettings.json");
            File.WriteAllText(shipped, """{"MultiSeat":{"UpdateCheckEnabled":false}}""");
            var local = Path.Combine(dir, "appsettings.local.json");

            await Post("""{"enabled":true}""", local);

            Assert.Equal("""{"MultiSeat":{"UpdateCheckEnabled":false}}""", File.ReadAllText(shipped));
            using var doc = JsonDocument.Parse(File.ReadAllText(local));
            Assert.True(doc.RootElement.GetProperty("MultiSeat").GetProperty("UpdateCheckEnabled").GetBoolean());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Persist_KeepsEveryOtherKey_AndTurnsItBackOff()
    {
        var dir = TempDir();
        try
        {
            var local = Path.Combine(dir, "appsettings.local.json");
            File.WriteAllText(local, """{"Logging":{"X":1},"MultiSeat":{"ApiKey":"k","AudioMode":"PerSession","UpdateCheckEnabled":true}}""");

            await Post("""{"enabled":false}""", local);

            using var doc = JsonDocument.Parse(File.ReadAllText(local));
            var ms = doc.RootElement.GetProperty("MultiSeat");
            Assert.False(ms.GetProperty("UpdateCheckEnabled").GetBoolean());
            Assert.Equal("k", ms.GetProperty("ApiKey").GetString());
            Assert.Equal("PerSession", ms.GetProperty("AudioMode").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("Logging").GetProperty("X").GetInt32());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Persist_CreatesTheFileAndItsFolder_AndLeavesNoTempFile()
    {
        var dir = TempDir();
        try
        {
            var local = Path.Combine(dir, "new", "appsettings.local.json");

            await Post("""{"enabled":true}""", local);

            Assert.True(File.Exists(local));
            Assert.Equal([local], Directory.GetFiles(Path.GetDirectoryName(local)!));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Persist_IsAtomic_AReaderNeverSeesAHalfWrittenFile()
    {
        var dir = TempDir();
        try
        {
            var local = Path.Combine(dir, "appsettings.local.json");
            File.WriteAllText(local, """{"MultiSeat":{"ApiKey":"k","UpdateCheckEnabled":false}}""");
            var stop = false;
            var bad = 0;
            var reader = Task.Run(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    try { using var _ = JsonDocument.Parse(File.ReadAllText(local)); }
                    catch (IOException) { /* the swap moment; a torn file is the JsonException below */ }
                    catch (JsonException) { Interlocked.Increment(ref bad); }
                }
            });

            for (var i = 0; i < 150; i++) await Post(i % 2 == 0 ? """{"enabled":true}""" : """{"enabled":false}""", local);
            stop = true;
            await reader;

            Assert.Equal(0, bad);
            Assert.Single(Directory.GetFiles(dir)); // no stray temp files
        }
        finally { Directory.Delete(dir, true); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr reserved);

    [Fact]
    public async Task Persist_ReplacesTheFile_RatherThanRewritingItInPlace()
    {
        // Atomic means a new file takes the name and the old one is left whole. A second name (hard
        // link) for the old file proves which happened: an in-place rewrite would change what the
        // link shows, a replace leaves it untouched.
        var dir = TempDir();
        try
        {
            var local = Path.Combine(dir, "appsettings.local.json");
            var link = Path.Combine(dir, "old-view.json");
            const string old = """{"MultiSeat":{"ApiKey":"k","UpdateCheckEnabled":false}}""";
            File.WriteAllText(local, old);
            Assert.True(CreateHardLinkW(link, local, IntPtr.Zero));

            await Post("""{"enabled":true}""", local);

            Assert.Equal(old, File.ReadAllText(link));
            Assert.Contains("true", File.ReadAllText(local));
            Assert.DoesNotContain("false", File.ReadAllText(local));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string Dacl(string path) =>
        new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);

    [Fact]
    public async Task Persist_KeepsTheFilesPermissions_ExplicitAcesAndNoInheritance()
    {
        // The file can hold the API key and an administrator may have tightened it. Flipping the
        // update switch must not widen it back to what the folder grants.
        var dir = TempDir();
        try
        {
            var local = Path.Combine(dir, "appsettings.local.json");
            File.WriteAllText(local, """{"MultiSeat":{"ApiKey":"k","UpdateCheckEnabled":false}}""");

            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var guests = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinGuestsSid, null);
            var fi = new FileInfo(local);
            var sec = fi.GetAccessControl();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var r in sec.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList())
                sec.RemoveAccessRuleAll(r);
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(guests, System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny));
            fi.SetAccessControl(sec);
            var before = Dacl(local);
            Assert.Contains("D:P", before);          // inheritance is off
            Assert.Contains(";;;BG)", before);       // the distinct deny for Guests
            var folderDefault = Path.Combine(dir, "probe.json");
            File.WriteAllText(folderDefault, "x");
            Assert.NotEqual(before, Dacl(folderDefault)); // the folder would not have granted this

            await Post("""{"enabled":true}""", local);

            Assert.Equal(before, Dacl(local));
            Assert.Contains("true", File.ReadAllText(local));
            Assert.Contains("\"ApiKey\": \"k\"", File.ReadAllText(local));
            Assert.Equal(["appsettings.local.json", "probe.json"], Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).Order().ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Persist_WhenTheSwapFails_Returns500_ChangesNothing_AndLeavesNoTempFile()
    {
        var dir = TempDir();
        try
        {
            var local = Path.Combine(dir, "appsettings.local.json");
            const string original = """{"MultiSeat":{"ApiKey":"k","UpdateCheckEnabled":false}}""";
            File.WriteAllText(local, original);
            File.SetAttributes(local, FileAttributes.ReadOnly); // makes the replace fail

            var result = await Post("""{"enabled":true}""", local);

            File.SetAttributes(local, FileAttributes.Normal);
            Assert.Equal(500, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode);
            Assert.Equal(original, File.ReadAllText(local));
            Assert.Equal([local], Directory.GetFiles(dir));
        }
        finally
        {
            try { File.SetAttributes(Path.Combine(dir, "appsettings.local.json"), FileAttributes.Normal); } catch { }
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Persist_LeavesAFileWithCommentsUntouched_AndReportsIt()
    {
        var dir = TempDir();
        try
        {
            var local = Path.Combine(dir, "appsettings.local.json");
            const string original = "{ // my notes\n \"MultiSeat\": { \"ApiKey\": \"k\" } }";
            File.WriteAllText(local, original);

            var result = await Post("""{"enabled":true}""", local);

            Assert.Equal(original, File.ReadAllText(local));
            var status = (result as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)!.StatusCode;
            Assert.Equal(500, status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact(Timeout = 60_000)]
    public async Task Persist_IsSeenByTheRunningConfiguration_WithoutARestart()
    {
        // The same loading Program.cs does: a JSON file with reloadOnChange, bound to the options.
        var dir = TempDir();
        try
        {
            var local = Path.Combine(dir, "appsettings.local.json");
            File.WriteAllText(local, """{"MultiSeat":{"ApiKey":"k","UpdateCheckEnabled":false}}""");
            var config = new ConfigurationBuilder()
                .AddJsonFile(local, optional: true, reloadOnChange: true).Build();
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            Microsoft.Extensions.DependencyInjection.OptionsConfigurationServiceCollectionExtensions
                .Configure<MultiSeatOptions>(services, config.GetSection(MultiSeatOptions.SectionName));
            using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
            var monitor = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<MultiSeatOptions>>(sp);
            Assert.False(monitor.CurrentValue.UpdateCheckEnabled);
            var changed = new TaskCompletionSource();
            using var _ = monitor.OnChange((o, _) => { if (o.UpdateCheckEnabled) changed.TrySetResult(); });

            await Post("""{"enabled":true}""", local);

            await changed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(monitor.CurrentValue.UpdateCheckEnabled);
            Assert.Equal("k", monitor.CurrentValue.ApiKey);
        }
        finally { Directory.Delete(dir, true); }
    }
}
