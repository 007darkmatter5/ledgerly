using Ledgerly.Data;

namespace Ledgerly.Finance;

public static class Recurrence
{
    /// <summary>
    /// Scheduled dates between <paramref name="from"/> and <paramref name="to"/> (inclusive).
    /// Every date is computed from <paramref name="start"/> so month-end anchors don't drift
    /// (a bill due Jan 31 is due Feb 28, then Mar 31).
    /// </summary>
    public static IEnumerable<DateOnly> Dates(Frequency frequency, DateOnly start, DateOnly? end, DateOnly from, DateOnly to)
    {
        var last = end is { } e && e < to ? e : to;
        if (last < start || last < from)
            yield break;

        if (frequency == Frequency.Once)
        {
            if (start >= from)
                yield return start;
            yield break;
        }

        var days = StepDays(frequency);
        var months = StepMonths(frequency);

        // Jump close to the window instead of walking from a start date that may be years back.
        var n = 0;
        if (from > start)
        {
            n = days > 0
                ? (from.DayNumber - start.DayNumber) / days
                : Math.Max(0, ((from.Year - start.Year) * 12 + from.Month - start.Month) / months - 1);
        }

        for (; ; n++)
        {
            var date = days > 0 ? start.AddDays(days * n) : start.AddMonths(months * n);
            if (date > last)
                yield break;
            if (date >= from)
                yield return date;
        }
    }

    /// <summary>The next scheduled date on or after <paramref name="onOrAfter"/>, if any.</summary>
    public static DateOnly? Next(Frequency frequency, DateOnly start, DateOnly? end, DateOnly onOrAfter) =>
        Dates(frequency, start, end, onOrAfter, DateOnly.MaxValue.AddYears(-1)).Select(d => (DateOnly?)d).FirstOrDefault();

    /// <summary>Average cost per month of an amount paid at this frequency.</summary>
    public static decimal MonthlyEquivalent(Frequency frequency, decimal amount) => frequency switch
    {
        Frequency.Once => 0m,
        Frequency.Weekly => amount * 52m / 12m,
        Frequency.EveryTwoWeeks => amount * 26m / 12m,
        _ => amount / StepMonths(frequency)
    };

    public static string Describe(Frequency frequency) => frequency switch
    {
        Frequency.Once => "One time",
        Frequency.Weekly => "Weekly",
        Frequency.EveryTwoWeeks => "Every 2 weeks",
        Frequency.Monthly => "Monthly",
        Frequency.EveryTwoMonths => "Every 2 months",
        Frequency.Quarterly => "Quarterly",
        Frequency.SemiAnnually => "Twice a year",
        Frequency.Annually => "Yearly",
        _ => frequency.ToString()
    };

    private static int StepDays(Frequency frequency) => frequency switch
    {
        Frequency.Weekly => 7,
        Frequency.EveryTwoWeeks => 14,
        _ => 0
    };

    private static int StepMonths(Frequency frequency) => frequency switch
    {
        Frequency.Monthly => 1,
        Frequency.EveryTwoMonths => 2,
        Frequency.Quarterly => 3,
        Frequency.SemiAnnually => 6,
        Frequency.Annually => 12,
        _ => 0
    };
}
