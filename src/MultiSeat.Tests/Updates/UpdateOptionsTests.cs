using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

/// <summary>
/// Update checks are OFF unless an operator turns them on. Both places that say so are pinned:
/// the C# default (what a host with no config file gets) and the shipped appsettings.json (what
/// a fresh install gets). If either flips, an upgrade would start making internet connections
/// from a SYSTEM service without anyone having been asked.
/// </summary>
public class UpdateOptionsTests
{
    [Fact]
    public void Default_IsOff_WithATwelveHourInterval()
    {
        var o = new MultiSeatOptions();

        Assert.False(o.UpdateCheckEnabled);
        Assert.Equal(12, o.UpdateCheckIntervalHours);
    }

    [Fact]
    public void ShippedAppSettings_SaysOff_AsARealBoolean()
    {
        // The file has // comments, so read it the way the host does (configuration) and also
        // as raw JSON with comments skipped, to prove the value is the literal false and not a string.
        var config = new ConfigurationBuilder()
            .AddJsonFile("service-appsettings.json", optional: false)
            .Build();
        Assert.Equal("False", config.GetSection("MultiSeat")["UpdateCheckEnabled"]);

        using var doc = JsonDocument.Parse(File.ReadAllText("service-appsettings.json"),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var value = doc.RootElement.GetProperty("MultiSeat").GetProperty("UpdateCheckEnabled");
        Assert.Equal(JsonValueKind.False, value.ValueKind);
    }

    [Fact]
    public void ShippedAppSettings_BindsToOff()
    {
        var o = new MultiSeatOptions();
        new ConfigurationBuilder()
            .AddJsonFile("service-appsettings.json", optional: false)
            .Build()
            .GetSection("MultiSeat")
            .Bind(o);

        Assert.False(o.UpdateCheckEnabled);
    }

    [Fact]
    public void ShippedAppSettings_ExplainsWhatTurningItOnDoes()
    {
        var text = File.ReadAllText("service-appsettings.json");
        var at = text.IndexOf("\"UpdateCheckEnabled\"", StringComparison.Ordinal);
        Assert.True(at > 0);

        // The comment sits right above the key, in the file's own // style, and says what is sent.
        var before = text[Math.Max(0, at - 700)..at];
        Assert.Contains("api.github.com", before);
        Assert.Contains("never downloads or installs", before);
    }

    [Fact]
    public void LocalOverride_TurnsItOn()
    {
        // appsettings.local.json is loaded after appsettings.json and wins.
        var local = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{ "MultiSeat": { "UpdateCheckEnabled": true, "UpdateCheckIntervalHours": 6 } }"""));
        var o = new MultiSeatOptions();
        new ConfigurationBuilder()
            .AddJsonFile("service-appsettings.json", optional: false)
            .AddJsonStream(local)
            .Build()
            .GetSection("MultiSeat")
            .Bind(o);

        Assert.True(o.UpdateCheckEnabled);
        Assert.Equal(6, o.UpdateCheckIntervalHours);
    }

    [Fact]
    public void ConfigWithNoUpdateKeys_KeepsTheDefaultOff()
    {
        var o = new MultiSeatOptions();
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MultiSeat:MaxSeats"] = "2" })
            .Build()
            .GetSection("MultiSeat")
            .Bind(o);

        Assert.False(o.UpdateCheckEnabled);
        Assert.Equal(12, o.UpdateCheckIntervalHours);
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(12, 12)]
    [InlineData(168, 168)]
    [InlineData(169, 168)]
    [InlineData(int.MaxValue, 168)]
    [InlineData(int.MinValue, 1)]
    public void Interval_IsClampedToOneThroughOneSixtyEightHours(int configured, int expected)
    {
        Assert.Equal(expected, UpdateIntervals.ClampHours(configured));
        Assert.Equal(TimeSpan.FromHours(expected), UpdateIntervals.ToInterval(configured));
    }

    // ── Constants, not settings ───────────────────────────────────────

    [Fact]
    public void RepositoriesAndHost_AreConstants_AndNotSettings()
    {
        // A setting that could point the check elsewhere would make it a generic outbound request.
        var names = typeof(MultiSeatOptions).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain(names, n => n.Contains("Repo", StringComparison.OrdinalIgnoreCase) && n.Contains("Update", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("ApiHost", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Url", StringComparison.OrdinalIgnoreCase) && n.Contains("Update", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["UpdateCheckEnabled", "UpdateCheckIntervalHours"], names.Where(n => n.StartsWith("UpdateCheck")).OrderBy(n => n));
    }

    [Fact]
    public void Constants_PinTheHostAndTheThreeRepositories()
    {
        Assert.Equal("api.github.com", MultiSeat.Shared.Constants.UpdateApiHost);
        Assert.Equal("vibesoftwarecoder", MultiSeat.Shared.Constants.UpdateRepoOwner);
        Assert.Equal("MultiSeat-update-check", MultiSeat.Shared.Constants.UpdateUserAgent);
        Assert.Equal(@"C:\ProgramData\MultiSeat\update-check.json", MultiSeat.Shared.Constants.DefaultUpdateStatePath);

        Assert.Equal("https://api.github.com/repos/vibesoftwarecoder/MultiSeat/releases?per_page=30",
            UpdateRepos.ReleaseListUri(UpdateComponent.MultiSeat).AbsoluteUri);
        Assert.Equal("https://api.github.com/repos/vibesoftwarecoder/ApolloVibe/releases?per_page=30",
            UpdateRepos.ReleaseListUri(UpdateComponent.ApolloVibe).AbsoluteUri);
        Assert.Equal("https://api.github.com/repos/vibesoftwarecoder/MoonlightVibe/releases?per_page=30",
            UpdateRepos.ReleaseListUri(UpdateComponent.MoonlightVibe).AbsoluteUri);
    }

    [Fact]
    public void UserAgent_CarriesNoVersionAndNoHostName()
    {
        var ua = MultiSeat.Shared.Constants.UpdateUserAgent;

        Assert.DoesNotContain("/", ua);
        Assert.DoesNotMatch(@"\d", ua);
        Assert.DoesNotContain(Environment.MachineName, ua, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseUrl_IsBuiltFromTheRepoAndTheEncodedTag_NeverFromGitHubText()
    {
        Assert.Equal("https://github.com/vibesoftwarecoder/MultiSeat/releases/tag/v0.6.19",
            UpdateRepos.ReleaseUrl(UpdateComponent.MultiSeat, "v0.6.19"));

        var hostile = UpdateRepos.ReleaseUrl(UpdateComponent.ApolloVibe, "\"><script>alert(1)</script>/../x?y#z");
        Assert.StartsWith("https://github.com/vibesoftwarecoder/ApolloVibe/releases/tag/", hostile);
        Assert.DoesNotContain("<", hostile);
        Assert.DoesNotContain("\"", hostile);
        Assert.DoesNotContain("?", hostile);
        Assert.DoesNotContain("#", hostile);
        Assert.DoesNotContain("/", hostile["https://github.com/vibesoftwarecoder/ApolloVibe/releases/tag/".Length..]);
    }
}
