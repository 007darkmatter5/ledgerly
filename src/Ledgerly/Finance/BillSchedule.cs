using Ledgerly.Data;

namespace Ledgerly.Finance;

/// <summary>One scheduled due date of a bill, merged with anything recorded for it.</summary>
public record BillDue(Bill Bill, DateOnly DueDate, BillOccurrence? Occurrence)
{
    /// <summary>The recorded amount, else a credit card payment's estimate, else the bill's expected amount.</summary>
    public decimal Amount => Occurrence?.Amount ?? CardEstimate ?? Bill.ExpectedAmount;
    public bool IsPaid => Occurrence?.IsPaid ?? false;
    public bool IsEstimate => Occurrence?.Amount is null && (Bill.IsVariable || CardEstimate is not null);

    private decimal? CardEstimate => Bill.PaymentEstimates?.TryGetValue(DueDate, out var estimate) == true ? estimate : null;

    /// <summary>
    /// Unpaid, not on autopay, and past due. Due dates before the pay-from account's balance
    /// date are assumed to be settled already, since that balance reflects them.
    /// </summary>
    public bool IsOverdue(DateOnly today) =>
        !IsPaid && !Bill.AutoPay && DueDate < today
        && (Bill.PayFromAccount is null || DueDate >= Bill.PayFromAccount.BalanceAsOf);
}

public static class BillSchedule
{
    public static IEnumerable<BillDue> Between(IEnumerable<Bill> bills, DateOnly from, DateOnly to) =>
        bills.Where(b => b.IsActive)
            .SelectMany(b => Recurrence.Dates(b.Frequency, b.StartDate, b.EndDate, from, to)
                .Select(d => new BillDue(b, d, b.Occurrences.FirstOrDefault(o => o.DueDate == d))))
            .OrderBy(d => d.DueDate)
            .ThenBy(d => d.Bill.Name);
}
