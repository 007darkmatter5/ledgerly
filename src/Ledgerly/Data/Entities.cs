namespace Ledgerly.Data;

public enum AccountType
{
    Checking,
    Savings,
    CreditCard,
    Cash,
    Other
}

public enum Frequency
{
    Once,
    Weekly,
    EveryTwoWeeks,
    Monthly,
    EveryTwoMonths,
    Quarterly,
    SemiAnnually,
    Annually
}

/// <summary>What a payment on a loan does.</summary>
public enum LoanPaymentKind
{
    /// <summary>The regular monthly payment. Any amount over the bill's usual amount counts as extra principal.</summary>
    MonthlyPayment,

    /// <summary>A separate payment that goes entirely toward the principal balance.</summary>
    ExtraPrincipal
}

public enum LoanType
{
    Mortgage,
    Auto,
    Student,
    Personal,
    CreditLine,
    Other
}

/// <summary>
/// A self-contained set of accounts, bills, income and loans owned by one user.
/// Each user has their own ledger, plus an optional sample ledger that is kept separate from it.
/// </summary>
public class Ledger
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsSample { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>An app-wide setting managed by admins (not per user or ledger).</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Data that belongs to exactly one <see cref="Ledger"/>.</summary>
public interface ILedgerEntity
{
    int Id { get; set; }
    int LedgerId { get; set; }
}

/// <summary>A company or person that bills are paid to, or that lends money for a loan.</summary>
public class Payee : ILedgerEntity
{
    public Payee Copy() => (Payee)MemberwiseClone();

    public int Id { get; set; }
    public int LedgerId { get; set; }

    /// <summary>Unique within a ledger, ignoring case.</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional website, e.g. the sign-in or payment page. Always http(s); see <see cref="WebLinks"/>.</summary>
    public string? WebsiteUrl { get; set; }

    public string? Notes { get; set; }
}

/// <summary>A user-defined group for bills, like "Utilities" or "Housing".</summary>
public class Category : ILedgerEntity
{
    public Category Copy() => (Category)MemberwiseClone();

    public int Id { get; set; }
    public int LedgerId { get; set; }

    /// <summary>Unique within a ledger, ignoring case.</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional hex color, e.g. "#2E7D5B".</summary>
    public string? Color { get; set; }
}

/// <summary>A money account that bills are paid from and income is deposited to.</summary>
public class Account : ILedgerEntity
{
    public Account Copy() => (Account)MemberwiseClone();

    public int Id { get; set; }
    public int LedgerId { get; set; }
    public string Name { get; set; } = "";
    public AccountType Type { get; set; }

    /// <summary>
    /// Balance at the start of <see cref="BalanceAsOf"/>. Projections apply every bill and
    /// income dated on or after that day. For credit cards, enter the amount owed as a negative.
    /// </summary>
    public decimal Balance { get; set; }

    public DateOnly BalanceAsOf { get; set; }

    /// <summary>Projections flag the balance when it drops below this amount.</summary>
    public decimal? LowBalanceThreshold { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

/// <summary>A recurring (or one-time) bill.</summary>
public class Bill : ILedgerEntity
{
    public Bill Copy() => (Bill)MemberwiseClone();

    public int Id { get; set; }
    public int LedgerId { get; set; }
    public string Name { get; set; } = "";
    public int? PayeeId { get; set; }
    public Payee? Payee { get; set; }
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>The amount normally expected.</summary>
    public decimal ExpectedAmount { get; set; }

    /// <summary>Optional high estimate for variable bills, used by worst-case projections.</summary>
    public decimal? MaxAmount { get; set; }

    public Frequency Frequency { get; set; } = Frequency.Monthly;

    /// <summary>The first due date; later due dates are derived from it.</summary>
    public DateOnly StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    public int? PayFromAccountId { get; set; }
    public Account? PayFromAccount { get; set; }

    /// <summary>Optional loan this bill pays.</summary>
    public int? LoanId { get; set; }
    public Loan? Loan { get; set; }

    /// <summary>For loan bills: whether this is the monthly payment or an extra principal payment.</summary>
    public LoanPaymentKind LoanPaymentKind { get; set; }

    public bool AutoPay { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    public List<BillOccurrence> Occurrences { get; set; } = [];

    public bool IsVariable => MaxAmount is { } max && max != ExpectedAmount;
}

/// <summary>
/// What actually happened (or is known) for one scheduled due date of a bill:
/// an amount override, and whether/when it was paid.
/// </summary>
public class BillOccurrence
{
    public int Id { get; set; }
    public int BillId { get; set; }
    public Bill? Bill { get; set; }

    /// <summary>The scheduled due date this record belongs to.</summary>
    public DateOnly DueDate { get; set; }

    /// <summary>Known or actual amount; overrides the bill's expected amount.</summary>
    public decimal? Amount { get; set; }

    public DateOnly? PaidOn { get; set; }
    public string? Notes { get; set; }

    /// <summary>For loan bills: overrides the bill's payment kind for this one payment.</summary>
    public LoanPaymentKind? PaymentKind { get; set; }

    public bool IsPaid => PaidOn is not null;
}

/// <summary>Recurring money coming in, such as a paycheck.</summary>
public class Income : ILedgerEntity
{
    public Income Copy() => (Income)MemberwiseClone();

    public int Id { get; set; }
    public int LedgerId { get; set; }
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public Frequency Frequency { get; set; } = Frequency.EveryTwoWeeks;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public int DepositToAccountId { get; set; }
    public Account? DepositToAccount { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

/// <summary>An amortizing loan with monthly payments.</summary>
public class Loan : ILedgerEntity
{
    public Loan Copy() => (Loan)MemberwiseClone();

    public int Id { get; set; }
    public int LedgerId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>The lender, one of the ledger's payees (its website is the lender's site).</summary>
    public int? LenderId { get; set; }
    public Payee? Lender { get; set; }

    public LoanType Type { get; set; }

    public decimal OriginalPrincipal { get; set; }

    /// <summary>Annual interest rate as a percentage, e.g. 6.25.</summary>
    public decimal AnnualRatePercent { get; set; }

    public int TermMonths { get; set; }
    public DateOnly FirstPaymentDate { get; set; }

    /// <summary>Principal + interest payment if it differs from the calculated one.</summary>
    public decimal? PaymentOverride { get; set; }

    /// <summary>Extra principal paid every month.</summary>
    public decimal ExtraPrincipal { get; set; }

    /// <summary>Balance reported by the lender, if known. Otherwise the schedule estimates it.</summary>
    public decimal? CurrentBalance { get; set; }

    public DateOnly? BalanceAsOf { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}
