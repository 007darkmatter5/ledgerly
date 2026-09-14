using Ledgerly.Data;
using Ledgerly.Services.Email;

namespace Ledgerly.Tests;

public class EmailTests
{
    private sealed class CapturingEmailService : IEmailService
    {
        public List<(string To, string Subject, string Html, string Text)> Sent { get; } = [];
        public Task<bool> IsConfiguredAsync() => Task.FromResult(true);

        public Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
        {
            Sent.Add((toAddress, subject, htmlBody, textBody));
            return Task.CompletedTask;
        }

        public Task SendWithSettingsAsync(EmailOptions settings, string toAddress, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default) =>
            SendAsync(toAddress, subject, htmlBody, textBody, cancellationToken);
    }

    [Fact]
    public async Task Password_reset_email_contains_the_link_safely_encoded()
    {
        var email = new CapturingEmailService();
        var link = "https://ledgerly.example.com/Account/ResetPassword?code=abc123&x=<script>";

        await new IdentityEmailSender(email).SendPasswordResetLinkAsync(new ApplicationUser(), "pat@example.com", link);

        var sent = Assert.Single(email.Sent);
        Assert.Equal("pat@example.com", sent.To);
        Assert.Contains("Reset", sent.Subject);
        Assert.Contains("code=abc123&amp;x=&lt;script&gt;", sent.Html);
        Assert.DoesNotContain("<script>", sent.Html);
        Assert.Contains(link, sent.Text);
    }

    [Theory]
    [InlineData(null, "me@example.com", false)]
    [InlineData("smtp.example.com", null, false)]
    [InlineData(" ", "me@example.com", false)]
    [InlineData("smtp.example.com", "me@example.com", true)]
    public void Email_is_configured_only_with_a_host_and_from_address(string? host, string? from, bool expected)
    {
        Assert.Equal(expected, new EmailOptions { Host = host, FromAddress = from }.IsConfigured);
    }
}
