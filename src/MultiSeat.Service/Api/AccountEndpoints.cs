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

        group.MapPost("/", (AccountCreateRequest request, AccountManager mgr) =>
        {
            if (!ApiInputValidation.IsValidAccountName(request.Username))
                return ApiInputValidation.AccountNameError();
            try
            {
                var account = mgr.CreateAccount(request.Username, request.Password);
                return Results.Created($"/api/accounts/{account.Username}", account);
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.ToResult(ex);
            }
        });

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
}

public sealed record AccountCreateRequest(string Username, string Password);
