using System.IO.Compression;
using System.Text.Json;
using Ledgerly.Data;
using Ledgerly.Services.Email;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ledgerly.Services.Backup;

/// <summary>A backup can't be read or restored. The message is safe to show to the admin.</summary>
public class BackupException(string message) : Exception(message);

public record BackupManifest(string Format, int FormatVersion, string AppVersion, DateTime CreatedAtUtc, string? LastMigration, int Users, int Ledgers);

/// <summary>
/// Whole-install backups for moving Ledgerly to another server: every user, ledger and setting. A backup is a
/// zip with a consistent copy of the SQLite database, a manifest, and the email server password (which is
/// otherwise encrypted with keys that stay on this install). Restoring replaces all data in this install.
/// </summary>
public class BackupService(
    IDbContextFactory<LedgerlyDbContext> dbFactory,
    EmailSettingsStore emailSettings,
    DataGeneration dataGeneration,
    AppInfo appInfo,
    TimeProvider clock,
    ILogger<BackupService> logger)
{
    public const string FormatName = "ledgerly-backup";
    public const int FormatVersion = 1;
    public const long MaxBackupBytes = 256L * 1024 * 1024;

    private const string ManifestEntry = "manifest.json";
    private const string DatabaseEntry = "ledgerly.db";
    private const string SecretsEntry = "secrets.json";
    private const int SafetyCopiesToKeep = 5;

    private static readonly SemaphoreSlim RestoreLock = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string FileName() => $"ledgerly-backup-{clock.GetLocalNow():yyyyMMdd-HHmmss}.zip";

    /// <summary>Writes a backup of the whole install to <paramref name="destination"/>.</summary>
    public async Task WriteBackupAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        var workDir = CreateWorkDirectory();
        try
        {
            var snapshotPath = Path.Combine(workDir, DatabaseEntry);
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
                await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [snapshotPath], cancellationToken);

            var manifest = await ReadManifestFromDatabaseAsync(snapshotPath, cancellationToken);
            var emailPassword = await emailSettings.GetSavedPasswordAsync();

            await using var zip = await ZipArchive.CreateAsync(destination, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken);
            await WriteJsonEntryAsync(zip, ManifestEntry, manifest, cancellationToken);
            await using (var entry = await zip.CreateEntry(DatabaseEntry, CompressionLevel.Optimal).OpenAsync(cancellationToken))
            await using (var snapshot = File.OpenRead(snapshotPath))
                await snapshot.CopyToAsync(entry, cancellationToken);
            if (emailPassword is not null)
                await WriteJsonEntryAsync(zip, SecretsEntry, new BackupSecrets(emailPassword), cancellationToken);

            logger.LogInformation("Created a backup with {Users} users and {Ledgers} ledgers.", manifest.Users, manifest.Ledgers);
        }
        finally
        {
            DeleteQuietly(workDir);
        }
    }

    /// <summary>
    /// Replaces all data in this install with the backup's. A copy of the current database is saved next to it
    /// first. Older backups are upgraded; backups from a newer version of Ledgerly are refused.
    /// </summary>
    public async Task<BackupManifest> RestoreAsync(Stream backup, CancellationToken cancellationToken = default)
    {
        if (!await RestoreLock.WaitAsync(0, cancellationToken))
            throw new BackupException("Another restore is already running.");

        var workDir = CreateWorkDirectory();
        try
        {
            var (manifest, databasePath, secrets) = await ExtractAsync(backup, workDir, cancellationToken);
            await ValidateDatabaseAsync(databasePath, cancellationToken);

            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                var liveConnectionString = db.Database.GetConnectionString()
                    ?? throw new InvalidOperationException("The database connection string isn't set.");
                await SaveSafetyCopyAsync(db, liveConnectionString, cancellationToken);
                ReplaceDatabase(databasePath, liveConnectionString);
            }

            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
                await db.Database.MigrateAsync(cancellationToken);

            dataGeneration.Increment();
            emailSettings.Reload();
            if (secrets?.EmailPassword is { Length: > 0 } password)
                await emailSettings.SetSavedPasswordAsync(password);

            logger.LogWarning("Restored a backup from Ledgerly {Version} created {CreatedAt:u}, with {Users} users and {Ledgers} ledgers. All previous data was replaced.",
                manifest.AppVersion, manifest.CreatedAtUtc, manifest.Users, manifest.Ledgers);
            return manifest;
        }
        finally
        {
            DeleteQuietly(workDir);
            RestoreLock.Release();
        }
    }

    private async Task<(BackupManifest Manifest, string DatabasePath, BackupSecrets? Secrets)> ExtractAsync(Stream backup, string workDir, CancellationToken cancellationToken)
    {
        ZipArchive zip;
        try
        {
            zip = await ZipArchive.CreateAsync(backup, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, cancellationToken);
        }
        catch (InvalidDataException)
        {
            throw new BackupException("That file isn't a Ledgerly backup (it isn't a zip file).");
        }

        await using (zip)
        {
            var manifest = await ReadJsonEntryAsync<BackupManifest>(zip, ManifestEntry, cancellationToken);
            if (manifest?.Format != FormatName)
                throw new BackupException("That file isn't a Ledgerly backup.");
            if (manifest.FormatVersion > FormatVersion)
                throw new BackupException($"That backup was made by a newer version of Ledgerly ({manifest.AppVersion}). Update this install first.");

            var databaseEntry = zip.GetEntry(DatabaseEntry) ?? throw new BackupException("The backup is missing its database.");
            if (databaseEntry.Length > MaxBackupBytes * 4)
                throw new BackupException("The backup's database is too large.");

            var databasePath = Path.Combine(workDir, DatabaseEntry);
            await using (var source = await databaseEntry.OpenAsync(cancellationToken))
            await using (var target = File.Create(databasePath))
                await source.CopyToAsync(target, cancellationToken);

            return (manifest, databasePath, await ReadJsonEntryAsync<BackupSecrets>(zip, SecretsEntry, cancellationToken));
        }
    }

    /// <summary>Checks the database is intact, is Ledgerly's, and isn't from a newer version than this one.</summary>
    private async Task ValidateDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        var applied = new List<string>();
        try
        {
            await using var connection = OpenConnection(databasePath, SqliteOpenMode.ReadOnly);
            {
                if (await ScalarAsync<string>(connection, "PRAGMA integrity_check", cancellationToken) != "ok")
                    throw new BackupException("The backup's database is damaged.");
                if (await ScalarAsync<long>(connection, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('__EFMigrationsHistory', 'AspNetUsers', 'Ledgers')", cancellationToken) != 3)
                    throw new BackupException("That file isn't a Ledgerly backup.");

                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    applied.Add(reader.GetString(0));
            }
        }
        catch (SqliteException)
        {
            throw new BackupException("The backup's database can't be read.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var known = db.Database.GetMigrations().ToHashSet();
        if (applied.Any(m => !known.Contains(m)))
            throw new BackupException($"That backup was made by a newer version of Ledgerly than this one ({appInfo.Version}). Update this install first, then restore.");
    }

    /// <summary>Keeps a copy of the current database in a "backups" folder beside it, in case the restore was a mistake.</summary>
    private async Task SaveSafetyCopyAsync(LedgerlyDbContext db, string liveConnectionString, CancellationToken cancellationToken)
    {
        var livePath = new SqliteConnectionStringBuilder(liveConnectionString).DataSource;
        if (string.IsNullOrEmpty(livePath) || livePath == ":memory:")
            return;

        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(livePath))!, "backups");
        Directory.CreateDirectory(folder);
        var copyPath = Path.Combine(folder, $"before-restore-{clock.GetLocalNow():yyyyMMdd-HHmmss}.db");
        await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [copyPath], cancellationToken);
        logger.LogInformation("Saved the current database to {Path} before restoring.", copyPath);

        foreach (var old in new DirectoryInfo(folder).GetFiles("before-restore-*.db").OrderByDescending(f => f.Name).Skip(SafetyCopiesToKeep))
            old.Delete();
    }

    /// <summary>Copies the backup into the live database with SQLite's backup API, which replaces its contents in one transaction.</summary>
    private static void ReplaceDatabase(string sourcePath, string liveConnectionString)
    {
        SqliteConnection.ClearAllPools();
        using (var source = OpenConnection(sourcePath, SqliteOpenMode.ReadOnly))
        using (var live = new SqliteConnection(liveConnectionString))
        {
            live.Open();
            source.BackupDatabase(live);
        }
        SqliteConnection.ClearAllPools();
    }

    private async Task<BackupManifest> ReadManifestFromDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(databasePath, SqliteOpenMode.ReadOnly);
        var lastMigration = await ScalarAsync<string>(connection, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1", cancellationToken);
        var users = await ScalarAsync<long>(connection, "SELECT count(*) FROM AspNetUsers", cancellationToken);
        var ledgers = await ScalarAsync<long>(connection, "SELECT count(*) FROM Ledgers WHERE IsSample = 0", cancellationToken);
        return new BackupManifest(FormatName, FormatVersion, appInfo.Version, clock.GetUtcNow().UtcDateTime, lastMigration, (int)users, (int)ledgers);
    }

    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task<T?> ScalarAsync<T>(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task WriteJsonEntryAsync<T>(ZipArchive zip, string name, T value, CancellationToken cancellationToken)
    {
        await using var stream = await zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync(cancellationToken);
        await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken);
    }

    private static async Task<T?> ReadJsonEntryAsync<T>(ZipArchive zip, string name, CancellationToken cancellationToken) where T : class
    {
        if (zip.GetEntry(name) is not { } entry)
            return null;
        try
        {
            await using var stream = await entry.OpenAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken);
        }
        catch (JsonException)
        {
            throw new BackupException("That file isn't a Ledgerly backup.");
        }
    }

    private static string CreateWorkDirectory() => Directory.CreateTempSubdirectory("ledgerly-backup-").FullName;

    private void DeleteQuietly(string directory)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't delete the temporary backup folder {Folder}.", directory);
        }
    }

    private sealed record BackupSecrets(string? EmailPassword);
}
