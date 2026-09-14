using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Ledgerly.Data;

public class LedgerlyDbContext(DbContextOptions<LedgerlyDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Ledger> Ledgers => Set<Ledger>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<BillOccurrence> BillOccurrences => Set<BillOccurrence>();
    public DbSet<Income> Incomes => Set<Income>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppSetting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(100);
        });

        modelBuilder.Entity<Ledger>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(100);
            // Deleting a user deletes their ledgers, and with them all of their data.
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(l => l.OwnerId).OnDelete(DeleteBehavior.Cascade);
            // One personal ledger and at most one sample ledger per user.
            e.HasIndex(l => new { l.OwnerId, l.IsSample }).IsUnique();
        });

        modelBuilder.Entity<Account>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(100);
            e.Property(a => a.Type).HasConversion<string>().HasMaxLength(20);
            e.HasOne<Ledger>().WithMany().HasForeignKey(a => a.LedgerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Bill>(e =>
        {
            e.Property(b => b.Name).HasMaxLength(100);
            e.Property(b => b.Frequency).HasConversion<string>().HasMaxLength(20);
            e.Property(b => b.LoanPaymentKind).HasConversion<string>().HasMaxLength(20);
            e.HasOne<Ledger>().WithMany().HasForeignKey(b => b.LedgerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(b => b.PayFromAccount).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(b => b.Loan).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasMany(b => b.Occurrences).WithOne(o => o.Bill).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BillOccurrence>(e =>
        {
            e.HasIndex(o => new { o.BillId, o.DueDate }).IsUnique();
            e.Property(o => o.PaymentKind).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<Income>(e =>
        {
            e.Property(i => i.Name).HasMaxLength(100);
            e.Property(i => i.Frequency).HasConversion<string>().HasMaxLength(20);
            e.HasOne<Ledger>().WithMany().HasForeignKey(i => i.LedgerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(i => i.DepositToAccount).WithMany().OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Loan>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(100);
            e.Property(l => l.Type).HasConversion<string>().HasMaxLength(20);
            e.HasOne<Ledger>().WithMany().HasForeignKey(l => l.LedgerId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
