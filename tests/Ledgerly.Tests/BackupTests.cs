using System.IO.Compression;
using Ledgerly.Data;
using Ledgerly.Services;
using Ledgerly.Services.Backup;
using Ledgerly.Services.Email;
using Ledgerly.Services.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ledgerly.Tests;

/// <summary>Whole-install backups, restored into a separate install with its own database and encryption keys.</summary>
public sealed class BackupTests : IAsyncLifetime
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ledgerly-backup-tests-").FullName;
    private Install _source = null!;
    private Install _target = null!;

    public async Task InitializeAsync()
    {
        _source = await Install.CreateAsync(Path.Combine(_folder, "source", "ledgerly.db"));
        _target = await Install.CreateAsync(Path.Combine(_folder, "target", "ledgerly.db"));
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Restoring_a_backup_replaces_all_data_and_keeps_the_email_password_readable()
    {
        await using (var db = _source.Db())
        {
            db.Users.Add(new ApplicationUser { Id = "alice", UserName = "alice@example.com", Email = "alice@example.com", PasswordHash = "hash" });
            var ledger = new Ledger { OwnerId = "alice", Name = "My ledger", CreatedAt = DateTime.UtcNow };
            db.Ledgers.Add(ledger);
            await db.SaveChangesAsync();
            db.Bills.Add(new Bill
            {
                LedgerId = ledger.Id, Name = "Rent", ExpectedAmount = 1200m, StartDate = new DateOnly(2026, 1, 1),
                Occurrences = [new BillOccurrence { DueDate = new DateOnly(2026, 9, 1), PaidOn = new DateOnly(2026, 8, 30), Amount = 1210m }]
            });
            await db.SaveChangesAsync();
        }
        await _source.Email.SaveAsync(new EmailOptions { Host = "smtp.example.com", Port = 587, Password = "smtp-secret", FromAddress = "me@example.com" });

        await using (var db = _target.Db())
        {
            db.Users.Add(new ApplicationUser { Id = "old", UserName = "old@example.com", Email = "old@example.com" });
            await db.SaveChangesAsync();
        }

        var backup = new MemoryStream();
        await _source.Backups.WriteBackupAsync(backup);
        backup.Position = 0;
        var manifest = await _target.Backups.RestoreAsync(backup);

        Assert.Equal((1, 1), (manifest.Users, manifest.Ledgers));
        await using (var db = _target.Db())
        {
            Assert.Equal("alice", Assert.Single(await db.Users.ToListAsync()).Id);
            var bill = await db.Bills.Include(b => b.Occurrences).SingleAsync();
            Assert.Equal(1210m, Assert.Single(bill.Occurrences).Amount);
        }

        // The target has different encryption keys, so the password only works because it was re-encrypted there.
        var email = await _target.Email.GetEffectiveAsync();
        Assert.Equal(("smtp.example.com", "smtp-secret"), (email.Options.Host, email.Options.Password));
        Assert.Equal(1, _target.Generation.Value);
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, "target", "backups"), "before-restore-*.db"));
    }

    [Fact]
    public async Task Backups_from_a_newer_version_or_other_files_are_refused_without_changing_anything()
    {
        await using (var db = _source.Db())
            await db.Database.ExecuteSqlRawAsync("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29990101000000_FromTheFuture', '99.0')");
        await using (var db = _target.Db())
        {
            db.Users.Add(new ApplicationUser { Id = "old", UserName = "old@example.com", Email = "old@example.com" });
            await db.SaveChangesAsync();
        }

        var newer = new MemoryStream();
        await _source.Backups.WriteBackupAsync(newer);
        newer.Position = 0;
        var error = await Assert.ThrowsAsync<BackupException>(() => _target.Backups.RestoreAsync(newer));
        Assert.Contains("newer version", error.Message);

        await Assert.ThrowsAsync<BackupException>(() => _target.Backups.RestoreAsync(new MemoryStream("not a zip"u8.ToArray())));

        var noManifest = new MemoryStream();
        using (var zip = new ZipArchive(noManifest, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("ledgerly.db");
        noManifest.Position = 0;
        await Assert.ThrowsAsync<BackupException>(() => _target.Backups.RestoreAsync(noManifest));

        await using (var db = _target.Db())
            Assert.Equal("old", Assert.Single(await db.Users.ToListAsync()).Id);
        Assert.Equal(0, _target.Generation.Value);
    }

    private sealed class Install
    {
        private readonly DbFactory _factory;

        private Install(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _factory = new DbFactory(new DbContextOptionsBuilder<LedgerlyDbContext>().UseSqlite($"Data Source={path}").Options);
            Email = new EmailSettingsStore(new StaticOptionsMonitor(), new AppSettingsStore(_factory), new EphemeralDataProtectionProvider(), NullLogger<EmailSettingsStore>.Instance);
            Backups = new BackupService(_factory, Email, Generation, new AppInfo(new ConfigurationBuilder().Build()), TimeProvider.System, NullLogger<BackupService>.Instance);
        }

        public EmailSettingsStore Email { get; }
        public DataGeneration Generation { get; } = new();
        public BackupService Backups { get; }

        public LedgerlyDbContext Db() => _factory.CreateDbContext();

        public static async Task<Install> CreateAsync(string path)
        {
            var install = new Install(path);
            await using var db = install.Db();
            await db.Database.MigrateAsync();
            return install;
        }
    }

    private sealed class DbFactory(DbContextOptions<LedgerlyDbContext> options) : IDbContextFactory<LedgerlyDbContext>
    {
        public LedgerlyDbContext CreateDbContext() => new(options);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<EmailOptions>
    {
        public EmailOptions CurrentValue { get; } = new();
        public EmailOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<EmailOptions, string?> listener) => null;
    }
}
