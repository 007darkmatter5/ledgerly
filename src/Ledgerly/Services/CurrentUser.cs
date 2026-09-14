using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Ledgerly.Services;

/// <summary>Who is signed in. Abstracted so data access can be tested without a web host.</summary>
public interface ICurrentUser
{
    /// <summary>The signed-in user's id, or null when nobody is signed in.</summary>
    Task<string?> GetUserIdAsync();
}

public class AuthenticationStateCurrentUser(AuthenticationStateProvider authenticationStateProvider) : ICurrentUser
{
    public async Task<string?> GetUserIdAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.Identity?.IsAuthenticated == true
            ? state.User.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;
    }
}
