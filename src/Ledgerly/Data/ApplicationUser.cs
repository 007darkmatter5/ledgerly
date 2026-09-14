using Microsoft.AspNetCore.Identity;

namespace Ledgerly.Data;

public class ApplicationUser : IdentityUser
{
    /// <summary>The ledger the user is currently viewing (their own, or the sample one).</summary>
    public int? ActiveLedgerId { get; set; }
}
