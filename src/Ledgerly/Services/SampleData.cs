using Ledgerly.Data;
using Ledgerly.Finance;

namespace Ledgerly.Services;

/// <summary>Example data the user can choose to load from the dashboard. Never inserted automatically.</summary>
public static class SampleData
{
    public static IEnumerable<object> Create(DateOnly today)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        DateOnly Day(int day) => monthStart.AddDays(day - 1);

        var checking = new Account { Name = "Everyday Checking", Type = AccountType.Checking, Balance = 2450m, BalanceAsOf = today, LowBalanceThreshold = 500m };
        var savings = new Account { Name = "Emergency Savings", Type = AccountType.Savings, Balance = 8000m, BalanceAsOf = today };
        var card = new Account { Name = "Rewards Card", Type = AccountType.CreditCard, Balance = -620m, BalanceAsOf = today };

        var mortgage = new Loan
        {
            Name = "Home Mortgage", Lender = "First Street Bank", Type = LoanType.Mortgage,
            OriginalPrincipal = 280_000m, AnnualRatePercent = 6.25m, TermMonths = 360,
            FirstPaymentDate = Day(1).AddYears(-3)
        };
        var auto = new Loan
        {
            Name = "Car Loan", Lender = "Credit Union", Type = LoanType.Auto,
            OriginalPrincipal = 28_000m, AnnualRatePercent = 5.9m, TermMonths = 60,
            FirstPaymentDate = Day(15).AddMonths(-14), ExtraPrincipal = 50m
        };
        var student = new Loan
        {
            Name = "Student Loan", Lender = "Loan Servicer", Type = LoanType.Student,
            OriginalPrincipal = 32_000m, AnnualRatePercent = 4.5m, TermMonths = 120,
            FirstPaymentDate = Day(20).AddYears(-4)
        };

        decimal PaymentOf(Loan l) => Amortization.MonthlyPayment(l.OriginalPrincipal, l.AnnualRatePercent, l.TermMonths);

        return
        [
            checking, savings, card, mortgage, auto, student,
            new Income { Name = "Paycheck", Amount = 2150m, Frequency = Frequency.EveryTwoWeeks, StartDate = NextWeekday(today, DayOfWeek.Friday), DepositToAccount = checking },

            new Bill { Name = "Mortgage", Payee = mortgage.Lender, Category = "Housing", ExpectedAmount = PaymentOf(mortgage), StartDate = mortgage.FirstPaymentDate, PayFromAccount = checking, Loan = mortgage, AutoPay = true },
            new Bill { Name = "Car Payment", Payee = auto.Lender, Category = "Transportation", ExpectedAmount = PaymentOf(auto) + auto.ExtraPrincipal, StartDate = auto.FirstPaymentDate, PayFromAccount = checking, Loan = auto, AutoPay = true },
            new Bill { Name = "Student Loan", Payee = student.Lender, Category = "Education", ExpectedAmount = PaymentOf(student), StartDate = student.FirstPaymentDate, PayFromAccount = checking, Loan = student },
            new Bill { Name = "Electric", Payee = "City Power", Category = "Utilities", ExpectedAmount = 120m, MaxAmount = 190m, StartDate = Day(12), PayFromAccount = checking },
            new Bill { Name = "Water & Sewer", Payee = "City Water", Category = "Utilities", ExpectedAmount = 95m, MaxAmount = 130m, Frequency = Frequency.Quarterly, StartDate = Day(25), PayFromAccount = checking },
            new Bill { Name = "Internet", Payee = "FiberNet", Category = "Utilities", ExpectedAmount = 70m, StartDate = Day(8), PayFromAccount = checking, AutoPay = true },
            new Bill { Name = "Phone", Payee = "Mobile Co", Category = "Utilities", ExpectedAmount = 85m, StartDate = Day(18), PayFromAccount = card, AutoPay = true },
            new Bill { Name = "Streaming", Payee = "StreamCo", Category = "Entertainment", ExpectedAmount = 15.99m, StartDate = Day(3), PayFromAccount = card, AutoPay = true },
            new Bill { Name = "Car Insurance", Payee = "SafeDrive", Category = "Insurance", ExpectedAmount = 540m, Frequency = Frequency.SemiAnnually, StartDate = Day(1).AddMonths(1), PayFromAccount = checking },
            new Bill { Name = "Credit Card Payment", Payee = "Rewards Card", Category = "Debt", ExpectedAmount = 300m, MaxAmount = 650m, StartDate = Day(22), PayFromAccount = checking }
        ];
    }

    private static DateOnly NextWeekday(DateOnly from, DayOfWeek day) =>
        from.AddDays(((int)day - (int)from.DayOfWeek + 7) % 7);
}
