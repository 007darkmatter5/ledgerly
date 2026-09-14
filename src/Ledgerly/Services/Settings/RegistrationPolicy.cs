using Ledgerly.Data;
using Microsoft.EntityFrameworkCore;

namespace Ledgerly.Services.Settings;

/// <summary>Whether new people can create accounts. Admins can turn sign-ups off.</summary>
public class RegistrationPolicy(AppSettingsStore settings, IDbContextFactory<LedgerlyDbContext> dbFactory)
{
    private const string Key = "Registration.Open";

    /// <summary>The admin's choice, ignoring the first-account exception.</summary>
    public async Task<bool> IsOpenSettingAsync() => await settings.GetAsync(Key) != "false";

    /// <summary>Sign-ups are allowed if the admin left them on, or if there are no accounts yet (so the first admin can be created).</summary>
    public async Task<bool> IsOpenAsync()
    {
        if (await IsOpenSettingAsync())
            return true;

        await using var db = await dbFactory.CreateDbContextAsync();
        return !await db.Users.AnyAsync();
    }

    public Task SetOpenAsync(bool open) => settings.SetAsync(Key, open ? "true" : "false");
}
