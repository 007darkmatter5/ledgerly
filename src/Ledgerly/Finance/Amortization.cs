namespace Ledgerly.Finance;

/// <summary>A one-off payment straight to principal, made on <paramref name="Date"/>.</summary>
public record ExtraPayment(DateOnly Date, decimal Amount);

/// <param name="ExtraPrincipal">The planned extra principal paid with every monthly payment.</param>
/// <param name="AdditionalPrincipal">One-off extra payments made after this payment and before the next one.</param>
public record AmortizationRow(
    int Number,
    DateOnly Date,
    decimal Payment,
    decimal Interest,
    decimal Principal,
    decimal ExtraPrincipal,
    decimal Balance,
    decimal CumulativeInterest,
    decimal AdditionalPrincipal = 0m)
{
    public decimal TotalPaid => Payment + ExtraPrincipal + AdditionalPrincipal;
}

public class AmortizationSchedule
{
    public required decimal Principal { get; init; }
    public required decimal AnnualRatePercent { get; init; }
    public required decimal ScheduledPayment { get; init; }
    public required IReadOnlyList<AmortizationRow> Rows { get; init; }

    /// <summary>True when the payment never covers the interest, so the balance never reaches zero.</summary>
    public bool NeverPaysOff { get; init; }

    /// <summary>The one-off payments actually applied (capped at the balance), and which row each belongs to.</summary>
    public IReadOnlyList<AppliedExtraPayment> AppliedExtraPayments { get; init; } = [];

    public decimal TotalInterest => Rows.Sum(r => r.Interest);
    public decimal TotalPaid => Rows.Sum(r => r.TotalPaid);
    public int NumberOfPayments => Rows.Count;
    public DateOnly? PayoffDate => NeverPaysOff || Rows.Count == 0 ? null : Rows[^1].Date;

    /// <summary>Balance after all payments (scheduled and one-off) dated on or before <paramref name="date"/>.</summary>
    public decimal BalanceOn(DateOnly date)
    {
        var row = Rows.LastOrDefault(r => r.Date <= date);
        if (row is null)
            return Principal - AppliedExtraPayments.Where(p => p.Date <= date).Sum(p => p.Amount);

        // A row's balance includes one-off payments made up to the next payment; undo those made after the date.
        return row.Balance + AppliedExtraPayments.Where(p => p.RowNumber == row.Number && p.Date > date).Sum(p => p.Amount);
    }
}

public record AppliedExtraPayment(int RowNumber, DateOnly Date, decimal Amount);

public static class Amortization
{
    private const int MaxPayments = 1200; // 100 years of monthly payments

    /// <summary>Standard fixed monthly payment that pays off the principal in <paramref name="termMonths"/>, rounded to the cent.</summary>
    public static decimal MonthlyPayment(decimal principal, decimal annualRatePercent, int termMonths)
    {
        if (principal <= 0 || termMonths <= 0)
            return 0m;

        var r = annualRatePercent / 100m / 12m;
        if (r == 0)
            return Math.Round(principal / termMonths, 2, MidpointRounding.AwayFromZero);

        // P * r / (1 - (1 + r)^-n), computed in decimal to avoid floating-point cents drift.
        var growth = 1m;
        for (var i = 0; i < termMonths; i++)
            growth *= 1 + r;

        var payment = principal * r * growth / (growth - 1);
        return Math.Round(payment, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Builds a monthly schedule. With <paramref name="termMonths"/>, the payment is calculated (unless
    /// <paramref name="payment"/> is given) and payment number <paramref name="termMonths"/> clears any
    /// rounding residue. Without it, <paramref name="payment"/> is required and runs until paid off.
    /// <paramref name="additionalPayments"/> are one-off principal payments; each reduces the balance right
    /// after the scheduled payment on or before its date (so it lowers the next month's interest).
    /// </summary>
    public static AmortizationSchedule Build(
        decimal principal,
        decimal annualRatePercent,
        DateOnly firstPaymentDate,
        int? termMonths = null,
        decimal? payment = null,
        decimal extraPrincipal = 0m,
        IEnumerable<ExtraPayment>? additionalPayments = null)
    {
        if (termMonths is null && payment is null)
            throw new ArgumentException("Provide a term, a payment, or both.");

        var scheduled = payment ?? MonthlyPayment(principal, annualRatePercent, termMonths!.Value);
        var rate = annualRatePercent / 100m / 12m;
        var rows = new List<AmortizationRow>();
        var balance = Math.Round(principal, 2, MidpointRounding.AwayFromZero);
        var cumulativeInterest = 0m;
        var neverPaysOff = false;
        extraPrincipal = Math.Max(0m, extraPrincipal);
        var lumps = (additionalPayments ?? []).Where(p => p.Amount > 0).OrderBy(p => p.Date).ToList();
        var nextLump = 0;
        var appliedLumps = new List<AppliedExtraPayment>();

        for (var n = 1; balance > 0 && n <= MaxPayments; n++)
        {
            var interest = Math.Round(balance * rate, 2, MidpointRounding.AwayFromZero);
            var isFinalTermPayment = termMonths == n;

            if (scheduled + extraPrincipal <= interest && !isFinalTermPayment && nextLump >= lumps.Count)
            {
                neverPaysOff = true;
                break;
            }

            decimal principalPart, extra, paid;
            if (isFinalTermPayment || balance + interest <= scheduled)
            {
                // Final payment: whatever clears the balance.
                principalPart = balance;
                extra = 0m;
                paid = balance + interest;
            }
            else
            {
                // A payment that doesn't cover the interest (possible when one-off payments keep the loan going) adds to the balance.
                principalPart = scheduled - interest;
                extra = Math.Max(0m, Math.Min(extraPrincipal, balance - principalPart));
                paid = scheduled;
            }

            balance -= principalPart + extra;

            // One-off payments made from this payment date up to (not including) the next one.
            var date = firstPaymentDate.AddMonths(n - 1);
            var nextDate = firstPaymentDate.AddMonths(n);
            var additional = 0m;
            while (nextLump < lumps.Count && lumps[nextLump].Date < nextDate)
            {
                var lump = lumps[nextLump++];
                var applied = Math.Min(lump.Amount, balance);
                if (applied <= 0)
                    continue;
                balance -= applied;
                additional += applied;
                appliedLumps.Add(new AppliedExtraPayment(n, lump.Date, applied));
            }

            cumulativeInterest += interest;
            rows.Add(new AmortizationRow(n, date, paid, interest, principalPart, extra, balance, cumulativeInterest, additional));
        }

        return new AmortizationSchedule
        {
            Principal = principal,
            AnnualRatePercent = annualRatePercent,
            ScheduledPayment = scheduled,
            Rows = rows,
            NeverPaysOff = neverPaysOff || balance > 0,
            AppliedExtraPayments = appliedLumps
        };
    }
}
