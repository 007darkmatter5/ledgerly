using System.Globalization;
using Ledgerly.Components;
using Ledgerly.Components.Account;
using Ledgerly.Data;
using Ledgerly.Services;
using Ledgerly.Services.Backup;
using Ledgerly.Services.Email;
using Ledgerly.Services.Settings;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Money and dates are formatted with one fixed culture. Containers usually have no LANG set, which would
// otherwise fall back to the invariant culture and show amounts as "¤1,234.00".
var culture = CultureInfo.GetCultureInfo(builder.Configuration["Ledgerly:Culture"] is { Length: > 0 } cultureName ? cultureName : "en-US");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// Data
var connectionString = builder.Configuration.GetConnectionString("Ledgerly") ?? "Data Source=App_Data/ledgerly.db";
builder.Services.AddDbContextFactory<LedgerlyDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICurrentUser, AuthenticationStateCurrentUser>();
builder.Services.AddScoped<LedgerService>();
builder.Services.AddSingleton<DataGeneration>();
builder.Services.AddScoped<BackupService>();

// Authentication
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // Email is optional, so accounts don't need email confirmation.
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<LedgerlyDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

// Re-check sign-ins against the database every minute, so role changes and password changes apply promptly.
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

// Encrypts saved secrets (the email server password) and auth cookies. A fixed application name keeps
// them readable if the app folder moves. Keys default to the user profile; containers set
// DataProtection:KeysPath to a persistent volume, separate from the database, so updates don't lose them.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Ledgerly");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddSingleton<AppInfo>();
builder.Services.AddHealthChecks();

// App-wide settings managed by admins
builder.Services.AddSingleton<AppSettingsStore>();
builder.Services.AddSingleton<RegistrationPolicy>();
builder.Services.AddScoped<AdminService>();

// Email (optional). Configured by an admin in the app, or by the "Email" configuration section, which takes priority.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddSingleton<EmailSettingsStore>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityEmailSender>();
builder.Services.AddScoped<EmailLinks>();

var app = builder.Build();

// Make sure the database's folder exists (App_Data locally, a mounted volume in containers).
var databasePath = new SqliteConnectionStringBuilder(connectionString).DataSource;
if (!string.IsNullOrEmpty(databasePath) && databasePath != ":memory:" && Path.GetDirectoryName(Path.GetFullPath(databasePath)) is { Length: > 0 } databaseFolder)
    Directory.CreateDirectory(databaseFolder);

using (var scope = app.Services.CreateScope())
{
    await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<LedgerlyDbContext>>().CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<AdminService>().EnsureAdminExistsAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Every request (and so every interactive circuit) uses the configured culture, whatever the browser sends.
var localization = new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(culture),
    SupportedCultures = [culture],
    SupportedUICultures = [culture]
};
localization.RequestCultureProviders.Clear();
app.UseRequestLocalization(localization);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapIdentityEndpoints();
app.MapBackupEndpoints();

// Used by the install script (and container orchestrators) to confirm the app started.
app.MapHealthChecks("/healthz");

app.Run();
