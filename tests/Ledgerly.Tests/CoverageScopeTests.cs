using Ledgerly.Data;
using Ledgerly.Finance;
using Ledgerly.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ledgerly.Tests;

/// <summary>
/// The coverage view reads through <see cref="LedgerService"/>, so a shared ledger only joins the answer
/// while it is accepted and switched on, and one account's balance never funds another account's bill.
/// </summary>
public sealed class CoverageScopeTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private TestDbFactory _factory = null!;
    private readonly SharingNotifier _notifier = new();
    private static readonly DateOnly Today = new(2026, 11, 6);

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

    [Fact]
    public async Task Shared_money_is_included_only_when_visible_and_stays_in_its_own_account()
    {
        var alice = ServiceFor("alice");
        var aliceChecking = new Account { Name = "Alice checking", Type = AccountType.Checking, Balance = 100m, BalanceAsOf = new DateOnly(2026, 11, 1) };
        await alice.SaveAccountAsync(aliceChecking);
        await alice.SaveBillAsync(new Bill { Name = "Alice rent", ExpectedAmount = 900m, StartDate = new DateOnly(2026, 11, 10), PayFromAccountId = aliceChecking.Id });
        await alice.ShareAsync("bob@example.com", LedgerRole.Viewer);

        var bob = ServiceFor("bob");
        var before = await ReportFor(bob);
        Assert.DoesNotContain(before.Events, e => e.Title == "Alice rent");
        Assert.DoesNotContain(before.CashAccounts, a => a.Name == "Alice checking");

        await bob.RespondToInvitationAsync(Assert.Single(await bob.GetInvitationsAsync()).MemberId, accept: true);
        await bob.SaveAccountAsync(new Account { Name = "Bob checking", Type = AccountType.Checking, Balance = 5000m, BalanceAsOf = new DateOnly(2026, 11, 1) });

        var shared = await ReportFor(bob);
        Assert.Equal(CoverageAnswer.Shortfall, shared.Answer);
        Assert.Equal(5100m, shared.RecordedCash);
        Assert.Equal(new DateOnly(2026, 11, 10), shared.CashAccounts.Single(a => a.Name == "Alice checking").ShortfallOn);
        Assert.Null(shared.CashAccounts.Single(a => a.Name == "Bob checking").ShortfallOn);
        Assert.Equal(5000m, shared.CashAccounts.Single(a => a.Name == "Bob checking").ProjectedEnding);

        await bob.SetSharedVisibleAsync(null, false);
        var hidden = await ReportFor(bob);
        Assert.Equal(CoverageAnswer.Covered, hidden.Answer);
        Assert.Equal(5000m, hidden.RecordedCash);
        Assert.DoesNotContain(hidden.Events, e => e.Title == "Alice rent");

        var carol = await ReportFor(ServiceFor("carol"));
        Assert.DoesNotContain(carol.Events, e => e.Title == "Alice rent");
        Assert.DoesNotContain(carol.CashAccounts, a => a.Name == "Alice checking");
    }

    private async Task<CoverageReport> ReportFor(LedgerService service)
    {
        var accounts = await service.GetAccountsAsync(activeOnly: true, includeShared: true);
        var bills = await service.GetBillsAsync(includeShared: true);
        var others = await service.GetCardPaymentsFromOthersAsync(includeShared: true);
        var incomes = await service.GetIncomesAsync(includeShared: true);
        var transfers = await service.GetTransfersAsync(includeShared: true);
        var transactions = await service.GetTransactionsAsync(includeShared: true);
        return Coverage.Build(accounts, bills, incomes, transfers, transactions, Today, CoverageHorizon.EndOfMonth, others);
    }

    private LedgerService ServiceFor(string userId) => new(_factory, new FixedUser(userId), TimeProvider.System, notifier: _notifier);

    private sealed class FixedUser(string? userId) : ICurrentUser
    {
        public Task<string?> GetUserIdAsync() => Task.FromResult(userId);
    }

    private sealed class TestDbFactory(DbContextOptions<LedgerlyDbContext> options) : IDbContextFactory<LedgerlyDbContext>
    {
        public LedgerlyDbContext CreateDbContext() => new(options);
    }
}
