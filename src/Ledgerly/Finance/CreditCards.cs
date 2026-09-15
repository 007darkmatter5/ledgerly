using Ledgerly.Data;

namespace Ledgerly.Finance;

public enum CardEntryKind
{
    /// <summary>A bill charged to the card.</summary>
    Charge,

    /// <summary>The card's typical monthly spending that isn't a bill, added when a statement closes.</summary>
    Spending,

    /// <summary>Interest added when a statement closes after the previous one wasn't paid in full.</summary>
    Interest,

    /// <summary>A bill that pays the card.</summary>
    Payment
}

/// <summary>One change to a card's balance. <paramref name="Amount"/> is the change in the amount owed (payments are negative).</summary>
public record CardEntry(DateOnly Date, CardEntryKind Kind, string Description, decimal Amount, decimal Owed, Bill? Bill = null, DateOnly? DueDate = null, bool IsEstimate = false);

/// <summary>A projected statement: what's owed when it closes, and the minimum payment on it.</summary>
public record CardStatement(DateOnly ClosingDate, decimal Charges, decimal Interest, decimal Balance, decimal MinimumPayment);

public record CardPayoff(int Months, decimal TotalInterest, DateOnly? PayoffDate, bool NeverPaysOff);

public class CardSimulation
{
    public required Account Card { get; init; }
    public required decimal StartingOwed { get; init; }
    public required IReadOnlyList<CardEntry> Entries { get; init; }
    public required IReadOnlyList<CardStatement> Statements { get; init; }
    public required IReadOnlyDictionary<(Bill Bill, DateOnly DueDate), decimal> Payments { get; init; }

    /// <summary>The projected amount owed at the end of <paramref name="date"/>.</summary>
    public decimal OwedOn(DateOnly date) => Entries.LastOrDefault(e => e.Date <= date)?.Owed ?? StartingOwed;

    public decimal? PaymentFor(Bill bill, DateOnly dueDate) => Payments.TryGetValue((bill, dueDate), out var amount) ? amount : null;
}

/// <summary>
/// Credit cards as revolving balances: bills charged to a card and its typical spending build up a statement,
/// and bills that pay the card cover the statement in full, the minimum, or a fixed amount. Interest is added
/// at a statement's close when the previous statement wasn't paid in full (an estimate of how issuers charge it).
/// </summary>
public static class CreditCards
{
    public const decimal DefaultMinimumPercent = 1m;
    public const decimal DefaultMinimumFloor = 25m;

    /// <summary>What's owed on the card (its balance is stored as a negative number).</summary>
    public static decimal Owed(Account card) => -card.Balance;

    public static decimal? Utilization(Account card, decimal owed) =>
        card.CreditLimit is > 0 and var limit ? Math.Max(0m, owed) / limit : null;

    /// <summary>The larger of a percentage of the balance plus interest, and the floor, but never more than the balance.</summary>
    public static decimal MinimumPayment(Account card, decimal owed, decimal interest)
    {
        if (owed <= 0)
            return 0m;
        var percent = card.MinimumPaymentPercent ?? DefaultMinimumPercent;
        var floor = card.MinimumPaymentFloor ?? DefaultMinimumFloor;
        return Math.Min(owed, Math.Round(Math.Max(floor, owed * percent / 100m + interest), 2));
    }

    /// <summary>
    /// The statement closing day: the card's setting, or else 25 days before the first payment's due date
    /// (a typical grace period), or else the 1st.
    /// </summary>
    public static int StatementDay(Account card, IEnumerable<Bill> bills) =>
        card.StatementDay
        ?? bills.Where(b => b.CardAccountId == card.Id).OrderBy(b => b.StartDate).Select(b => (int?)b.StartDate.AddDays(-25).Day).FirstOrDefault()
        ?? 1;

    /// <summary>Statement closing dates after <paramref name="from"/> through <paramref name="to"/>.</summary>
    public static IEnumerable<DateOnly> ClosingDates(int statementDay, DateOnly from, DateOnly to)
    {
        for (var month = new DateOnly(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
        {
            var date = month.AddDays(Math.Min(statementDay, DateTime.DaysInMonth(month.Year, month.Month)) - 1);
            if (date > from && date <= to)
                yield return date;
        }
    }

    /// <summary>
    /// Plays a card forward from its balance date through <paramref name="through"/>: charges, typical spending,
    /// statements with interest, and payments worked out by each paying bill's rule (unless an amount was recorded).
    /// </summary>
    public static CardSimulation Simulate(Account card, IEnumerable<Bill> bills, DateOnly through, ProjectionMode mode = ProjectionMode.Expected)
    {
        var billList = bills as IReadOnlyCollection<Bill> ?? bills.ToList();
        var asOf = card.BalanceAsOf;
        var events = new List<(DateOnly Date, int Order, Action Apply)>();

        var owed = Owed(card);
        var entries = new List<CardEntry>();
        var statements = new List<CardStatement>();
        var payments = new Dictionary<(Bill, DateOnly), decimal>();
        CardStatement? lastStatement = null;
        var paidSinceClose = 0m;
        var chargesSinceClose = 0m;

        void Add(DateOnly date, CardEntryKind kind, string description, decimal amount, Bill? bill = null, DateOnly? due = null, bool estimate = false)
        {
            owed += amount;
            entries.Add(new CardEntry(date, kind, description, amount, owed, bill, due, estimate));
        }

        // Due dates are looked up from a little before the balance date, so late payments made after it still count.
        IEnumerable<(Bill Bill, DateOnly Due, DateOnly Date, BillOccurrence? Occurrence)> Occurrences(Func<Bill, bool> which) =>
            billList.Where(b => b.IsActive && which(b))
                .SelectMany(b => Recurrence.Dates(b.Frequency, b.StartDate, b.EndDate, asOf.AddMonths(-3), through)
                    .Select(due => (b, due, b.Occurrences.FirstOrDefault(o => o.DueDate == due))))
                .Select(x => (x.b, x.due, x.Item3?.PaidOn ?? x.due, x.Item3))
                .Where(x => x.Item3 >= asOf && x.Item3 <= through);

        foreach (var (bill, due, date, occurrence) in Occurrences(b => b.PayFromAccountId == card.Id && b.CardAccountId != card.Id))
        {
            var amount = occurrence?.Amount ?? (mode == ProjectionMode.WorstCase ? bill.MaxAmount ?? bill.ExpectedAmount : bill.ExpectedAmount);
            var estimate = occurrence?.Amount is null && bill.IsVariable;
            events.Add((date, 0, () =>
            {
                chargesSinceClose += amount;
                Add(date, CardEntryKind.Charge, bill.Name, amount, bill, due, estimate);
            }));
        }

        foreach (var (bill, due, date, occurrence) in Occurrences(b => b.CardAccountId == card.Id))
        {
            events.Add((date, 1, () =>
            {
                var amount = occurrence?.Amount ?? PaymentAmount(bill);
                payments[(bill, due)] = amount;
                paidSinceClose += amount;
                if (amount != 0)
                    Add(date, CardEntryKind.Payment, bill.Name, -amount, bill, due, occurrence?.Amount is null);
            }));
        }

        var statementDay = StatementDay(card, billList);
        var paysInFull = billList.Any(b => b.IsActive && b.CardAccountId == card.Id && b.CardPaymentRule == CardPaymentRule.StatementBalance);
        foreach (var close in ClosingDates(statementDay, asOf.AddDays(-1), through))
        {
            if (card.MonthlySpending is > 0 and var spending)
            {
                // The first cycle only adds spending for the part of it after the balance date.
                var previousClose = ClosingDates(statementDay, close.AddMonths(-1).AddDays(-3), close.AddDays(-1)).LastOrDefault(close.AddMonths(-1));
                var cycleDays = close.DayNumber - previousClose.DayNumber;
                var remainingDays = close.DayNumber - Math.Max(previousClose.DayNumber, asOf.DayNumber);
                var amount = Math.Round(spending * remainingDays / Math.Max(1, cycleDays), 2);
                events.Add((close, 2, () =>
                {
                    chargesSinceClose += amount;
                    Add(close, CardEntryKind.Spending, "Everyday spending (estimate)", amount, estimate: true);
                }));
            }

            events.Add((close, 3, () =>
            {
                // Interest applies when the last statement wasn't paid off before this one closed. Before the first
                // projected statement there's nothing to go on, so assume a card paid in full isn't carrying a balance.
                var carrying = lastStatement is null
                    ? !paysInFull && owed > 0
                    : lastStatement.Balance > 0.005m && paidSinceClose < lastStatement.Balance - 0.005m;
                var interest = carrying && card.AprPercent is > 0 && owed > 0 ? Math.Round(owed * card.AprPercent.Value / 1200m, 2) : 0m;
                if (interest > 0)
                    Add(close, CardEntryKind.Interest, "Interest (estimate)", interest, estimate: true);

                lastStatement = new CardStatement(close, chargesSinceClose, interest, Math.Max(0m, owed), MinimumPayment(card, owed, interest));
                statements.Add(lastStatement);
                paidSinceClose = 0m;
                chargesSinceClose = 0m;
            }));
        }

        foreach (var (_, _, apply) in events.OrderBy(e => e.Date).ThenBy(e => e.Order))
            apply();

        return new CardSimulation { Card = card, StartingOwed = Owed(card), Entries = entries, Statements = statements, Payments = payments };

        decimal PaymentAmount(Bill bill)
        {
            // Before the first projected statement, go by the latest real statement if it was entered, else today's balance.
            var billed = lastStatement?.Balance ?? card.StatementBalance ?? Owed(card);
            var amount = bill.CardPaymentRule switch
            {
                CardPaymentRule.StatementBalance => billed - paidSinceClose,
                CardPaymentRule.Minimum => (lastStatement?.MinimumPayment ?? MinimumPayment(card, billed, 0m)) - paidSinceClose,
                _ => bill.ExpectedAmount
            };
            return Math.Round(Math.Clamp(amount, 0m, Math.Max(0m, owed)), 2);
        }
    }

    /// <summary>Estimated amounts for every bill that pays a card, stored on the bills for schedules and dashboards.</summary>
    public static void EstimatePayments(IReadOnlyCollection<Bill> bills, DateOnly through)
    {
        foreach (var group in bills.Where(b => b.CardAccount is not null).GroupBy(b => b.CardAccountId))
        {
            var simulation = Simulate(group.First().CardAccount!, bills, through);
            foreach (var bill in group)
            {
                bill.PaymentEstimates = simulation.Payments
                    .Where(p => ReferenceEquals(p.Key.Bill, bill))
                    .ToDictionary(p => p.Key.DueDate, p => p.Value);
            }
        }
    }

    /// <summary>
    /// How long paying off today's balance takes if nothing more is charged, paying by <paramref name="rule"/> each month.
    /// </summary>
    public static CardPayoff PayoffIfNoNewCharges(Account card, CardPaymentRule rule, decimal fixedAmount, DateOnly start)
    {
        var owed = Owed(card);
        if (owed <= 0)
            return new CardPayoff(0, 0m, start, false);
        if (rule == CardPaymentRule.StatementBalance)
            return new CardPayoff(1, 0m, start.AddMonths(1), false);

        var monthlyRate = (card.AprPercent ?? 0m) / 1200m;
        var totalInterest = 0m;
        for (var month = 1; month <= 600; month++)
        {
            var interest = Math.Round(owed * monthlyRate, 2);
            var payment = rule == CardPaymentRule.Minimum ? MinimumPayment(card, owed, interest) : Math.Min(fixedAmount, owed + interest);
            if (payment <= interest && payment < owed + interest)
                return new CardPayoff(0, 0m, null, true);

            totalInterest += interest;
            owed = owed + interest - payment;
            if (owed <= 0.005m)
                return new CardPayoff(month, totalInterest, start.AddMonths(month), false);
        }
        return new CardPayoff(0, 0m, null, true);
    }
}
