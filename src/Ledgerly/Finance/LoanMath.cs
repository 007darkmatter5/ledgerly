using Ledgerly.Data;

namespace Ledgerly.Finance;

/// <summary>
/// Amortization views of a stored <see cref="Loan"/>. Monthly payments are assumed made on schedule;
/// <c>extraPayments</c> (see <see cref="LoanPayments.ExtrasFor"/>) are one-off principal payments that
/// were actually made, each applied from the day it was paid.
/// </summary>
public static class LoanMath
{
    /// <summary>The monthly principal + interest payment.</summary>
    public static decimal Payment(Loan loan) =>
        loan.PaymentOverride ?? Amortization.MonthlyPayment(loan.OriginalPrincipal, loan.AnnualRatePercent, loan.TermMonths);

    /// <summary>The schedule from the original loan terms: planned extra principal from the first payment, plus one-off extra payments.</summary>
    public static AmortizationSchedule OriginalSchedule(Loan loan, IReadOnlyCollection<ExtraPayment> extraPayments, bool includeExtra = true)
    {
        var planned = includeExtra ? loan.ExtraPrincipal : 0m;
        IReadOnlyCollection<ExtraPayment> oneOff = includeExtra ? extraPayments : [];

        // A custom payment runs until paid off; otherwise the term sets the payment and ends the schedule.
        return loan.PaymentOverride is { } payment
            ? Amortization.Build(loan.OriginalPrincipal, loan.AnnualRatePercent, loan.FirstPaymentDate,
                payment: payment, extraPrincipal: planned, additionalPayments: oneOff)
            : Amortization.Build(loan.OriginalPrincipal, loan.AnnualRatePercent, loan.FirstPaymentDate,
                termMonths: loan.TermMonths, extraPrincipal: planned, additionalPayments: oneOff);
    }

    /// <summary>
    /// The loan's path as best known: from the lender-reported balance when there is one (with extra payments
    /// made after that date), otherwise from the original terms.
    /// </summary>
    public static AmortizationSchedule ActualSchedule(Loan loan, IReadOnlyCollection<ExtraPayment> extraPayments)
    {
        if (loan.CurrentBalance is not { } balance)
            return OriginalSchedule(loan, extraPayments);

        var asOf = loan.BalanceAsOf ?? loan.FirstPaymentDate;
        return Amortization.Build(balance, loan.AnnualRatePercent, NextPaymentAfter(loan, asOf),
            payment: Payment(loan), extraPrincipal: loan.ExtraPrincipal,
            additionalPayments: extraPayments.Where(p => p.Date > asOf));
    }

    /// <summary>The balance today: the lender's figure (or the original terms) moved forward by scheduled and extra payments.</summary>
    public static decimal CurrentBalance(Loan loan, DateOnly today, IReadOnlyCollection<ExtraPayment> extraPayments) =>
        ActualSchedule(loan, extraPayments).BalanceOn(today);

    /// <summary>
    /// The payments still to come, starting from today's balance. <paramref name="extraPrincipal"/> replaces the
    /// planned monthly extra for future payments only, for what-if comparisons.
    /// </summary>
    public static AmortizationSchedule RemainingSchedule(Loan loan, DateOnly today, IReadOnlyCollection<ExtraPayment> extraPayments, decimal? extraPrincipal = null)
    {
        var balance = CurrentBalance(loan, today, extraPayments);
        var next = NextPaymentAfter(loan, today);
        var extra = extraPrincipal ?? loan.ExtraPrincipal;

        // With the original calculated payment, the final payment of the term still clears rounding residue.
        if (loan.CurrentBalance is null && loan.PaymentOverride is null)
        {
            var paymentsMade = Recurrence.Dates(Frequency.Monthly, loan.FirstPaymentDate, null, loan.FirstPaymentDate, today).Count();
            return Amortization.Build(balance, loan.AnnualRatePercent, next,
                termMonths: Math.Max(1, loan.TermMonths - paymentsMade), payment: Payment(loan), extraPrincipal: extra);
        }

        return Amortization.Build(balance, loan.AnnualRatePercent, next, payment: Payment(loan), extraPrincipal: extra);
    }

    /// <summary>The first monthly payment date after <paramref name="date"/>.</summary>
    private static DateOnly NextPaymentAfter(Loan loan, DateOnly date) =>
        Recurrence.Next(Frequency.Monthly, loan.FirstPaymentDate, null, date.AddDays(1)) ?? date.AddMonths(1);
}
