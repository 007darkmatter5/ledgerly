using Ledgerly.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ledgerly.Services;

/// <summary>Which home page a person sees. Classic is the default and stays the default when nothing is stored.</summary>
public enum HomeView
{
    Classic,
    Coverage
}

/// <summary>
/// Per-user choice of home page, stored on the existing Identity user-claim row so it doesn't need a migration
/// and doesn't follow another person who signs in on the same browser. Absent or unrecognized values are classic.
/// </summary>
public class HomeViewStore(IDbContextFactory<LedgerlyDbContext> dbFactory, ICurrentUser currentUser)
{
    public const string ClaimType = "ledgerly:home-view";
    public const string CoverageValue = "coverage";

    public static HomeView Parse(string? value) =>
        string.Equals(value, CoverageValue, StringComparison.Ordinal) ? HomeView.Coverage : HomeView.Classic;

    public async Task<HomeView> GetAsync()
    {
        var userId = await currentUser.GetUserIdAsync();
        if (string.IsNullOrEmpty(userId))
            return HomeView.Classic;

        await using var db = await dbFactory.CreateDbContextAsync();
        var value = await db.UserClaims.AsNoTracking()
            .Where(c => c.UserId == userId && c.ClaimType == ClaimType)
            .Select(c => c.ClaimValue)
            .FirstOrDefaultAsync();
        return Parse(value);
    }

    public async Task SetAsync(HomeView view)
    {
        var userId = await currentUser.GetUserIdAsync();
        if (string.IsNullOrEmpty(userId))
            throw new InvalidOperationException("Sign in to choose a view.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.UserClaims.Where(c => c.UserId == userId && c.ClaimType == ClaimType).ToListAsync();
        if (view == HomeView.Classic)
        {
            if (existing.Count == 0)
                return;
            db.UserClaims.RemoveRange(existing);
        }
        else if (existing.Count == 1 && existing[0].ClaimValue == CoverageValue)
        {
            return;
        }
        else
        {
            db.UserClaims.RemoveRange(existing);
            db.UserClaims.Add(new IdentityUserClaim<string> { UserId = userId, ClaimType = ClaimType, ClaimValue = CoverageValue });
        }

        await db.SaveChangesAsync();
    }
}
