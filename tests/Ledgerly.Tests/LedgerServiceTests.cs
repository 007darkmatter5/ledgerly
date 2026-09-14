using Ledgerly.Data;
using Ledgerly.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ledgerly.Tests;

/// <summary>Data isolation between users, and between a user's own ledger and their sample ledger.</summary>
public sealed class LedgerServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private TestDbFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new TestDbFactory(new DbContextOptionsBuilder<LedgerlyDbContext>().UseSqlite(_connection).Options);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        db.Users.AddRange(
            new ApplicationUser { Id = "alice", UserName = "alice@example.com", Email = "alice@example.com" },
            new ApplicationUser { Id = "bob", UserName = "bob@example.com", Email = "bob@example.com" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private LedgerService ServiceFor(string userId) => new(_factory, new FixedUser(userId), TimeProvider.System);

    private static Account Checking(string name = "Checking") =>
        new() { Name = name, Type = AccountType.Checking, Balance = 1000m, BalanceAsOf = new DateOnly(2026, 9, 1) };

    [Fact]
    public async Task Users_only_see_their_own_data()
    {
        var alice = ServiceFor("alice");
        var bob = ServiceFor("bob");
        await alice.SaveAccountAsync(Checking("Alice checking"));
        await bob.SaveAccountAsync(Checking("Bob checking"));

        Assert.Equal(["Alice checking"], (await alice.GetAccountsAsync()).Select(a => a.Name));
        Assert.Equal(["Bob checking"], (await bob.GetAccountsAsync()).Select(a => a.Name));
    }

    [Fact]
    public async Task Users_cannot_edit_or_delete_another_users_rows_by_id()
    {
        var alice = ServiceFor("alice");
        var account = Checking("Alice checking");
        await alice.SaveAccountAsync(account);
        var loan = new Loan { Name = "Alice car", OriginalPrincipal = 1000m, TermMonths = 12, FirstPaymentDate = new DateOnly(2026, 1, 1) };
        await alice.SaveLoanAsync(loan);

        var bob = ServiceFor("bob");
        var hijack = account.Copy();
        hijack.Name = "Hacked";

        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveAccountAsync(hijack));
        await bob.DeleteAccountAsync(account.Id);
        Assert.Null(await bob.GetLoanAsync(loan.Id));

        var aliceAccounts = await ServiceFor("alice").GetAccountsAsync();
        Assert.Equal(["Alice checking"], aliceAccounts.Select(a => a.Name));
    }

    [Fact]
    public async Task Users_cannot_point_a_bill_at_another_users_account()
    {
        var aliceAccount = Checking();
        await ServiceFor("alice").SaveAccountAsync(aliceAccount);

        var bill = new Bill { Name = "Sneaky", ExpectedAmount = 10m, StartDate = new DateOnly(2026, 9, 1), PayFromAccountId = aliceAccount.Id };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceFor("bob").SaveBillAsync(bill));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceFor("bob").RecordOccurrenceAsync(999, new DateOnly(2026, 9, 1), 5m, null));
    }

    [Fact]
    public async Task Recording_an_extra_loan_payment_creates_a_paid_extra_bill_in_the_same_ledger()
    {
        var alice = ServiceFor("alice");
        var account = Checking();
        await alice.SaveAccountAsync(account);
        var loan = new Loan { Name = "Car", OriginalPrincipal = 20_000m, AnnualRatePercent = 5m, TermMonths = 60, FirstPaymentDate = new DateOnly(2026, 1, 15) };
        await alice.SaveLoanAsync(loan);

        await alice.RecordExtraLoanPaymentAsync(loan.Id, account.Id, 750m, new DateOnly(2026, 9, 2), "Bonus");

        var bill = Assert.Single(await alice.GetBillsAsync());
        Assert.Equal(LoanPaymentKind.ExtraPrincipal, bill.LoanPaymentKind);
        Assert.Equal(loan.Id, bill.LoanId);
        Assert.Equal(account.Id, bill.PayFromAccountId);
        var occurrence = Assert.Single(bill.Occurrences);
        Assert.Equal(new DateOnly(2026, 9, 2), occurrence.PaidOn);
        Assert.Equal(750m, occurrence.Amount);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceFor("bob").RecordExtraLoanPaymentAsync(loan.Id, null, 10m, new DateOnly(2026, 9, 2), null));
    }

    [Fact]
    public async Task Payment_type_override_is_saved_per_payment()
    {
        var alice = ServiceFor("alice");
        var loan = new Loan { Name = "Car", OriginalPrincipal = 20_000m, AnnualRatePercent = 5m, TermMonths = 60, FirstPaymentDate = new DateOnly(2026, 1, 15) };
        await alice.SaveLoanAsync(loan);
        var bill = new Bill { Name = "Car payment", ExpectedAmount = 377.42m, StartDate = new DateOnly(2026, 1, 15), LoanId = loan.Id };
        await alice.SaveBillAsync(bill);

        await alice.RecordOccurrenceAsync(bill.Id, new DateOnly(2026, 9, 15), 500m, new DateOnly(2026, 9, 15), null, LoanPaymentKind.ExtraPrincipal);

        var saved = Assert.Single(Assert.Single(await alice.GetBillsAsync()).Occurrences);
        Assert.Equal(LoanPaymentKind.ExtraPrincipal, saved.PaymentKind);
    }

    [Fact]
    public async Task Payee_website_is_normalized_and_unsafe_links_are_rejected()
    {
        var alice = ServiceFor("alice");
        var payee = new Payee { Name = " My Credit Union ", WebsiteUrl = "mycreditunion.org/pay", Notes = "  Member services 555-0100 " };
        await alice.SavePayeeAsync(payee);

        var saved = Assert.Single(await alice.GetPayeesAsync());
        Assert.Equal("My Credit Union", saved.Name);
        Assert.Equal("https://mycreditunion.org/pay", saved.WebsiteUrl);
        Assert.Equal("Member services 555-0100", saved.Notes);

        payee.WebsiteUrl = "javascript:alert(document.cookie)";
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SavePayeeAsync(payee));
        Assert.Equal("https://mycreditunion.org/pay", Assert.Single(await alice.GetPayeesAsync()).WebsiteUrl);

        payee.WebsiteUrl = "  ";
        await alice.SavePayeeAsync(payee);
        Assert.Null(Assert.Single(await alice.GetPayeesAsync()).WebsiteUrl);
    }

    [Fact]
    public async Task Payees_are_private_and_unique_per_ledger()
    {
        var alice = ServiceFor("alice");
        var bank = new Payee { Name = "First Bank" };
        await alice.SavePayeeAsync(bank);
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SavePayeeAsync(new Payee { Name = "first bank" }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SavePayeeAsync(new Payee { Name = " " }));

        var bob = ServiceFor("bob");
        await bob.SavePayeeAsync(new Payee { Name = "First Bank" });
        Assert.Single(await bob.GetPayeesAsync());

        var hijack = bank.Copy();
        hijack.Name = "Hijacked";
        hijack.WebsiteUrl = "https://evil.example.com";
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SavePayeeAsync(hijack));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveBillAsync(
            new Bill { Name = "Sneaky", ExpectedAmount = 1m, StartDate = new DateOnly(2026, 9, 1), PayeeId = bank.Id }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveLoanAsync(
            new Loan { Name = "Sneaky", OriginalPrincipal = 1m, TermMonths = 12, FirstPaymentDate = new DateOnly(2026, 9, 1), LenderId = bank.Id }));
        Assert.Null(Assert.Single(await ServiceFor("alice").GetPayeesAsync()).WebsiteUrl);
    }

    [Fact]
    public async Task Running_balance_view_is_remembered_per_ledger_and_ignores_other_users_accounts()
    {
        var alice = ServiceFor("alice");
        var checking = new Account { Name = "Checking" };
        var savings = new Account { Name = "Savings" };
        await alice.SaveAccountAsync(checking);
        await alice.SaveAccountAsync(savings);

        var bob = ServiceFor("bob");
        var bobs = new Account { Name = "Bob's" };
        await bob.SaveAccountAsync(bobs);

        await alice.SaveProjectionViewAsync([savings.Id, checking.Id, bobs.Id], 90, worstCase: true);

        var ledger = await ServiceFor("alice").GetActiveLedgerAsync();
        Assert.Equal($"{Math.Min(checking.Id, savings.Id)},{Math.Max(checking.Id, savings.Id)}", ledger.ProjectionAccountIds);
        Assert.Equal(90, ledger.ProjectionDays);
        Assert.True(ledger.ProjectionWorstCase);
        Assert.Null((await ServiceFor("bob").GetActiveLedgerAsync()).ProjectionAccountIds);

        await alice.StartSampleAsync();
        Assert.Null((await alice.GetActiveLedgerAsync()).ProjectionAccountIds);
    }

    [Fact]
    public async Task Deleting_a_payee_keeps_its_bills_and_loans()
    {
        var alice = ServiceFor("alice");
        var bank = new Payee { Name = "First Bank", WebsiteUrl = "https://firstbank.example.com" };
        await alice.SavePayeeAsync(bank);
        var loan = new Loan { Name = "Car", OriginalPrincipal = 20_000m, AnnualRatePercent = 5m, TermMonths = 60, FirstPaymentDate = new DateOnly(2026, 1, 15), LenderId = bank.Id };
        await alice.SaveLoanAsync(loan);
        await alice.SaveBillAsync(new Bill { Name = "Car payment", ExpectedAmount = 377.42m, StartDate = new DateOnly(2026, 1, 15), LoanId = loan.Id, PayeeId = bank.Id });

        Assert.Equal("https://firstbank.example.com/", (await alice.GetLoanAsync(loan.Id))!.Lender?.WebsiteUrl);
        var bill = Assert.Single(await alice.GetBillsAsync());
        Assert.Equal("First Bank", bill.Payee?.Name);
        Assert.Equal("First Bank", bill.Loan?.Lender?.Name);

        await alice.DeletePayeeAsync(bank.Id);

        Assert.Null((await alice.GetLoanAsync(loan.Id))!.LenderId);
        Assert.Null(Assert.Single(await alice.GetBillsAsync()).PayeeId);
    }

    [Fact]
    public async Task Extra_loan_payment_bill_uses_the_lender_as_payee()
    {
        var alice = ServiceFor("alice");
        var bank = new Payee { Name = "First Bank" };
        await alice.SavePayeeAsync(bank);
        var loan = new Loan { Name = "Car", OriginalPrincipal = 20_000m, AnnualRatePercent = 5m, TermMonths = 60, FirstPaymentDate = new DateOnly(2026, 1, 15), LenderId = bank.Id };
        await alice.SaveLoanAsync(loan);

        await alice.RecordExtraLoanPaymentAsync(loan.Id, null, 500m, new DateOnly(2026, 9, 1), null);

        Assert.Equal(bank.Id, Assert.Single(await alice.GetBillsAsync()).PayeeId);
    }

    [Fact]
    public async Task Categories_are_private_unique_per_ledger_and_trimmed()
    {
        var alice = ServiceFor("alice");
        var utilities = new Category { Name = "  Utilities ", Color = "#F39C12" };
        await alice.SaveCategoryAsync(utilities);

        Assert.Equal("Utilities", utilities.Name);
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveCategoryAsync(new Category { Name = "utilities" }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveCategoryAsync(new Category { Name = "   " }));

        // Renaming a category to its own name (different case) is fine.
        utilities.Name = "UTILITIES";
        await alice.SaveCategoryAsync(utilities);

        // Another user can have a category with the same name, and can't see or edit Alice's.
        var bob = ServiceFor("bob");
        await bob.SaveCategoryAsync(new Category { Name = "Utilities" });
        Assert.Equal(["Utilities"], (await bob.GetCategoriesAsync()).Select(c => c.Name));
        var hijack = utilities.Copy();
        hijack.Name = "Hacked";
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveCategoryAsync(hijack));
        Assert.Equal(["UTILITIES"], (await ServiceFor("alice").GetCategoriesAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task Deleting_a_category_keeps_its_bills_uncategorized()
    {
        var alice = ServiceFor("alice");
        var housing = new Category { Name = "Housing" };
        await alice.SaveCategoryAsync(housing);
        await alice.SaveBillAsync(new Bill { Name = "Rent", ExpectedAmount = 900m, StartDate = new DateOnly(2026, 9, 1), CategoryId = housing.Id });

        Assert.Equal("Housing", Assert.Single(await alice.GetBillsAsync()).Category?.Name);

        await alice.DeleteCategoryAsync(housing.Id);

        var bill = Assert.Single(await alice.GetBillsAsync());
        Assert.Null(bill.CategoryId);
        Assert.Empty(await alice.GetCategoriesAsync());
    }

    [Fact]
    public async Task Users_cannot_file_a_bill_under_another_users_category()
    {
        var aliceCategory = new Category { Name = "Secret" };
        await ServiceFor("alice").SaveCategoryAsync(aliceCategory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceFor("bob").SaveBillAsync(
            new Bill { Name = "Sneaky", ExpectedAmount = 1m, StartDate = new DateOnly(2026, 9, 1), CategoryId = aliceCategory.Id }));
    }

    [Fact]
    public async Task Sample_data_includes_categories_for_its_bills()
    {
        var alice = ServiceFor("alice");
        await alice.StartSampleAsync();

        var sample = ServiceFor("alice");
        var categories = await sample.GetCategoriesAsync();
        Assert.Contains(categories, c => c.Name == "Utilities");
        Assert.All(await sample.GetBillsAsync(), b => Assert.Contains(categories, c => c.Id == b.CategoryId));

        var payees = await sample.GetPayeesAsync();
        Assert.All(await sample.GetBillsAsync(), b => Assert.Contains(payees, p => p.Id == b.PayeeId));
        Assert.All(await sample.GetLoansAsync(), l => Assert.Contains(payees, p => p.Id == l.LenderId));
    }

    [Fact]
    public async Task Sample_data_lives_in_its_own_ledger()
    {
        var alice = ServiceFor("alice");
        await alice.SaveAccountAsync(Checking("My real checking"));

        await alice.StartSampleAsync();

        var inSample = ServiceFor("alice"); // fresh instance, like a page reload
        Assert.True((await inSample.GetActiveLedgerAsync()).IsSample);
        var sampleAccounts = (await inSample.GetAccountsAsync()).Select(a => a.Name).ToList();
        Assert.Contains("Everyday Checking", sampleAccounts);
        Assert.DoesNotContain("My real checking", sampleAccounts);

        await inSample.SwitchLedgerAsync(sample: false);
        var real = ServiceFor("alice");
        Assert.False((await real.GetActiveLedgerAsync()).IsSample);
        Assert.Equal(["My real checking"], (await real.GetAccountsAsync()).Select(a => a.Name));
        Assert.Empty(await real.GetBillsAsync());
    }

    [Fact]
    public async Task Deleting_sample_data_removes_all_of_it_and_keeps_real_data()
    {
        var alice = ServiceFor("alice");
        var realAccount = Checking("My real checking");
        await alice.SaveAccountAsync(realAccount);
        await alice.SaveBillAsync(new Bill { Name = "Rent", ExpectedAmount = 900m, StartDate = new DateOnly(2026, 9, 1), PayFromAccountId = realAccount.Id });

        await alice.StartSampleAsync();
        var sample = ServiceFor("alice");
        var sampleBill = (await sample.GetBillsAsync()).First();
        await sample.RecordOccurrenceAsync(sampleBill.Id, sampleBill.StartDate, 1m, sampleBill.StartDate);
        await sample.DeleteSampleAsync();

        Assert.False(await ServiceFor("alice").HasSampleLedgerAsync());
        await using (var db = _factory.CreateDbContext())
        {
            var sampleIds = await db.Ledgers.Where(l => l.IsSample).Select(l => l.Id).ToListAsync();
            Assert.Empty(sampleIds);
            Assert.Equal(1, await db.Accounts.CountAsync());
            Assert.Equal(1, await db.Bills.CountAsync());
            Assert.Equal(0, await db.Loans.CountAsync());
            Assert.Equal(0, await db.Incomes.CountAsync());
            Assert.Equal(0, await db.BillOccurrences.CountAsync());
        }

        var real = ServiceFor("alice");
        Assert.False((await real.GetActiveLedgerAsync()).IsSample);
        Assert.Equal(["Rent"], (await real.GetBillsAsync()).Select(b => b.Name));
    }

    [Fact]
    public async Task Starting_sample_over_replaces_it_with_a_fresh_copy()
    {
        var alice = ServiceFor("alice");
        await alice.StartSampleAsync();
        var sample = ServiceFor("alice");
        foreach (var bill in await sample.GetBillsAsync())
            await sample.DeleteBillAsync(bill.Id);
        Assert.Empty(await sample.GetBillsAsync());

        await sample.StartSampleAsync();

        Assert.NotEmpty(await ServiceFor("alice").GetBillsAsync());
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Ledgers.CountAsync(l => l.OwnerId == "alice" && l.IsSample));
    }

    [Fact]
    public async Task Deleting_a_user_deletes_their_ledgers_and_data()
    {
        var alice = ServiceFor("alice");
        await alice.SaveAccountAsync(Checking());
        await alice.StartSampleAsync();
        await ServiceFor("bob").SaveAccountAsync(Checking("Bob checking"));

        await using (var db = _factory.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == "alice").ExecuteDeleteAsync();
        }

        await using var check = _factory.CreateDbContext();
        Assert.Equal(0, await check.Ledgers.CountAsync(l => l.OwnerId == "alice"));
        Assert.Equal(["Bob checking"], await check.Accounts.Select(a => a.Name).ToListAsync());
        Assert.Equal(0, await check.Bills.CountAsync());
    }

    [Fact]
    public async Task Signed_out_access_is_refused()
    {
        var anonymous = new LedgerService(_factory, new FixedUser(null), TimeProvider.System);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => anonymous.GetAccountsAsync());
    }

    private sealed class FixedUser(string? userId) : ICurrentUser
    {
        public Task<string?> GetUserIdAsync() => Task.FromResult(userId);
    }

    private sealed class TestDbFactory(DbContextOptions<LedgerlyDbContext> options) : IDbContextFactory<LedgerlyDbContext>
    {
        public LedgerlyDbContext CreateDbContext() => new(options);
    }
}
