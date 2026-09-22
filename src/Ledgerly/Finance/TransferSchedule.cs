using Ledgerly.Data;

namespace Ledgerly.Finance;

/// <summary>One scheduled date of a transfer, merged with anything recorded for it.</summary>
public record TransferDue(Transfer Transfer, DateOnly ScheduledDate, TransferOccurrence? Occurrence)
{
    /// <summary>The recorded amount, else the transfer's usual amount.</summary>
    public decimal Amount => Occurrence?.Amount ?? Transfer.Amount;

    public bool IsDone => Occurrence?.IsDone ?? false;

    /// <summary>The day the money moves: the day it actually moved, else the scheduled day.</summary>
    public DateOnly EffectiveDate => Occurrence?.CompletedOn ?? ScheduledDate;

    /// <summary>
    /// Not recorded as done, not automatic, and the date has passed. Dates before the from-account's
    /// balance date are assumed to have happened already, since that balance reflects them.
    /// </summary>
    public bool IsMissed(DateOnly today) =>
        !IsDone && !Transfer.IsAutomatic && ScheduledDate < today
        && (Transfer.FromAccount is null || ScheduledDate >= Transfer.FromAccount.BalanceAsOf);
}

public static class TransferSchedule
{
    public static IEnumerable<TransferDue> Between(IEnumerable<Transfer> transfers, DateOnly from, DateOnly to) =>
        transfers.Where(t => t.IsActive)
            .SelectMany(t => Recurrence.Dates(t.Frequency, t.StartDate, t.EndDate, from, to)
                .Select(d => new TransferDue(t, d, t.Occurrences.FirstOrDefault(o => o.ScheduledDate == d))))
            .OrderBy(d => d.ScheduledDate)
            .ThenBy(d => d.Transfer.Name);
}
