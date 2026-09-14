using Ledgerly.Data;
using Ledgerly.Finance;
using Microsoft.EntityFrameworkCore;

namespace Ledgerly.Services;

/// <summary>
/// Data access for the app, always limited to the signed-in user's active ledger.
/// Each call uses its own short-lived DbContext.
/// </summary>
public class LedgerService(IDbContextFactory<LedgerlyDbContext> dbFactory, ICurrentUser currentUser, TimeProvider clock)
{
    private const string MyLedgerName = "My ledger";
    private const string SampleLedgerName = "Sample data";

    private Ledger? _activeLedger;

    public DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    // Ledgers

    /// <summary>The ledger being viewed. Creates the user's own ledger on first use.</summary>
    public async Task<Ledger> GetActiveLedgerAsync()
    {
        if (_activeLedger is not null)
            return _activeLedger;

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
            _activeLedger = ledger;

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

    // Accounts

    public async Task<List<Account>> GetAccountsAsync(bool activeOnly = false)
    {
        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && (!activeOnly || a.IsActive))
            .ToListAsync();
        return accounts.OrderBy(a => a.Type).ThenBy(a => a.Name).ToList();
    }

    public Task SaveAccountAsync(Account account) => SaveAsync(account);

    public Task DeleteAccountAsync(int id) => DeleteAsync<Account>(id);

    // Categories

    public async Task<List<Category>> GetCategoriesAsync()
    {
        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var categories = await db.Categories.AsNoTracking().Where(c => c.LedgerId == ledgerId).ToListAsync();
        return categories.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Adds or renames a category. Throws <see cref="LedgerValidationException"/> for a blank or duplicate name.</summary>
    public async Task SaveCategoryAsync(Category category)
    {
        category.Name = category.Name.Trim();
        if (category.Name.Length == 0)
            throw new LedgerValidationException("Enter a category name.");
        if (category.Name.Length > 50)
            throw new LedgerValidationException("Category names can be up to 50 characters.");

        var ledgerId = await LedgerIdAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var names = await db.Categories.AsNoTracking()
                .Where(c => c.LedgerId == ledgerId && c.Id != category.Id)
                .Select(c => c.Name)
                .ToListAsync();
            if (names.Contains(category.Name, StringComparer.OrdinalIgnoreCase))
                throw new LedgerValidationException($"There's already a category called \"{category.Name}\".");
        }

        await SaveAsync(category);
    }

    /// <summary>Deletes a category. Its bills are kept and become uncategorized.</summary>
    public Task DeleteCategoryAsync(int id) => DeleteAsync<Category>(id);

    // Payees

    public async Task<List<Payee>> GetPayeesAsync()
    {
        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var payees = await db.Payees.AsNoTracking().Where(p => p.LedgerId == ledgerId).ToListAsync();
        return payees.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Adds or updates a payee. Throws <see cref="LedgerValidationException"/> for a blank or duplicate
    /// name, or a website that isn't a valid http(s) address. The website is stored normalized.
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

        var ledgerId = await LedgerIdAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var names = await db.Payees.AsNoTracking()
                .Where(p => p.LedgerId == ledgerId && p.Id != payee.Id)
                .Select(p => p.Name)
                .ToListAsync();
            if (names.Contains(payee.Name, StringComparer.OrdinalIgnoreCase))
                throw new LedgerValidationException($"There's already a payee called \"{payee.Name}\".");
        }

        await SaveAsync(payee);
    }

    /// <summary>Deletes a payee. Its bills and loans are kept, without a payee or lender.</summary>
    public Task DeletePayeeAsync(int id) => DeleteAsync<Payee>(id);

    // Bills

    public async Task<List<Bill>> GetBillsAsync()
    {
        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var bills = await db.Bills.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .Include(b => b.PayFromAccount)
            .Include(b => b.Loan).ThenInclude(l => l!.Lender)
            .Include(b => b.Payee)
            .Include(b => b.Category)
            .Include(b => b.Occurrences)
            .ToListAsync();
        return bills.OrderBy(b => b.Name).ToList();
    }

    public async Task SaveBillAsync(Bill bill)
    {
        var ledgerId = await LedgerIdAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await EnsureInLedgerAsync(db.Accounts, bill.PayFromAccountId, ledgerId);
            await EnsureInLedgerAsync(db.Loans, bill.LoanId, ledgerId);
            await EnsureInLedgerAsync(db.Categories, bill.CategoryId, ledgerId);
            await EnsureInLedgerAsync(db.Payees, bill.PayeeId, ledgerId);
        }
        await SaveAsync(bill);
    }

    public Task DeleteBillAsync(int id) => DeleteAsync<Bill>(id);

    /// <summary>
    /// Records the amount and/or payment for one due date of a bill. For loan bills, <paramref name="paymentKind"/>
    /// overrides whether this payment is the monthly payment or extra principal (null uses the bill's setting).
    /// </summary>
    public async Task RecordOccurrenceAsync(int billId, DateOnly dueDate, decimal? amount, DateOnly? paidOn, string? notes = null, LoanPaymentKind? paymentKind = null)
    {
        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        await EnsureInLedgerAsync(db.Bills, billId, ledgerId);
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
    /// Records an extra principal payment already made on a loan, as a one-time paid bill so it also
    /// appears in the paying account's running balance.
    /// </summary>
    public async Task RecordExtraLoanPaymentAsync(int loanId, int? payFromAccountId, decimal amount, DateOnly paidOn, string? notes)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "The payment must be more than zero.");

        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        await EnsureInLedgerAsync(db.Loans, loanId, ledgerId);
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

    public async Task<List<Income>> GetIncomesAsync()
    {
        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var incomes = await db.Incomes.AsNoTracking()
            .Where(i => i.LedgerId == ledgerId)
            .Include(i => i.DepositToAccount)
            .ToListAsync();
        return incomes.OrderBy(i => i.Name).ToList();
    }

    public async Task SaveIncomeAsync(Income income)
    {
        var ledgerId = await LedgerIdAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
            await EnsureInLedgerAsync(db.Accounts, income.DepositToAccountId, ledgerId);
        await SaveAsync(income);
    }

    public Task DeleteIncomeAsync(int id) => DeleteAsync<Income>(id);

    // Loans

    public async Task<List<Loan>> GetLoansAsync()
    {
        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var loans = await db.Loans.AsNoTracking().Where(l => l.LedgerId == ledgerId).Include(l => l.Lender).ToListAsync();
        return loans.OrderBy(l => l.Name).ToList();
    }

    public async Task<Loan?> GetLoanAsync(int id)
    {
        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Loans.AsNoTracking().Include(l => l.Lender).SingleOrDefaultAsync(l => l.Id == id && l.LedgerId == ledgerId);
    }

    public async Task SaveLoanAsync(Loan loan)
    {
        var ledgerId = await LedgerIdAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
            await EnsureInLedgerAsync(db.Payees, loan.LenderId, ledgerId);
        await SaveAsync(loan);
    }

    public Task DeleteLoanAsync(int id) => DeleteAsync<Loan>(id);

    // Projection

    public async Task<ProjectionResult> ProjectAsync(IEnumerable<int> accountIds, DateOnly through, ProjectionMode mode = ProjectionMode.Expected)
    {
        var ids = accountIds.ToHashSet();
        var accounts = (await GetAccountsAsync()).Where(a => ids.Contains(a.Id)).ToList();
        return Projection.Build(accounts, await GetBillsAsync(), await GetIncomesAsync(), through, mode);
    }

    public async Task<bool> HasAnyDataAsync()
    {
        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Accounts.AnyAsync(a => a.LedgerId == ledgerId)
            || await db.Bills.AnyAsync(b => b.LedgerId == ledgerId)
            || await db.Loans.AnyAsync(l => l.LedgerId == ledgerId);
    }

    // Helpers

    private async Task<string> RequireUserIdAsync() =>
        await currentUser.GetUserIdAsync() ?? throw new UnauthorizedAccessException("Sign in to use Ledgerly.");

    private async Task<int> LedgerIdAsync() => (await GetActiveLedgerAsync()).Id;

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
        _activeLedger = ledger;
    }

    /// <summary>Rejects references (e.g. a bill's pay-from account) to rows outside the active ledger.</summary>
    private static async Task EnsureInLedgerAsync<T>(DbSet<T> set, int? id, int ledgerId) where T : class, ILedgerEntity
    {
        if (id is { } value && !await set.AnyAsync(e => e.Id == value && e.LedgerId == ledgerId))
            throw new InvalidOperationException($"{typeof(T).Name} {value} was not found.");
    }

    /// <summary>
    /// Inserts or updates scalar values only, so navigation properties on the passed-in object are
    /// never attached. Updates only succeed for rows in the active ledger.
    /// </summary>
    private async Task SaveAsync<T>(T entity) where T : class, ILedgerEntity, new()
    {
        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
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

    private async Task DeleteAsync<T>(int id) where T : class, ILedgerEntity
    {
        var ledgerId = await LedgerIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Set<T>().Where(e => e.Id == id && e.LedgerId == ledgerId).ExecuteDeleteAsync();
    }
}
