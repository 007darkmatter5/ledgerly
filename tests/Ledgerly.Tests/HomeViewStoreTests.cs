using Ledgerly.Data;
using Ledgerly.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ledgerly.Tests;

public sealed class HomeViewStoreTests : IAsyncLifetime
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

    [Fact]
    public void Unrecognized_values_stay_on_the_classic_view()
    {
        Assert.Equal(HomeView.Classic, HomeViewStore.Parse(null));
        Assert.Equal(HomeView.Classic, HomeViewStore.Parse(""));
        Assert.Equal(HomeView.Classic, HomeViewStore.Parse("Coverage"));
        Assert.Equal(HomeView.Coverage, HomeViewStore.Parse("coverage"));
    }

    [Fact]
    public async Task The_choice_defaults_to_classic_and_stays_with_that_user()
    {
        var alice = new HomeViewStore(_factory, new FixedUser("alice"));
        var bob = new HomeViewStore(_factory, new FixedUser("bob"));

        Assert.Equal(HomeView.Classic, await alice.GetAsync());
        Assert.Equal(HomeView.Classic, await bob.GetAsync());
        Assert.Equal(HomeView.Classic, await new HomeViewStore(_factory, new FixedUser(null)).GetAsync());

        await alice.SetAsync(HomeView.Coverage);

        Assert.Equal(HomeView.Coverage, await alice.GetAsync());
        Assert.Equal(HomeView.Classic, await bob.GetAsync());

        await alice.SetAsync(HomeView.Classic);

        Assert.Equal(HomeView.Classic, await alice.GetAsync());
        await using var db = _factory.CreateDbContext();
        Assert.Empty(db.UserClaims.Where(c => c.ClaimType == HomeViewStore.ClaimType));
    }

    [Fact]
    public async Task A_signed_out_session_cannot_store_a_choice()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HomeViewStore(_factory, new FixedUser(null)).SetAsync(HomeView.Coverage));
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
