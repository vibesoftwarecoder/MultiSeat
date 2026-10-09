using System.Text.Json;
using System.Text.Json.Serialization;
using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

/// <summary>
/// The result model is serialized as is by the API later. These tests pin the JSON it produces
/// with the API's own options (camelCase, enums as strings, see ApiServer) against the shape in
/// the design, so step 7 cannot silently change what the dashboard receives.
/// </summary>
public class UpdateModelsTests
{
    // Exactly the options ApiServer registers for minimal-API responses.
    private static readonly JsonSerializerOptions ApiOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Theory]
    [InlineData(UpdateStatus.UpToDate, "upToDate")]
    [InlineData(UpdateStatus.UpdateAvailable, "updateAvailable")]
    [InlineData(UpdateStatus.Ahead, "ahead")]
    [InlineData(UpdateStatus.UnknownInstalled, "unknownInstalled")]
    [InlineData(UpdateStatus.NotInstalled, "notInstalled")]
    [InlineData(UpdateStatus.LatestOnly, "latestOnly")]
    [InlineData(UpdateStatus.Unavailable, "unavailable")]
    [InlineData(UpdateStatus.Disabled, "disabled")]
    public void Status_SerializesAsTheDesignNames(UpdateStatus status, string expected)
    {
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(status, ApiOptions));
    }

    [Theory]
    [InlineData(InstalledSource.Assembly, "assembly")]
    [InlineData(InstalledSource.Marker, "marker")]
    [InlineData(InstalledSource.MarkerModified, "marker-modified")]
    [InlineData(InstalledSource.ReleaseHash, "release-hash")]
    [InlineData(InstalledSource.CommitMatch, "commit-match")]
    public void Source_SerializesAsTheDesignNames_WithTheApiOptionsAndWithoutThem(InstalledSource source, string expected)
    {
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(source, ApiOptions));
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(source));
        Assert.Equal(source, JsonSerializer.Deserialize<InstalledSource>($"\"{expected}\"", ApiOptions));
    }

    [Fact]
    public void Component_SerializesIntoTheDesignShape()
    {
        var c = new ComponentUpdateStatus(
            "multiseat", "MultiSeat", UpdateStatus.UpdateAvailable,
            new InstalledInfo("0.6.18", "0.6.18 (ebcd253)", InstalledSource.Assembly, null),
            null,
            new LatestInfo("0.6.19", "v0.6.19", new DateTimeOffset(2026, 10, 9, 10, 20, 56, TimeSpan.Zero), UpdateRepos.ReleaseUrl(UpdateComponent.MultiSeat, "v0.6.19")),
            true,
            new DateTimeOffset(2026, 10, 9, 14, 2, 11, TimeSpan.Zero),
            null);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(c, ApiOptions));
        var root = doc.RootElement;

        Assert.Equal(
            ["id", "name", "status", "installed", "installedNote", "latest", "announce", "checkedAt", "error"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("updateAvailable", root.GetProperty("status").GetString());
        Assert.Equal(["version", "display", "source", "note"], root.GetProperty("installed").EnumerateObject().Select(p => p.Name));
        Assert.Equal("assembly", root.GetProperty("installed").GetProperty("source").GetString());
        Assert.Equal(["version", "tag", "publishedAt", "releaseUrl"], root.GetProperty("latest").EnumerateObject().Select(p => p.Name));
        Assert.Equal("https://github.com/vibesoftwarecoder/MultiSeat/releases/tag/v0.6.19", root.GetProperty("latest").GetProperty("releaseUrl").GetString());
        Assert.Equal(JsonValueKind.True, root.GetProperty("announce").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("installedNote").ValueKind);
    }

    [Fact]
    public void TheModel_HasNoFieldThatCarriesGitHubTextToTheDashboard()
    {
        var names = new[] { typeof(ComponentUpdateStatus), typeof(InstalledInfo), typeof(LatestInfo) }
            .SelectMany(t => t.GetProperties().Select(p => p.Name))
            .ToArray();

        Assert.DoesNotContain(names, n => n is "Body" or "HtmlUrl" or "Notes" or "Assets" or "Digest");
    }
}
