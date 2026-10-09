using System.Text.Json;

namespace MultiSeat.Tests.Updates;

/// <summary>
/// Test data under Updates/Fixtures, copied beside the test assembly. The apollovibe-ms*.json
/// files are the real `gh release view --json` output of the published releases (notes included),
/// and github-releases-apollovibe.json is a real `releases?per_page=30` response, so the parsers
/// are tested against what GitHub and the release author actually wrote.
/// </summary>
internal static class Fixtures
{
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Updates", "Fixtures", name);

    /// <summary>The release-notes text of a real ApolloVibe release fixture.</summary>
    public static string Notes(string fixture)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path(fixture)));
        return doc.RootElement.GetProperty("body").GetString()!;
    }
}
