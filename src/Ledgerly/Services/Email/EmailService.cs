using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Ledgerly.Services.Email;

public interface IEmailService
{
    Task<bool> IsConfiguredAsync();

    /// <summary>Sends one message with the current settings. Throws <see cref="EmailNotConfiguredException"/> if no server is set up.</summary>
    Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default);

    /// <summary>Sends one message with specific settings, e.g. to test settings before saving them.</summary>
    Task SendWithSettingsAsync(EmailOptions settings, string toAddress, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default);
}

public class EmailNotConfiguredException() : InvalidOperationException("No email server is configured.");

/// <summary>Sends mail through an SMTP server. Settings come from <see cref="EmailSettingsStore"/> on every send.</summary>
public class SmtpEmailService(EmailSettingsStore settingsStore, ILogger<SmtpEmailService> logger) : IEmailService
{
    public async Task<bool> IsConfiguredAsync() => (await settingsStore.GetEffectiveAsync()).IsConfigured;

    public async Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
    {
        var effective = await settingsStore.GetEffectiveAsync();
        if (!effective.IsConfigured)
            throw new EmailNotConfiguredException();

        await SendWithSettingsAsync(effective.Options, toAddress, subject, htmlBody, textBody, cancellationToken);
    }

    public async Task SendWithSettingsAsync(EmailOptions settings, string toAddress, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
    {
        if (!settings.IsConfigured)
            throw new EmailNotConfiguredException();

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        await client.ConnectAsync(settings.Host, settings.Port, settings.Security switch
        {
            EmailSecurity.StartTls => SecureSocketOptions.StartTls,
            EmailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            EmailSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto
        }, cancellationToken);

        if (!string.IsNullOrEmpty(settings.Username))
            await client.AuthenticateAsync(settings.Username, settings.Password ?? "", cancellationToken);

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        logger.LogInformation("Sent email \"{Subject}\" via {Host}:{Port}.", subject, settings.Host, settings.Port);
    }
}
