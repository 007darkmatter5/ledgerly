using Ledgerly.Data;

namespace Ledgerly.Finance;

/// <summary>How far the coverage view looks ahead. The end date is inclusive.</summary>
public enum CoverageHorizon
{
    /// <summary>Through the next payday. Has no end date when payday is missing or ambiguous.</summary>
    NextPayday,

    /// <summary>Today through today plus 30 days, the same window as the dashboard.</summary>
    Next30Days,

    /// <summary>Today through the last calendar day of today's month.</summary>
    EndOfMonth
}

/// <summary>Whether recurring income determines a single next payday.</summary>
public enum PaydayClarity
{
    /// <summary>Every active recurring income next falls on one date.</summary>
    Resolved,

    /// <summary>Nothing recurring is scheduled on or after today. A one-time deposit is not a payday.</summary>
    Missing,

    /// <summary>Active recurring incomes next fall on different dates. No date is chosen.</summary>
    Ambiguous
}

public enum CoverageAnswer
{
    /// <summary>Every cash account stays at or above zero, and every bill in the window has an account that can fund it.</summary>
    Covered,

    /// <summary>A cash account is already below zero, or a scheduled item takes it below zero.</summary>
    Shortfall,

    /// <summary>Saying yes would skip a missing payday, an unassigned bill, or an account that isn't usable today.</summary>
    Unknown
}

/// <summary>Whether a timeline row is money already recorded or still an assumption.</summary>
public enum CoverageItemState
{
    /// <summary>A transaction dated on or before today.</summary>
    Logged,

    /// <summary>A bill or transfer marked done, on or before today.</summary>
    Recorded,

    /// <summary>Still on the schedule. Not money already in hand.</summary>
    Scheduled,

    /// <summary>Autopay, or a transfer the bank sends on its own. Counted without being marked.</summary>
    Assumed,

    /// <summary>A variable bill or a card payment whose amount isn't recorded yet.</summary>
    Estimated,

    /// <summary>Income. Always scheduled — there is no separate "received" record.</summary>
    ScheduledIncome
}

public enum CoverageGapKind
{
    /// <summary>No pay-from account, so no balance can fund it.</summary>
    BillWithoutAccount,

    /// <summary>The pay-from account isn't in the accounts that were loaded (inactive, or not visible).</summary>
    BillAccountUnavailable,

    /// <summary>The pay-from balance is dated after today, so it isn't money available now.</summary>
    BalanceDatedAfterToday
}

/// <summary>One active recurring income and the next date on or after today.</summary>
public sealed record PaydayCandidate(int IncomeId, int LedgerId, string Name, DateOnly Date, decimal Amount, int DepositAccountId);

public sealed record PaydayResolution(PaydayClarity Clarity, DateOnly? Date, IReadOnlyList<PaydayCandidate> Candidates)
{
    /// <summary>
    /// The next payday is the next occurrence on or after <paramref name="today"/> of active recurring income
    /// (not one-time). It is resolved only when every such income shares that date. Differing dates stay
    /// ambiguous — the earlier date is not used.
    /// </summary>
    public static PaydayResolution Resolve(IEnumerable<Income> incomes, DateOnly today)
    {
        var candidates = new List<PaydayCandidate>();
        foreach (var income in incomes)
        {
            if (!income.IsActive || income.Frequency == Frequency.Once)
                continue;
            var next = Recurrence.Next(income.Frequency, income.StartDate, income.EndDate, today);
            if (next is not { } date)
                continue;
            candidates.Add(new PaydayCandidate(income.Id, income.LedgerId, income.Name, date, income.Amount, income.DepositToAccountId));
        }

        candidates.Sort((a, b) =>
        {
            var byDate = a.Date.CompareTo(b.Date);
            return byDate != 0 ? byDate : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        if (candidates.Count == 0)
            return new PaydayResolution(PaydayClarity.Missing, null, candidates);
        var payday = candidates[0].Date;
        if (candidates.All(c => c.Date == payday))
            return new PaydayResolution(PaydayClarity.Resolved, payday, candidates);
        return new PaydayResolution(PaydayClarity.Ambiguous, null, candidates);
    }
}

public static class CoverageWindows
{
    /// <summary>
    /// Inclusive last day, or null when <see cref="CoverageHorizon.NextPayday"/> has no single payday.
    /// <paramref name="today"/> is the app's local calendar date (the same value as <c>LedgerService.Today</c>).
    /// The window is not shifted into UTC.
    /// </summary>
    public static DateOnly? Through(CoverageHorizon horizon, DateOnly today, PaydayResolution payday) => horizon switch
    {
        CoverageHorizon.Next30Days => today.AddDays(30),
        CoverageHorizon.EndOfMonth => new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)),
        CoverageHorizon.NextPayday => payday.Clarity == PaydayClarity.Resolved ? payday.Date : null,
        _ => null
    };
}

/// <summary>A bill in the window that no visible, current account can fund.</summary>
public sealed record CoverageGap(
    CoverageGapKind Kind,
    int BillId,
    int LedgerId,
    string BillName,
    DateOnly DueDate,
    decimal Amount,
    string Href);

/// <summary>One projected row for a single cash account. The balance is that account's, not a combined total.</summary>
public sealed record CoverageEvent
{
    public required DateOnly Date { get; init; }
    public required int AccountId { get; init; }
    public required int LedgerId { get; init; }
    public required string AccountName { get; init; }
    public required string Title { get; init; }
    public required decimal Amount { get; init; }
    public required decimal BalanceAfter { get; init; }
    public required CoverageItemState State { get; init; }
    public required bool TriggersShortfall { get; init; }
    public required string Href { get; init; }
    public required ProjectionEntryKind Kind { get; init; }
}

/// <summary>A charge on a credit card. It is not cash leaving a bank account.</summary>
public sealed record CardCharge(int BillId, int LedgerId, int CardAccountId, string CardName, string Name, DateOnly DueDate, decimal Amount, bool Estimate, string Href);

/// <summary>Recorded and projected position of one account. Credit cards are listed for context and are not cash.</summary>
public sealed record AccountSnapshot
{
    public required int AccountId { get; init; }
    public required int LedgerId { get; init; }
    public required string Name { get; init; }
    public required AccountType Type { get; init; }
    public required DateOnly BalanceAsOf { get; init; }
    public required decimal RecordedBalance { get; init; }

    /// <summary>Recorded balance plus logged activity on or before today. Scheduled income and unpaid bills are not included.</summary>
    public required decimal PostedBalance { get; init; }

    /// <summary>Balance at the horizon from <see cref="Projection.Build"/>. Null when the horizon has no end date.</summary>
    public decimal? ProjectedEnding { get; init; }

    public bool AlreadyShort { get; init; }
    public DateOnly? ShortfallOn { get; init; }
    public string? ShortfallItem { get; init; }
    public decimal? BalanceAtShortfall { get; init; }
    public DateOnly? RecoversOn { get; init; }
    public DateOnly? BelowWarningOn { get; init; }
    public decimal? WarningAmount { get; init; }
    public bool BalanceDatedAfterToday { get; init; }
    public string ProjectionHref => $"projection?accounts={AccountId}";

    public bool IsShort => AlreadyShort || ShortfallOn is not null;
}

public sealed record CoverageReport
{
    public required DateOnly Today { get; init; }
    public required CoverageHorizon Horizon { get; init; }
    public required DateOnly? Through { get; init; }
    public required PaydayResolution Payday { get; init; }
    public required CoverageAnswer Answer { get; init; }
    public required IReadOnlyList<AccountSnapshot> CashAccounts { get; init; }
    public required IReadOnlyList<AccountSnapshot> Cards { get; init; }

    /// <summary>Sum of recorded cash balances dated today or earlier. Not one spending pool.</summary>
    public required decimal RecordedCash { get; init; }

    /// <summary>Sum of <see cref="AccountSnapshot.PostedBalance"/> for those same accounts.</summary>
    public required decimal PostedCash { get; init; }

    /// <summary>Scheduled income from today through the horizon, once per deposit. Zero when the horizon isn't known.</summary>
    public required decimal ScheduledIncome { get; init; }

    /// <summary>Cash bills from today through the horizon. Transfers and card charges are not included. Zero when the horizon isn't known.</summary>
    public required decimal ScheduledBills { get; init; }

    public required IReadOnlyList<CoverageEvent> Events { get; init; }
    public required IReadOnlyList<CoverageGap> Gaps { get; init; }
    public required IReadOnlyList<CardCharge> CardCharges { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public bool HasHorizon => Through is not null;

    public AccountSnapshot? EarliestShortfall =>
        CashAccounts.Where(a => a.IsShort).OrderBy(a => a.ShortfallOn ?? a.BalanceAsOf).ThenBy(a => a.Name).FirstOrDefault();

    /// <summary>Rules the view is applying. Shown so projected numbers aren't read as recorded ones.</summary>
    public static IReadOnlyList<string> Assumptions { get; } =
    [
        "Recorded balances are what you entered, as of each account's as-of date. Adding them up does not make one pool of spending money.",
        "A bill is only covered by the account that pays it. Money elsewhere counts only when a transfer into that account is scheduled.",
        "Income, unpaid bills, and future transactions are scheduled. They are not treated as money already recorded.",
        "Logged transactions on or before today, and bills or transfers marked done by today, are recorded activity on top of the as-of balance.",
        "Same-day income is applied before bills, matching the running balance.",
        "Autopay bills and automatic transfers count on their date even when they aren't marked.",
        "Amounts are the expected ones. A variable bill's higher \"up to\" amount is on the running balance page.",
        "Credit card balances are debt, not cash. A charge on a card is not a cash bill; the cash side is the payment from a bank account.",
        "Dates use Ledgerly's local calendar day — the same \"today\" as the rest of the app — and are not shifted into UTC."
    ];
}

/// <summary>
/// Answers "can I cover my upcoming bills?" from the same projection the running balance uses,
/// one cash account at a time so an unrelated balance cannot fund a bill.
/// </summary>
public static class Coverage
{
    public static CoverageReport Build(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Bill> bills,
        IReadOnlyList<Income> incomes,
        IReadOnlyList<Transfer> transfers,
        IReadOnlyList<Transaction> transactions,
        DateOnly today,
        CoverageHorizon horizon,
        IReadOnlyList<Bill>? cardPaymentsFromOthers = null)
    {
        var payday = PaydayResolution.Resolve(incomes, today);
        var through = CoverageWindows.Through(horizon, today, payday);
        var accountsById = accounts.ToDictionary(a => a.Id);
        var cash = accounts.Where(a => !a.IsCreditCard).ToList();
        var cards = accounts.Where(a => a.IsCreditCard).ToList();

        var projectedBills = new List<Bill>();
        var seenBills = new HashSet<int>();
        foreach (var bill in bills.Concat(cardPaymentsFromOthers ?? []))
        {
            if (bill.Id != 0 && !seenBills.Add(bill.Id))
                continue;
            projectedBills.Add(bill);
        }

        var billsById = projectedBills.Where(b => b.Id != 0).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        var transfersById = transfers.Where(t => t.Id != 0).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());

        // No payday date means "through today" for the recorded position only. It is not a forecast.
        var projectTo = through ?? today;
        var events = new List<CoverageEvent>();
        var cashSnapshots = new List<AccountSnapshot>();

        foreach (var account in cash)
        {
            var projection = Projection.Build([account], projectedBills, incomes, transfers, transactions, projectTo);
            var trigger = account.Balance < 0 ? null : projection.FirstBelowZero;
            var posted = account.Balance + projection.Entries.Where(e => IsPosted(e, today)).Sum(e => e.Amount);
            var alreadyShort = account.Balance < 0 && account.BalanceAsOf <= today;
            var shortfall = alreadyShort ? null : trigger;
            DateOnly? recovers = null;
            var afterShort = alreadyShort;
            foreach (var entry in projection.Entries)
            {
                if (afterShort && entry.RunningBalance >= 0 && recovers is null && !ReferenceEquals(entry, shortfall))
                    recovers = entry.Date;
                if (ReferenceEquals(entry, shortfall))
                    afterShort = true;
            }

            cashSnapshots.Add(new AccountSnapshot
            {
                AccountId = account.Id,
                LedgerId = account.LedgerId,
                Name = account.Name,
                Type = account.Type,
                BalanceAsOf = account.BalanceAsOf,
                RecordedBalance = account.Balance,
                PostedBalance = posted,
                ProjectedEnding = through is null ? null : projection.EndingBalance,
                AlreadyShort = alreadyShort,
                ShortfallOn = alreadyShort ? account.BalanceAsOf : shortfall?.Date,
                ShortfallItem = shortfall?.Description,
                BalanceAtShortfall = alreadyShort ? account.Balance : shortfall?.RunningBalance,
                RecoversOn = recovers,
                BelowWarningOn = projection.FirstBelowThreshold?.Date,
                WarningAmount = account.LowBalanceThreshold,
                BalanceDatedAfterToday = account.BalanceAsOf > today
            });

            foreach (var entry in projection.Entries)
            {
                billsById.TryGetValue(entry.BillId ?? 0, out var bill);
                transfersById.TryGetValue(entry.TransferId ?? 0, out var transfer);
                events.Add(new CoverageEvent
                {
                    Date = entry.Date,
                    AccountId = account.Id,
                    LedgerId = account.LedgerId,
                    AccountName = account.Name,
                    Title = entry.Description,
                    Amount = entry.Amount,
                    BalanceAfter = entry.RunningBalance,
                    State = StateOf(entry, bill, transfer, today),
                    TriggersShortfall = ReferenceEquals(entry, shortfall),
                    Href = HrefFor(entry, bill),
                    Kind = entry.Kind
                });
            }
        }

        var cardSnapshots = cards.Select(account => new AccountSnapshot
        {
            AccountId = account.Id,
            LedgerId = account.LedgerId,
            Name = account.Name,
            Type = account.Type,
            BalanceAsOf = account.BalanceAsOf,
            RecordedBalance = account.Balance,
            PostedBalance = account.Balance,
            BalanceDatedAfterToday = account.BalanceAsOf > today,
            WarningAmount = account.LowBalanceThreshold
        }).ToList();

        var gaps = new List<CoverageGap>();
        var cardCharges = new List<CardCharge>();
        if (through is { } end)
        {
            foreach (var due in BillSchedule.Between(bills, today.AddDays(-90), end))
            {
                if (due.IsPaid)
                    continue;
                var inWindow = due.DueDate >= today && due.DueDate <= end;
                var overdue = due.DueDate < today && due.IsOverdue(today);
                if (!inWindow && !overdue)
                    continue;

                if (due.Bill.PayFromAccountId is not int accountId)
                {
                    if (inWindow || overdue)
                        gaps.Add(Gap(CoverageGapKind.BillWithoutAccount, due));
                    continue;
                }

                if (!accountsById.TryGetValue(accountId, out var payFrom))
                {
                    gaps.Add(Gap(CoverageGapKind.BillAccountUnavailable, due));
                    continue;
                }

                if (payFrom.IsCreditCard)
                {
                    if (inWindow)
                        cardCharges.Add(new CardCharge(due.Bill.Id, due.Bill.LedgerId, payFrom.Id, payFrom.Name, due.Bill.Name,
                            due.DueDate, due.Amount, due.IsEstimate,
                            due.Bill.LoanId is not null ? HrefForBill(due.Bill) : $"cards/{payFrom.Id}"));
                    continue;
                }

                if (payFrom.BalanceAsOf > today)
                    gaps.Add(Gap(CoverageGapKind.BalanceDatedAfterToday, due));
            }
        }

        events.Sort((a, b) =>
        {
            var byDate = a.Date.CompareTo(b.Date);
            if (byDate != 0)
                return byDate;
            var inflow = (a.Amount < 0).CompareTo(b.Amount < 0);
            if (inflow != 0)
                return inflow;
            var byAccount = string.Compare(a.AccountName, b.AccountName, StringComparison.Ordinal);
            return byAccount != 0 ? byAccount : string.Compare(a.Title, b.Title, StringComparison.Ordinal);
        });

        var currentCash = cashSnapshots.Where(a => !a.BalanceDatedAfterToday).ToList();
        var scheduledIncome = through is null ? 0m : events.Where(e => e.Kind == ProjectionEntryKind.Income && e.Date >= today).Sum(e => e.Amount);
        var scheduledBills = through is null ? 0m : events.Where(e => e.Kind == ProjectionEntryKind.Bill && e.Date >= today).Sum(e => -e.Amount);

        var notes = NotesFor(today, payday, events);
        if (cashSnapshots.Any(a => a.BalanceDatedAfterToday))
            notes.Add("An account balance is dated after today, so it isn't money available now and can't be used to say bills are covered.");

        var anyAccount = cashSnapshots.Count > 0 || cardSnapshots.Count > 0;
        var futureBalance = cashSnapshots.Any(a => a.BalanceDatedAfterToday);
        var answer = cashSnapshots.Any(a => a.IsShort) ? CoverageAnswer.Shortfall
            : !anyAccount || through is null || gaps.Count > 0 || futureBalance ? CoverageAnswer.Unknown
            : CoverageAnswer.Covered;

        return new CoverageReport
        {
            Today = today,
            Horizon = horizon,
            Through = through,
            Payday = payday,
            Answer = answer,
            CashAccounts = cashSnapshots,
            Cards = cardSnapshots,
            RecordedCash = currentCash.Sum(a => a.RecordedBalance),
            PostedCash = currentCash.Sum(a => a.PostedBalance),
            ScheduledIncome = scheduledIncome,
            ScheduledBills = scheduledBills,
            Events = events,
            Gaps = gaps,
            CardCharges = cardCharges,
            Notes = notes
        };
    }

    /// <summary>Logged or marked-done activity on or before today. Income is never posted.</summary>
    public static bool IsPosted(ProjectionEntry entry, DateOnly today) =>
        entry.Date <= today && entry.Kind switch
        {
            ProjectionEntryKind.Spent or ProjectionEntryKind.Received => true,
            ProjectionEntryKind.Bill or ProjectionEntryKind.CardPayment
                or ProjectionEntryKind.TransferIn or ProjectionEntryKind.TransferOut => entry.IsPaid,
            _ => false
        };

    private static CoverageItemState StateOf(ProjectionEntry entry, Bill? bill, Transfer? transfer, DateOnly today)
    {
        if (entry.Kind is ProjectionEntryKind.Spent or ProjectionEntryKind.Received)
            return entry.Date <= today ? CoverageItemState.Logged : CoverageItemState.Scheduled;
        if (entry.Kind == ProjectionEntryKind.Income)
            return CoverageItemState.ScheduledIncome;
        if (entry.IsEstimate && !entry.IsPaid)
            return CoverageItemState.Estimated;
        if (entry.IsPaid)
            return CoverageItemState.Recorded;
        if (bill?.AutoPay == true || transfer?.IsAutomatic == true)
            return CoverageItemState.Assumed;
        return CoverageItemState.Scheduled;
    }

    private static List<string> NotesFor(DateOnly today, PaydayResolution payday, List<CoverageEvent> events)
    {
        var notes = new List<string>();
        switch (payday.Clarity)
        {
            case PaydayClarity.Resolved:
                var names = string.Join(", ", payday.Candidates.Select(c => c.Name));
                notes.Add($"Next payday is {payday.Date:MMM d, yyyy} ({names}). That day is included, and the deposit is counted before bills due the same day.");
                break;
            case PaydayClarity.Missing:
                notes.Add("No recurring income is scheduled on or after today, so \"until next payday\" has no end date. A one-time deposit is not treated as payday.");
                break;
            default:
                notes.Add("Recurring income next arrives on more than one date, so no payday was chosen. This view will not call the bills covered for that horizon.");
                break;
        }

        if (events.Any(e => e.Kind == ProjectionEntryKind.Income && e.Date < today))
            notes.Add("A scheduled deposit before today is in the projection because the balance is as of an earlier day. It is not recorded income. If you already included it in the balance, move that account's as-of date forward.");
        return notes;
    }

    private static CoverageGap Gap(CoverageGapKind kind, BillDue due) =>
        new(kind, due.Bill.Id, due.Bill.LedgerId, due.Bill.Name, due.DueDate, due.Amount, HrefForBill(due.Bill));

    private static string HrefFor(ProjectionEntry entry, Bill? bill)
    {
        if (entry.Transaction is not null)
            return "transactions";
        if (bill is not null)
            return HrefForBill(bill);
        if (entry.Kind == ProjectionEntryKind.Income)
            return "income";
        if (entry.TransferId is not null)
            return "transfers";
        return $"projection?accounts={entry.AccountId}";
    }

    private static string HrefForBill(Bill bill)
    {
        if (bill.LoanId is int loanId)
            return $"loans/{loanId}";
        if (bill.CardAccountId is int cardId)
            return $"cards/{cardId}";
        if (bill.PayFromAccount?.IsCreditCard == true && bill.PayFromAccountId is int fromCard)
            return $"cards/{fromCard}";
        return "bills";
    }
}
