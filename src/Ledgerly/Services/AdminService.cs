using Ledgerly.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ledgerly.Services;

public record UserSummary(string Id, string Email, bool IsAdmin, bool TwoFactorEnabled, bool IsLockedOut);

/// <summary>Admins manage app-wide settings (email server, sign-ups) and who else is an admin.</summary>
public class AdminService(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager, LedgerlyDbContext db)
{
    public const string AdminRole = "Admin";

    /// <summary>
    /// Makes sure the Admin role exists and that someone holds it. If there are accounts but no admin
    /// (e.g. accounts created before admins existed), the longest-standing account becomes admin.
    /// </summary>
    public async Task EnsureAdminExistsAsync()
    {
        if (!await roleManager.RoleExistsAsync(AdminRole))
            await roleManager.CreateAsync(new IdentityRole(AdminRole));

        if ((await userManager.GetUsersInRoleAsync(AdminRole)).Count > 0)
            return;

        // Users don't record when they signed up; their personal ledger's creation time is the best proxy.
        var oldest = await db.Users
            .Select(u => new
            {
                User = u,
                Since = db.Ledgers.Where(l => l.OwnerId == u.Id && !l.IsSample).Select(l => (DateTime?)l.CreatedAt).FirstOrDefault()
            })
            .ToListAsync();

        var first = oldest
            .OrderBy(x => x.Since is null)
            .ThenBy(x => x.Since)
            .ThenBy(x => x.User.Email)
            .Select(x => x.User)
            .FirstOrDefault();

        if (first is not null)
            await AddToAdminAsync(first);
    }

    /// <summary>Call right after an account is created, before signing it in, so the admin role is in its first cookie.</summary>
    public async Task OnUserCreatedAsync(ApplicationUser user)
    {
        if (!await roleManager.RoleExistsAsync(AdminRole))
            await roleManager.CreateAsync(new IdentityRole(AdminRole));

        if ((await userManager.GetUsersInRoleAsync(AdminRole)).Count == 0)
            await userManager.AddToRoleAsync(user, AdminRole);
    }

    public async Task<List<UserSummary>> GetUsersAsync()
    {
        var admins = (await userManager.GetUsersInRoleAsync(AdminRole)).Select(u => u.Id).ToHashSet();
        var users = await db.Users.AsNoTracking().ToListAsync();
        var now = DateTimeOffset.UtcNow;
        return users
            .Select(u => new UserSummary(u.Id, u.Email ?? u.UserName ?? "", admins.Contains(u.Id), u.TwoFactorEnabled, u.LockoutEnd > now))
            .OrderByDescending(u => u.IsAdmin)
            .ThenBy(u => u.Email)
            .ToList();
    }

    /// <summary>Grants or removes admin. Refuses to remove the last admin. Returns an error message, or null on success.</summary>
    public async Task<string?> SetAdminAsync(string userId, bool isAdmin)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
            return "That account no longer exists.";

        var currentlyAdmin = await userManager.IsInRoleAsync(user, AdminRole);
        if (currentlyAdmin == isAdmin)
            return null;

        if (!isAdmin && (await userManager.GetUsersInRoleAsync(AdminRole)).Count <= 1)
            return "Ledgerly needs at least one admin. Make someone else an admin first.";

        var result = isAdmin
            ? await AddToAdminAsync(user)
            : await userManager.RemoveFromRoleAsync(user, AdminRole);
        if (!result.Succeeded)
            return string.Join(" ", result.Errors.Select(e => e.Description));

        // Changing the security stamp signs the user out elsewhere, so their role change takes effect.
        await userManager.UpdateSecurityStampAsync(user);
        return null;
    }

    /// <summary>True if this user is the only admin while other accounts exist, so deleting them would leave no admin.</summary>
    public async Task<bool> IsOnlyAdminWithOtherUsersAsync(ApplicationUser user)
    {
        if (!await userManager.IsInRoleAsync(user, AdminRole))
            return false;

        var adminCount = (await userManager.GetUsersInRoleAsync(AdminRole)).Count;
        var otherUsers = await db.Users.AnyAsync(u => u.Id != user.Id);
        return adminCount == 1 && otherUsers;
    }

    private Task<IdentityResult> AddToAdminAsync(ApplicationUser user) => userManager.AddToRoleAsync(user, AdminRole);
}
