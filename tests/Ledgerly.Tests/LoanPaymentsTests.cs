using Ledgerly.Data;
using Ledgerly.Finance;

namespace Ledgerly.Tests;

/// <summary>Monthly vs extra loan payments, and how extra payments change the balance.</summary>
public class LoanPaymentsTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static Loan CarLoan(int id = 7) => new()
    {
        Id = id, Name = "Car", OriginalPrincipal = 30_000m, AnnualRatePercent = 5m, TermMonths = 60, FirstPaymentDate = D(2025, 1, 15)
    };

    private static Bill MonthlyBill(Loan loan, decimal amount = 566.14m) => new()
    {
        Id = 1, Name = "Car payment", LoanId = loan.Id, Loan = loan, LoanPaymentKind = LoanPaymentKind.MonthlyPayment,
        ExpectedAmount = amount, Frequency = Frequency.Monthly, StartDate = loan.FirstPaymentDate
    };

    private static Bill ExtraBill(Loan loan, decimal amount, Frequency frequency = Frequency.Once, DateOnly? start = null) => new()
    {
        Id = 2, Name = "Car extra", LoanId = loan.Id, Loan = loan, LoanPaymentKind = LoanPaymentKind.ExtraPrincipal,
        ExpectedAmount = amount, Frequency = frequency, StartDate = start ?? D(2026, 3, 1)
    };

    [Fact]
    public void Paying_more_than_the_monthly_bill_counts_the_difference_as_extra()
    {
        var loan = CarLoan();
        var bill = MonthlyBill(loan);
        bill.Occurrences = [new BillOccurrence { DueDate = D(2026, 3, 15), PaidOn = D(2026, 3, 14), Amount = 700m }];

        var payment = Assert.Single(LoanPayments.For(loan, [bill], D(2026, 4, 1)));

        Assert.Equal(700m, payment.Amount);
        Assert.Equal(133.86m, payment.ExtraPrincipal);
        Assert.Equal(566.14m, payment.MonthlyPortion);
        Assert.Equal(LoanPaymentKind.MonthlyPayment, payment.Kind);
    }

    [Fact]
    public void Monthly_bill_that_includes_escrow_is_not_extra_at_its_usual_amount()
    {
        var loan = CarLoan();
        var bill = MonthlyBill(loan, amount: 800m); // payment plus escrow
        bill.Occurrences = [new BillOccurrence { DueDate = D(2026, 3, 15), PaidOn = D(2026, 3, 15) }];

        Assert.Equal(0m, Assert.Single(LoanPayments.For(loan, [bill], D(2026, 4, 1))).ExtraPrincipal);
    }

    [Fact]
    public void Extra_principal_bill_counts_its_whole_amount_and_can_be_overridden_per_payment()
    {
        var loan = CarLoan();
        var extra = ExtraBill(loan, 250m, Frequency.Monthly);
        extra.Occurrences =
        [
            new BillOccurrence { DueDate = D(2026, 3, 1), PaidOn = D(2026, 3, 1) },
            new BillOccurrence { DueDate = D(2026, 4, 1), PaidOn = D(2026, 4, 2), PaymentKind = LoanPaymentKind.MonthlyPayment }
        ];
        var monthly = MonthlyBill(loan);
        monthly.Occurrences = [new BillOccurrence { DueDate = D(2026, 4, 15), PaidOn = D(2026, 4, 15), Amount = 400m, PaymentKind = LoanPaymentKind.ExtraPrincipal }];

        var payments = LoanPayments.For(loan, [extra, monthly], D(2026, 5, 1));

        Assert.Equal([250m, 0m, 400m], payments.Select(p => p.ExtraPrincipal));
        Assert.Equal([D(2026, 3, 1), D(2026, 4, 2), D(2026, 4, 15)], payments.Select(p => p.Date));
    }

    [Fact]
    public void Autopay_extra_payments_count_on_their_due_dates_without_being_marked_paid()
    {
        var loan = CarLoan();
        var extra = ExtraBill(loan, 100m, Frequency.Monthly, start: D(2026, 1, 20));
        extra.AutoPay = true;

        var extras = LoanPayments.ExtrasFor(loan, [extra], D(2026, 3, 25));

        Assert.Equal([D(2026, 1, 20), D(2026, 2, 20), D(2026, 3, 20)], extras.Select(e => e.Date));
        Assert.All(extras, e => Assert.Equal(100m, e.Amount));
    }

    [Fact]
    public void Unpaid_future_and_other_loans_payments_are_ignored()
    {
        var loan = CarLoan();
        var unpaid = ExtraBill(loan, 500m); // not autopay, not marked paid
        var future = ExtraBill(loan, 300m);
        future.Occurrences = [new BillOccurrence { DueDate = D(2026, 3, 1), PaidOn = D(2026, 6, 1) }];
        var otherLoan = ExtraBill(CarLoan(id: 99), 900m);
        otherLoan.Occurrences = [new BillOccurrence { DueDate = D(2026, 3, 1), PaidOn = D(2026, 3, 1) }];

        Assert.Empty(LoanPayments.For(loan, [unpaid, future, otherLoan], D(2026, 4, 1)));
    }

    [Fact]
    public void Extra_payment_lowers_the_balance_from_its_date_and_shortens_the_loan()
    {
        var loan = CarLoan();
        ExtraPayment[] extras = [new(D(2026, 3, 20), 5_000m)];

        var before = LoanMath.CurrentBalance(loan, D(2026, 3, 19), extras);
        var after = LoanMath.CurrentBalance(loan, D(2026, 3, 20), extras);
        var withoutExtra = LoanMath.CurrentBalance(loan, D(2026, 3, 20), []);

        Assert.Equal(withoutExtra, before);
        Assert.Equal(withoutExtra - 5_000m, after);

        var withSchedule = LoanMath.ActualSchedule(loan, extras);
        var withoutSchedule = LoanMath.ActualSchedule(loan, []);
        Assert.True(withSchedule.NumberOfPayments < withoutSchedule.NumberOfPayments);
        Assert.True(withSchedule.TotalInterest < withoutSchedule.TotalInterest);
        Assert.Equal(5_000m, withSchedule.Rows.Sum(r => r.AdditionalPrincipal));
        Assert.Equal(30_000m, withSchedule.Rows.Sum(r => r.Principal + r.ExtraPrincipal + r.AdditionalPrincipal));

        // Interest the month after the extra payment is lower than on the plain schedule.
        var april = withSchedule.Rows.Single(r => r.Date == D(2026, 4, 15));
        var aprilPlain = withoutSchedule.Rows.Single(r => r.Date == D(2026, 4, 15));
        Assert.True(april.Interest < aprilPlain.Interest);
    }

    [Fact]
    public void Remaining_schedule_starts_from_the_balance_after_extra_payments()
    {
        var loan = CarLoan();
        ExtraPayment[] extras = [new(D(2026, 2, 1), 3_000m)];
        var today = D(2026, 3, 1);

        var remaining = LoanMath.RemainingSchedule(loan, today, extras);
        var plain = LoanMath.RemainingSchedule(loan, today, []);

        // $3,000 lower, plus a little more because the extra payment also cut February's and March's interest.
        Assert.InRange(plain.Principal - remaining.Principal, 3_000m, 3_050m);
        Assert.Equal(D(2026, 3, 15), remaining.Rows[0].Date);
        Assert.True(remaining.PayoffDate < plain.PayoffDate);
        Assert.Equal(0m, remaining.Rows[^1].Balance);
    }

    [Fact]
    public void Lender_balance_moves_forward_with_scheduled_and_later_extra_payments()
    {
        var loan = CarLoan();
        loan.CurrentBalance = 20_000m;
        loan.BalanceAsOf = D(2026, 1, 20);
        ExtraPayment[] extras =
        [
            new(D(2026, 1, 10), 1_000m), // before the lender's balance date: already included in it
            new(D(2026, 2, 25), 2_000m)
        ];

        // Scheduled payments on Feb 15 and Mar 15 are assumed made.
        var plain = LoanMath.CurrentBalance(loan, D(2026, 3, 20), []);
        var withExtras = LoanMath.CurrentBalance(loan, D(2026, 3, 20), extras);

        Assert.True(plain < 20_000m - 2 * 400m);
        Assert.Equal(20_000m, LoanMath.CurrentBalance(loan, D(2026, 2, 1), []));
        // Only the Feb 25 payment applies; it also trims March's interest, so the gap is a bit more than $2,000.
        Assert.InRange(plain - withExtras, 2_000m, 2_010m);
    }

    [Fact]
    public void One_off_payment_can_pay_the_loan_off_early()
    {
        var schedule = Amortization.Build(5_000m, 6m, D(2026, 1, 1), termMonths: 24, additionalPayments: [new(D(2026, 2, 10), 10_000m)]);

        Assert.Equal(2, schedule.NumberOfPayments);
        Assert.Equal(0m, schedule.Rows[^1].Balance);
        Assert.False(schedule.NeverPaysOff);
        Assert.Equal(5_000m, schedule.Rows.Sum(r => r.Principal + r.AdditionalPrincipal));
    }
}
