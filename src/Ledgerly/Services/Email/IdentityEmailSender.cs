using System.Net;
using Ledgerly.Data;
using Microsoft.AspNetCore.Identity;

namespace Ledgerly.Services.Email;

/// <summary>
/// The emails ASP.NET Core Identity sends. Unlike the template's sender, links are passed in
/// unencoded; they're HTML-encoded here where they're placed into the message.
/// </summary>
public class IdentityEmailSender(IEmailService email) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string toAddress, string confirmationLink) =>
        email.SendAsync(toAddress, "Confirm your Ledgerly email",
            EmailTemplates.Html("Confirm your email", "Confirm this address for your Ledgerly account.", "Confirm email", confirmationLink,
                "If you didn't create a Ledgerly account, you can ignore this email."),
            EmailTemplates.Text("Confirm this address for your Ledgerly account:", confirmationLink));

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string toAddress, string resetLink) =>
        email.SendAsync(toAddress, "Reset your Ledgerly password",
            EmailTemplates.Html("Reset your password", "Someone asked to reset the password for your Ledgerly account. The link works once and expires in a day.",
                "Choose a new password", resetLink, "If this wasn't you, ignore this email. Your password won't change."),
            EmailTemplates.Text("Reset your Ledgerly password with this link (it works once and expires in a day):", resetLink));

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string toAddress, string resetCode) =>
        email.SendAsync(toAddress, "Your Ledgerly password reset code",
            EmailTemplates.Html("Reset your password", $"Your password reset code is {resetCode}.", null, null,
                "If this wasn't you, ignore this email. Your password won't change."),
            $"Your Ledgerly password reset code is {resetCode}.");
}

public static class EmailTemplates
{
    /// <summary>A simple, email-client-safe HTML message with an optional button. All text is HTML-encoded.</summary>
    public static string Html(string heading, string body, string? buttonText, string? buttonUrl, string footer)
    {
        Func<string, string> e = WebUtility.HtmlEncode;
        var button = buttonText is null || buttonUrl is null
            ? ""
            : $"""
               <p style="margin:24px 0">
                 <a href="{e(buttonUrl)}" style="background:#2e7d5b;color:#ffffff;text-decoration:none;padding:12px 20px;border-radius:4px;display:inline-block;font-weight:600">{e(buttonText)}</a>
               </p>
               <p style="color:#5d6b66;font-size:13px">Or paste this link into your browser:<br><a href="{e(buttonUrl)}" style="color:#2e7d5b;word-break:break-all">{e(buttonUrl)}</a></p>
               """;

        return $"""
                <!doctype html>
                <html><body style="margin:0;padding:24px;background:#f4f6f5;font-family:Segoe UI,Roboto,Arial,sans-serif;color:#1c2421">
                  <div style="max-width:520px;margin:0 auto;background:#ffffff;border:1px solid #d3dbd8;border-radius:8px;padding:28px">
                    <p style="margin:0 0 16px;font-size:18px;font-weight:600;color:#1f5a41">Ledgerly</p>
                    <h1 style="margin:0 0 12px;font-size:22px;font-weight:500">{e(heading)}</h1>
                    <p style="margin:0;line-height:1.5">{e(body)}</p>
                    {button}
                    <p style="margin:24px 0 0;color:#5d6b66;font-size:13px">{e(footer)}</p>
                  </div>
                </body></html>
                """;
    }

    public static string Text(string intro, string url) => $"{intro}\n\n{url}\n";
}
