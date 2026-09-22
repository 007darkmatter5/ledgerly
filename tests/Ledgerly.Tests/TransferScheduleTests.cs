using Ledgerly.Data;
using Ledgerly.Finance;

namespace Ledgerly.Tests;

public class TransferScheduleTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static readonly Account Checking = new() { Id = 1, Name = "Checking", Balance = 1000m, BalanceAsOf = D(9, 1) };

    /// <summary>A $200 move into savings on the 5th of every month, starting two months before the balance date.</summary>
    private static Transfer ToSavings(params TransferOccurrence[] occurrences) => new()
    {
        Id = 1,
        Name = "To savings",
        Amount = 200m,
        Frequency = Frequency.Monthly,
        StartDate = D(7, 5),
        FromAccountId = 1,
        FromAccount = Checking,
        ToAccountId = 2,
        Occurrences = [.. occurrences]
    };

    [Fact]
    public void Scheduled_dates_are_merged_with_what_was_recorded_for_them()
    {
        var transfer = ToSavings(new TransferOccurrence { ScheduledDate = D(9, 5), Amount = 250m, CompletedOn = D(9, 6) });

        var dues = TransferSchedule.Between([transfer], D(9, 1), D(10, 31)).ToList();

        Assert.Equal([D(9, 5), D(10, 5)], dues.Select(d => d.ScheduledDate));
        Assert.Equal(250m, dues[0].Amount);
        Assert.True(dues[0].IsDone);
        Assert.Equal(D(9, 6), dues[0].EffectiveDate);

        Assert.Equal(200m, dues[1].Amount);
        Assert.False(dues[1].IsDone);
        Assert.Equal(D(10, 5), dues[1].EffectiveDate);
    }

    [Fact]
    public void Inactive_transfers_are_left_out()
    {
        var transfer = ToSavings();
        transfer.IsActive = false;

        Assert.Empty(TransferSchedule.Between([transfer], D(9, 1), D(10, 31)));
    }

    [Fact]
    public void A_date_is_missed_only_when_it_has_passed_unmarked_on_a_transfer_the_bank_does_not_make()
    {
        var today = D(9, 20);
        var due = Assert.Single(TransferSchedule.Between([ToSavings()], D(9, 1), D(9, 30)));
        Assert.True(due.IsMissed(today));
        Assert.False(due.IsMissed(D(9, 1))); // still to come

        var automatic = ToSavings();
        automatic.IsAutomatic = true;
        Assert.False(Assert.Single(TransferSchedule.Between([automatic], D(9, 1), D(9, 30))).IsMissed(today));

        var done = ToSavings(new TransferOccurrence { ScheduledDate = D(9, 5), CompletedOn = D(9, 5) });
        Assert.False(Assert.Single(TransferSchedule.Between([done], D(9, 1), D(9, 30))).IsMissed(today));

        // Before the from-account's balance date, so that balance already reflects it.
        Assert.False(Assert.Single(TransferSchedule.Between([ToSavings()], D(8, 1), D(8, 31))).IsMissed(today));
    }
}
