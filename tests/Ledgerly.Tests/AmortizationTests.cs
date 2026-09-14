using Ledgerly.Data;
using Ledgerly.Finance;

namespace Ledgerly.Tests;

public class AmortizationTests
{
    private static readonly DateOnly First = new(2026, 1, 1);

    [Theory]
    [InlineData(200_000, 6, 360, 1199.10)]
    [InlineData(30_000, 5, 60, 566.14)]
    [InlineData(250_000, 6.5, 360, 1580.17)]
    [InlineData(12_000, 0, 12, 1000.00)]
    public void Monthly_payment_matches_standard_formula(decimal principal, decimal rate, int term, decimal expected)
    {
        Assert.Equal(expected, Amortization.MonthlyPayment(principal, rate, term));
    }

    [Fact]
    public void Term_schedule_pays_off_exactly_on_the_last_payment()
    {
        var schedule = Amortization.Build(200_000m, 6m, First, termMonths: 360);

        Assert.Equal(360, schedule.NumberOfPayments);
        Assert.Equal(0m, schedule.Rows[^1].Balance);
        Assert.Equal(new DateOnly(2055, 12, 1), schedule.PayoffDate);
        Assert.False(schedule.NeverPaysOff);

        // Every row reconciles: principal paid equals the drop in balance.
        var previous = 200_000m;
        foreach (var row in schedule.Rows)
        {
            Assert.Equal(previous - row.Principal - row.ExtraPrincipal, row.Balance);
            previous = row.Balance;
        }

        Assert.Equal(200_000m, schedule.Rows.Sum(r => r.Principal));
        // The payment is rounded down by ~$0.001; compounded over 360 months that leaves ~$1.05 for the final payment.
        Assert.Equal(1200.14m, schedule.Rows[^1].Payment);
        Assert.InRange(schedule.TotalInterest, 231_670m, 231_690m);
    }

    [Fact]
    public void First_payment_splits_interest_and_principal()
    {
        var row = Amortization.Build(200_000m, 6m, First, termMonths: 360).Rows[0];

        Assert.Equal(1000.00m, row.Interest); // 200,000 * 0.5%
        Assert.Equal(199.10m, row.Principal);
        Assert.Equal(199_800.90m, row.Balance);
    }

    [Fact]
    public void Extra_principal_pays_off_sooner_and_saves_interest()
    {
        var standard = Amortization.Build(200_000m, 6m, First, termMonths: 360);
        var extra = Amortization.Build(200_000m, 6m, First, termMonths: 360, extraPrincipal: 200m);

        Assert.True(extra.NumberOfPayments < standard.NumberOfPayments);
        Assert.True(extra.TotalInterest < standard.TotalInterest);
        Assert.Equal(0m, extra.Rows[^1].Balance);
        Assert.Equal(200_000m, extra.Rows.Sum(r => r.Principal + r.ExtraPrincipal));
    }

    [Fact]
    public void Fixed_payment_runs_until_paid_off()
    {
        var schedule = Amortization.Build(10_000m, 12m, First, payment: 500m);

        Assert.Equal(0m, schedule.Rows[^1].Balance);
        Assert.True(schedule.Rows[^1].Payment <= 500m);
        Assert.All(schedule.Rows.SkipLast(1), r => Assert.Equal(500m, r.Payment));
    }

    [Fact]
    public void Payment_that_does_not_cover_interest_never_pays_off()
    {
        var schedule = Amortization.Build(100_000m, 12m, First, payment: 900m); // interest is 1,000/month

        Assert.True(schedule.NeverPaysOff);
        Assert.Null(schedule.PayoffDate);
    }

    [Fact]
    public void Balance_on_a_date_uses_payments_made_by_then()
    {
        var schedule = Amortization.Build(200_000m, 6m, First, termMonths: 360);

        Assert.Equal(200_000m, schedule.BalanceOn(new DateOnly(2025, 12, 31)));
        Assert.Equal(schedule.Rows[0].Balance, schedule.BalanceOn(First));
        Assert.Equal(schedule.Rows[11].Balance, schedule.BalanceOn(new DateOnly(2026, 12, 15)));
    }

    [Fact]
    public void Remaining_schedule_starts_from_the_lender_balance_after_its_date()
    {
        var loan = new Loan
        {
            OriginalPrincipal = 30_000m, AnnualRatePercent = 5m, TermMonths = 60,
            FirstPaymentDate = new DateOnly(2025, 3, 15),
            CurrentBalance = 20_000m, BalanceAsOf = new DateOnly(2026, 9, 15)
        };

        var remaining = LoanMath.RemainingSchedule(loan, new DateOnly(2026, 9, 20), []);

        Assert.Equal(20_000m, remaining.Principal);
        Assert.Equal(new DateOnly(2026, 10, 15), remaining.Rows[0].Date);
        Assert.Equal(566.14m, remaining.ScheduledPayment);
        Assert.Equal(0m, remaining.Rows[^1].Balance);
    }

    [Fact]
    public void Remaining_schedule_without_lender_balance_continues_the_original()
    {
        var loan = new Loan { OriginalPrincipal = 30_000m, AnnualRatePercent = 5m, TermMonths = 60, FirstPaymentDate = new DateOnly(2025, 1, 1) };
        var today = new DateOnly(2026, 1, 10); // 13 payments made

        var original = LoanMath.OriginalSchedule(loan, []);
        var remaining = LoanMath.RemainingSchedule(loan, today, []);

        Assert.Equal(47, remaining.NumberOfPayments);
        Assert.Equal(new DateOnly(2026, 2, 1), remaining.Rows[0].Date);
        Assert.Equal(original.Rows[12].Balance, remaining.Principal);
        Assert.Equal(original.Rows.Skip(13).Sum(r => r.Interest), remaining.TotalInterest);
        Assert.Equal(original.PayoffDate, remaining.PayoffDate);
        Assert.Equal(0m, remaining.Rows[^1].Balance);
    }

    [Fact]
    public void What_if_extra_changes_only_future_payments()
    {
        var loan = new Loan { OriginalPrincipal = 30_000m, AnnualRatePercent = 5m, TermMonths = 60, FirstPaymentDate = new DateOnly(2025, 1, 1), ExtraPrincipal = 100m };
        var today = new DateOnly(2026, 1, 10);

        var withExtra = LoanMath.RemainingSchedule(loan, today, []);
        var withoutExtra = LoanMath.RemainingSchedule(loan, today, [], extraPrincipal: 0m);

        // Both start from today's balance, which reflects the extra already paid.
        Assert.Equal(withExtra.Principal, withoutExtra.Principal);
        Assert.Equal(LoanMath.CurrentBalance(loan, today, []), withExtra.Principal);
        Assert.True(withoutExtra.NumberOfPayments > withExtra.NumberOfPayments);
        Assert.True(withoutExtra.TotalInterest > withExtra.TotalInterest);
    }
}
