using System.Diagnostics.CodeAnalysis;

namespace Ledgerly.Services.Email;

public enum EmailSecurity
{
    /// <summary>Let the client decide: implicit TLS on port 465, otherwise STARTTLS when the server offers it.</summary>
    Auto,

    /// <summary>Connect unencrypted, then upgrade with STARTTLS (usually port 587). Fails if the server doesn't support it.</summary>
    StartTls,

    /// <summary>TLS from the start (usually port 465).</summary>
    SslOnConnect,

    /// <summary>No encryption. Only for local test servers.</summary>
    None
}

/// <summary>
/// SMTP settings, bound from the "Email" configuration section. Keep the password out of appsettings.json:
/// use user secrets in development or environment variables (Email__Password) elsewhere.
/// </summary>
public class EmailOptions
{
    public const string SectionName = "Email";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public EmailSecurity Security { get; set; } = EmailSecurity.Auto;
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>The address emails come from. Many providers require it to match the account you sign in with.</summary>
    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "Ledgerly";

    /// <summary>
    /// The address people use to reach Ledgerly, e.g. "https://ledgerly.example.com". Links in emails use it,
    /// so a forged Host header can't redirect password reset links. Falls back to the current request's address.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    [MemberNotNullWhen(true, nameof(Host), nameof(FromAddress))]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}
