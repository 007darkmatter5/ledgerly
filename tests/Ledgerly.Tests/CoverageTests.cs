using Ledgerly.Data;
using Ledgerly.Finance;

namespace Ledgerly.Tests;

public class CoverageTests
{
    private static readonly DateOnly Today = new(2026, 11, 6);

    private static Account Cash(int id, string name, decimal balance, DateOnly? asOf = null, AccountType type = AccountType.Checking, decimal? threshold = null) =>
        new()
        {
            Id = id,
            Name = name,
            Type = type,
            Balance = balance,
            BalanceAsOf = asOf ?? new DateOnly(2026, 11, 1),
            LowBalanceThreshold = threshold
        };

    private static Bill Due(string name, decimal amount, DateOnly start, int? accountId, Frequency frequency = Frequency.Once,
        bool autopay = false, decimal? max = null, int? loanId = null, int? cardId = null, int id = 0) =>
        new()
        {
            Id = id,
            Name = name,
            ExpectedAmount = amount,
            MaxAmount = max,
            StartDate = start,
            Frequency = frequency,
            PayFromAccountId = accountId,
            AutoPay = autopay,
            LoanId = loanId,
            CardAccountId = cardId
        };

    private static Income Pay(string name, decimal amount, DateOnly start, int accountId, Frequency frequency = Frequency.EveryTwoWeeks,
        DateOnly? end = null, bool active = true, int id = 0) =>
        new()
        {
            Id = id,
            Name = name,
            Amount = amount,
            StartDate = start,
            EndDate = end,
            Frequency = frequency,
            DepositToAccountId = accountId,
            IsActive = active
        };

    private static CoverageReport Build(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Bill>? bills = null,
        IReadOnlyList<Income>? incomes = null,
        IReadOnlyList<Transfer>? transfers = null,
        IReadOnlyList<Transaction>? transactions = null,
        CoverageHorizon horizon = CoverageHorizon.EndOfMonth,
        DateOnly? today = null,
        IReadOnlyList<Bill>? cardPaymentsFromOthers = null) =>
        Coverage.Build(accounts, bills ?? [], incomes ?? [], transfers ?? [], transactions ?? [], today ?? Today, horizon, cardPaymentsFromOthers);

    [Fact]
    public void Payday_follows_the_schedule_and_includes_that_day()
    {
        // Sept 4 2026 is a Friday. Nov 6 is 63 days later, so the next biweekly date is Nov 13, not a guessed Friday.
        var income = Pay("Paycheck", 1800m, new DateOnly(2026, 9, 4), 1);
        var payday = PaydayResolution.Resolve([income], Today);

        Assert.Equal(PaydayClarity.Resolved, payday.Clarity);
        Assert.Equal(new DateOnly(2026, 11, 13), payday.Date);
        Assert.Equal(new DateOnly(2026, 11, 13), CoverageWindows.Through(CoverageHorizon.NextPayday, Today, payday));

        var onPayday = Due("Rent", 1800m, new DateOnly(2026, 11, 13), 1, id: 1);
        var after = Due("Later", 10m, new DateOnly(2026, 11, 14), 1, id: 2);
        var report = Build([Cash(1, "Checking", 0m)], [onPayday, after], [income], horizon: CoverageHorizon.NextPayday);

        Assert.Equal(CoverageAnswer.Covered, report.Answer);
        Assert.Contains(report.Events, e => e.Title == "Rent");
        Assert.DoesNotContain(report.Events, e => e.Title == "Later");
        Assert.Equal(1800m, report.ScheduledIncome);
    }

    [Fact]
    public void Month_end_payday_does_not_drift()
    {
        var payday = PaydayResolution.Resolve([Pay("Pay", 100m, new DateOnly(2026, 1, 31), 1, Frequency.Monthly)], new DateOnly(2026, 2, 10));

        Assert.Equal(new DateOnly(2026, 2, 28), payday.Date);
    }

    [Fact]
    public void One_time_ended_and_inactive_income_are_not_a_payday()
    {
        Income[] incomes =
        [
            Pay("Bonus", 500m, new DateOnly(2026, 11, 20), 1, Frequency.Once),
            Pay("Old job", 500m, new DateOnly(2026, 1, 2), 1, end: new DateOnly(2026, 1, 31)),
            Pay("Paused", 500m, new DateOnly(2026, 11, 6), 1, Frequency.Weekly, active: false)
        ];

        var payday = PaydayResolution.Resolve(incomes, Today);

        Assert.Equal(PaydayClarity.Missing, payday.Clarity);
        Assert.Null(payday.Date);
        Assert.Null(CoverageWindows.Through(CoverageHorizon.NextPayday, Today, payday));
    }

    [Fact]
    public void Ambiguous_payday_does_not_pick_a_date_or_a_positive_answer()
    {
        // Weekly lands on today. A bill on Nov 20 would sit outside a horizon that stopped today,
        // so choosing the earlier income would report the $100 as covering the bills.
        Income[] incomes =
        [
            Pay("Weekly", 10m, Today, 1, Frequency.Weekly),
            Pay("Monthly", 10m, new DateOnly(2026, 11, 30), 1, Frequency.Monthly)
        ];
        var bill = Due("Rent", 5000m, new DateOnly(2026, 11, 20), 1);

        var payday = PaydayResolution.Resolve(incomes, Today);
        var report = Build([Cash(1, "Checking", 100m)], [bill], incomes, horizon: CoverageHorizon.NextPayday);

        Assert.Equal(PaydayClarity.Ambiguous, payday.Clarity);
        Assert.Null(payday.Date);
        Assert.Equal([Today, new DateOnly(2026, 11, 30)], payday.Candidates.Select(c => c.Date));
        Assert.Null(report.Through);
        Assert.Equal(CoverageAnswer.Unknown, report.Answer);
        Assert.Equal(0m, report.ScheduledIncome);
        Assert.Equal(0m, report.ScheduledBills);
        Assert.All(report.CashAccounts, a => Assert.Null(a.ProjectedEnding));

        var thirty = Build([Cash(1, "Checking", 100m)], [bill], incomes, horizon: CoverageHorizon.Next30Days);
        Assert.Equal(new DateOnly(2026, 12, 6), thirty.Through);
        Assert.Equal(CoverageAnswer.Shortfall, thirty.Answer);
    }

    [Fact]
    public void Incomes_that_share_a_payday_stay_resolved_and_are_counted_once()
    {
        Income[] incomes =
        [
            Pay("Job", 1000m, new DateOnly(2026, 9, 4), 1, id: 1),
            Pay("Partner", 200m, new DateOnly(2026, 9, 4), 1, id: 2)
        ];
        var payday = PaydayResolution.Resolve(incomes, Today);
        var report = Build([Cash(1, "Checking", 0m)], [Due("Rent", 1100m, new DateOnly(2026, 11, 13), 1)], incomes, horizon: CoverageHorizon.NextPayday);

        Assert.Equal(PaydayClarity.Resolved, payday.Clarity);
        Assert.Equal(CoverageAnswer.Covered, report.Answer);
        Assert.Equal(1200m, report.ScheduledIncome);
        Assert.Equal(2, report.Events.Count(e => e.Kind == ProjectionEntryKind.Income));
    }

    [Theory]
    [InlineData(2026, 11, 6, 2026, 11, 30)]
    [InlineData(2026, 1, 31, 2026, 1, 31)]
    [InlineData(2026, 3, 1, 2026, 3, 31)]
    [InlineData(2028, 2, 1, 2028, 2, 29)]
    [InlineData(2026, 2, 1, 2026, 2, 28)]
    public void End_of_month_follows_the_local_calendar_date(int year, int month, int day, int endYear, int endMonth, int endDay)
    {
        var today = new DateOnly(year, month, day);
        var payday = PaydayResolution.Resolve([], today);

        Assert.Equal(new DateOnly(endYear, endMonth, endDay), CoverageWindows.Through(CoverageHorizon.EndOfMonth, today, payday));
    }

    [Fact]
    public void Thirty_day_and_month_windows_include_the_last_day_only()
    {
        Bill[] bills =
        [
            Due("Month end", 10m, new DateOnly(2026, 11, 30), 1, id: 1),
            Due("Next month", 10m, new DateOnly(2026, 12, 1), 1, id: 2),
            Due("Day 30", 10m, new DateOnly(2026, 12, 6), 1, id: 3),
            Due("Day 31", 10m, new DateOnly(2026, 12, 7), 1, id: 4)
        ];

        var month = Build([Cash(1, "Checking", 1000m)], bills, horizon: CoverageHorizon.EndOfMonth);
        var thirty = Build([Cash(1, "Checking", 1000m)], bills, horizon: CoverageHorizon.Next30Days);

        Assert.Equal(new DateOnly(2026, 12, 6), thirty.Through);
        Assert.Equal(["Month end"], month.Events.Select(e => e.Title));
        Assert.Equal(["Month end", "Next month", "Day 30"], thirty.Events.Select(e => e.Title));
    }

    [Fact]
    public void Savings_balance_does_not_cover_a_checking_bill()
    {
        Account[] accounts = [Cash(1, "Checking", 100m), Cash(2, "Savings", 5000m, type: AccountType.Savings)];
        var report = Build(accounts, [Due("Rent", 400m, new DateOnly(2026, 11, 10), 1)]);

        Assert.Equal(CoverageAnswer.Shortfall, report.Answer);
        Assert.Equal(5100m, report.RecordedCash);
        var checking = Assert.Single(report.CashAccounts, a => a.Name == "Checking");
        var savings = Assert.Single(report.CashAccounts, a => a.Name == "Savings");
        Assert.Equal(new DateOnly(2026, 11, 10), checking.ShortfallOn);
        Assert.Equal("Rent", checking.ShortfallItem);
        Assert.Equal(-300m, checking.BalanceAtShortfall);
        Assert.Null(savings.ShortfallOn);
        Assert.Equal(5000m, savings.ProjectedEnding);
        Assert.Equal(400m, report.ScheduledBills);
    }

    [Fact]
    public void A_transfer_covers_the_bill_only_when_it_arrives_first()
    {
        Account[] accounts = [Cash(1, "Checking", 100m), Cash(2, "Savings", 5000m, type: AccountType.Savings)];
        var bill = Due("Rent", 400m, new DateOnly(2026, 11, 10), 1, id: 1);
        var before = new Transfer { Id = 3, Name = "Move", Amount = 400m, StartDate = new DateOnly(2026, 11, 8), FromAccountId = 2, ToAccountId = 1 };
        var covered = Build(accounts, [bill], transfers: [before]);

        Assert.Equal(CoverageAnswer.Covered, covered.Answer);
        Assert.Equal(100m, covered.CashAccounts.Single(a => a.AccountId == 1).ProjectedEnding);
        Assert.Equal(4600m, covered.CashAccounts.Single(a => a.AccountId == 2).ProjectedEnding);
        Assert.Equal(2, covered.Events.Count(e => e.Title == "Move"));
        Assert.Equal(0m, covered.Events.Where(e => e.Title == "Move").Sum(e => e.Amount));
        Assert.Equal(0m, covered.ScheduledIncome);

        var after = new Transfer { Id = 4, Name = "Move", Amount = 400m, StartDate = new DateOnly(2026, 11, 12), FromAccountId = 2, ToAccountId = 1 };
        var shortfall = Build(accounts, [bill], transfers: [after]);
        var checking = shortfall.CashAccounts.Single(a => a.AccountId == 1);

        Assert.Equal(CoverageAnswer.Shortfall, shortfall.Answer);
        Assert.Equal(new DateOnly(2026, 11, 10), checking.ShortfallOn);
        Assert.Equal(new DateOnly(2026, 11, 12), checking.RecoversOn);
        Assert.Equal(100m, checking.ProjectedEnding);
    }

    [Fact]
    public void Same_day_income_is_applied_before_the_bill()
    {
        var report = Build(
            [Cash(1, "Checking", 100m)],
            [Due("Rent", 400m, new DateOnly(2026, 11, 10), 1)],
            [Pay("Pay", 400m, new DateOnly(2026, 11, 10), 1, Frequency.Once)]);

        Assert.Equal(CoverageAnswer.Covered, report.Answer);
        Assert.Equal(100m, report.CashAccounts[0].ProjectedEnding);
        Assert.Equal(["Pay", "Rent"], report.Events.Select(e => e.Title));
    }

    [Fact]
    public void A_bill_the_day_before_payday_is_still_a_shortfall()
    {
        var income = Pay("Pay", 500m, new DateOnly(2026, 11, 13), 1, Frequency.Monthly);
        var report = Build([Cash(1, "Checking", 0m)], [Due("Rent", 500m, new DateOnly(2026, 11, 12), 1)], [income], horizon: CoverageHorizon.NextPayday);
        var checking = report.CashAccounts[0];

        Assert.Equal(new DateOnly(2026, 11, 13), report.Through);
        Assert.Equal(CoverageAnswer.Shortfall, report.Answer);
        Assert.Equal(new DateOnly(2026, 11, 12), checking.ShortfallOn);
        Assert.Equal(new DateOnly(2026, 11, 13), checking.RecoversOn);
    }

    [Fact]
    public void Unassigned_or_invisible_bills_block_a_yes()
    {
        var unassigned = Build([Cash(1, "Checking", 5000m)], [Due("Mystery", 10m, new DateOnly(2026, 11, 10), null, id: 8)]);
        var missing = Build([Cash(1, "Checking", 5000m)], [Due("Hidden", 10m, new DateOnly(2026, 11, 10), 99, id: 9)]);

        Assert.Equal(CoverageAnswer.Unknown, unassigned.Answer);
        Assert.Equal(CoverageGapKind.BillWithoutAccount, Assert.Single(unassigned.Gaps).Kind);
        Assert.Equal("bills", unassigned.Gaps[0].Href);
        Assert.Equal(CoverageAnswer.Unknown, missing.Answer);
        Assert.Equal(CoverageGapKind.BillAccountUnavailable, Assert.Single(missing.Gaps).Kind);
        Assert.DoesNotContain(unassigned.Events, e => e.Title == "Mystery");
    }

    [Fact]
    public void A_balance_dated_after_today_is_not_available_money()
    {
        var savings = Cash(2, "Savings", 5000m, new DateOnly(2026, 11, 20), AccountType.Savings);
        var report = Build(
            [Cash(1, "Checking", 100m), savings],
            [Due("Old", 100m, new DateOnly(2026, 11, 10), 2)]);

        Assert.Equal(CoverageAnswer.Unknown, report.Answer);
        Assert.Equal(100m, report.RecordedCash);
        Assert.Equal(CoverageGapKind.BalanceDatedAfterToday, Assert.Single(report.Gaps).Kind);
        Assert.DoesNotContain(report.Events, e => e.Title == "Old");
    }

    [Fact]
    public void An_already_negative_balance_is_a_shortfall_before_upcoming_items()
    {
        var report = Build([Cash(1, "Checking", -40m)]);
        var checking = report.CashAccounts[0];

        Assert.Equal(CoverageAnswer.Shortfall, report.Answer);
        Assert.True(checking.AlreadyShort);
        Assert.Equal(new DateOnly(2026, 11, 1), checking.ShortfallOn);
        Assert.Null(checking.ShortfallItem);
        Assert.DoesNotContain(report.Events, e => e.TriggersShortfall);
    }

    [Fact]
    public void Posted_activity_is_separate_from_scheduled_income_and_bills()
    {
        var bill = Due("Phone", 400m, new DateOnly(2026, 11, 10), 1, id: 1);
        var paid = Due("Power", 50m, new DateOnly(2026, 11, 20), 1, id: 2);
        paid.Occurrences.Add(new BillOccurrence { DueDate = new DateOnly(2026, 11, 20), Amount = 50m, PaidOn = new DateOnly(2026, 11, 4) });
        var autopay = Due("Netflix", 80m, new DateOnly(2026, 11, 15), 1, autopay: true, id: 3);
        Transaction[] transactions =
        [
            new() { Description = "Groceries", Amount = 100m, Date = new DateOnly(2026, 11, 3), AccountId = 1, Direction = TransactionDirection.Spent },
            new() { Description = "Trip", Amount = 50m, Date = new DateOnly(2026, 11, 20), AccountId = 1, Direction = TransactionDirection.Spent }
        ];
        var income = Pay("Pay", 2000m, new DateOnly(2026, 11, 13), 1, Frequency.Once);

        var report = Build([Cash(1, "Checking", 1000m)], [bill, paid, autopay], [income], transactions: transactions);

        var checking = report.CashAccounts[0];
        Assert.Equal(1000m, checking.RecordedBalance);
        Assert.Equal(850m, checking.PostedBalance);
        Assert.Equal(2000m, report.ScheduledIncome);
        Assert.Equal(480m, report.ScheduledBills);
        Assert.Equal(2320m, checking.ProjectedEnding);
        Assert.Equal(CoverageItemState.Logged, report.Events.Single(e => e.Title == "Groceries").State);
        Assert.Equal(CoverageItemState.Scheduled, report.Events.Single(e => e.Title == "Trip").State);
        Assert.Equal(CoverageItemState.Recorded, report.Events.Single(e => e.Title == "Power").State);
        Assert.Equal(new DateOnly(2026, 11, 4), report.Events.Single(e => e.Title == "Power").Date);
        Assert.Equal(CoverageItemState.Assumed, report.Events.Single(e => e.Title == "Netflix").State);
        Assert.Equal(CoverageItemState.ScheduledIncome, report.Events.Single(e => e.Title == "Pay").State);
        Assert.Equal(CoverageItemState.Scheduled, report.Events.Single(e => e.Title == "Phone").State);
    }

    [Fact]
    public void Overdue_unpaid_bills_still_count_and_bills_before_the_balance_date_do_not()
    {
        var overdue = Build([Cash(1, "Checking", 50m)], [Due("Rent", 80m, new DateOnly(2026, 11, 3), 1)]);
        var settled = Build([Cash(1, "Checking", 500m, Today)], [Due("Settled", 400m, new DateOnly(2026, 11, 5), 1)]);

        Assert.Equal(CoverageAnswer.Shortfall, overdue.Answer);
        Assert.Equal(new DateOnly(2026, 11, 3), overdue.CashAccounts[0].ShortfallOn);
        Assert.True(overdue.Events.Single().TriggersShortfall);
        Assert.Equal(CoverageAnswer.Covered, settled.Answer);
        Assert.Empty(settled.Events);
        Assert.Empty(settled.Gaps);
    }

    [Fact]
    public void Expected_amount_is_used_for_a_variable_bill()
    {
        var report = Build([Cash(1, "Checking", 150m)], [Due("Electric", 100m, new DateOnly(2026, 11, 10), 1, max: 500m)]);

        Assert.Equal(CoverageAnswer.Covered, report.Answer);
        Assert.Equal(100m, report.ScheduledBills);
        Assert.Equal(50m, report.CashAccounts[0].ProjectedEnding);
    }

    [Fact]
    public void Credit_card_debt_and_card_charges_are_not_cash()
    {
        var card = Cash(3, "Visa", -2000m, type: AccountType.CreditCard);
        var charge = Due("Store", 3000m, new DateOnly(2026, 11, 10), 3, id: 4);
        charge.PayFromAccount = card;
        var report = Build([Cash(1, "Checking", 500m), card], [charge]);

        Assert.Equal(CoverageAnswer.Covered, report.Answer);
        Assert.Equal(500m, report.RecordedCash);
        Assert.Single(report.Cards);
        Assert.Equal(0m, report.ScheduledBills);
        var listed = Assert.Single(report.CardCharges);
        Assert.Equal("cards/3", listed.Href);
        Assert.DoesNotContain(report.Events, e => e.Title == "Store");
    }

    [Fact]
    public void A_card_payment_reduces_its_bank_account_once()
    {
        var payment = Due("Visa payment", 600m, new DateOnly(2026, 11, 10), 1, cardId: 9, id: 6);
        payment.Occurrences.Add(new BillOccurrence { DueDate = payment.StartDate, Amount = 600m });
        var duplicate = Build([Cash(1, "Checking", 100m)], [payment, payment], cardPaymentsFromOthers: [payment]);

        Assert.Equal(CoverageAnswer.Shortfall, duplicate.Answer);
        Assert.Equal(600m, duplicate.ScheduledBills);
        Assert.Single(duplicate.Events, e => e.Title == "Visa payment");
        Assert.Equal("cards/9", duplicate.Events[0].Href);
    }

    [Fact]
    public void Loan_bills_link_to_the_loan()
    {
        var report = Build([Cash(1, "Checking", 1000m)], [Due("Car", 300m, new DateOnly(2026, 11, 15), 1, loanId: 4, id: 7)]);

        Assert.Equal("loans/4", Assert.Single(report.Events).Href);
    }

    [Fact]
    public void A_transfer_is_not_counted_as_income()
    {
        var transfer = new Transfer { Id = 2, Name = "To savings", Amount = 300m, StartDate = new DateOnly(2026, 11, 8), FromAccountId = 1, ToAccountId = 2 };
        var report = Build(
            [Cash(1, "Checking", 1000m), Cash(2, "Savings", 0m, type: AccountType.Savings)],
            incomes: [Pay("Pay", 100m, new DateOnly(2026, 11, 7), 1, Frequency.Once)],
            transfers: [transfer]);

        Assert.Equal(100m, report.ScheduledIncome);
        Assert.Equal("transfers", report.Events.Single(e => e.Title == "To savings" && e.AccountId == 1).Href);
        Assert.Equal(CoverageItemState.Scheduled, report.Events.Single(e => e.Kind == ProjectionEntryKind.TransferOut).State);
    }

    [Fact]
    public void No_accounts_is_unknown_rather_than_covered()
    {
        var report = Build([]);

        Assert.Equal(CoverageAnswer.Unknown, report.Answer);
        Assert.Equal(0m, report.RecordedCash);
    }
}
