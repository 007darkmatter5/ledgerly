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
            new ApplicationUser { Id = "alice", UserName = "alice@example.com", Email = "alice@example.com", NormalizedEmail = "ALICE@EXAMPLE.COM" },
            new ApplicationUser { Id = "bob", UserName = "bob@example.com", Email = "bob@example.com", NormalizedEmail = "BOB@EXAMPLE.COM" },
            new ApplicationUser { Id = "carol", UserName = "carol@example.com", Email = "carol@example.com", NormalizedEmail = "CAROL@EXAMPLE.COM" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private readonly SharingNotifier _notifier = new();

    private LedgerService ServiceFor(string userId) => new(_factory, new FixedUser(userId), TimeProvider.System, notifier: _notifier);

    private static Account Checking(string name = "Checking") =>
        new() { Name = name, Type = AccountType.Checking, Balance = 1000m, BalanceAsOf = new DateOnly(2026, 9, 1) };

    private static Transfer NewTransfer(string name, int from, int to) =>
        new() { Name = name, Amount = 200m, StartDate = new DateOnly(2026, 9, 5), FromAccountId = from, ToAccountId = to };

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
    public async Task Marking_bills_paid_in_bulk_keeps_recorded_details_and_only_touches_own_bills()
    {
        var alice = ServiceFor("alice");
        var today = alice.Today;
        var rent = new Bill { Name = "Rent", ExpectedAmount = 1200m, StartDate = today.AddMonths(-3) };
        var water = new Bill { Name = "Water", ExpectedAmount = 30m, StartDate = today.AddMonths(-3) };
        await alice.SaveBillAsync(rent);
        await alice.SaveBillAsync(water);
        var dueRent = today.AddMonths(-1);
        var alreadyPaid = today.AddMonths(-2);
        await alice.RecordOccurrenceAsync(rent.Id, dueRent, 1250m, null, "Includes late fee");
        await alice.RecordOccurrenceAsync(rent.Id, alreadyPaid, null, alreadyPaid.AddDays(3));

        await alice.MarkPaidAsync([(rent.Id, dueRent), (rent.Id, alreadyPaid), (water.Id, dueRent), (water.Id, dueRent)]);

        var bills = await alice.GetBillsAsync();
        var marked = bills.Single(b => b.Name == "Rent").Occurrences.Single(o => o.DueDate == dueRent);
        Assert.Equal((dueRent, 1250m, "Includes late fee"), (marked.PaidOn!.Value, marked.Amount!.Value, marked.Notes!));
        Assert.Equal(alreadyPaid.AddDays(3), bills.Single(b => b.Name == "Rent").Occurrences.Single(o => o.DueDate == alreadyPaid).PaidOn);
        Assert.Equal(dueRent, Assert.Single(bills.Single(b => b.Name == "Water").Occurrences).PaidOn);

        var bob = ServiceFor("bob");
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.MarkPaidAsync([(water.Id, today)]));
        Assert.Single((await alice.GetBillsAsync()).Single(b => b.Name == "Water").Occurrences);
    }

    [Fact]
    public async Task Card_payments_must_pay_a_credit_card_from_a_bank_account_in_the_same_ledger()
    {
        var alice = ServiceFor("alice");
        var checking = Checking();
        var card = new Account { Name = "Visa", Type = AccountType.CreditCard, Balance = -400m, BalanceAsOf = new DateOnly(2026, 9, 1), StatementDay = 25, CreditLimit = 3000m };
        var otherCard = new Account { Name = "Amex", Type = AccountType.CreditCard, BalanceAsOf = new DateOnly(2026, 9, 1) };
        await alice.SaveAccountAsync(checking);
        await alice.SaveAccountAsync(card);
        await alice.SaveAccountAsync(otherCard);
        Bill Payment(int? from, int cardId) => new() { Name = "Visa payment", StartDate = new DateOnly(2026, 10, 20), PayFromAccountId = from, CardAccountId = cardId };

        await alice.SaveBillAsync(Payment(checking.Id, card.Id));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveBillAsync(Payment(otherCard.Id, card.Id)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveBillAsync(Payment(checking.Id, checking.Id)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveBillAsync(Payment(card.Id, card.Id)));

        var bob = ServiceFor("bob");
        var bobChecking = Checking("Bob checking");
        await bob.SaveAccountAsync(bobChecking);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveBillAsync(Payment(bobChecking.Id, card.Id)));

        // Loaded bills carry the payment estimate worked out from the card.
        var payment = Assert.Single(await alice.GetBillsAsync());
        Assert.Equal(400m, payment.PaymentEstimates![new DateOnly(2026, 10, 20)]);

        // Card details are cleared if the account stops being a card, and must make sense on a card.
        card.StatementDay = 32;
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveAccountAsync(card));
        checking.CreditLimit = 1000m;
        await alice.SaveAccountAsync(checking);
        Assert.Null((await alice.GetAccountsAsync()).Single(a => a.Id == checking.Id).CreditLimit);
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
    public async Task Transfers_are_private_and_must_move_money_between_two_of_the_ledgers_own_bank_accounts()
    {
        var alice = ServiceFor("alice");
        var checking = Checking("Alice checking");
        var savings = new Account { Name = "Alice savings", Type = AccountType.Savings, Balance = 100m, BalanceAsOf = new DateOnly(2026, 9, 1) };
        var card = new Account { Name = "Alice card", Type = AccountType.CreditCard, Balance = -50m, BalanceAsOf = new DateOnly(2026, 9, 1) };
        await alice.SaveAccountAsync(checking);
        await alice.SaveAccountAsync(savings);
        await alice.SaveAccountAsync(card);

        var transfer = NewTransfer(" To savings ", checking.Id, savings.Id);
        await alice.SaveTransferAsync(transfer);
        Assert.Equal("To savings", Assert.Single(await alice.GetTransfersAsync()).Name);

        // The same account at both ends, a blank name, and a credit card at either end are all rejected.
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveTransferAsync(NewTransfer("Loop", checking.Id, checking.Id)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveTransferAsync(NewTransfer("   ", checking.Id, savings.Id)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveTransferAsync(NewTransfer("Card", checking.Id, card.Id)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveTransferAsync(NewTransfer("Cash advance", card.Id, checking.Id)));

        // Bob can't see it, point one at Alice's accounts, edit hers, record against it, or delete it.
        var bob = ServiceFor("bob");
        Assert.Empty(await bob.GetTransfersAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveTransferAsync(NewTransfer("Sneaky", checking.Id, savings.Id)));

        var hijack = transfer.Copy();
        hijack.Name = "Hacked";
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveTransferAsync(hijack));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bob.RecordTransferOccurrenceAsync(transfer.Id, new DateOnly(2026, 9, 5), 10m, null));
        await bob.DeleteTransferAsync(transfer.Id);

        Assert.Equal(["To savings"], (await ServiceFor("alice").GetTransfersAsync()).Select(t => t.Name));
    }

    [Fact]
    public async Task Recording_a_transfer_keeps_one_row_per_date_and_clears_it_when_nothing_is_left()
    {
        var alice = ServiceFor("alice");
        var checking = Checking();
        var savings = new Account { Name = "Savings", Type = AccountType.Savings, Balance = 0m, BalanceAsOf = new DateOnly(2026, 9, 1) };
        await alice.SaveAccountAsync(checking);
        await alice.SaveAccountAsync(savings);

        var transfer = NewTransfer("To savings", checking.Id, savings.Id);
        await alice.SaveTransferAsync(transfer);

        var date = new DateOnly(2026, 9, 5);
        await alice.RecordTransferOccurrenceAsync(transfer.Id, date, 250m, date.AddDays(1), "Bonus");
        await alice.RecordTransferOccurrenceAsync(transfer.Id, date, 275m, date.AddDays(1), "Bonus");

        var occurrence = Assert.Single(Assert.Single(await alice.GetTransfersAsync()).Occurrences);
        Assert.Equal(275m, occurrence.Amount);
        Assert.Equal(date.AddDays(1), occurrence.CompletedOn);

        await alice.RecordTransferOccurrenceAsync(transfer.Id, date, null, null, null);
        Assert.Empty(Assert.Single(await alice.GetTransfersAsync()).Occurrences);
    }

    [Fact]
    public async Task Deleting_an_account_deletes_the_transfers_that_need_it()
    {
        var alice = ServiceFor("alice");
        var checking = Checking();
        var savings = new Account { Name = "Savings", Type = AccountType.Savings, Balance = 0m, BalanceAsOf = new DateOnly(2026, 9, 1) };
        await alice.SaveAccountAsync(checking);
        await alice.SaveAccountAsync(savings);

        var transfer = NewTransfer("To savings", checking.Id, savings.Id);
        await alice.SaveTransferAsync(transfer);
        await alice.RecordTransferOccurrenceAsync(transfer.Id, new DateOnly(2026, 9, 5), null, new DateOnly(2026, 9, 5));

        await alice.DeleteAccountAsync(savings.Id);

        Assert.Empty(await alice.GetTransfersAsync());
    }

    private static Transaction NewTransaction(string description, int accountId, decimal amount = 42.50m) =>
        new() { Description = description, Amount = amount, Date = new DateOnly(2026, 9, 5), AccountId = accountId };

    [Fact]
    public async Task Transactions_are_private_and_validated()
    {
        var alice = ServiceFor("alice");
        var checking = Checking("Alice checking");
        var card = new Account { Name = "Alice card", Type = AccountType.CreditCard, Balance = -50m, BalanceAsOf = new DateOnly(2026, 9, 1) };
        await alice.SaveAccountAsync(checking);
        await alice.SaveAccountAsync(card);

        var dinner = NewTransaction(" Dinner out ", card.Id);
        dinner.Notes = "   ";
        await alice.SaveTransactionAsync(dinner);
        var saved = Assert.Single(await alice.GetTransactionsAsync());
        Assert.Equal(("Dinner out", (string?)null, "Alice card"), (saved.Description, saved.Notes, saved.Account?.Name));
        Assert.Equal(card.Id, await alice.GetLastTransactionAccountIdAsync());

        // A blank description, a zero or negative amount, and no account are rejected.
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveTransactionAsync(NewTransaction("  ", checking.Id)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveTransactionAsync(NewTransaction("Free", checking.Id, 0m)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveTransactionAsync(NewTransaction("Negative", checking.Id, -5m)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SaveTransactionAsync(NewTransaction("Nowhere", 0)));

        // Bob can't see it, log one on Alice's account or under her category or payee, edit hers, or delete it.
        var bob = ServiceFor("bob");
        var bobChecking = Checking("Bob checking");
        await bob.SaveAccountAsync(bobChecking);
        var category = new Category { Name = "Dining" };
        var payee = new Payee { Name = "Luigi's" };
        await alice.SaveCategoryAsync(category);
        await alice.SavePayeeAsync(payee);

        Assert.Empty(await bob.GetTransactionsAsync());
        Assert.Null(await bob.GetLastTransactionAccountIdAsync());
        Assert.Equal(0, await bob.CountTransactionsAsync(card.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveTransactionAsync(NewTransaction("Sneaky", checking.Id)));
        var underHerCategory = NewTransaction("Sneaky", bobChecking.Id);
        underHerCategory.CategoryId = category.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveTransactionAsync(underHerCategory));
        var toHerPayee = NewTransaction("Sneaky", bobChecking.Id);
        toHerPayee.PayeeId = payee.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveTransactionAsync(toHerPayee));

        var hijack = dinner.Copy();
        hijack.AccountId = bobChecking.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveTransactionAsync(hijack));
        await bob.DeleteTransactionAsync(dinner.Id);

        Assert.Equal(["Dinner out"], (await ServiceFor("alice").GetTransactionsAsync()).Select(t => t.Description));
        Assert.Equal(1, await ServiceFor("alice").CountTransactionsAsync(card.Id));
    }

    [Fact]
    public async Task Transactions_can_be_filtered_by_date_and_are_newest_first()
    {
        var alice = ServiceFor("alice");
        var checking = Checking();
        await alice.SaveAccountAsync(checking);
        foreach (var day in new[] { 3, 20, 11 })
        {
            var transaction = NewTransaction($"Day {day}", checking.Id);
            transaction.Date = new DateOnly(2026, 9, day);
            await alice.SaveTransactionAsync(transaction);
        }

        Assert.Equal(["Day 20", "Day 11", "Day 3"], (await alice.GetTransactionsAsync()).Select(t => t.Description));
        Assert.Equal(["Day 11", "Day 3"],
            (await alice.GetTransactionsAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 11))).Select(t => t.Description));
    }

    [Fact]
    public async Task Deleting_a_category_or_payee_keeps_transactions_and_deleting_the_account_removes_them()
    {
        var alice = ServiceFor("alice");
        var checking = Checking();
        var category = new Category { Name = "Dining" };
        var payee = new Payee { Name = "Luigi's" };
        await alice.SaveAccountAsync(checking);
        await alice.SaveCategoryAsync(category);
        await alice.SavePayeeAsync(payee);
        var dinner = NewTransaction("Dinner out", checking.Id);
        (dinner.CategoryId, dinner.PayeeId) = (category.Id, payee.Id);
        await alice.SaveTransactionAsync(dinner);

        await alice.DeleteCategoryAsync(category.Id);
        await alice.DeletePayeeAsync(payee.Id);
        var kept = Assert.Single(await alice.GetTransactionsAsync());
        Assert.Equal(((int?)null, (int?)null), (kept.CategoryId, kept.PayeeId));

        await alice.DeleteAccountAsync(checking.Id);
        Assert.Empty(await alice.GetTransactionsAsync());
    }

    [Fact]
    public async Task Card_payment_estimates_include_transactions_charged_to_the_card()
    {
        var alice = ServiceFor("alice");
        var today = alice.Today;
        var checking = Checking();
        checking.BalanceAsOf = today;
        var card = new Account { Name = "Card", Type = AccountType.CreditCard, Balance = 0m, BalanceAsOf = today, StatementDay = today.AddDays(5).Day };
        await alice.SaveAccountAsync(checking);
        await alice.SaveAccountAsync(card);
        await alice.SaveBillAsync(new Bill
        {
            Name = "Card payment", StartDate = today.AddDays(20), PayFromAccountId = checking.Id,
            CardAccountId = card.Id, CardPaymentRule = CardPaymentRule.StatementBalance
        });
        var dinner = NewTransaction("Dinner out", card.Id, 80m);
        dinner.Date = today;
        await alice.SaveTransactionAsync(dinner);

        var payment = Assert.Single(await alice.GetBillsAsync());
        Assert.Equal(80m, payment.PaymentEstimates![today.AddDays(20)]);
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

    // Sharing

    /// <summary>Alice's ledger with a checking account and a rent bill, shared with and accepted by Bob.</summary>
    private async Task<(Account Checking, Bill Rent)> AliceSharesWithBobAsync()
    {
        var alice = ServiceFor("alice");
        var checking = Checking("Alice checking");
        await alice.SaveAccountAsync(checking);
        var rent = new Bill { Name = "Alice rent", ExpectedAmount = 900m, StartDate = new DateOnly(2026, 9, 1), PayFromAccountId = checking.Id };
        await alice.SaveBillAsync(rent);
        await alice.ShareAsync("bob@example.com");

        var bob = ServiceFor("bob");
        await bob.RespondToInvitationAsync(Assert.Single(await bob.GetInvitationsAsync()).MemberId, accept: true);
        return (checking, rent);
    }

    [Fact]
    public async Task Sharing_needs_an_existing_account_and_shows_nothing_until_accepted()
    {
        var alice = ServiceFor("alice");
        await alice.SaveBillAsync(new Bill { Name = "Alice rent", ExpectedAmount = 900m, StartDate = new DateOnly(2026, 9, 1) });

        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.ShareAsync("  "));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.ShareAsync("nobody@example.com"));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.ShareAsync("ALICE@example.com"));

        var notified = new List<string>();
        _notifier.Changed += notified.Add;
        await alice.ShareAsync(" Bob@Example.com ");
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.ShareAsync("bob@example.com"));
        Assert.Equal(["bob"], notified);

        // Bob sees an invitation, but none of Alice's data yet.
        var bob = ServiceFor("bob");
        var invitation = Assert.Single(await bob.GetInvitationsAsync());
        Assert.Equal(("alice@example.com", LedgerRole.Viewer), (invitation.OwnerName, invitation.Role));
        Assert.Empty((await bob.GetScopeAsync()).Shared);
        Assert.Empty(await bob.GetBillsAsync(includeShared: true));
        var share = Assert.Single(await alice.GetSharesAsync());
        Assert.Equal(("bob@example.com", false), (share.Email, share.IsAccepted));

        await bob.RespondToInvitationAsync(invitation.MemberId, accept: true);
        Assert.Equal(["bob", "alice"], notified);

        var shared = Assert.Single((await bob.GetScopeAsync()).Shared);
        Assert.Equal(("alice's ledger", "alice@example.com", LedgerService.LayerColors[0], true, true), (shared.Name, shared.OwnerName, shared.Color, shared.IsVisible, shared.IsReadOnly));
        Assert.Equal(["Alice rent"], (await bob.GetBillsAsync(includeShared: true)).Select(b => b.Name));
        Assert.Empty(await bob.GetBillsAsync());
        Assert.Empty(await bob.GetInvitationsAsync());
        Assert.True(Assert.Single(await alice.GetSharesAsync()).IsAccepted);

        // Carol, who it isn't shared with, still sees nothing.
        Assert.Empty(await ServiceFor("carol").GetBillsAsync(includeShared: true));
    }

    [Fact]
    public async Task A_shared_ledger_is_read_only()
    {
        var (aliceChecking, rent) = await AliceSharesWithBobAsync();
        var bob = ServiceFor("bob");
        var bobChecking = Checking("Bob checking");
        await bob.SaveAccountAsync(bobChecking);

        var hijack = rent.Copy();
        hijack.ExpectedAmount = 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveBillAsync(hijack));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.RecordOccurrenceAsync(rent.Id, new DateOnly(2026, 9, 1), 1m, new DateOnly(2026, 9, 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.MarkPaidAsync([(rent.Id, new DateOnly(2026, 9, 1))]));
        await bob.DeleteBillAsync(rent.Id);
        await bob.DeleteAccountAsync(aliceChecking.Id);

        // Seeing Alice's account doesn't let Bob point his own things at it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveBillAsync(
            new Bill { Name = "Sneaky", ExpectedAmount = 1m, StartDate = new DateOnly(2026, 9, 1), PayFromAccountId = aliceChecking.Id }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveTransactionAsync(NewTransaction("Sneaky", aliceChecking.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.SaveTransferAsync(NewTransfer("Sneaky", bobChecking.Id, aliceChecking.Id)));

        var alice = ServiceFor("alice");
        var bill = Assert.Single(await alice.GetBillsAsync());
        Assert.Equal((900m, 0), (bill.ExpectedAmount, bill.Occurrences.Count));
        Assert.Single(await alice.GetAccountsAsync());
    }

    [Fact]
    public async Task Shared_ledgers_can_be_switched_off_and_access_ends_when_either_side_stops()
    {
        await AliceSharesWithBobAsync();
        var bob = ServiceFor("bob");
        var memberId = Assert.Single((await bob.GetScopeAsync()).Shared).MemberId;

        await bob.SetSharedVisibleAsync(memberId, false);
        Assert.False(Assert.Single((await bob.GetScopeAsync()).Shared).IsVisible);
        Assert.Empty(await bob.GetBillsAsync(includeShared: true));
        Assert.Empty(await bob.GetAccountsAsync(includeShared: true));

        await bob.SetSharedVisibleAsync(null, true);
        Assert.Single(await bob.GetBillsAsync(includeShared: true));

        // Alice stops sharing: Bob loses access at once.
        var alice = ServiceFor("alice");
        await alice.StopSharingAsync(Assert.Single(await alice.GetSharesAsync()).MemberId);
        Assert.Empty((await bob.GetScopeAsync()).Shared);
        Assert.Empty(await bob.GetBillsAsync(includeShared: true));

        // Shared again, Bob declines; shared again, Bob accepts and then leaves.
        await alice.ShareAsync("bob@example.com");
        await bob.RespondToInvitationAsync(Assert.Single(await bob.GetInvitationsAsync()).MemberId, accept: false);
        Assert.Empty(await alice.GetSharesAsync());

        await alice.ShareAsync("bob@example.com");
        await bob.RespondToInvitationAsync(Assert.Single(await bob.GetInvitationsAsync()).MemberId, accept: true);
        await bob.LeaveSharedLedgerAsync(Assert.Single((await bob.GetScopeAsync()).Shared).MemberId);
        Assert.Empty(await alice.GetSharesAsync());
        Assert.Empty(await bob.GetBillsAsync(includeShared: true));
    }

    [Fact]
    public async Task Only_the_person_a_ledger_is_shared_with_can_answer_or_change_it()
    {
        var alice = ServiceFor("alice");
        await alice.SaveBillAsync(new Bill { Name = "Alice rent", ExpectedAmount = 900m, StartDate = new DateOnly(2026, 9, 1) });
        await alice.ShareAsync("bob@example.com");
        var memberId = Assert.Single(await alice.GetSharesAsync()).MemberId;

        // Carol can't accept Bob's invitation, and Alice can't accept on his behalf.
        var carol = ServiceFor("carol");
        await carol.RespondToInvitationAsync(memberId, accept: true);
        await alice.RespondToInvitationAsync(memberId, accept: true);
        Assert.False(Assert.Single(await alice.GetSharesAsync()).IsAccepted);
        Assert.Empty(await carol.GetBillsAsync(includeShared: true));

        var bob = ServiceFor("bob");
        await bob.RespondToInvitationAsync(memberId, accept: true);

        // Nobody else can switch it on or off, rename it, leave it for him, or stop someone else's sharing.
        await carol.SetSharedVisibleAsync(memberId, false);
        await carol.UpdateSharedLedgerAsync(memberId, "Mine now", null);
        await carol.LeaveSharedLedgerAsync(memberId);
        await carol.StopSharingAsync(memberId);
        await bob.StopSharingAsync(memberId);
        var shared = Assert.Single((await bob.GetScopeAsync()).Shared);
        Assert.Equal(("alice's ledger", true), (shared.Name, shared.IsVisible));
    }

    [Fact]
    public async Task Owners_are_named_and_shared_ledgers_can_be_renamed_and_recolored()
    {
        await ServiceFor("alice").SetDisplayNameAsync("  Alice  ");
        await AliceSharesWithBobAsync();
        var bob = ServiceFor("bob");
        var shared = Assert.Single((await bob.GetScopeAsync()).Shared);
        Assert.Equal(("Alice's ledger", "Alice"), (shared.Name, shared.OwnerName));

        await bob.UpdateSharedLedgerAsync(shared.MemberId, " Household ", "#16A085");
        Assert.Equal(("Household", "#16A085"), (Assert.Single((await bob.GetScopeAsync()).Shared) is var s ? (s.Name, s.Color) : default));
        await Assert.ThrowsAsync<LedgerValidationException>(() => bob.UpdateSharedLedgerAsync(shared.MemberId, null, "red; background:url(x)"));

        await bob.UpdateSharedLedgerAsync(shared.MemberId, "  ", "#16A085");
        Assert.Equal("Alice's ledger", Assert.Single((await bob.GetScopeAsync()).Shared).Name);
        await Assert.ThrowsAsync<LedgerValidationException>(() => bob.SetDisplayNameAsync(new string('x', 51)));
    }

    [Fact]
    public async Task Sample_data_is_never_shared_and_hides_shared_ledgers()
    {
        var alice = ServiceFor("alice");
        await alice.SaveAccountAsync(Checking("Alice real checking"));
        await alice.StartSampleAsync();
        await alice.ShareAsync("bob@example.com");

        var bob = ServiceFor("bob");
        await bob.RespondToInvitationAsync(Assert.Single(await bob.GetInvitationsAsync()).MemberId, accept: true);
        Assert.Equal(["Alice real checking"], (await bob.GetAccountsAsync(includeShared: true)).Select(a => a.Name));

        await bob.StartSampleAsync();
        var sampleScope = await bob.GetScopeAsync();
        Assert.False(sampleScope.HasVisibleShared);
        Assert.DoesNotContain(await bob.GetAccountsAsync(includeShared: true), a => a.Name == "Alice real checking");
    }

    [Fact]
    public async Task A_projection_can_include_a_shared_account()
    {
        var (aliceChecking, _) = await AliceSharesWithBobAsync();
        var bob = ServiceFor("bob");

        var result = await bob.ProjectAsync([aliceChecking.Id], new DateOnly(2026, 9, 30), includeShared: true);

        Assert.Equal(1000m, result.StartingBalance);
        Assert.Contains(result.Entries, e => e.Description == "Alice rent" && e.Amount == -900m);
        Assert.Empty((await bob.ProjectAsync([aliceChecking.Id], new DateOnly(2026, 9, 30))).Accounts);
    }

    [Fact]
    public async Task Deleting_either_user_ends_the_sharing()
    {
        await AliceSharesWithBobAsync();
        await using (var db = _factory.CreateDbContext())
            await db.Users.Where(u => u.Id == "bob").ExecuteDeleteAsync();

        await using var check = _factory.CreateDbContext();
        Assert.Equal(0, await check.LedgerMembers.CountAsync());
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
