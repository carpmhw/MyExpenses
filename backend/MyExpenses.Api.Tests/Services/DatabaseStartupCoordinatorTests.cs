using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class DatabaseStartupCoordinatorTests
{
    /// <summary>驗證既有資料庫會先建立含舊 migration identity 的 verified backup 才套用新 migration。</summary>
    [Fact]
    public async Task InitializeAsync_BackupsExistingDatabaseBeforePendingMigration()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "MyExpenses.db");
        await using var db = await CreateDatabaseAtMigrationAsync(databasePath);
        var backupDirectory = Path.Combine(temporaryDirectory.RootPath, "backups");
        var backupService = CreateBackupService(databasePath, backupDirectory);
        var coordinator = new DatabaseStartupCoordinator(backupService);

        await coordinator.InitializeAsync(db, (_, _) => Task.CompletedTask);

        Assert.True(coordinator.IsReady);
        Assert.Contains(
            "20260802132902_AddSingleOwnerInvariant",
            await db.Database.GetAppliedMigrationsAsync());
        var backups = Directory.GetFiles(backupDirectory, "*.db");
        var metadata = await SqliteBackupService.ReadMetadataAsync(backups.Single());
        Assert.Equal("20260802090418_AddAtomicFinancialCommands", metadata!.MigrationIdentity);
    }

    /// <summary>驗證 pre-migration backup 失敗時不會套用 migration 或宣告 readiness。</summary>
    [Fact]
    public async Task InitializeAsync_StopsBeforeMigrationWhenBackupFails()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "MyExpenses.db");
        await using var db = await CreateDatabaseAtMigrationAsync(databasePath);
        var blockedBackupPath = Path.Combine(temporaryDirectory.RootPath, "backup-target");
        await File.WriteAllTextAsync(blockedBackupPath, "not a directory");
        var backupService = CreateBackupService(databasePath, blockedBackupPath);
        var coordinator = new DatabaseStartupCoordinator(backupService);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.InitializeAsync(db, (_, _) => Task.CompletedTask));

        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(coordinator.IsReady);
        Assert.DoesNotContain(
            "20260802132902_AddSingleOwnerInvariant",
            await db.Database.GetAppliedMigrationsAsync());
    }

    /// <summary>建立停在上一個 migration 的實體 SQLite database，模擬既有安裝。</summary>
    private static async Task<AppDbContext> CreateDatabaseAtMigrationAsync(string databasePath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var db = new AppDbContext(options);
        await db.Database.MigrateAsync("20260802090418_AddAtomicFinancialCommands");
        return db;
    }

    /// <summary>建立測試用 consistent backup service。</summary>
    private static SqliteBackupService CreateBackupService(string databasePath, string backupDirectory)
        => new(new SqliteBackupOptions
        {
            DatabasePath = databasePath,
            BackupDirectory = backupDirectory,
            RetentionLimit = 7,
        });

    /// <summary>提供每個測試獨立且自動清理的暫存目錄。</summary>
    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        /// <summary>建立測試暫存目錄。</summary>
        public TemporaryDirectory()
        {
            RootPath = Directory.CreateTempSubdirectory("myexpenses-startup-tests-").FullName;
        }

        /// <summary>取得測試暫存目錄的絕對路徑。</summary>
        public string RootPath { get; }

        /// <summary>刪除測試產生的所有資料。</summary>
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
