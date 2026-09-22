using Ledgerly.Data;
using Ledgerly.Finance;

namespace Ledgerly.Tests;

public class CreditCardsTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static readonly Account Checking = new() { Id = 1, Name = "Checking", Type = AccountType.Checking, Balance = 3000m, BalanceAsOf = D(9, 1) };

    private static Account Card(decimal owed = 500m, int statementDay = 25, decimal? apr = 24m, decimal? spending = null) => new()
    {
        Id = 2, Name = "Card", Type = AccountType.CreditCard, Balance = -owed, BalanceAsOf = D(9, 1),
        CreditLimit = 5000m, AprPercent = apr, StatementDay = statementDay, MonthlySpending = spending
    };

    private static Bill Charge(string name, decimal amount, DateOnly start) =>
        new() { Id = name.GetHashCode(), Name = name, ExpectedAmount = amount, StartDate = start, PayFromAccountId = 2 };

    private static Bill Payment(Account card, CardPaymentRule rule, DateOnly firstDue, decimal amount = 0m) => new()
    {
        Id = 99, Name = "Card payment", ExpectedAmount = amount, StartDate = firstDue, PayFromAccountId = 1,
        CardAccountId = card.Id, CardAccount = card, CardPaymentRule = rule
    };

    [Fact]
    public void Paying_the_statement_in_full_pays_what_was_owed_at_the_close_and_charges_no_interest()
    {
        var card = Card(owed: 500m);
        var phone = Charge("Phone", 85m, D(9, 18));
        var payment = Payment(card, CardPaymentRule.StatementBalance, D(10, 20));

        var simulation = CreditCards.Simulate(card, [phone, payment], D(11, 30));

        // Sep 25 statement: 500 + Sep 18 phone. Oct 25 statement: the Oct 18 phone only, since Sep was paid in full.
        Assert.Equal([585m, 85m, 85m], simulation.Statements.Select(s => s.Balance));
        Assert.All(simulation.Statements, s => Assert.Equal(0m, s.Interest));
        Assert.Equal(585m, simulation.PaymentFor(payment, D(10, 20)));
        Assert.Equal(85m, simulation.PaymentFor(payment, D(11, 20)));
        Assert.Equal(85m, simulation.OwedOn(D(11, 30)));
    }

    [Fact]
    public void Paying_the_minimum_carries_the_rest_and_adds_interest_at_the_next_close()
    {
        var card = Card(owed: 2000m, apr: 24m);
        var payment = Payment(card, CardPaymentRule.Minimum, D(10, 20));

        var simulation = CreditCards.Simulate(card, [payment], D(10, 31));

        // First close: not paying in full, so 2% a month on 2,000 = 40 interest; minimum = 1% of 2,040 + 40 = 60.40.
        var first = simulation.Statements[0];
        Assert.Equal((40m, 2040m, 60.40m), (first.Interest, first.Balance, first.MinimumPayment));
        Assert.Equal(60.40m, simulation.PaymentFor(payment, D(10, 20)));

        // Oct 25: 2,040 − 60.40 carried, so interest again.
        Assert.Equal(Math.Round(1979.60m * 0.02m, 2), simulation.Statements[1].Interest);
    }

    [Fact]
    public void A_fixed_payment_never_pays_more_than_is_owed_and_recorded_amounts_win()
    {
        var card = Card(owed: 150m, apr: null);
        var payment = Payment(card, CardPaymentRule.FixedAmount, D(9, 20), amount: 100m);
        payment.Occurrences = [new BillOccurrence { DueDate = D(9, 20), Amount = 120m, PaidOn = D(9, 19) }];

        var simulation = CreditCards.Simulate(card, [payment], D(10, 31));

        Assert.Equal(120m, simulation.PaymentFor(payment, D(9, 20)));
        Assert.Equal(30m, simulation.PaymentFor(payment, D(10, 20)));
        Assert.Equal(0m, simulation.OwedOn(D(10, 31)));
        Assert.Equal(D(9, 19), simulation.Entries.First(e => e.Kind == CardEntryKind.Payment).Date);
    }

    [Fact]
    public void Everyday_spending_is_added_at_each_close_and_prorated_for_the_first_cycle()
    {
        var card = Card(owed: 0m, statementDay: 15, spending: 310m);

        var simulation = CreditCards.Simulate(card, [], D(10, 31));

        // Aug 15 → Sep 15 is 31 days, 14 of them after the Sep 1 balance date.
        Assert.Equal([140m, 310m], simulation.Entries.Where(e => e.Kind == CardEntryKind.Spending).Select(e => e.Amount));
    }

    [Fact]
    public void Short_months_close_on_their_last_day()
    {
        Assert.Equal([D(9, 30), D(10, 31), new DateOnly(2027, 2, 28)],
            CreditCards.ClosingDates(31, D(9, 1), new DateOnly(2027, 3, 1)).Where(d => d.Month is 9 or 10 or 2));
    }

    [Fact]
    public void Paying_a_card_moves_money_between_projected_accounts_without_counting_it_as_spending()
    {
        var card = Card(owed: 500m);
        var payment = Payment(card, CardPaymentRule.StatementBalance, D(9, 20));
        Bill[] bills = [Charge("Phone", 85m, D(9, 18)), payment];

        var checkingOnly = Projection.Build([Checking], bills, [], [], D(9, 30));
        var both = Projection.Build([Checking, card], bills, [], [], D(9, 30));
        var cardOnly = Projection.Build([card], bills, [], [], D(9, 30));

        // Before the first projected statement, paying in full pays the balance as of the balance date; later charges go on the next statement.
        var paid = Assert.Single(checkingOnly.Entries);
        Assert.Equal(-500m, paid.Amount);
        Assert.True(paid.IsEstimate);
        Assert.Equal(500m, checkingOnly.TotalOut);

        Assert.Equal(3000m - 500m - 85m, both.EndingBalance);
        Assert.Equal(85m, both.TotalOut);
        Assert.Equal(0m, both.TotalIn);
        Assert.All(both.Entries.Where(e => e.BillId == payment.Id), e => Assert.True(e.IsTransfer));

        Assert.Equal(-85m, cardOnly.EndingBalance);
        Assert.Equal(500m, cardOnly.TotalIn);
    }

    [Fact]
    public void Bill_schedules_show_card_payment_estimates()
    {
        var card = Card(owed: 500m);
        var payment = Payment(card, CardPaymentRule.StatementBalance, D(10, 20));
        Bill[] bills = [Charge("Phone", 85m, D(9, 18)), payment];

        CreditCards.EstimatePayments(bills, D(12, 31));
        var due = BillSchedule.Between(bills, D(10, 1), D(10, 31)).Single(d => d.Bill == payment);

        Assert.Equal(585m, due.Amount);
        Assert.True(due.IsEstimate);
    }

    [Fact]
    public void Payoff_without_new_charges_depends_on_how_the_card_is_paid()
    {
        var card = Card(owed: 2000m, apr: 24m);

        Assert.Equal(1, CreditCards.PayoffIfNoNewCharges(card, CardPaymentRule.StatementBalance, 0m, D(9, 1)).Months);

        var fixedPayoff = CreditCards.PayoffIfNoNewCharges(card, CardPaymentRule.FixedAmount, 200m, D(9, 1));
        // n = −ln(1 − r·P/A) / ln(1 + r) = −ln(0.8) / ln(1.02) ≈ 11.3, so 12 payments.
        Assert.Equal(12, fixedPayoff.Months);
        Assert.InRange(fixedPayoff.TotalInterest, 200m, 260m);

        Assert.True(CreditCards.PayoffIfNoNewCharges(card, CardPaymentRule.FixedAmount, 40m, D(9, 1)).NeverPaysOff);
        Assert.False(CreditCards.PayoffIfNoNewCharges(card, CardPaymentRule.Minimum, 0m, D(9, 1)).NeverPaysOff);
    }
}
