using Microsoft.AspNetCore.Identity;

namespace Ledgerly.Data;

public class ApplicationUser : IdentityUser
{
    /// <summary>The ledger the user is currently viewing (their own, or the sample one).</summary>
    public int? ActiveLedgerId { get; set; }

    /// <summary>Optional name shown to people this user shares a ledger with (otherwise their email).</summary>
    public string? DisplayName { get; set; }

    /// <summary>How other users see this person: their display name, else their email.</summary>
    public static string NameOf(string? displayName, string? email) =>
        string.IsNullOrWhiteSpace(displayName) ? email ?? "Someone" : displayName;
}
