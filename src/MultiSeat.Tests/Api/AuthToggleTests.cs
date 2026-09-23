using MultiSeat.Service.Api;
using Xunit;

namespace MultiSeat.Tests.Api;

/// <summary>
/// Issue #61. Turning API authentication on or off from the dashboard had three defects, and the
/// worst of them was not the one reported.
///
/// 1. **A hard lockout.** The middleware rejects a request when <c>presented != authState.ApiKey</c>.
///    On a host configured <c>ApiKey = "disabled"</c> the resolved key is empty, and the old
///    <c>SetEnabled(true)</c> enabled authentication against it — so every request 401'd, including
///    the POST that would turn it off again, which is itself gated. Recovery meant editing a config
///    file and restarting the service.
/// 2. **Persisted to a file that is then ignored.** Program.cs loads appsettings.json first and
///    appsettings.local.json second, so the local file wins. Writing to appsettings.json while the
///    local file set ApiKey meant the toggle reverted on the next restart, silently.
/// 3. **"" carried a hidden meaning.** An empty ApiKey does not mean "no key" — it means "fall back
///    to api-key.txt", generating one the operator has never seen.
/// </summary>
public class AuthToggleTests
{
    // ── 1. The lockout, made unreachable by construction ──────────────────────────────

    [Fact]
    public void Enable_WithNoKey_IsRefused()
    {
        var state = new ApiAuthState(enabled: false, apiKey: "");

        Assert.Throws<ArgumentException>(() => state.Enable(""));
        Assert.False(state.IsEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Enable_WithoutAUsableKey_NeverLeavesAuthOn(string? key)
    {
        // The state a host sits in when configured "disabled": auth off, key empty.
        var state = new ApiAuthState(enabled: false, apiKey: "");

        Assert.Throws<ArgumentException>(() => state.Enable(key!));

        // The important half: it must not be left enforcing an empty key, because that rejects
        // every caller including the one that could undo it.
        Assert.False(state.IsEnabled);
        Assert.Equal("", state.ApiKey);
    }

    [Fact]
    public void Enable_SetsTheKeyBeforeTheFlag()
    {
        var state = new ApiAuthState(enabled: false, apiKey: "");

        state.Enable("a-real-key");

        Assert.True(state.IsEnabled);
        Assert.Equal("a-real-key", state.ApiKey);
    }

    [Fact]
    public void Disable_KeepsTheKeyForReuse()
    {
        var state = new ApiAuthState(enabled: true, apiKey: "a-real-key");

        state.Disable();

        Assert.False(state.IsEnabled);
        Assert.Equal("a-real-key", state.ApiKey);
    }

    // ── 2. Persisting to the file that actually decides ───────────────────────────────

    [Fact]
    public void PersistTarget_IsTheLocalFile_WhenItSetsTheApiKey()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """{"MultiSeat":{"ApiKey":""}}""");
            File.WriteAllText(Path.Combine(dir, "appsettings.local.json"), """{"MultiSeat":{"ApiKey":"host-key"}}""");

            Assert.Equal(
                Path.Combine(dir, "appsettings.local.json"),
                SystemEndpoints.ResolveApiKeySettingsFile(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PersistTarget_IsAppSettings_WhenNoLocalFileExists()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """{"MultiSeat":{"ApiKey":""}}""");

            Assert.Equal(
                Path.Combine(dir, "appsettings.json"),
                SystemEndpoints.ResolveApiKeySettingsFile(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PersistTarget_IsAppSettings_WhenTheLocalFileDoesNotSetTheKey()
    {
        // A local file that overrides something else does not decide the key, so it is the wrong
        // place to write one.
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """{"MultiSeat":{"ApiKey":""}}""");
            File.WriteAllText(Path.Combine(dir, "appsettings.local.json"), """{"MultiSeat":{"AudioMode":"PerSession"}}""");

            Assert.Equal(
                Path.Combine(dir, "appsettings.json"),
                SystemEndpoints.ResolveApiKeySettingsFile(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PersistTarget_FallsBack_WhenTheLocalFileIsUnparseable()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """{"MultiSeat":{"ApiKey":""}}""");
            File.WriteAllText(Path.Combine(dir, "appsettings.local.json"), "{ this is not json");

            Assert.Equal(
                Path.Combine(dir, "appsettings.json"),
                SystemEndpoints.ResolveApiKeySettingsFile(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"multiseat-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
