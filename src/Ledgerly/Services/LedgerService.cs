using Ledgerly.Data;
using Ledgerly.Finance;
using Microsoft.EntityFrameworkCore;

namespace Ledgerly.Services;

/// <summary>
/// Data access for the app. Reads default to the signed-in user's active ledger and can also include ledgers shared
/// with them (see <see cref="LedgerScope"/>). Writes go to the ledger the item belongs to (new items: its
/// <see cref="ILedgerEntity.LedgerId"/>, 0 meaning the active ledger), only if the user's role there allows it, and
/// everything the item refers to must be in that same ledger.
/// Each call uses its own short-lived DbContext.
/// </summary>
public class LedgerService(IDbContextFactory<LedgerlyDbContext> dbFactory, ICurrentUser currentUser, TimeProvider clock, DataGeneration? dataGeneration = null,
    SharingNotifier? notifier = null)
{
    private const string MyLedgerName = "My ledger";
    private const string SampleLedgerName = "Sample data";

    private Ledger? _activeLedger;
    private int _activeLedgerGeneration;

    public DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    // Ledgers

    /// <summary>The ledger being viewed. Creates the user's own ledger on first use.</summary>
    public async Task<Ledger> GetActiveLedgerAsync()
    {
        if (_activeLedger is not null && _activeLedgerGeneration == (dataGeneration?.Value ?? 0))
            return _activeLedger;
        _activeLedger = null;

        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activeId = await db.Users.Where(u => u.Id == userId).Select(u => u.ActiveLedgerId).SingleAsync();

        // Only trust the stored id if it's one of this user's ledgers.
        var ledger = activeId is null
            ? null
            : await db.Ledgers.AsNoTracking().SingleOrDefaultAsync(l => l.Id == activeId && l.OwnerId == userId);

        if (ledger is null)
            await SetActiveLedgerAsync(db, userId, await GetOrCreateLedgerAsync(db, userId, isSample: false));
        else
            (_activeLedger, _activeLedgerGeneration) = (ledger, dataGeneration?.Value ?? 0);

        return _activeLedger!;
    }

    public async Task<bool> HasSampleLedgerAsync()
    {
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Ledgers.AnyAsync(l => l.OwnerId == userId && l.IsSample);
    }

    /// <summary>Replaces any existing sample ledger with a fresh one and switches to it.</summary>
    public async Task StartSampleAsync()
    {
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Ledgers.Where(l => l.OwnerId == userId && l.IsSample).ExecuteDeleteAsync();

        var ledger = await GetOrCreateLedgerAsync(db, userId, isSample: true);
        foreach (var item in SampleData.Create(Today))
        {
            if (item is ILedgerEntity entity)
                entity.LedgerId = ledger.Id;
            db.Add(item);
        }
        await db.SaveChangesAsync();

        await SetActiveLedgerAsync(db, userId, ledger);
    }

    /// <summary>Switches between the user's own ledger and the sample ledger (if it exists).</summary>
    public async Task SwitchLedgerAsync(bool sample)
    {
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var ledger = sample
            ? await db.Ledgers.AsNoTracking().SingleOrDefaultAsync(l => l.OwnerId == userId && l.IsSample)
                ?? throw new InvalidOperationException("There is no sample data to switch to.")
            : await GetOrCreateLedgerAsync(db, userId, isSample: false);
        await SetActiveLedgerAsync(db, userId, ledger);
    }

    /// <summary>Deletes the sample ledger and everything in it, and switches back to the user's own ledger.</summary>
    public async Task DeleteSampleAsync()
    {
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Ledgers.Where(l => l.OwnerId == userId && l.IsSample).ExecuteDeleteAsync();
        await SetActiveLedgerAsync(db, userId, await GetOrCreateLedgerAsync(db, userId, isSample: false));
    }

    /// <summary>
    /// Remembers the Running Balance selection on the active ledger. It can include accounts of shared ledgers that are
    /// switched on; ids the user can't see are dropped.
    /// </summary>
    public async Task SaveProjectionViewAsync(IEnumerable<int> accountIds, int days, bool worstCase)
    {
        var ledger = await GetActiveLedgerAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var requested = accountIds.ToHashSet();
        var ledgerIds = await ReadLedgerIdsAsync(includeShared: true);
        var valid = await db.Accounts.Where(a => ledgerIds.Contains(a.LedgerId) && requested.Contains(a.Id))
            .Select(a => a.Id).ToListAsync();
        var ids = valid.Count == 0 ? null : string.Join(',', valid.Order());

        await db.Ledgers.Where(l => l.Id == ledger.Id && l.OwnerId == ledger.OwnerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.ProjectionAccountIds, ids)
                .SetProperty(l => l.ProjectionDays, days)
                .SetProperty(l => l.ProjectionWorstCase, worstCase));

        ledger.ProjectionAccountIds = ids;
        ledger.ProjectionDays = days;
        ledger.ProjectionWorstCase = worstCase;
    }

    // Accounts

    /// <summary>
    /// The ledger's accounts. With <paramref name="includeShared"/>, also the accounts of shared ledgers that are
    /// switched on (to display only). Pickers in dialogs pass the item's <paramref name="ledgerId"/> instead, so
    /// they only offer what the item can refer to.
    /// </summary>
    public async Task<List<Account>> GetAccountsAsync(bool activeOnly = false, bool includeShared = false, int ledgerId = 0)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared, ledgerId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => ledgerIds.Contains(a.LedgerId) && (!activeOnly || a.IsActive))
            .ToListAsync();
        return accounts.OrderBy(a => a.Type).ThenBy(a => a.Name).ToList();
    }

    /// <summary>Saves an account. Credit card details are cleared for other types. Throws <see cref="LedgerValidationException"/> for invalid card details.</summary>
    public async Task SaveAccountAsync(Account account)
    {
        if (!account.IsCreditCard)
        {
            (account.CreditLimit, account.AprPercent, account.StatementDay, account.MonthlySpending) = (null, null, null, null);
            (account.StatementBalance, account.MinimumPaymentPercent, account.MinimumPaymentFloor) = (null, null, null);
        }
        else if (account.StatementDay is < 1 or > 31)
            throw new LedgerValidationException("The statement closing day must be between 1 and 31.");
        else if (account.AprPercent is < 0 or > 100)
            throw new LedgerValidationException("The APR must be between 0% and 100%.");
        else if (account.CreditLimit < 0 || account.MonthlySpending < 0 || account.StatementBalance < 0
                 || account.MinimumPaymentFloor < 0 || account.MinimumPaymentPercent is < 0 or > 100)
            throw new LedgerValidationException("Credit card amounts can't be negative.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await SaveAsync(db, account, await TargetLedgerAsync(db, account, LedgerRole.Editor));
    }

    public Task DeleteAccountAsync(int id) => DeleteAsync<Account>(id, LedgerRole.Editor);

    // Categories

    public async Task<List<Category>> GetCategoriesAsync(bool includeShared = false, int ledgerId = 0)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared, ledgerId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var categories = await db.Categories.AsNoTracking().Where(c => ledgerIds.Contains(c.LedgerId)).ToListAsync();
        return categories.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Adds or renames a category. Throws <see cref="LedgerValidationException"/> for a blank or duplicate name.
    /// Contributors can add categories (for their transactions); changing one takes an editor.
    /// </summary>
    public async Task SaveCategoryAsync(Category category)
    {
        category.Name = category.Name.Trim();
        if (category.Name.Length == 0)
            throw new LedgerValidationException("Enter a category name.");
        if (category.Name.Length > 50)
            throw new LedgerValidationException("Category names can be up to 50 characters.");

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var ledgerId = await TargetLedgerAsync(db, category, category.Id == 0 ? LedgerRole.Contributor : LedgerRole.Editor);
            var names = await db.Categories.AsNoTracking()
                .Where(c => c.LedgerId == ledgerId && c.Id != category.Id)
                .Select(c => c.Name)
                .ToListAsync();
            if (names.Contains(category.Name, StringComparer.OrdinalIgnoreCase))
                throw new LedgerValidationException($"There's already a category called \"{category.Name}\".");
            await SaveAsync(db, category, ledgerId);
        }
    }

    /// <summary>Deletes a category. Its bills are kept and become uncategorized.</summary>
    public Task DeleteCategoryAsync(int id) => DeleteAsync<Category>(id, LedgerRole.Editor);

    // Payees

    public async Task<List<Payee>> GetPayeesAsync(bool includeShared = false, int ledgerId = 0)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared, ledgerId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var payees = await db.Payees.AsNoTracking().Where(p => ledgerIds.Contains(p.LedgerId)).ToListAsync();
        return payees.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Adds or updates a payee. Throws <see cref="LedgerValidationException"/> for a blank or duplicate
    /// name, or a website that isn't a valid http(s) address. The website is stored normalized.
    /// Contributors can add payees (for their transactions); changing one takes an editor.
    /// </summary>
    public async Task SavePayeeAsync(Payee payee)
    {
        payee.Name = payee.Name.Trim();
        if (payee.Name.Length == 0)
            throw new LedgerValidationException("Enter a payee name.");
        if (payee.Name.Length > 100)
            throw new LedgerValidationException("Payee names can be up to 100 characters.");
        if (!WebLinks.TryNormalize(payee.WebsiteUrl, out var url))
            throw new LedgerValidationException("Enter the website as a web address, like https://www.mybank.com.");
        payee.WebsiteUrl = url;
        payee.Notes = string.IsNullOrWhiteSpace(payee.Notes) ? null : payee.Notes.Trim();

        await using var db = await dbFactory.CreateDbContextAsync();
        var ledgerId = await TargetLedgerAsync(db, payee, payee.Id == 0 ? LedgerRole.Contributor : LedgerRole.Editor);
        var names = await db.Payees.AsNoTracking()
            .Where(p => p.LedgerId == ledgerId && p.Id != payee.Id)
            .Select(p => p.Name)
            .ToListAsync();
        if (names.Contains(payee.Name, StringComparer.OrdinalIgnoreCase))
            throw new LedgerValidationException($"There's already a payee called \"{payee.Name}\".");

        await SaveAsync(db, payee, ledgerId);
    }

    /// <summary>Deletes a payee. Its bills and loans are kept, without a payee or lender.</summary>
    public Task DeletePayeeAsync(int id) => DeleteAsync<Payee>(id, LedgerRole.Editor);

    // Bills

    /// <summary>The ledger's bills; with <paramref name="includeShared"/>, also those of shared ledgers that are switched on.</summary>
    public async Task<List<Bill>> GetBillsAsync(bool includeShared = false)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared);
        await using var db = await dbFactory.CreateDbContextAsync();
        var bills = await db.Bills.AsNoTracking()
            .Where(b => ledgerIds.Contains(b.LedgerId))
            .Include(b => b.PayFromAccount)
            .Include(b => b.CardAccount)
            .Include(b => b.Loan).ThenInclude(l => l!.Lender)
            .Include(b => b.Payee)
            .Include(b => b.Category)
            .Include(b => b.Occurrences)
            .ToListAsync();

        // A payment into a card in another ledger only counts while the payer may still record payments there, and
        // only shows the card to someone who can see that ledger.
        var crossLedger = bills.Where(b => b.CardAccount is { } card && card.LedgerId != b.LedgerId).Select(b => b.Id).ToList();
        if (crossLedger.Count > 0)
        {
            var linked = await CardPaymentsLinkedAcrossLedgers(db).Where(b => crossLedger.Contains(b.Id)).Select(b => b.Id).ToListAsync();
            var scope = await GetScopeAsync();
            var seen = scope.Shared.Select(s => s.LedgerId).Append(scope.HomeLedgerId).ToHashSet();
            foreach (var bill in bills.Where(b => crossLedger.Contains(b.Id) && (!linked.Contains(b.Id) || !seen.Contains(b.CardAccount!.LedgerId))))
                (bill.CardAccount, bill.CardUnavailable) = (null, true);
        }

        // Card payments depend on everything that moves the card: bills and transactions in its own ledger (which may
        // not be among those read here) and payments from other ledgers.
        var cards = bills.Where(b => b.CardAccount is not null).Select(b => b.CardAccount!).DistinctBy(c => c.Id).ToList();
        if (cards.Count > 0)
        {
            var cardIds = cards.Select(c => c.Id).ToList();
            var cardLedgerIds = cards.Select(c => c.LedgerId).Distinct().ToList();
            var loaded = bills.Select(b => b.Id).ToHashSet();
            var cardBills = (await db.Bills.AsNoTracking()
                    .Where(b => cardLedgerIds.Contains(b.LedgerId)
                        && ((b.CardAccountId != null && cardIds.Contains(b.CardAccountId.Value)) || (b.PayFromAccountId != null && cardIds.Contains(b.PayFromAccountId.Value))))
                    .Include(b => b.Occurrences)
                    .ToListAsync())
                .Where(b => !loaded.Contains(b.Id));
            var fromOthers = await CardPaymentsFromOthersAsync(db, cards, [.. ledgerIds, .. cardLedgerIds]);
            var cardTransactions = await db.Transactions.AsNoTracking().Where(t => cardIds.Contains(t.AccountId)).ToListAsync();
            CreditCards.EstimatePayments([.. bills, .. cardBills, .. fromOthers], cardTransactions, Today.AddYears(1));
        }
        return bills.OrderBy(b => b.Name).ToList();
    }

    /// <summary>
    /// Payments into the cards of the ledgers read (as <see cref="GetBillsAsync"/>) that someone those ledgers are
    /// shared with records in their own ledger, from their own account. They hold only what the card's side may see:
    /// the schedule, rule, recorded amounts and dates, and who pays from which account (<see cref="Bill.PaidBy"/>).
    /// Pass them along with the bills to anything that plays a card forward; never list them as the user's bills.
    /// </summary>
    public async Task<List<Bill>> GetCardPaymentsFromOthersAsync(bool includeShared = false)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared);
        await using var db = await dbFactory.CreateDbContextAsync();
        var cards = await db.Accounts.AsNoTracking().Where(a => ledgerIds.Contains(a.LedgerId) && a.Type == AccountType.CreditCard).ToListAsync();
        return await CardPaymentsFromOthersAsync(db, cards, ledgerIds);
    }

    /// <summary>Payments into <paramref name="cards"/> from bills outside <paramref name="loadedLedgerIds"/>, trimmed to what the card's side may see.</summary>
    private static async Task<List<Bill>> CardPaymentsFromOthersAsync(LedgerlyDbContext db, IReadOnlyCollection<Account> cards, IReadOnlyCollection<int> loadedLedgerIds)
    {
        var cardIds = cards.Select(c => c.Id).ToList();
        if (cardIds.Count == 0)
            return [];
        var bills = await CardPaymentsLinkedAcrossLedgers(db).AsNoTracking()
            .Where(b => cardIds.Contains(b.CardAccountId!.Value) && !loadedLedgerIds.Contains(b.LedgerId))
            .Include(b => b.PayFromAccount)
            .Include(b => b.Occurrences)
            .ToListAsync();
        if (bills.Count == 0)
            return [];

        var payerLedgers = bills.Select(b => b.LedgerId).Distinct().ToList();
        var payers = (await (
                from l in db.Ledgers
                join u in db.Users on l.OwnerId equals u.Id
                where payerLedgers.Contains(l.Id)
                select new { l.Id, u.DisplayName, u.Email })
            .ToListAsync()).ToDictionary(p => p.Id, p => ApplicationUser.NameOf(p.DisplayName, p.Email));
        var cardsById = cards.ToDictionary(c => c.Id);
        return bills.Select(b => new Bill
        {
            Id = b.Id,
            LedgerId = b.LedgerId,
            Name = $"Payment from {payers[b.LedgerId]}",
            ExpectedAmount = b.ExpectedAmount,
            Frequency = b.Frequency,
            StartDate = b.StartDate,
            EndDate = b.EndDate,
            CardAccountId = b.CardAccountId,
            CardAccount = cardsById[b.CardAccountId!.Value],
            CardPaymentRule = b.CardPaymentRule,
            AutoPay = b.AutoPay,
            IsActive = b.IsActive,
            Occurrences = b.Occurrences.Select(o => new BillOccurrence { BillId = o.BillId, DueDate = o.DueDate, Amount = o.Amount, PaidOn = o.PaidOn }).ToList(),
            PaidBy = new CardPayer(payers[b.LedgerId], b.PayFromAccount?.Name)
        }).ToList();
    }

    /// <summary>
    /// Bills that pay a card in another ledger and still count there: the bill is in its owner's personal ledger, and
    /// the card's ledger is shared with that owner (accepted) with at least <see cref="CardPayerRole"/>. Checked on
    /// every read, so removed access or a lower role applies at once.
    /// </summary>
    private static IQueryable<Bill> CardPaymentsLinkedAcrossLedgers(LedgerlyDbContext db) =>
        db.Bills.Where(b => b.CardAccountId != null && db.Ledgers.Any(payer =>
            payer.Id == b.LedgerId && !payer.IsSample && payer.Id != b.CardAccount!.LedgerId
            && db.LedgerMembers.Any(m => m.LedgerId == b.CardAccount!.LedgerId && m.UserId == payer.OwnerId && m.AcceptedAt != null && m.Role != LedgerRole.Viewer)));

    /// <summary>
    /// The role someone needs on a shared ledger to pay its cards from their own ledger. It's their own money, like
    /// recording a payment. Keep <see cref="CardPaymentsLinkedAcrossLedgers"/> in step with it.
    /// </summary>
    public const LedgerRole CardPayerRole = LedgerRole.Contributor;

    /// <summary>
    /// Saves a bill. Throws <see cref="LedgerValidationException"/> if it links a loan and a card, or pays a card from a card.
    /// Everything it refers to is in its own ledger, except that a bill in the user's personal ledger may pay a card in a
    /// ledger shared with them with at least <see cref="CardPayerRole"/> (from one of their own accounts).
    /// </summary>
    public async Task SaveBillAsync(Bill bill)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ledgerId = await TargetLedgerAsync(db, bill, LedgerRole.Editor);
        await EnsureInLedgerAsync(db.Accounts, bill.PayFromAccountId, ledgerId);
        await EnsureCardPayableAsync(db, bill, ledgerId);
        await EnsureInLedgerAsync(db.Loans, bill.LoanId, ledgerId);
        await EnsureInLedgerAsync(db.Categories, bill.CategoryId, ledgerId);
        await EnsureInLedgerAsync(db.Payees, bill.PayeeId, ledgerId);

        if (bill.CardAccountId is { } cardId)
        {
            if (bill.LoanId is not null)
                throw new LedgerValidationException("A bill can pay a loan or a credit card, not both.");
            var types = await db.Accounts.Where(a => a.Id == cardId || a.Id == bill.PayFromAccountId).Select(a => new { a.Id, a.Type }).ToListAsync();
            if (types.Single(a => a.Id == cardId).Type != AccountType.CreditCard)
                throw new LedgerValidationException("Choose a credit card account for this payment.");
            if (bill.PayFromAccountId == cardId || types.Any(a => a.Id == bill.PayFromAccountId && a.Type == AccountType.CreditCard))
                throw new LedgerValidationException("Pay a credit card from a bank account, not from a credit card.");
        }
        await SaveAsync(db, bill, ledgerId);
    }

    /// <summary>
    /// Checks the card a bill pays: one in the bill's own ledger, or a card in a shared ledger (see <see cref="SaveBillAsync"/>).
    /// A link that's already saved may stay while it still counts, so someone else editing the bill doesn't break it.
    /// </summary>
    private async Task EnsureCardPayableAsync(LedgerlyDbContext db, Bill bill, int ledgerId)
    {
        if (bill.CardAccountId is not { } cardId)
            return;
        var cardLedgerId = await db.Accounts.Where(a => a.Id == cardId).Select(a => (int?)a.LedgerId).SingleOrDefaultAsync()
            ?? throw new InvalidOperationException($"Account {cardId} was not found.");
        if (cardLedgerId == ledgerId)
            return;

        if (bill.Id != 0 && await db.Bills.AnyAsync(b => b.Id == bill.Id && b.CardAccountId == cardId))
        {
            if (!await CardPaymentsLinkedAcrossLedgers(db).AnyAsync(b => b.Id == bill.Id))
                throw new LedgerValidationException("That card isn't shared with you for payments any more. Choose another under \"Pays off\".");
            return;
        }

        var scope = await GetScopeAsync();
        var role = scope.RoleIn(cardLedgerId) ?? throw new InvalidOperationException($"Account {cardId} was not found.");
        if (ledgerId != scope.HomeLedgerId || scope.HomeIsSample)
            throw new LedgerValidationException("A payment on a card from someone else's ledger has to come from your own ledger.");
        if (role < CardPayerRole)
            throw new LedgerValidationException($"Paying a card in a shared ledger needs “{LedgerScope.AccessText(CardPayerRole)}” access there.");
    }

    public Task DeleteBillAsync(int id) => DeleteAsync<Bill>(id, LedgerRole.Editor);

    /// <summary>
    /// Records the amount and/or payment for one due date of a bill. For loan bills, <paramref name="paymentKind"/>
    /// overrides whether this payment is the monthly payment or extra principal (null uses the bill's setting).
    /// </summary>
    public async Task RecordOccurrenceAsync(int billId, DateOnly dueDate, decimal? amount, DateOnly? paidOn, string? notes = null, LoanPaymentKind? paymentKind = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await LedgerOfAsync(db.Bills, billId, LedgerRole.Contributor);
        var occurrence = await db.BillOccurrences.SingleOrDefaultAsync(o => o.BillId == billId && o.DueDate == dueDate);

        if (amount is null && paidOn is null && string.IsNullOrWhiteSpace(notes) && paymentKind is null)
        {
            // Nothing left to record: go back to the bill's defaults.
            if (occurrence is not null)
                db.BillOccurrences.Remove(occurrence);
        }
        else
        {
            occurrence ??= db.BillOccurrences.Add(new BillOccurrence { BillId = billId, DueDate = dueDate }).Entity;
            occurrence.Amount = amount;
            occurrence.PaidOn = paidOn;
            occurrence.Notes = notes;
            occurrence.PaymentKind = paymentKind;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Marks due dates paid on their due date (or today, if that's earlier), keeping any amount, notes or
    /// payment type already recorded for them. The bills can be in any ledger the user can record payments in.
    /// </summary>
    public async Task MarkPaidAsync(IEnumerable<(int BillId, DateOnly DueDate)> dues)
    {
        var list = dues.Distinct().ToList();
        if (list.Count == 0)
            return;

        var ledgerIds = await WritableLedgerIdsAsync(LedgerRole.Contributor);
        await using var db = await dbFactory.CreateDbContextAsync();
        var billIds = list.Select(d => d.BillId).Distinct().ToList();
        var owned = await db.Bills.Where(b => ledgerIds.Contains(b.LedgerId) && billIds.Contains(b.Id)).Select(b => b.Id).ToListAsync();
        if (billIds.Except(owned).FirstOrDefault() is var missing and not 0)
            throw new InvalidOperationException($"Bill {missing} was not found.");

        var dueDates = list.Select(d => d.DueDate).Distinct().ToList();
        var existing = await db.BillOccurrences.Where(o => billIds.Contains(o.BillId) && dueDates.Contains(o.DueDate)).ToListAsync();
        foreach (var (billId, dueDate) in list)
        {
            var occurrence = existing.FirstOrDefault(o => o.BillId == billId && o.DueDate == dueDate)
                ?? db.BillOccurrences.Add(new BillOccurrence { BillId = billId, DueDate = dueDate }).Entity;
            occurrence.PaidOn ??= dueDate < Today ? dueDate : Today;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Records an extra principal payment already made on a loan, as a one-time paid bill so it also
    /// appears in the paying account's running balance. It goes in the loan's ledger, and contributors can record one.
    /// </summary>
    public async Task RecordExtraLoanPaymentAsync(int loanId, int? payFromAccountId, decimal amount, DateOnly paidOn, string? notes)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "The payment must be more than zero.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var ledgerId = await LedgerOfAsync(db.Loans, loanId, LedgerRole.Contributor);
        await EnsureInLedgerAsync(db.Accounts, payFromAccountId, ledgerId);
        var loan = await db.Loans.AsNoTracking().SingleAsync(l => l.Id == loanId);

        // Use the same category as the loan's other bills, if any.
        var categoryId = await db.Bills.Where(b => b.LedgerId == ledgerId && b.LoanId == loanId && b.CategoryId != null)
            .Select(b => b.CategoryId).FirstOrDefaultAsync();

        db.Bills.Add(new Bill
        {
            LedgerId = ledgerId,
            Name = $"{loan.Name} extra payment",
            PayeeId = loan.LenderId,
            CategoryId = categoryId,
            LoanId = loanId,
            LoanPaymentKind = LoanPaymentKind.ExtraPrincipal,
            ExpectedAmount = amount,
            Frequency = Frequency.Once,
            StartDate = paidOn,
            PayFromAccountId = payFromAccountId,
            Occurrences = [new BillOccurrence { DueDate = paidOn, PaidOn = paidOn, Amount = amount, Notes = notes }]
        });
        await db.SaveChangesAsync();
    }

    // Income

    public async Task<List<Income>> GetIncomesAsync(bool includeShared = false)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared);
        await using var db = await dbFactory.CreateDbContextAsync();
        var incomes = await db.Incomes.AsNoTracking()
            .Where(i => ledgerIds.Contains(i.LedgerId))
            .Include(i => i.DepositToAccount)
            .ToListAsync();
        return incomes.OrderBy(i => i.Name).ToList();
    }

    public async Task SaveIncomeAsync(Income income)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ledgerId = await TargetLedgerAsync(db, income, LedgerRole.Editor);
        await EnsureInLedgerAsync(db.Accounts, income.DepositToAccountId, ledgerId);
        await SaveAsync(db, income, ledgerId);
    }

    public Task DeleteIncomeAsync(int id) => DeleteAsync<Income>(id, LedgerRole.Editor);

    // Transfers

    public async Task<List<Transfer>> GetTransfersAsync(bool includeShared = false)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared);
        await using var db = await dbFactory.CreateDbContextAsync();
        var transfers = await db.Transfers.AsNoTracking()
            .Where(t => ledgerIds.Contains(t.LedgerId))
            .Include(t => t.FromAccount)
            .Include(t => t.ToAccount)
            .Include(t => t.Occurrences)
            .ToListAsync();
        return transfers.OrderBy(t => t.Name).ToList();
    }

    /// <summary>
    /// Saves a transfer between two of the ledger's accounts. Throws <see cref="LedgerValidationException"/>
    /// for a blank name, a negative amount, the same account at both ends, or a credit card at either end.
    /// </summary>
    public async Task SaveTransferAsync(Transfer transfer)
    {
        transfer.Name = transfer.Name.Trim();
        if (transfer.Name.Length == 0)
            throw new LedgerValidationException("Enter a name for the transfer.");
        if (transfer.Amount < 0)
            throw new LedgerValidationException("A transfer amount can't be negative.");
        if (transfer.FromAccountId == transfer.ToAccountId)
            throw new LedgerValidationException("Choose two different accounts to move money between.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var ledgerId = await TargetLedgerAsync(db, transfer, LedgerRole.Editor);
        await EnsureInLedgerAsync(db.Accounts, transfer.FromAccountId, ledgerId);
        await EnsureInLedgerAsync(db.Accounts, transfer.ToAccountId, ledgerId);

        // A card's balance is worked out by CreditCards.Simulate from the bills that charge and pay it,
        // so money arriving another way would be counted twice.
        var cards = await db.Accounts
            .Where(a => (a.Id == transfer.FromAccountId || a.Id == transfer.ToAccountId) && a.Type == AccountType.CreditCard)
            .AnyAsync();
        if (cards)
            throw new LedgerValidationException("Transfers move money between bank accounts. To pay a credit card, add a bill that pays the card.");

        await SaveAsync(db, transfer, ledgerId);
    }

    public Task DeleteTransferAsync(int id) => DeleteAsync<Transfer>(id, LedgerRole.Editor);

    /// <summary>Records the amount and/or completion for one scheduled date of a transfer.</summary>
    public async Task RecordTransferOccurrenceAsync(int transferId, DateOnly scheduledDate, decimal? amount, DateOnly? completedOn, string? notes = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await LedgerOfAsync(db.Transfers, transferId, LedgerRole.Contributor);
        var occurrence = await db.TransferOccurrences.SingleOrDefaultAsync(o => o.TransferId == transferId && o.ScheduledDate == scheduledDate);

        if (amount is null && completedOn is null && string.IsNullOrWhiteSpace(notes))
        {
            // Nothing left to record: go back to the transfer's defaults.
            if (occurrence is not null)
                db.TransferOccurrences.Remove(occurrence);
        }
        else
        {
            occurrence ??= db.TransferOccurrences.Add(new TransferOccurrence { TransferId = transferId, ScheduledDate = scheduledDate }).Entity;
            occurrence.Amount = amount;
            occurrence.CompletedOn = completedOn;
            occurrence.Notes = notes;
        }

        await db.SaveChangesAsync();
    }

    // Transactions

    /// <summary>The ledger's one-off transactions, newest first, optionally limited to dates <paramref name="from"/> through <paramref name="to"/>.</summary>
    public async Task<List<Transaction>> GetTransactionsAsync(DateOnly? from = null, DateOnly? to = null, bool includeShared = false)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared);
        await using var db = await dbFactory.CreateDbContextAsync();
        var transactions = await db.Transactions.AsNoTracking()
            .Where(t => ledgerIds.Contains(t.LedgerId) && (from == null || t.Date >= from) && (to == null || t.Date <= to))
            .Include(t => t.Account)
            .Include(t => t.Category)
            .Include(t => t.Payee)
            .ToListAsync();
        return transactions.OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).ToList();
    }

    /// <summary>
    /// Saves a one-off transaction. Throws <see cref="LedgerValidationException"/> for a blank description,
    /// no account, or an amount that isn't more than zero.
    /// </summary>
    public async Task SaveTransactionAsync(Transaction transaction)
    {
        transaction.Description = transaction.Description.Trim();
        if (transaction.Description.Length == 0)
            throw new LedgerValidationException("Enter what the transaction was for.");
        if (transaction.Description.Length > 100)
            throw new LedgerValidationException("Descriptions can be up to 100 characters.");
        if (transaction.Amount <= 0)
            throw new LedgerValidationException("Enter an amount more than zero.");
        if (transaction.AccountId == 0)
            throw new LedgerValidationException("Choose an account.");
        transaction.Notes = string.IsNullOrWhiteSpace(transaction.Notes) ? null : transaction.Notes.Trim();

        await using var db = await dbFactory.CreateDbContextAsync();
        var ledgerId = await TargetLedgerAsync(db, transaction, LedgerRole.Contributor);
        await EnsureInLedgerAsync(db.Accounts, transaction.AccountId, ledgerId);
        await EnsureInLedgerAsync(db.Categories, transaction.CategoryId, ledgerId);
        await EnsureInLedgerAsync(db.Payees, transaction.PayeeId, ledgerId);

        await SaveAsync(db, transaction, ledgerId);
    }

    public Task DeleteTransactionAsync(int id) => DeleteAsync<Transaction>(id, LedgerRole.Contributor);

    /// <summary>The account of the most recently added transaction in a ledger (0: the active one), to suggest for the next one.</summary>
    public async Task<int?> GetLastTransactionAccountIdAsync(int ledgerId = 0)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared: false, ledgerId);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Transactions.Where(t => ledgerIds.Contains(t.LedgerId)).OrderByDescending(t => t.Id).Select(t => (int?)t.AccountId).FirstOrDefaultAsync();
    }

    /// <summary>How many transactions an account has, to warn before deleting it (they're deleted with it).</summary>
    public async Task<int> CountTransactionsAsync(int accountId)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared: true);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Transactions.CountAsync(t => ledgerIds.Contains(t.LedgerId) && t.AccountId == accountId);
    }

    // Loans

    public async Task<List<Loan>> GetLoansAsync(bool includeShared = false, int ledgerId = 0)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared, ledgerId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var loans = await db.Loans.AsNoTracking().Where(l => ledgerIds.Contains(l.LedgerId)).Include(l => l.Lender).ToListAsync();
        return loans.OrderBy(l => l.Name).ToList();
    }

    /// <summary>A loan in the user's own ledger or a shared ledger that's switched on.</summary>
    public async Task<Loan?> GetLoanAsync(int id)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared: true);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Loans.AsNoTracking().Include(l => l.Lender).SingleOrDefaultAsync(l => l.Id == id && ledgerIds.Contains(l.LedgerId));
    }

    public async Task SaveLoanAsync(Loan loan)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ledgerId = await TargetLedgerAsync(db, loan, LedgerRole.Editor);
        await EnsureInLedgerAsync(db.Payees, loan.LenderId, ledgerId);
        await SaveAsync(db, loan, ledgerId);
    }

    public Task DeleteLoanAsync(int id) => DeleteAsync<Loan>(id, LedgerRole.Editor);

    // Projection

    public async Task<ProjectionResult> ProjectAsync(IEnumerable<int> accountIds, DateOnly through, ProjectionMode mode = ProjectionMode.Expected, bool includeShared = false)
    {
        var ids = accountIds.ToHashSet();
        var accounts = (await GetAccountsAsync(includeShared: includeShared)).Where(a => ids.Contains(a.Id)).ToList();
        List<Bill> bills = [.. await GetBillsAsync(includeShared), .. await GetCardPaymentsFromOthersAsync(includeShared)];
        return Projection.Build(accounts, bills, await GetIncomesAsync(includeShared), await GetTransfersAsync(includeShared),
            await GetTransactionsAsync(includeShared: includeShared), through, mode);
    }

    public async Task<bool> HasAnyDataAsync(bool includeShared = false)
    {
        var ledgerIds = await ReadLedgerIdsAsync(includeShared);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Accounts.AnyAsync(a => ledgerIds.Contains(a.LedgerId))
            || await db.Bills.AnyAsync(b => ledgerIds.Contains(b.LedgerId))
            || await db.Transfers.AnyAsync(t => ledgerIds.Contains(t.LedgerId))
            || await db.Transactions.AnyAsync(t => ledgerIds.Contains(t.LedgerId))
            || await db.Loans.AnyAsync(l => ledgerIds.Contains(l.LedgerId));
    }

    // Sharing

    /// <summary>Colors given to shared ledgers when they're accepted, in order, skipping ones the user already uses.</summary>
    public static readonly string[] LayerColors = ["#8E44AD", "#D35400", "#16A085", "#C0392B", "#5C6BC0", "#795548", "#E91E63", "#607D8B"];

    /// <summary>
    /// The home ledger and the shared ledgers the user has accepted, with their layer settings. Read fresh on every
    /// call, so access that's been removed, or a layer switched off in another tab, applies straight away.
    /// </summary>
    public async Task<LedgerScope> GetScopeAsync()
    {
        var home = await GetActiveLedgerAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await (
                from m in db.LedgerMembers.AsNoTracking()
                join l in db.Ledgers on m.LedgerId equals l.Id
                join u in db.Users on l.OwnerId equals u.Id
                where m.UserId == home.OwnerId && m.AcceptedAt != null && !l.IsSample
                select new { m.Id, m.LedgerId, m.Role, m.IsVisible, m.Color, m.Nickname, m.AcceptedAt, u.DisplayName, u.Email })
            .ToListAsync();

        var shared = rows.OrderBy(r => r.AcceptedAt).Select((r, i) =>
        {
            var defaultName = $"{ApplicationUser.ShortNameOf(r.DisplayName, r.Email)}'s ledger";
            return new SharedLedger(r.Id, r.LedgerId, r.Nickname, defaultName, ApplicationUser.NameOf(r.DisplayName, r.Email), r.Color ?? LayerColors[i % LayerColors.Length], r.Role, r.IsVisible);
        }).ToList();
        return new LedgerScope { HomeLedgerId = home.Id, HomeIsSample = home.IsSample, Shared = shared };
    }

    /// <summary>
    /// Shares the user's own ledger (never the sample one) with the account that uses <paramref name="email"/>, as
    /// an invitation they have to accept. Throws <see cref="LedgerValidationException"/> for a blank or unknown
    /// email, the user's own, or someone it's already shared with.
    /// </summary>
    public async Task ShareAsync(string email, LedgerRole role = LedgerRole.Viewer)
    {
        email = email.Trim();
        if (email.Length == 0)
            throw new LedgerValidationException("Enter the email address they sign in with.");

        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var normalized = email.ToUpperInvariant();
        var recipient = await db.Users.Where(u => u.NormalizedEmail == normalized).Select(u => u.Id).SingleOrDefaultAsync()
            ?? throw new LedgerValidationException($"No one on this Ledgerly signs in with {email}.");
        if (recipient == userId)
            throw new LedgerValidationException("That's your own email address.");

        var ledger = await GetOrCreateLedgerAsync(db, userId, isSample: false);
        if (await db.LedgerMembers.AnyAsync(m => m.LedgerId == ledger.Id && m.UserId == recipient))
            throw new LedgerValidationException($"Your ledger is already shared with {email}.");

        db.LedgerMembers.Add(new LedgerMember { LedgerId = ledger.Id, UserId = recipient, Role = role, InvitedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        notifier?.Notify(recipient);
    }

    /// <summary>Changes what someone the user shares their ledger with may do in it. Applies straight away.</summary>
    public async Task SetShareRoleAsync(int memberId, LedgerRole role)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var member = await db.LedgerMembers.Where(m => m.Id == memberId && m.Ledger!.OwnerId == userId).Select(m => m.UserId).SingleOrDefaultAsync();
        if (member is null)
            return;
        await db.LedgerMembers.Where(m => m.Id == memberId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, role));
        notifier?.Notify(member);
    }

    /// <summary>Who the user's own ledger is shared with, including invitations not yet answered.</summary>
    public async Task<List<LedgerShare>> GetSharesAsync()
    {
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await (
                from m in db.LedgerMembers.AsNoTracking()
                join l in db.Ledgers on m.LedgerId equals l.Id
                join u in db.Users on m.UserId equals u.Id
                where l.OwnerId == userId
                select new { m.Id, u.DisplayName, u.Email, m.Role, m.AcceptedAt, m.InvitedAt })
            .ToListAsync();
        return rows.Select(r => new LedgerShare(r.Id, ApplicationUser.NameOf(r.DisplayName, r.Email), r.Email ?? "", r.Role, r.AcceptedAt is not null, r.InvitedAt))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Stops sharing the user's ledger with someone, or withdraws an invitation.</summary>
    public async Task StopSharingAsync(int memberId)
    {
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var member = await db.LedgerMembers.Where(m => m.Id == memberId && m.Ledger!.OwnerId == userId).Select(m => m.UserId).SingleOrDefaultAsync();
        if (member is null)
            return;
        await db.LedgerMembers.Where(m => m.Id == memberId).ExecuteDeleteAsync();
        notifier?.Notify(member);
    }

    /// <summary>Invitations waiting for the user to accept or decline, oldest first.</summary>
    public async Task<List<LedgerInvitation>> GetInvitationsAsync()
    {
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await (
                from m in db.LedgerMembers.AsNoTracking()
                join l in db.Ledgers on m.LedgerId equals l.Id
                join u in db.Users on l.OwnerId equals u.Id
                where m.UserId == userId && m.AcceptedAt == null
                select new { m.Id, u.DisplayName, u.Email, m.Role, m.InvitedAt })
            .ToListAsync();
        return rows.OrderBy(r => r.InvitedAt)
            .Select(r => new LedgerInvitation(r.Id, ApplicationUser.NameOf(r.DisplayName, r.Email), r.Email ?? "", r.Role, r.InvitedAt)).ToList();
    }

    /// <summary>Accepts an invitation (switching the ledger on, in a color not already in use) or declines it.</summary>
    public async Task RespondToInvitationAsync(int memberId, bool accept)
    {
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var member = await db.LedgerMembers.Include(m => m.Ledger)
            .SingleOrDefaultAsync(m => m.Id == memberId && m.UserId == userId && m.AcceptedAt == null);
        if (member is null)
            return;

        if (accept)
        {
            var used = await db.LedgerMembers.Where(m => m.UserId == userId && m.Color != null).Select(m => m.Color!).ToListAsync();
            member.AcceptedAt = DateTime.UtcNow;
            member.IsVisible = true;
            member.Color = LayerColors.FirstOrDefault(c => !used.Contains(c)) ?? LayerColors[used.Count % LayerColors.Length];
        }
        else
            db.LedgerMembers.Remove(member);

        await db.SaveChangesAsync();
        notifier?.Notify(member.Ledger!.OwnerId);
    }

    /// <summary>Stops seeing a ledger someone shared with the user.</summary>
    public async Task LeaveSharedLedgerAsync(int memberId)
    {
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var owner = await db.LedgerMembers.Where(m => m.Id == memberId && m.UserId == userId).Select(m => m.Ledger!.OwnerId).SingleOrDefaultAsync();
        if (owner is null)
            return;
        await db.LedgerMembers.Where(m => m.Id == memberId).ExecuteDeleteAsync();
        notifier?.Notify(owner);
    }

    /// <summary>Switches a shared ledger on or off, or all of them when <paramref name="memberId"/> is null.</summary>
    public async Task SetSharedVisibleAsync(int? memberId, bool visible)
    {
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.LedgerMembers.Where(m => m.UserId == userId && m.AcceptedAt != null && (memberId == null || m.Id == memberId))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsVisible, visible));
    }

    /// <summary>Renames or recolors a shared ledger for the user. A blank name goes back to "Owner's ledger".</summary>
    public async Task UpdateSharedLedgerAsync(int memberId, string? nickname, string? color)
    {
        nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        if (nickname?.Length > 100)
            throw new LedgerValidationException("Names can be up to 100 characters.");
        if (color is not null && !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            throw new LedgerValidationException("Choose a color from the list.");

        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.LedgerMembers.Where(m => m.Id == memberId && m.UserId == userId && m.AcceptedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Nickname, nickname).SetProperty(m => m.Color, color));
    }

    /// <summary>Sets the name other people see when this user shares a ledger with them.</summary>
    public async Task SetDisplayNameAsync(string? displayName)
    {
        displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (displayName?.Length > 50)
            throw new LedgerValidationException("Names can be up to 50 characters.");
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisplayName, displayName));
    }

    public async Task<string?> GetDisplayNameAsync()
    {
        var userId = await RequireUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleOrDefaultAsync();
    }

    // Helpers

    private async Task<string> RequireUserIdAsync() =>
        await currentUser.GetUserIdAsync() ?? throw new UnauthorizedAccessException("Sign in to use Ledgerly.");

    private async Task<int> LedgerIdAsync() => (await GetActiveLedgerAsync()).Id;

    /// <summary>
    /// Ledgers to read from: the home ledger, plus shared ledgers that are switched on when asked for; or just
    /// <paramref name="ledgerId"/> when it's given (it must be one of those). Writes never use this.
    /// </summary>
    private async Task<List<int>> ReadLedgerIdsAsync(bool includeShared, int ledgerId = 0)
    {
        if (ledgerId == 0)
            return includeShared ? [.. (await GetScopeAsync()).VisibleLedgerIds] : [await LedgerIdAsync()];
        if (!(await GetScopeAsync()).VisibleLedgerIds.Contains(ledgerId))
            throw new InvalidOperationException($"Ledger {ledgerId} was not found.");
        return [ledgerId];
    }

    /// <summary>The home ledger and the shared ledgers (switched on) where the user has at least <paramref name="role"/>.</summary>
    private async Task<List<int>> WritableLedgerIdsAsync(LedgerRole role)
    {
        var scope = await GetScopeAsync();
        return [scope.HomeLedgerId, .. scope.WritableShared(role).Select(s => s.LedgerId)];
    }

    /// <summary>Checks the user may write to a ledger with <paramref name="role"/>. 0 means the home ledger.</summary>
    private async Task<int> RequireWritableAsync(int ledgerId, LedgerRole role)
    {
        var scope = await GetScopeAsync();
        if (ledgerId == 0)
            return scope.HomeLedgerId;
        RequireRole(scope, ledgerId, role);
        return ledgerId;
    }

    private static void RequireRole(LedgerScope scope, int ledgerId, LedgerRole role)
    {
        var actual = scope.RoleIn(ledgerId) ?? throw new InvalidOperationException($"Ledger {ledgerId} was not found.");
        if (actual < role)
            throw new LedgerValidationException(actual == LedgerRole.Viewer
                ? "This ledger is shared with you view only, so you can't change it."
                : "You can add transactions and record payments in this ledger, but only its owner or an editor can change this.");
    }

    /// <summary>
    /// The ledger a row is in, checking the user may write there with <paramref name="role"/>. Rows in ledgers the user
    /// can't see are reported as not found.
    /// </summary>
    private async Task<int> LedgerOfAsync<T>(DbSet<T> set, int id, LedgerRole role) where T : class, ILedgerEntity
    {
        var scope = await GetScopeAsync();
        var visible = scope.VisibleLedgerIds;
        var ledgerId = await set.Where(e => e.Id == id && visible.Contains(e.LedgerId)).Select(e => (int?)e.LedgerId).SingleOrDefaultAsync()
            ?? throw new InvalidOperationException($"{typeof(T).Name} {id} was not found.");
        RequireRole(scope, ledgerId, role);
        return ledgerId;
    }

    /// <summary>
    /// Where a save goes: the ledger an existing row is already in (it can't move), or for a new one the ledger it
    /// names (0: the home ledger). Either way the user needs at least <paramref name="role"/> there.
    /// </summary>
    private Task<int> TargetLedgerAsync<T>(LedgerlyDbContext db, T entity, LedgerRole role) where T : class, ILedgerEntity =>
        entity.Id == 0 ? RequireWritableAsync(entity.LedgerId, role) : LedgerOfAsync(db.Set<T>(), entity.Id, role);

    private static async Task<Ledger> GetOrCreateLedgerAsync(LedgerlyDbContext db, string userId, bool isSample)
    {
        var ledger = await db.Ledgers.AsNoTracking().FirstOrDefaultAsync(l => l.OwnerId == userId && l.IsSample == isSample);
        if (ledger is not null)
            return ledger;

        ledger = new Ledger { OwnerId = userId, IsSample = isSample, Name = isSample ? SampleLedgerName : MyLedgerName, CreatedAt = DateTime.UtcNow };
        db.Ledgers.Add(ledger);
        try
        {
            await db.SaveChangesAsync();
            return ledger;
        }
        catch (DbUpdateException)
        {
            // Another request (e.g. prerendering) created it first.
            db.ChangeTracker.Clear();
            return await db.Ledgers.AsNoTracking().SingleAsync(l => l.OwnerId == userId && l.IsSample == isSample);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task SetActiveLedgerAsync(LedgerlyDbContext db, string userId, Ledger ledger)
    {
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.ActiveLedgerId, ledger.Id));
        (_activeLedger, _activeLedgerGeneration) = (ledger, dataGeneration?.Value ?? 0);
    }

    /// <summary>
    /// Rejects references (e.g. a bill's pay-from account) to rows outside the item's own ledger, so nothing points
    /// across ledgers.
    /// </summary>
    private static async Task EnsureInLedgerAsync<T>(DbSet<T> set, int? id, int ledgerId) where T : class, ILedgerEntity
    {
        if (id is { } value && !await set.AnyAsync(e => e.Id == value && e.LedgerId == ledgerId))
            throw new InvalidOperationException($"{typeof(T).Name} {value} was not found.");
    }

    /// <summary>
    /// Inserts or updates scalar values only, so navigation properties on the passed-in object are never attached.
    /// <paramref name="ledgerId"/> comes from <see cref="TargetLedgerAsync"/>; updates only succeed for rows in it.
    /// </summary>
    private static async Task SaveAsync<T>(LedgerlyDbContext db, T entity, int ledgerId) where T : class, ILedgerEntity, new()
    {
        T target;
        if (entity.Id == 0)
        {
            target = new T();
            db.Add(target);
        }
        else
        {
            target = await db.Set<T>().SingleOrDefaultAsync(e => e.Id == entity.Id && e.LedgerId == ledgerId)
                ?? throw new InvalidOperationException($"{typeof(T).Name} {entity.Id} was not found.");
        }

        db.Entry(target).CurrentValues.SetValues(entity);
        target.LedgerId = ledgerId;
        if (entity.Id == 0)
            target.Id = 0; // let the database assign it

        await db.SaveChangesAsync();
        entity.Id = target.Id;
        entity.LedgerId = ledgerId;
    }

    private async Task DeleteAsync<T>(int id, LedgerRole role) where T : class, ILedgerEntity
    {
        var scope = await GetScopeAsync();
        var visible = scope.VisibleLedgerIds;
        await using var db = await dbFactory.CreateDbContextAsync();
        var ledgerId = await db.Set<T>().Where(e => e.Id == id && visible.Contains(e.LedgerId)).Select(e => (int?)e.LedgerId).SingleOrDefaultAsync();
        if (ledgerId is null)
            return; // already gone, or not something the user can see
        RequireRole(scope, ledgerId.Value, role);
        await db.Set<T>().Where(e => e.Id == id && e.LedgerId == ledgerId).ExecuteDeleteAsync();
    }
}
