using Ledgerly.Data;
using Ledgerly.Finance;

namespace Ledgerly.Tests;

public class RecurrenceTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Monthly_on_the_31st_clamps_to_month_end_without_drifting()
    {
        var dates = Recurrence.Dates(Frequency.Monthly, D(2027, 1, 31), null, D(2027, 1, 1), D(2027, 4, 30)).ToList();

        Assert.Equal([D(2027, 1, 31), D(2027, 2, 28), D(2027, 3, 31), D(2027, 4, 30)], dates);
    }

    [Fact]
    public void Every_two_weeks_starting_long_ago_lands_on_the_right_dates_in_the_window()
    {
        var dates = Recurrence.Dates(Frequency.EveryTwoWeeks, D(2020, 1, 3), null, D(2026, 9, 1), D(2026, 9, 30)).ToList();

        Assert.All(dates, d => Assert.Equal(0, (d.DayNumber - D(2020, 1, 3).DayNumber) % 14));
        Assert.Equal(2, dates.Count);
        Assert.True(dates[0] >= D(2026, 9, 1));
    }

    [Fact]
    public void Quarterly_starting_before_the_window_skips_ahead_correctly()
    {
        var dates = Recurrence.Dates(Frequency.Quarterly, D(2025, 1, 15), null, D(2026, 3, 1), D(2026, 12, 31)).ToList();

        Assert.Equal([D(2026, 4, 15), D(2026, 7, 15), D(2026, 10, 15)], dates);
    }

    [Fact]
    public void End_date_stops_the_schedule()
    {
        var dates = Recurrence.Dates(Frequency.Monthly, D(2026, 1, 5), D(2026, 3, 5), D(2026, 1, 1), D(2026, 12, 31)).ToList();

        Assert.Equal([D(2026, 1, 5), D(2026, 2, 5), D(2026, 3, 5)], dates);
    }

    [Fact]
    public void One_time_date_only_appears_when_in_the_window()
    {
        Assert.Single(Recurrence.Dates(Frequency.Once, D(2026, 5, 1), null, D(2026, 4, 1), D(2026, 5, 31)));
        Assert.Empty(Recurrence.Dates(Frequency.Once, D(2026, 5, 1), null, D(2026, 5, 2), D(2026, 5, 31)));
    }

    [Fact]
    public void Next_returns_the_first_date_on_or_after()
    {
        Assert.Equal(D(2026, 9, 15), Recurrence.Next(Frequency.Monthly, D(2026, 1, 15), null, D(2026, 9, 15)));
        Assert.Equal(D(2026, 10, 15), Recurrence.Next(Frequency.Monthly, D(2026, 1, 15), null, D(2026, 9, 16)));
        Assert.Null(Recurrence.Next(Frequency.Monthly, D(2026, 1, 15), D(2026, 6, 15), D(2026, 9, 1)));
    }

    [Theory]
    [InlineData(Frequency.Weekly, 100, 433.33)]
    [InlineData(Frequency.EveryTwoWeeks, 100, 216.67)]
    [InlineData(Frequency.Monthly, 100, 100)]
    [InlineData(Frequency.Quarterly, 300, 100)]
    [InlineData(Frequency.Annually, 1200, 100)]
    [InlineData(Frequency.Once, 500, 0)]
    public void Monthly_equivalent(Frequency frequency, decimal amount, decimal expected)
    {
        Assert.Equal(expected, Math.Round(Recurrence.MonthlyEquivalent(frequency, amount), 2));
    }
}
