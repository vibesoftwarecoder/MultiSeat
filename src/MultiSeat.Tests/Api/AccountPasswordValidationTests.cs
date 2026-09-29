using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MultiSeat.Service.Api;
using Xunit;

namespace MultiSeat.Tests.Api;

/// <summary>
/// Issue #76: POST /api/accounts accepted a null, empty, or whitespace-only password and handed
/// it straight to NetUserAdd, creating a Windows account nothing could actually log into.
///
/// mgr is passed as null throughout, deliberately. A rejected password must return its 400
/// without ever touching the account manager — if the check moved to run after
/// AccountManager.CreateAccount was called, these tests would fail with a
/// NullReferenceException instead of quietly passing. The "still works" test relies on the same
/// signal in reverse: a real password has to reach AccountManager.CreateAccount, and with mgr
/// null that call throws, so seeing that exception (not a 400) is the proof it got through.
/// </summary>
public class AccountPasswordValidationTests
{
    private static int StatusOf(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string ErrorOf(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return JsonSerializer.SerializeToElement(value).GetProperty("error").GetString()!;
    }

    // ── Rejected before AccountManager is ever touched ────────────────────────

    [Fact]
    public void NullPassword_IsRejected_AndNeverReachesAccountManager()
    {
        var result = AccountEndpoints.CreateAccount(
            new AccountCreateRequest("GuestTest", null!), mgr: null!);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Contains("Password", ErrorOf(result));
    }

    [Fact]
    public void EmptyPassword_IsRejected_AndNeverReachesAccountManager()
    {
        var result = AccountEndpoints.CreateAccount(
            new AccountCreateRequest("GuestTest", ""), mgr: null!);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Contains("Password", ErrorOf(result));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \t  ")]
    public void WhitespaceOnlyPassword_IsRejected_AndNeverReachesAccountManager(string password)
    {
        var result = AccountEndpoints.CreateAccount(
            new AccountCreateRequest("GuestTest", password), mgr: null!);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Contains("Password", ErrorOf(result));
    }

    // ── A real password is unaffected — it still reaches account creation ─────

    [Fact]
    public void ARealPassword_StillReachesAccountCreation()
    {
        // mgr is null, so getting past both validation checks means the very next line —
        // mgr.CreateAccount(...) — throws. A 400 here would mean the fix started rejecting
        // legitimate passwords too, which the earlier tests don't exercise.
        Assert.Throws<NullReferenceException>(() => AccountEndpoints.CreateAccount(
            new AccountCreateRequest("GuestTest", "Sup3r!Secret"), mgr: null!));
    }

    // ── The validation helper itself ───────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("  \t\t  ")]
    public void IsValidPassword_RejectsNullEmptyAndWhitespace(string? password)
    {
        Assert.False(ApiInputValidation.IsValidPassword(password));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("Sup3r!Secret")]
    [InlineData(" leading-and-trailing-space-but-real-content ")]
    public void IsValidPassword_AcceptsAnythingWithRealContent(string password)
    {
        Assert.True(ApiInputValidation.IsValidPassword(password));
    }
}
