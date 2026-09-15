using Ledgerly.Data;
using Ledgerly.Finance;

namespace Ledgerly.Tests;

public class PaymentHistoryTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);
    private static readonly DateOnly Today = D(9, 15);
    private static readonly Account Checking = new() { Id = 1, Name = "Checking", BalanceAsOf = D(8, 1) };

    private static Bill Bill(string name, decimal amount, DateOnly start, bool autoPay = false, bool active = true) =>
        new() { Id = name.GetHashCode(), Name = name, ExpectedAmount = amount, StartDate = start, AutoPay = autoPay, IsActive = active, PayFromAccountId = 1, PayFromAccount = Checking };

    [Fact]
    public void Marked_payments_count_by_paid_date_newest_first_with_actual_amounts()
    {
        var power = Bill("Power", 100m, D(7, 10));
        power.Occurrences =
        [
            new() { DueDate = D(8, 10), PaidOn = D(8, 12), Amount = 130m },
            new() { DueDate = D(9, 10), PaidOn = D(9, 1) },
            new() { DueDate = D(7, 10), PaidOn = D(7, 31) }, // outside the period
            new() { DueDate = D(10, 10), Amount = 90m }      // amount only, not paid
        ];

        var history = PaymentHistory.Between([power], D(8, 1), D(9, 30), Today);

        Assert.Equal([D(9, 1), D(8, 12)], history.Select(p => p.PaidOn));
        Assert.Equal([100m, 130m], history.Select(p => p.Amount));
        Assert.Equal(2, history[1].DaysLate);
        Assert.All(history, p => Assert.False(p.IsAssumed));
    }

    [Fact]
    public void Unmarked_autopay_counts_on_its_due_date_up_to_today_only_when_included()
    {
        var phone = Bill("Phone", 50m, D(8, 20), autoPay: true);
        phone.Occurrences = [new() { DueDate = D(8, 20), PaidOn = D(8, 21), Amount = 55m }];

        var history = PaymentHistory.Between([phone], D(8, 1), D(10, 31), Today);

        // Aug 20 is marked paid (so it isn't counted twice) and Sep 20 is still in the future.
        var marked = Assert.Single(history);
        Assert.Equal(D(8, 21), marked.PaidOn);
        Assert.False(marked.IsAssumed);
        Assert.Equal(55m, marked.Amount);

        var streaming = Bill("Streaming", 15m, D(8, 5), autoPay: true);
        var assumed = PaymentHistory.Between([streaming], D(8, 1), D(9, 30), Today);
        Assert.Equal([D(9, 5), D(8, 5)], assumed.Select(p => p.PaidOn));
        Assert.All(assumed, p => Assert.True(p.IsAssumed));

        Assert.Empty(PaymentHistory.Between([streaming], D(8, 1), D(9, 30), Today, includeAutopay: false));
    }

    [Fact]
    public void Future_periods_show_payments_made_ahead_and_unpaid_due_dates_coming_up()
    {
        var rent = Bill("Rent", 1200m, D(7, 1));
        rent.Occurrences = [new() { DueDate = D(10, 1), PaidOn = D(9, 14) }];
        var phone = Bill("Phone", 50m, D(7, 20), autoPay: true);

        var history = PaymentHistory.Between([rent, phone], D(10, 1), D(10, 31), Today);
        var comingUp = PaymentHistory.ComingUp([rent, phone], D(10, 1), D(10, 31), Today);

        var paidAhead = Assert.Single(history);
        Assert.Equal((D(9, 14), D(10, 1)), (paidAhead.PaidOn, paidAhead.DueDate));
        Assert.Equal([("Phone", D(10, 20))], comingUp.Select(d => (d.Bill.Name, d.DueDate)));

        // Within the current month, "coming up" starts tomorrow; today's due dates belong to the other lists.
        Assert.Equal([D(9, 20)], PaymentHistory.ComingUp([phone], D(9, 1), D(9, 30), D(9, 20).AddDays(-5)).Select(d => d.DueDate));
        Assert.Empty(PaymentHistory.ComingUp([phone], D(9, 1), D(9, 30), D(9, 20)));
        Assert.Empty(PaymentHistory.ComingUp([phone], D(8, 1), D(8, 31), Today));
    }

    [Fact]
    public void Inactive_bills_keep_marked_payments_but_add_no_assumed_autopay()
    {
        var gym = Bill("Gym", 40m, D(6, 1), autoPay: true, active: false);
        gym.Occurrences = [new() { DueDate = D(8, 1), PaidOn = D(8, 1) }];

        var history = PaymentHistory.Between([gym], D(6, 1), D(9, 30), Today);

        Assert.Equal(D(8, 1), Assert.Single(history).PaidOn);
    }

    [Fact]
    public void Not_marked_paid_lists_past_and_todays_unpaid_non_autopay_due_dates_after_the_balance_date()
    {
        var rent = Bill("Rent", 1200m, D(7, 15));
        rent.Occurrences = [new() { DueDate = D(8, 15), PaidOn = D(8, 14) }];
        var water = Bill("Water", 30m, D(7, 3));
        var autopay = Bill("Insurance", 90m, D(7, 1), autoPay: true);

        var due = PaymentHistory.NotMarkedPaid([rent, water, autopay], D(7, 1), D(12, 31), Today);

        // July due dates fall before Checking's Aug 1 balance date; Aug 15 rent is paid; Oct isn't due yet.
        Assert.Equal([("Water", D(8, 3)), ("Water", D(9, 3)), ("Rent", D(9, 15))], due.Select(d => (d.Bill.Name, d.DueDate)));
    }
}
