using Ledgerly.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ledgerly.Components.Account;

internal static class IdentityEndpoints
{
    /// <summary>Endpoints the account components post to that can't be handled inside a component.</summary>
    public static IEndpointConventionBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var accountGroup = endpoints.MapGroup("/Account");

        accountGroup.MapPost("/Logout", async (
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect($"~/{returnUrl}");
        });

        return accountGroup;
    }
}
