using MultiSeat.Service.Configuration;
using Xunit;

namespace MultiSeat.Tests.Sessions;

public sealed class SteamIsolationOptionsTests
{
    [Fact]
    public void ReadyTimeoutDefaultsToThreeMinutes() =>
        Assert.Equal(TimeSpan.FromSeconds(180), new MultiSeatOptions().UsableSteamIsolationReadyTimeout);

    // A typo must not make every isolated launch fail at once (0, negative) or hold a seat's
    // lifecycle gate for hours (very large), so the value is kept in 10 s .. 15 min.
    [Theory]
    [InlineData(-5, 10)]
    [InlineData(0, 10)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(45, 45)]
    [InlineData(900, 900)]
    [InlineData(901, 900)]
    [InlineData(int.MaxValue, 900)]
    public void ReadyTimeoutIsClampedToAUsableRange(int configured, int expectedSeconds)
    {
        var options = new MultiSeatOptions { SteamIsolationReadyTimeoutSeconds = configured };
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.UsableSteamIsolationReadyTimeout);
    }

    [Fact]
    public void ShippedSettingsFileCarriesTheSameDefault()
    {
        // The default in code and the default in the shipped appsettings.json are two copies of
        // one number; if they drift, a host that never touched the file behaves differently from
        // what the code's own default says.
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        // The shipped file carries comments, which the default JSON reader rejects.
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path),
            new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        var shipped = document.RootElement.GetProperty("MultiSeat")
            .GetProperty("SteamIsolationReadyTimeoutSeconds").GetInt32();
        Assert.Equal(new MultiSeatOptions().SteamIsolationReadyTimeoutSeconds, shipped);
    }
}
