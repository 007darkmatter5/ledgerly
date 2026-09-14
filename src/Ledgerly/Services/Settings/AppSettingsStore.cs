using Ledgerly.Data;
using Microsoft.EntityFrameworkCore;

namespace Ledgerly.Services.Settings;

/// <summary>Reads and writes app-wide settings. Callers are responsible for checking the user is an admin.</summary>
public class AppSettingsStore(IDbContextFactory<LedgerlyDbContext> dbFactory)
{
    public async Task<string?> GetAsync(string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AppSettings.Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync();
    }

    public async Task SetAsync(string key, string value)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var setting = await db.AppSettings.FindAsync(key);
        if (setting is null)
            db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else
            setting.Value = value;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.AppSettings.Where(s => s.Key == key).ExecuteDeleteAsync();
    }
}
