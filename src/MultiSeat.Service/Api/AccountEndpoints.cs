using MultiSeat.Service.Accounts;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Api;

public static class AccountEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/accounts").WithTags("Accounts");

        group.MapGet("/", (AccountManager mgr) =>
            Results.Ok(mgr.ListManagedAccounts()));

        group.MapPost("/", CreateAccount);

        group.MapPost("/link", (AccountCreateRequest request, AccountManager mgr) =>
        {
            if (!ApiInputValidation.IsValidAccountName(request.Username))
                return ApiInputValidation.AccountNameError();
            try
            {
                var account = mgr.LinkExistingAccount(request.Username, request.Password);
                return Results.Created($"/api/accounts/{account.Username}", account);
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.ToResult(ex);
            }
        });

        group.MapDelete("/{username}", (string username, AccountManager mgr) =>
        {
            if (!ApiInputValidation.IsValidAccountName(username))
                return ApiInputValidation.AccountNameError();
            try
            {
                mgr.DeleteAccount(username);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.ToResult(ex);
            }
        });
    }

    /// <summary>
    /// POST /api/accounts. A method rather than a lambda so tests can call the real handler.
    /// </summary>
    internal static IResult CreateAccount(AccountCreateRequest request, AccountManager mgr)
    {
        if (!ApiInputValidation.IsValidAccountName(request.Username))
            return ApiInputValidation.AccountNameError();

        // Checked before the account manager is touched: NetUserAdd accepts a blank password
        // without complaint, and a null one here would just get a strong one generated for it —
        // so this is what actually stops a caller from creating an account nothing can log into
        // (issue #76).
        if (!ApiInputValidation.IsValidPassword(request.Password))
            return ApiInputValidation.PasswordError();

        try
        {
            var account = mgr.CreateAccount(request.Username, request.Password);
            return Results.Created($"/api/accounts/{account.Username}", account);
        }
        catch (InvalidOperationException ex)
        {
            return ApiErrors.ToResult(ex);
        }
    }
}

public sealed record AccountCreateRequest(string Username, string Password);
