using System.Security.Cryptography;
using System.Text.Json;
using Ledgerly.Services.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Ledgerly.Services.Email;

public enum EmailSettingsSource
{
    /// <summary>No email server is set up.</summary>
    None,

    /// <summary>From configuration (user secrets, environment variables, appsettings). Takes priority and can't be edited in the app.</summary>
    Configuration,

    /// <summary>Saved by an admin in the app.</summary>
    AdminPage
}

public record EffectiveEmailSettings(EmailOptions Options, EmailSettingsSource Source)
{
    public bool IsConfigured => Source != EmailSettingsSource.None;
}

/// <summary>What the admin page shows about saved settings. The password itself is never returned.</summary>
public record SavedEmailSettings(EmailOptions Options, bool HasPassword, bool PasswordUnreadable);

/// <summary>
/// Email server settings. Configuration wins when it sets a host; otherwise the settings an admin saved
/// in the app are used. The saved password is encrypted with ASP.NET Core Data Protection, whose keys are
/// kept in the Windows user profile rather than next to the database.
/// </summary>
public class EmailSettingsStore(IOptionsMonitor<EmailOptions> configuration, AppSettingsStore settings, IDataProtectionProvider dataProtection, ILogger<EmailSettingsStore> logger)
{
    private const string Key = "Email.Server";
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Ledgerly.EmailServer.Password");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SavedEmailSettings? _cached;
    private bool _loaded;

    public bool IsControlledByConfiguration => !string.IsNullOrWhiteSpace(configuration.CurrentValue.Host);

    public async Task<EffectiveEmailSettings> GetEffectiveAsync()
    {
        if (IsControlledByConfiguration)
        {
            var fromConfig = configuration.CurrentValue;
            return new(fromConfig, fromConfig.IsConfigured ? EmailSettingsSource.Configuration : EmailSettingsSource.None);
        }

        var saved = await GetSavedAsync(includePassword: true);
        return saved is not null && saved.Options.IsConfigured
            ? new(saved.Options, EmailSettingsSource.AdminPage)
            : new(new EmailOptions(), EmailSettingsSource.None);
    }

    /// <summary>Settings saved in the app, without the password, for showing in the admin form.</summary>
    public Task<SavedEmailSettings?> GetSavedForDisplayAsync() => GetSavedAsync(includePassword: false);

    /// <summary>Saves settings. A null or empty password keeps the saved one unless <paramref name="removePassword"/> is set.</summary>
    public async Task SaveAsync(EmailOptions options, bool removePassword = false)
    {
        string? protectedPassword;
        if (removePassword)
            protectedPassword = null;
        else if (!string.IsNullOrEmpty(options.Password))
            protectedPassword = _protector.Protect(options.Password);
        else
            protectedPassword = (await ReadStoredAsync())?.ProtectedPassword;

        var stored = new StoredSettings(options.Host?.Trim(), options.Port, options.Security, NullIfBlank(options.Username),
            protectedPassword, options.FromAddress?.Trim(), string.IsNullOrWhiteSpace(options.FromName) ? "Ledgerly" : options.FromName.Trim(),
            NullIfBlank(options.PublicBaseUrl)?.TrimEnd('/'));

        await settings.SetAsync(Key, JsonSerializer.Serialize(stored));
        Invalidate();
    }

    /// <summary>Returns the password to use when testing a form: the typed one, or the saved one if left blank.</summary>
    public async Task<string?> ResolvePasswordAsync(string? typedPassword, bool removePassword)
    {
        if (removePassword)
            return null;
        if (!string.IsNullOrEmpty(typedPassword))
            return typedPassword;
        return (await GetSavedAsync(includePassword: true))?.Options.Password;
    }

    public async Task ClearAsync()
    {
        await settings.DeleteAsync(Key);
        Invalidate();
    }

    /// <summary>Forgets cached settings, e.g. after a restore replaced the database.</summary>
    public void Reload() => Invalidate();

    /// <summary>The saved password in plain text, for carrying it into a backup. Null if there is none or it can't be decrypted.</summary>
    public async Task<string?> GetSavedPasswordAsync() => (await GetSavedAsync(includePassword: true))?.Options.Password;

    /// <summary>Encrypts a password with this install's keys and saves it with the settings already saved in the app.</summary>
    public async Task SetSavedPasswordAsync(string password)
    {
        Invalidate();
        if (await GetSavedAsync(includePassword: true) is not { } saved)
            return;

        var options = Copy(saved.Options);
        options.Password = password;
        await SaveAsync(options);
    }

    private void Invalidate()
    {
        _cached = null;
        _loaded = false;
    }

    private async Task<SavedEmailSettings?> GetSavedAsync(bool includePassword)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_loaded)
            {
                _cached = await LoadAsync();
                _loaded = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        if (_cached is null || includePassword)
            return _cached;

        var withoutPassword = Copy(_cached.Options);
        withoutPassword.Password = null;
        return _cached with { Options = withoutPassword };
    }

    private async Task<SavedEmailSettings?> LoadAsync()
    {
        var stored = await ReadStoredAsync();
        if (stored is null)
            return null;

        string? password = null;
        var unreadable = false;
        if (stored.ProtectedPassword is not null)
        {
            try
            {
                password = _protector.Unprotect(stored.ProtectedPassword);
            }
            catch (CryptographicException ex)
            {
                // Happens if the encryption keys were lost (e.g. a different Windows user or machine).
                logger.LogWarning(ex, "The saved email server password couldn't be decrypted.");
                unreadable = true;
            }
        }

        var options = new EmailOptions
        {
            Host = stored.Host,
            Port = stored.Port,
            Security = stored.Security,
            Username = stored.Username,
            Password = password,
            FromAddress = stored.FromAddress,
            FromName = stored.FromName ?? "Ledgerly",
            PublicBaseUrl = stored.PublicBaseUrl
        };
        return new SavedEmailSettings(options, stored.ProtectedPassword is not null, unreadable);
    }

    private async Task<StoredSettings?> ReadStoredAsync()
    {
        var json = await settings.GetAsync(Key);
        return json is null ? null : JsonSerializer.Deserialize<StoredSettings>(json);
    }

    private static EmailOptions Copy(EmailOptions o) => new()
    {
        Host = o.Host, Port = o.Port, Security = o.Security, Username = o.Username, Password = o.Password,
        FromAddress = o.FromAddress, FromName = o.FromName, PublicBaseUrl = o.PublicBaseUrl
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record StoredSettings(
        string? Host, int Port, EmailSecurity Security, string? Username, string? ProtectedPassword,
        string? FromAddress, string? FromName, string? PublicBaseUrl);
}
