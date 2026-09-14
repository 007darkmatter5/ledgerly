using Ledgerly.Data;
using Ledgerly.Services;
using Ledgerly.Services.Email;
using Ledgerly.Services.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ledgerly.Tests;

/// <summary>Admin role rules, the sign-up switch, and email server settings storage.</summary>
public sealed class AdminAndSettingsTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<LedgerlyDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<LedgerlyDbContext>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<RegistrationPolicy>();
        services.AddScoped<AdminService>();
        _services = services.BuildServiceProvider();

        await using var db = await _services.GetRequiredService<IDbContextFactory<LedgerlyDbContext>>().CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<ApplicationUser> CreateUserAsync(IServiceScope scope, string email, bool register = true)
    {
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email };
        Assert.True((await users.CreateAsync(user)).Succeeded);
        if (register)
            await scope.ServiceProvider.GetRequiredService<AdminService>().OnUserCreatedAsync(user);
        return user;
    }

    private static Task<bool> IsAdminAsync(IServiceScope scope, ApplicationUser user) =>
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().IsInRoleAsync(user, AdminService.AdminRole);

    [Fact]
    public async Task First_account_becomes_admin_and_later_ones_dont()
    {
        using var scope = _services.CreateScope();
        var first = await CreateUserAsync(scope, "first@example.com");
        var second = await CreateUserAsync(scope, "second@example.com");

        Assert.True(await IsAdminAsync(scope, first));
        Assert.False(await IsAdminAsync(scope, second));
    }

    [Fact]
    public async Task Existing_accounts_without_an_admin_get_one_at_startup()
    {
        using var scope = _services.CreateScope();
        var newer = await CreateUserAsync(scope, "newer@example.com", register: false);
        var older = await CreateUserAsync(scope, "older@example.com", register: false);
        await using (var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<LedgerlyDbContext>>().CreateDbContextAsync())
        {
            db.Ledgers.AddRange(
                new Ledger { OwnerId = newer.Id, Name = "My ledger", CreatedAt = new DateTime(2026, 9, 1) },
                new Ledger { OwnerId = older.Id, Name = "My ledger", CreatedAt = new DateTime(2026, 1, 1) });
            await db.SaveChangesAsync();
        }

        var admin = scope.ServiceProvider.GetRequiredService<AdminService>();
        await admin.EnsureAdminExistsAsync();
        await admin.EnsureAdminExistsAsync(); // running again doesn't add more admins

        Assert.True(await IsAdminAsync(scope, older));
        Assert.False(await IsAdminAsync(scope, newer));
    }

    [Fact]
    public async Task The_last_admin_cant_be_removed_but_can_hand_over()
    {
        using var scope = _services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<AdminService>();
        var first = await CreateUserAsync(scope, "first@example.com");
        var second = await CreateUserAsync(scope, "second@example.com");

        Assert.NotNull(await admin.SetAdminAsync(first.Id, isAdmin: false));
        Assert.True(await IsAdminAsync(scope, first));
        Assert.True(await admin.IsOnlyAdminWithOtherUsersAsync(first));

        Assert.Null(await admin.SetAdminAsync(second.Id, isAdmin: true));
        Assert.Null(await admin.SetAdminAsync(first.Id, isAdmin: false));

        Assert.False(await IsAdminAsync(scope, first));
        Assert.True(await IsAdminAsync(scope, second));
        Assert.False(await admin.IsOnlyAdminWithOtherUsersAsync(first));
    }

    [Fact]
    public async Task Changing_admin_signs_the_user_out_elsewhere()
    {
        using var scope = _services.CreateScope();
        await CreateUserAsync(scope, "first@example.com");
        var second = await CreateUserAsync(scope, "second@example.com");
        var stampBefore = second.SecurityStamp;

        await scope.ServiceProvider.GetRequiredService<AdminService>().SetAdminAsync(second.Id, isAdmin: true);

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.NotEqual(stampBefore, (await users.FindByIdAsync(second.Id))!.SecurityStamp);
    }

    [Fact]
    public async Task Sign_ups_can_be_turned_off_except_before_the_first_account()
    {
        var policy = _services.GetRequiredService<RegistrationPolicy>();
        Assert.True(await policy.IsOpenAsync());

        await policy.SetOpenAsync(false);
        Assert.True(await policy.IsOpenAsync()); // no accounts yet, so the first admin can still sign up
        Assert.False(await policy.IsOpenSettingAsync());

        using (var scope = _services.CreateScope())
            await CreateUserAsync(scope, "first@example.com");
        Assert.False(await policy.IsOpenAsync());

        await policy.SetOpenAsync(true);
        Assert.True(await policy.IsOpenAsync());
    }

    private EmailSettingsStore CreateEmailStore(EmailOptions? fromConfiguration = null) => new(
        new StaticOptionsMonitor(fromConfiguration ?? new EmailOptions()),
        _services.GetRequiredService<AppSettingsStore>(),
        _services.GetRequiredService<IDataProtectionProvider>(),
        NullLogger<EmailSettingsStore>.Instance);

    private static EmailOptions Smtp(string? password = "s3cret-password") => new()
    {
        Host = "smtp.example.com", Port = 587, Security = EmailSecurity.StartTls,
        Username = "me@example.com", Password = password, FromAddress = "me@example.com"
    };

    [Fact]
    public async Task Saved_email_password_is_encrypted_at_rest_and_never_shown()
    {
        var store = CreateEmailStore();
        await store.SaveAsync(Smtp());

        var stored = await _services.GetRequiredService<AppSettingsStore>().GetAsync("Email.Server");
        Assert.NotNull(stored);
        Assert.DoesNotContain("s3cret-password", stored);

        var effective = await CreateEmailStore().GetEffectiveAsync();
        Assert.Equal(EmailSettingsSource.AdminPage, effective.Source);
        Assert.Equal("s3cret-password", effective.Options.Password);

        var display = await store.GetSavedForDisplayAsync();
        Assert.Null(display!.Options.Password);
        Assert.True(display.HasPassword);
    }

    [Fact]
    public async Task Blank_password_keeps_the_saved_one_unless_removed()
    {
        var store = CreateEmailStore();
        await store.SaveAsync(Smtp());

        var changed = Smtp(password: null);
        changed.Port = 465;
        await store.SaveAsync(changed);
        var afterKeep = await store.GetEffectiveAsync();
        Assert.Equal(465, afterKeep.Options.Port);
        Assert.Equal("s3cret-password", afterKeep.Options.Password);
        Assert.Equal("s3cret-password", await store.ResolvePasswordAsync("", removePassword: false));

        await store.SaveAsync(Smtp(password: null), removePassword: true);
        Assert.Null((await store.GetEffectiveAsync()).Options.Password);
        Assert.False((await store.GetSavedForDisplayAsync())!.HasPassword);
    }

    [Fact]
    public async Task Configuration_overrides_settings_saved_in_the_app()
    {
        await CreateEmailStore().SaveAsync(Smtp());

        var store = CreateEmailStore(new EmailOptions { Host = "smtp.config.example", FromAddress = "config@example.com" });

        Assert.True(store.IsControlledByConfiguration);
        var effective = await store.GetEffectiveAsync();
        Assert.Equal(EmailSettingsSource.Configuration, effective.Source);
        Assert.Equal("smtp.config.example", effective.Options.Host);
    }

    [Fact]
    public async Task Removing_email_settings_turns_email_off()
    {
        var store = CreateEmailStore();
        await store.SaveAsync(Smtp());
        Assert.True((await store.GetEffectiveAsync()).IsConfigured);

        await store.ClearAsync();

        Assert.False((await store.GetEffectiveAsync()).IsConfigured);
        Assert.Null(await store.GetSavedForDisplayAsync());
    }

    [Fact]
    public async Task Password_encrypted_with_lost_keys_is_reported_not_crashed()
    {
        await CreateEmailStore().SaveAsync(Smtp());

        // A different key ring, as if the app moved to another Windows account or machine.
        var otherKeys = new EmailSettingsStore(new StaticOptionsMonitor(new EmailOptions()), _services.GetRequiredService<AppSettingsStore>(),
            new EphemeralDataProtectionProvider(), NullLogger<EmailSettingsStore>.Instance);

        var display = await otherKeys.GetSavedForDisplayAsync();
        Assert.True(display!.PasswordUnreadable);
        Assert.Null((await otherKeys.GetEffectiveAsync()).Options.Password);
    }

    private sealed class StaticOptionsMonitor(EmailOptions value) : IOptionsMonitor<EmailOptions>
    {
        public EmailOptions CurrentValue => value;
        public EmailOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<EmailOptions, string?> listener) => null;
    }
}
