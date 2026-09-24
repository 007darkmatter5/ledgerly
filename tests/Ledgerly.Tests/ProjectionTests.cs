using Ledgerly.Data;
using Ledgerly.Finance;

namespace Ledgerly.Tests;

public class ProjectionTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static readonly Account Checking = new() { Id = 1, Name = "Checking", Balance = 1000m, BalanceAsOf = D(9, 1), LowBalanceThreshold = 200m };
    private static readonly Account Savings = new() { Id = 2, Name = "Savings", Balance = 5000m, BalanceAsOf = D(9, 1) };

    private static Bill Bill(string name, decimal amount, DateOnly start, int? accountId = 1, Frequency frequency = Frequency.Monthly, decimal? max = null) =>
        new() { Id = name.GetHashCode(), Name = name, ExpectedAmount = amount, MaxAmount = max, StartDate = start, Frequency = frequency, PayFromAccountId = accountId };

    private static Transfer Transfer(string name, decimal amount, DateOnly start, int from = 1, int to = 2, Frequency frequency = Frequency.Once) =>
        new() { Id = name.GetHashCode(), Name = name, Amount = amount, StartDate = start, Frequency = frequency, FromAccountId = from, ToAccountId = to };

    [Fact]
    public void Applies_bills_and_income_in_date_order_from_the_starting_balance()
    {
        Bill[] bills = [Bill("Rent", 800m, D(9, 5)), Bill("Phone", 50m, D(9, 20))];
        Income[] incomes = [new() { Name = "Pay", Amount = 600m, Frequency = Frequency.EveryTwoWeeks, StartDate = D(9, 12), DepositToAccountId = 1 }];

        var result = Projection.Build([Checking], bills, incomes, [], [], D(9, 30));

        Assert.Equal(["Rent", "Pay", "Phone", "Pay"], result.Entries.Select(e => e.Description));
        Assert.Equal([200m, 800m, 750m, 1350m], result.Entries.Select(e => e.RunningBalance));
        Assert.Equal(1350m, result.EndingBalance);
        Assert.Equal(850m, result.TotalOut);
        Assert.Equal(1200m, result.TotalIn);
        Assert.Equal(D(9, 5), result.LowestPoint!.Date);
    }

    [Fact]
    public void Same_day_income_is_applied_before_bills()
    {
        var result = Projection.Build([Checking],
            [Bill("Big bill", 1500m, D(9, 10))],
            [new Income { Name = "Pay", Amount = 1000m, Frequency = Frequency.Once, StartDate = D(9, 10), DepositToAccountId = 1 }],
            [], [], D(9, 30));

        Assert.Equal(ProjectionEntryKind.Income, result.Entries[0].Kind);
        Assert.Null(result.FirstBelowZero);
    }

    [Fact]
    public void Flags_first_dates_below_zero_and_below_threshold()
    {
        var result = Projection.Build([Checking], [Bill("A", 850m, D(9, 3), frequency: Frequency.Once), Bill("B", 300m, D(9, 8), frequency: Frequency.Once)], [], [], [], D(9, 30));

        Assert.Equal(D(9, 3), result.FirstBelowThreshold!.Date);
        Assert.Equal(D(9, 8), result.FirstBelowZero!.Date);
        Assert.Equal(-150m, result.EndingBalance);
    }

    [Fact]
    public void Account_that_starts_negative_is_not_flagged_as_going_negative()
    {
        var card = new Account { Id = 3, Name = "Card", Balance = -620m, BalanceAsOf = D(9, 1), LowBalanceThreshold = 0m };

        var result = Projection.Build([card], [Bill("Phone", 85m, D(9, 18), accountId: 3)], [], [], [], D(9, 30));

        Assert.Null(result.FirstBelowZero);
        Assert.Null(result.FirstBelowThreshold);
        Assert.Equal(-705m, result.LowestPoint!.RunningBalance);
    }

    [Fact]
    public void Ignores_dates_before_the_balance_date_and_bills_for_other_or_no_accounts()
    {
        Bill[] bills =
        [
            Bill("Before balance date", 100m, D(8, 31), frequency: Frequency.Once),
            Bill("On balance date", 100m, D(9, 1), frequency: Frequency.Once),
            Bill("Savings bill", 100m, D(9, 10), accountId: 2),
            Bill("Unassigned", 100m, D(9, 10), accountId: null)
        ];

        var result = Projection.Build([Checking], bills, [], [], [], D(9, 30));

        Assert.Equal(["On balance date"], result.Entries.Select(e => e.Description));
    }

    [Fact]
    public void Paid_bills_use_the_paid_date_and_amount()
    {
        var electric = Bill("Electric", 120m, D(9, 15), frequency: Frequency.Once, max: 200m);
        electric.Occurrences = [new BillOccurrence { DueDate = D(9, 15), Amount = 143.27m, PaidOn = D(9, 12) }];

        var entry = Assert.Single(Projection.Build([Checking], [electric], [], [], [], D(9, 30)).Entries);

        Assert.Equal(D(9, 12), entry.Date);
        Assert.Equal(-143.27m, entry.Amount);
        Assert.True(entry.IsPaid);
        Assert.False(entry.IsEstimate);
    }

    [Fact]
    public void Bill_paid_early_before_the_balance_date_is_already_in_the_balance()
    {
        var bill = Bill("Insurance", 500m, D(9, 10), frequency: Frequency.Once);
        bill.Occurrences = [new BillOccurrence { DueDate = D(9, 10), PaidOn = D(8, 28) }];

        Assert.Empty(Projection.Build([Checking], [bill], [], [], [], D(9, 30)).Entries);
    }

    [Fact]
    public void Bill_due_before_the_balance_date_but_paid_after_it_is_included()
    {
        var bill = Bill("Late", 75m, D(8, 25), frequency: Frequency.Once);
        bill.Occurrences = [new BillOccurrence { DueDate = D(8, 25), PaidOn = D(9, 3) }];

        var entry = Assert.Single(Projection.Build([Checking], [bill], [], [], [], D(9, 30)).Entries);
        Assert.Equal(D(9, 3), entry.Date);
    }

    [Fact]
    public void Worst_case_uses_max_amount_unless_an_amount_is_recorded()
    {
        var water = Bill("Water", 80m, D(9, 10), max: 130m);
        water.Occurrences = [new BillOccurrence { DueDate = D(10, 10), Amount = 95m }];

        var expected = Projection.Build([Checking], [water], [], [], [], D(10, 31));
        var worst = Projection.Build([Checking], [water], [], [], [], D(10, 31), ProjectionMode.WorstCase);

        Assert.Equal([-80m, -95m], expected.Entries.Select(e => e.Amount));
        Assert.Equal([-130m, -95m], worst.Entries.Select(e => e.Amount));
        Assert.True(worst.Entries[0].IsEstimate);
        Assert.False(worst.Entries[1].IsEstimate);
    }

    [Fact]
    public void Combines_multiple_accounts()
    {
        var result = Projection.Build([Checking, Savings], [Bill("Rent", 800m, D(9, 5)), Bill("Transfer", 100m, D(9, 6), accountId: 2)], [], [], [], D(9, 30));

        Assert.Equal(6000m, result.StartingBalance);
        Assert.Equal(5100m, result.EndingBalance);
        Assert.Equal(200m, result.LowBalanceThreshold);
    }

    [Fact]
    public void Inactive_bills_and_income_are_skipped()
    {
        var bill = Bill("Old gym", 40m, D(9, 5));
        bill.IsActive = false;

        Assert.Empty(Projection.Build([Checking], [bill],
            [new Income { Name = "Old job", Amount = 1m, StartDate = D(9, 5), Frequency = Frequency.Weekly, DepositToAccountId = 1, IsActive = false }], [], [], D(9, 30)).Entries);
    }

    [Fact]
    public void Overdue_means_unpaid_manual_and_after_the_account_balance_date()
    {
        var today = D(9, 20);
        var manual = Bill("Manual", 10m, D(9, 10), frequency: Frequency.Once);
        manual.PayFromAccount = Checking;
        var auto = Bill("Auto", 10m, D(9, 10), frequency: Frequency.Once);
        auto.AutoPay = true;
        auto.PayFromAccount = Checking;
        var settled = Bill("Before balance date", 10m, D(8, 25), frequency: Frequency.Once);
        settled.PayFromAccount = Checking;

        var overdue = BillSchedule.Between([manual, auto, settled], D(8, 1), today.AddDays(-1)).Where(d => d.IsOverdue(today));

        Assert.Equal(["Manual"], overdue.Select(d => d.Bill.Name));
    }

    [Fact]
    public void A_transfer_between_two_projected_accounts_moves_money_without_counting_as_money_in_or_out()
    {
        var result = Projection.Build([Checking, Savings], [], [], [Transfer("To savings", 300m, D(9, 10))], [], D(9, 30));

        Assert.Equal([ProjectionEntryKind.TransferIn, ProjectionEntryKind.TransferOut], result.Entries.Select(e => e.Kind));
        Assert.Equal([2, 1], result.Entries.Select(e => e.AccountId));
        Assert.Equal([300m, -300m], result.Entries.Select(e => e.Amount));
        Assert.All(result.Entries, e => Assert.True(e.IsTransfer));

        Assert.Equal(6000m, result.StartingBalance);
        Assert.Equal(6000m, result.EndingBalance);
        Assert.Equal(0m, result.TotalIn);
        Assert.Equal(0m, result.TotalOut);
    }

    [Fact]
    public void A_transfer_with_only_one_end_projected_really_does_leave_or_arrive()
    {
        var transfer = Transfer("To savings", 300m, D(9, 10));

        var fromOnly = Projection.Build([Checking], [], [], [transfer], [], D(9, 30));
        var toOnly = Projection.Build([Savings], [], [], [transfer], [], D(9, 30));

        var leaving = Assert.Single(fromOnly.Entries);
        Assert.Equal(ProjectionEntryKind.TransferOut, leaving.Kind);
        Assert.False(leaving.IsTransfer);
        Assert.Equal(300m, fromOnly.TotalOut);
        Assert.Equal(700m, fromOnly.EndingBalance);

        var arriving = Assert.Single(toOnly.Entries);
        Assert.Equal(ProjectionEntryKind.TransferIn, arriving.Kind);
        Assert.False(arriving.IsTransfer);
        Assert.Equal(300m, toOnly.TotalIn);
        Assert.Equal(5300m, toOnly.EndingBalance);
    }

    [Fact]
    public void A_recorded_transfer_uses_its_amount_and_the_day_the_money_moved()
    {
        var transfer = Transfer("To savings", 300m, D(9, 10));
        transfer.Occurrences = [new TransferOccurrence { ScheduledDate = D(9, 10), Amount = 450m, CompletedOn = D(9, 12) }];

        var entry = Assert.Single(Projection.Build([Checking], [], [], [transfer], [], D(9, 30)).Entries);

        Assert.Equal(D(9, 12), entry.Date);
        Assert.Equal(D(9, 10), entry.DueDate);
        Assert.Equal(-450m, entry.Amount);
        Assert.True(entry.IsPaid);
        Assert.Equal(transfer.Id, entry.TransferId);
    }

    [Fact]
    public void Inactive_transfers_and_transfers_between_other_accounts_are_left_out()
    {
        var inactive = Transfer("Old standing order", 100m, D(9, 5));
        inactive.IsActive = false;
        var elsewhere = Transfer("Between other accounts", 100m, D(9, 5), from: 8, to: 9);
        var beforeTheBalanceDate = Transfer("Already in the balance", 100m, D(8, 20));

        Assert.Empty(Projection.Build([Checking, Savings], [], [], [inactive, elsewhere, beforeTheBalanceDate], [], D(9, 30)).Entries);
    }

    private static Transaction Transaction(string description, decimal amount, DateOnly date, int accountId = 1,
        TransactionDirection direction = TransactionDirection.Spent) =>
        new() { Id = description.GetHashCode(), Description = description, Amount = amount, Date = date, AccountId = accountId, Direction = direction };

    [Fact]
    public void Transactions_spend_and_receive_money_on_their_date()
    {
        var dinner = Transaction("Dinner out", 62.40m, D(9, 12));
        var refund = Transaction("Refund", 20m, D(9, 14), direction: TransactionDirection.Received);

        var result = Projection.Build([Checking], [], [], [], [dinner, refund], D(9, 30));

        Assert.Equal([ProjectionEntryKind.Spent, ProjectionEntryKind.Received], result.Entries.Select(e => e.Kind));
        Assert.Equal([-62.40m, 20m], result.Entries.Select(e => e.Amount));
        Assert.Same(dinner, result.Entries[0].Transaction);
        Assert.Equal(1000m - 62.40m + 20m, result.EndingBalance);
        Assert.Equal(62.40m, result.TotalOut);
        Assert.Equal(20m, result.TotalIn);
    }

    [Fact]
    public void Same_day_money_received_is_applied_before_spending()
    {
        var result = Projection.Build([Checking], [], [], [],
            [Transaction("Big purchase", 1500m, D(9, 10)), Transaction("Sold the bike", 600m, D(9, 10), direction: TransactionDirection.Received)], D(9, 30));

        Assert.Equal(ProjectionEntryKind.Received, result.Entries[0].Kind);
        Assert.Equal(100m, result.EndingBalance);
        Assert.Null(result.FirstBelowZero);
    }

    [Fact]
    public void Transactions_before_the_balance_date_after_the_range_or_on_other_accounts_are_left_out()
    {
        Transaction[] transactions =
        [
            Transaction("Already in the balance", 50m, D(8, 31)),
            Transaction("After the range", 50m, D(10, 1)),
            Transaction("Other account", 50m, D(9, 10), accountId: 2)
        ];

        Assert.Empty(Projection.Build([Checking], [], [], [], transactions, D(9, 30)).Entries);
    }

    [Fact]
    public void Transactions_on_a_projected_card_are_charged_to_it_once()
    {
        var card = new Account { Id = 3, Name = "Card", Type = AccountType.CreditCard, Balance = -100m, BalanceAsOf = D(9, 1), StatementDay = 25 };

        var result = Projection.Build([card], [], [], [], [Transaction("Dinner out", 60m, D(9, 12), accountId: 3)], D(9, 30));

        var entry = Assert.Single(result.Entries);
        Assert.Equal((ProjectionEntryKind.Spent, -60m), (entry.Kind, entry.Amount));
        Assert.Equal(-160m, result.EndingBalance);
    }
}
