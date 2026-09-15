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
        var card = new Account
        {
            Name = "Rewards Card", Type = AccountType.CreditCard, Balance = -620m, BalanceAsOf = today,
            CreditLimit = 6000m, AprPercent = 24.99m, StatementDay = 28, MonthlySpending = 450m
        };

        Payee NewPayee(string name) => new() { Name = name };
        var firstStreetBank = NewPayee("First Street Bank");
        var creditUnion = NewPayee("Credit Union");
        var loanServicer = NewPayee("Loan Servicer");
        var cityPower = NewPayee("City Power");
        var cityWater = NewPayee("City Water");
        var fiberNet = NewPayee("FiberNet");
        var mobileCo = NewPayee("Mobile Co");
        var streamCo = NewPayee("StreamCo");
        var safeDrive = NewPayee("SafeDrive");
        var rewardsCard = NewPayee("Rewards Card");

        var mortgage = new Loan
        {
            Name = "Home Mortgage", Lender = firstStreetBank, Type = LoanType.Mortgage,
            OriginalPrincipal = 280_000m, AnnualRatePercent = 6.25m, TermMonths = 360,
            FirstPaymentDate = Day(1).AddYears(-3)
        };
        var auto = new Loan
        {
            Name = "Car Loan", Lender = creditUnion, Type = LoanType.Auto,
            OriginalPrincipal = 28_000m, AnnualRatePercent = 5.9m, TermMonths = 60,
            FirstPaymentDate = Day(15).AddMonths(-14), ExtraPrincipal = 50m
        };
        var student = new Loan
        {
            Name = "Student Loan", Lender = loanServicer, Type = LoanType.Student,
            OriginalPrincipal = 32_000m, AnnualRatePercent = 4.5m, TermMonths = 120,
            FirstPaymentDate = Day(20).AddYears(-4)
        };

        var housing = new Category { Name = "Housing", Color = "#2E7D5B" };
        var transportation = new Category { Name = "Transportation", Color = "#3F6FB5" };
        var education = new Category { Name = "Education", Color = "#8E44AD" };
        var utilities = new Category { Name = "Utilities", Color = "#F39C12" };
        var entertainment = new Category { Name = "Entertainment", Color = "#E91E63" };
        var insurance = new Category { Name = "Insurance", Color = "#16A085" };
        var debt = new Category { Name = "Debt", Color = "#C0392B" };

        decimal PaymentOf(Loan l) => Amortization.MonthlyPayment(l.OriginalPrincipal, l.AnnualRatePercent, l.TermMonths);

        return
        [
            checking, savings, card, mortgage, auto, student,
            housing, transportation, education, utilities, entertainment, insurance, debt,
            firstStreetBank, creditUnion, loanServicer, cityPower, cityWater, fiberNet, mobileCo, streamCo, safeDrive, rewardsCard,
            new Income { Name = "Paycheck", Amount = 2150m, Frequency = Frequency.EveryTwoWeeks, StartDate = NextWeekday(today, DayOfWeek.Friday), DepositToAccount = checking },

            new Bill { Name = "Mortgage", Payee = firstStreetBank, Category = housing, ExpectedAmount = PaymentOf(mortgage), StartDate = mortgage.FirstPaymentDate, PayFromAccount = checking, Loan = mortgage, AutoPay = true },
            new Bill { Name = "Car Payment", Payee = creditUnion, Category = transportation, ExpectedAmount = PaymentOf(auto) + auto.ExtraPrincipal, StartDate = auto.FirstPaymentDate, PayFromAccount = checking, Loan = auto, AutoPay = true },
            new Bill { Name = "Student Loan", Payee = loanServicer, Category = education, ExpectedAmount = PaymentOf(student), StartDate = student.FirstPaymentDate, PayFromAccount = checking, Loan = student },
            new Bill { Name = "Electric", Payee = cityPower, Category = utilities, ExpectedAmount = 120m, MaxAmount = 190m, StartDate = Day(12), PayFromAccount = checking },
            new Bill { Name = "Water & Sewer", Payee = cityWater, Category = utilities, ExpectedAmount = 95m, MaxAmount = 130m, Frequency = Frequency.Quarterly, StartDate = Day(25), PayFromAccount = checking },
            new Bill { Name = "Internet", Payee = fiberNet, Category = utilities, ExpectedAmount = 70m, StartDate = Day(8), PayFromAccount = checking, AutoPay = true },
            new Bill { Name = "Phone", Payee = mobileCo, Category = utilities, ExpectedAmount = 85m, StartDate = Day(18), PayFromAccount = card, AutoPay = true },
            new Bill { Name = "Streaming", Payee = streamCo, Category = entertainment, ExpectedAmount = 15.99m, StartDate = Day(3), PayFromAccount = card, AutoPay = true },
            new Bill { Name = "Car Insurance", Payee = safeDrive, Category = insurance, ExpectedAmount = 540m, Frequency = Frequency.SemiAnnually, StartDate = Day(1).AddMonths(1), PayFromAccount = checking },
            new Bill { Name = "Rewards Card Payment", Payee = rewardsCard, Category = debt, StartDate = Day(22), PayFromAccount = checking, CardAccount = card, CardPaymentRule = CardPaymentRule.StatementBalance }
        ];
    }

    private static DateOnly NextWeekday(DateOnly from, DayOfWeek day) =>
        from.AddDays(((int)day - (int)from.DayOfWeek + 7) % 7);
}
