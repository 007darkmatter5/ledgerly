using Ledgerly.Data;
using Ledgerly.Finance;

namespace Ledgerly.Components.Shared;

/// <summary>User-facing text for enums and values.</summary>
public static class Display
{
    public static string Of(Frequency frequency) => Recurrence.Describe(frequency);

    public static string Of(AccountType type) => type == AccountType.CreditCard ? "Credit card" : type.ToString();

    public static string Of(LoanType type) => type == LoanType.CreditLine ? "Line of credit" : type.ToString();

    public static string Date(DateOnly date) => date.ToString("MMM d, yyyy");

    public static string ShortDate(DateOnly date) => date.ToString("ddd, MMM d");

    public static string Months(int months) => (months / 12, months % 12) switch
    {
        (0, var m) => $"{m} mo",
        (var y, 0) => $"{y} yr",
        (var y, var m) => $"{y} yr {m} mo"
    };

    /// <summary>"Today", "in 3 days", "5 days ago".</summary>
    public static string Relative(DateOnly date, DateOnly today) => (date.DayNumber - today.DayNumber) switch
    {
        0 => "Today",
        1 => "Tomorrow",
        -1 => "Yesterday",
        > 0 and var d => $"in {d} days",
        var d => $"{-d} days ago"
    };

    public static string Icon(AccountType type) => type switch
    {
        AccountType.Checking => MudBlazor.Icons.Material.Filled.AccountBalance,
        AccountType.Savings => MudBlazor.Icons.Material.Filled.Savings,
        AccountType.CreditCard => MudBlazor.Icons.Material.Filled.CreditCard,
        AccountType.Cash => MudBlazor.Icons.Material.Filled.Wallet,
        _ => MudBlazor.Icons.Material.Filled.AccountBalanceWallet
    };
}
