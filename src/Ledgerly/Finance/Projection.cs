using Ledgerly.Data;

namespace Ledgerly.Finance;

public enum ProjectionMode
{
    /// <summary>Use each bill's expected amount.</summary>
    Expected,

    /// <summary>Use each variable bill's maximum amount.</summary>
    WorstCase
}

public enum ProjectionEntryKind
{
    Bill,
    Income
}

public record ProjectionEntry(
    DateOnly Date,
    ProjectionEntryKind Kind,
    string Description,
    int AccountId,
    string AccountName,
    decimal Amount,
    decimal RunningBalance,
    int? BillId = null,
    DateOnly? DueDate = null,
    bool IsPaid = false,
    bool IsEstimate = false);

public class ProjectionResult
{
    public required IReadOnlyList<Account> Accounts { get; init; }
    public required DateOnly Through { get; init; }
    public required IReadOnlyList<ProjectionEntry> Entries { get; init; }

    public decimal StartingBalance => Accounts.Sum(a => a.Balance);
    public decimal EndingBalance => Entries.Count == 0 ? StartingBalance : Entries[^1].RunningBalance;
    public decimal TotalOut => -Entries.Where(e => e.Kind == ProjectionEntryKind.Bill).Sum(e => e.Amount);
    public decimal TotalIn => Entries.Where(e => e.Kind == ProjectionEntryKind.Income).Sum(e => e.Amount);

    /// <summary>Sum of the accounts' thresholds, or null if none set one.</summary>
    public decimal? LowBalanceThreshold =>
        Accounts.Any(a => a.LowBalanceThreshold is not null) ? Accounts.Sum(a => a.LowBalanceThreshold ?? 0m) : null;

    public ProjectionEntry? LowestPoint => Entries.MinBy(e => e.RunningBalance);

    /// <summary>The entry that takes the balance negative. Null if it never does, or already started negative (e.g. a credit card).</summary>
    public ProjectionEntry? FirstBelowZero =>
        StartingBalance < 0 ? null : Entries.FirstOrDefault(e => e.RunningBalance < 0);

    /// <summary>The entry that takes the balance below the warning threshold, if it started at or above it.</summary>
    public ProjectionEntry? FirstBelowThreshold =>
        LowBalanceThreshold is { } t && StartingBalance >= t ? Entries.FirstOrDefault(e => e.RunningBalance < t) : null;

    public bool IsBelowThreshold(decimal balance) => LowBalanceThreshold is { } t && balance < t;
}

public static class Projection
{
    /// <summary>
    /// Projects the combined running balance of <paramref name="accounts"/> through <paramref name="through"/>.
    /// Each account starts from its balance as of its as-of date; every bill occurrence and income
    /// dated on or after that day is applied. Paid bills use the date they were paid.
    /// </summary>
    public static ProjectionResult Build(
        IReadOnlyCollection<Account> accounts,
        IEnumerable<Bill> bills,
        IEnumerable<Income> incomes,
        DateOnly through,
        ProjectionMode mode = ProjectionMode.Expected)
    {
        var byId = accounts.ToDictionary(a => a.Id);
        var items = new List<ProjectionEntry>();

        foreach (var bill in bills)
        {
            if (!bill.IsActive || bill.PayFromAccountId is not { } accountId || !byId.TryGetValue(accountId, out var account))
                continue;

            var recorded = bill.Occurrences.ToDictionary(o => o.DueDate);

            // Look back a bit so bills paid late (after the as-of date) for earlier due dates are counted.
            var lookback = account.BalanceAsOf.AddMonths(-3);
            foreach (var due in Recurrence.Dates(bill.Frequency, bill.StartDate, bill.EndDate, lookback, through))
            {
                recorded.TryGetValue(due, out var occurrence);
                var date = occurrence?.PaidOn ?? due;
                if (date < account.BalanceAsOf || date > through)
                    continue;

                var amount = occurrence?.Amount
                    ?? (mode == ProjectionMode.WorstCase ? bill.MaxAmount ?? bill.ExpectedAmount : bill.ExpectedAmount);
                var isEstimate = occurrence?.Amount is null && bill.IsVariable;

                items.Add(new ProjectionEntry(date, ProjectionEntryKind.Bill, bill.Name, account.Id, account.Name,
                    -amount, 0m, bill.Id, due, occurrence?.IsPaid ?? false, isEstimate));
            }
        }

        foreach (var income in incomes)
        {
            if (!income.IsActive || !byId.TryGetValue(income.DepositToAccountId, out var account))
                continue;

            foreach (var date in Recurrence.Dates(income.Frequency, income.StartDate, income.EndDate, account.BalanceAsOf, through))
            {
                items.Add(new ProjectionEntry(date, ProjectionEntryKind.Income, income.Name, account.Id, account.Name,
                    income.Amount, 0m));
            }
        }

        var entries = new List<ProjectionEntry>(items.Count);
        var running = accounts.Sum(a => a.Balance);

        // Within a day, apply income before bills so same-day paychecks cover same-day bills.
        foreach (var item in items.OrderBy(i => i.Date).ThenBy(i => i.Kind == ProjectionEntryKind.Bill).ThenBy(i => i.Description))
        {
            running += item.Amount;
            entries.Add(item with { RunningBalance = running });
        }

        return new ProjectionResult { Accounts = accounts.ToList(), Through = through, Entries = entries };
    }
}
