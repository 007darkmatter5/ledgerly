using Ledgerly.Data;

namespace Ledgerly.Finance;

/// <summary>A payment made on a loan through one of its bills.</summary>
/// <param name="Date">When it was paid.</param>
/// <param name="Amount">The whole amount paid.</param>
/// <param name="ExtraPrincipal">The part that goes straight to principal, beyond the regular monthly payment.</param>
/// <param name="IsAssumed">True for an autopay payment that wasn't marked paid, assumed made on its due date.</param>
public record LoanPayment(DateOnly Date, Bill Bill, DateOnly DueDate, decimal Amount, LoanPaymentKind Kind, decimal ExtraPrincipal, bool IsAssumed)
{
    public decimal MonthlyPortion => Amount - ExtraPrincipal;
}

/// <summary>
/// Works out a loan's payments from its bills. Monthly payments are assumed made on schedule, so only
/// extra principal changes the balance: a whole "extra principal" payment, or whatever a monthly payment
/// exceeds its bill's usual amount by (the usual amount may include escrow, so it isn't treated as extra).
/// </summary>
public static class LoanPayments
{
    public static LoanPaymentKind KindOf(Bill bill, BillOccurrence? occurrence) =>
        occurrence?.PaymentKind ?? bill.LoanPaymentKind;

    /// <summary>How much of a payment is extra principal.</summary>
    public static decimal ExtraPortion(Bill bill, LoanPaymentKind kind, decimal amount) =>
        kind == LoanPaymentKind.ExtraPrincipal ? amount : Math.Max(0m, amount - bill.ExpectedAmount);

    /// <summary>Payments on <paramref name="loan"/> made on or before <paramref name="today"/>, oldest first.</summary>
    public static List<LoanPayment> For(Loan loan, IEnumerable<Bill> bills, DateOnly today)
    {
        var payments = new List<LoanPayment>();
        foreach (var bill in bills.Where(b => b.LoanId == loan.Id))
        {
            foreach (var occurrence in bill.Occurrences.Where(o => o.PaidOn is { } paid && paid <= today))
            {
                var kind = KindOf(bill, occurrence);
                var amount = occurrence.Amount ?? bill.ExpectedAmount;
                payments.Add(new LoanPayment(occurrence.PaidOn!.Value, bill, occurrence.DueDate, amount, kind, ExtraPortion(bill, kind, amount), IsAssumed: false));
            }

            // Autopay payments happen whether or not they're marked paid.
            if (bill.AutoPay && bill.IsActive)
            {
                var recorded = bill.Occurrences.ToDictionary(o => o.DueDate);
                foreach (var due in Recurrence.Dates(bill.Frequency, bill.StartDate, bill.EndDate, bill.StartDate, today))
                {
                    recorded.TryGetValue(due, out var occurrence);
                    if (occurrence?.IsPaid == true)
                        continue;
                    var kind = KindOf(bill, occurrence);
                    var amount = occurrence?.Amount ?? bill.ExpectedAmount;
                    payments.Add(new LoanPayment(due, bill, due, amount, kind, ExtraPortion(bill, kind, amount), IsAssumed: true));
                }
            }
        }

        return payments.OrderBy(p => p.Date).ThenBy(p => p.Bill.Name).ToList();
    }

    /// <summary>The extra principal payments on <paramref name="loan"/>, for amortization.</summary>
    public static List<ExtraPayment> ExtrasFor(Loan loan, IEnumerable<Bill> bills, DateOnly today) =>
        For(loan, bills, today)
            .Where(p => p.ExtraPrincipal > 0)
            .Select(p => new ExtraPayment(p.Date, p.ExtraPrincipal))
            .ToList();
}
