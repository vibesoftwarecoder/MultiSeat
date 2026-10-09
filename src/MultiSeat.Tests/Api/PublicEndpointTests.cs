using Microsoft.AspNetCore.Http;
using MultiSeat.Service.Api;
using Xunit;

namespace MultiSeat.Tests.Api;

/// <summary>
/// Pins which requests skip the API key while authentication is enabled.
///
/// Exactly one does: GET /api/system/auth, so the dashboard can render the auth toggle before it
/// has a key. Everything else must be gated — most importantly POST on that same path, which is
/// the call that turns authentication off. An exemption there would let anyone who can reach the
/// port disable the protection for every other endpoint.
///
/// The endpoints also carry AllowAnonymous(), which grants nothing: there is no UseAuthorization()
/// in this pipeline, so no authorization metadata is ever read. This predicate is the entire rule,
/// which is what these tests exist to hold still.
/// </summary>
public class PublicEndpointTests
{
    [Fact]
    public void ReadingAuthState_IsPublic()
    {
        Assert.True(ApiServer.IsAlwaysPublic(new PathString("/api/system/auth"), "GET"));
    }

    [Theory]
    [InlineData("POST")]    // turns authentication OFF — must never be exempt
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void ChangingAuthState_IsNotPublic(string method)
    {
        Assert.False(ApiServer.IsAlwaysPublic(new PathString("/api/system/auth"), method));
    }

    // The update routes report installed versions and one of them can trigger a connection to the
    // internet, so all of them need the key (and the settings one writes a config file).
    [Theory]
    [InlineData("/api/system/updates", "GET")]
    [InlineData("/api/system/updates", "POST")]
    [InlineData("/api/system/updates/check", "POST")]
    [InlineData("/api/system/updates/check", "GET")]
    [InlineData("/api/system/updates/settings", "POST")]
    [InlineData("/api/system/updates/settings", "GET")]
    public void TheUpdateRoutes_AreNotPublic(string path, string method)
    {
        Assert.False(ApiServer.IsAlwaysPublic(new PathString(path), method));
    }

    [Theory]
    [InlineData("/api/seats", "GET")]
    [InlineData("/api/accounts", "GET")]
    [InlineData("/api/system/auth/extra", "GET")]   // prefix must not be enough
    [InlineData("/ws/seats", "GET")]                // broadcasts full SeatInfo
    public void EverythingElse_IsGated(string path, string method)
    {
        Assert.False(ApiServer.IsAlwaysPublic(new PathString(path), method));
    }
}
