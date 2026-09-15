using Ledgerly.Data;

namespace Ledgerly.Finance;

/// <summary>A bill payment: one marked paid, or an autopay due date assumed paid on its due date.</summary>
public record PaymentRecord(Bill Bill, DateOnly DueDate, DateOnly PaidOn, decimal Amount, BillOccurrence? Occurrence)
{
    /// <summary>Autopay that nobody marked paid, counted as paid on its due date.</summary>
    public bool IsAssumed => Occurrence?.PaidOn is null;

    public BillDue Due => new(Bill, DueDate, Occurrence);

    /// <summary>Days paid after the due date (0 when on time or early).</summary>
    public int DaysLate => Math.Max(0, PaidOn.DayNumber - DueDate.DayNumber);
}

public static class PaymentHistory
{
    /// <summary>
    /// Payments dated <paramref name="from"/> through <paramref name="to"/>, newest first. Marked payments count
    /// by the date paid, including bills that are now inactive. With <paramref name="includeAutopay"/>, unmarked
    /// autopay due dates of active bills up to <paramref name="today"/> count as paid on their due date.
    /// </summary>
    public static List<PaymentRecord> Between(IEnumerable<Bill> bills, DateOnly from, DateOnly to, DateOnly today, bool includeAutopay = true)
    {
        var billList = bills.ToList();
        var records = billList
            .SelectMany(b => b.Occurrences
                .Where(o => o.PaidOn is { } paid && paid >= from && paid <= to)
                .Select(o => new PaymentRecord(b, o.DueDate, o.PaidOn!.Value, o.Amount ?? b.ExpectedAmount, o)))
            .ToList();

        if (includeAutopay)
        {
            records.AddRange(BillSchedule.Between(billList.Where(b => b.AutoPay), from, Min(to, today))
                .Where(d => !d.IsPaid)
                .Select(d => new PaymentRecord(d.Bill, d.DueDate, d.DueDate, d.Amount, d.Occurrence)));
        }

        return records.OrderByDescending(r => r.PaidOn).ThenBy(r => r.Bill.Name).ThenByDescending(r => r.DueDate).ToList();
    }

    /// <summary>
    /// Due dates from <paramref name="from"/> through today (or <paramref name="to"/>) that aren't marked paid and
    /// aren't on autopay, oldest first. Like overdue bills, due dates before the pay-from account's balance date
    /// are left out, since that balance already reflects them.
    /// </summary>
    public static List<BillDue> NotMarkedPaid(IEnumerable<Bill> bills, DateOnly from, DateOnly to, DateOnly today) =>
        BillSchedule.Between(bills, from, Min(to, today))
            .Where(d => d.IsOverdue(today) || (d.DueDate == today && !d.IsPaid && !d.Bill.AutoPay))
            .ToList();

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
