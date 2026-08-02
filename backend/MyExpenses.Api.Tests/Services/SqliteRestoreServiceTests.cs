using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class SqliteRestoreServiceTests
{
    private const string SourceMigration = "20260802090418_AddAtomicFinancialCommands";

    /// <summary>驗證有效 backup 會取代 active database，並保留可讀取的 current rollback copy。</summary>
    [Fact]
    public async Task RestoreVerifiedBackupAsync_RestoresValidBackupAndPreservesRollbackCopy()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporaryDirectory.RootPath, "source.db");
        var activePath = Path.Combine(temporaryDirectory.RootPath, "active.db");
        var backupDirectory = Path.Combine(temporaryDirectory.RootPath, "backups");
        await CreateDatabaseAtSourceMigrationAsync(sourcePath);
        await CreateCurrentDatabaseAsync(activePath);

        var backup = await CreateVerifiedBackupAsync(sourcePath, backupDirectory);
        var service = CreateRestoreService(activePath, backupDirectory);

        var result = await service.RestoreVerifiedBackupAsync(backup.BackupPath!);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(activePath, result.ActiveDatabasePath);
        Assert.NotNull(result.RollbackPath);
        Assert.True(File.Exists(result.RollbackPath));
        Assert.Equal("owner@example.com", await ReadUserEmailAsync(activePath));
        Assert.Equal("current@example.com", await ReadUserEmailAsync(result.RollbackPath!));
    }

    /// <summary>驗證 integrity 損壞的 backup 會被拒絕且 active database 維持原本內容。</summary>
    [Fact]
    public async Task RestoreVerifiedBackupAsync_RejectsCorruptBackupWithoutReplacingActiveDatabase()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var activePath = Path.Combine(temporaryDirectory.RootPath, "active.db");
        var corruptBackupPath = Path.Combine(temporaryDirectory.RootPath, "corrupt.db");
        var backupDirectory = Path.Combine(temporaryDirectory.RootPath, "backups");
        await CreateCurrentDatabaseAsync(activePath);
        await File.WriteAllTextAsync(corruptBackupPath, "not a sqlite database");
        var service = CreateRestoreService(activePath, backupDirectory);

        var result = await service.RestoreVerifiedBackupAsync(corruptBackupPath);

        Assert.False(result.Succeeded);
        Assert.Null(result.RollbackPath);
        Assert.Equal("current@example.com", await ReadUserEmailAsync(activePath));
        Assert.Empty(Directory.GetFiles(temporaryDirectory.RootPath, "*.rollback-*.db"));
    }

    /// <summary>驗證 backup metadata 與 migration history 不一致時不會替換 active database。</summary>
    [Fact]
    public async Task RestoreVerifiedBackupAsync_RejectsMismatchedEmbeddedMetadata()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporaryDirectory.RootPath, "source.db");
        var activePath = Path.Combine(temporaryDirectory.RootPath, "active.db");
        var backupDirectory = Path.Combine(temporaryDirectory.RootPath, "backups");
        await CreateDatabaseAtSourceMigrationAsync(sourcePath);
        await CreateCurrentDatabaseAsync(activePath);
        var backup = await CreateVerifiedBackupAsync(sourcePath, backupDirectory);

        await using (var connection = new SqliteConnection($"Data Source={backup.BackupPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE __MyExpensesBackupMetadata SET MigrationIdentity = 'unknown-migration' WHERE Id = 1";
            await command.ExecuteNonQueryAsync();
        }

        var service = CreateRestoreService(activePath, backupDirectory);
        var result = await service.RestoreVerifiedBackupAsync(backup.BackupPath!);

        Assert.False(result.Succeeded);
        Assert.Equal("current@example.com", await ReadUserEmailAsync(activePath));
        Assert.Empty(Directory.GetFiles(temporaryDirectory.RootPath, "*.rollback-*.db"));
    }

    /// <summary>驗證 restore 後既有 startup coordinator 會套用較新的 migration 並保留 owner 與金融資料。</summary>
    [Fact]
    public async Task RestoreVerifiedBackupAsync_AllowsStartupMigrationAndPreservesOwnerAndFinancialCounts()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporaryDirectory.RootPath, "source.db");
        var activePath = Path.Combine(temporaryDirectory.RootPath, "active.db");
        var backupDirectory = Path.Combine(temporaryDirectory.RootPath, "backups");
        await CreateDatabaseAtSourceMigrationAsync(sourcePath);
        var backup = await CreateVerifiedBackupAsync(sourcePath, backupDirectory);

        var restoreService = CreateRestoreService(activePath, backupDirectory);
        var restoreResult = await restoreService.RestoreVerifiedBackupAsync(backup.BackupPath!);
        Assert.True(restoreResult.Succeeded, restoreResult.FailureReason);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={activePath}")
            .Options;
        await using var db = new AppDbContext(options);
        var coordinator = new DatabaseStartupCoordinator(CreateBackupService(activePath, backupDirectory));

        await coordinator.InitializeAsync(db, (_, _) => Task.CompletedTask);

        Assert.True(coordinator.IsReady);
        Assert.Contains(
            "20260802132902_AddSingleOwnerInvariant",
            await db.Database.GetAppliedMigrationsAsync());
        var owner = await db.Users.SingleAsync();
        Assert.Equal("owner@example.com", owner.Email);
        Assert.True(BCrypt.Net.BCrypt.Verify("Owner!Password123", owner.PasswordHash));
        Assert.Equal(1, await db.Transactions.CountAsync());
        Assert.Equal(1, await db.BankAccounts.CountAsync());
        Assert.Equal(1, await db.CreditCards.CountAsync());
    }

    /// <summary>建立測試用已驗證 backup。</summary>
    private static async Task<SqliteBackupResult> CreateVerifiedBackupAsync(
        string sourcePath,
        string backupDirectory)
    {
        var service = CreateBackupService(sourcePath, backupDirectory);
        var result = await service.CreateVerifiedBackupAsync(SourceMigration);
        Assert.True(result.Succeeded, result.FailureReason);
        return result;
    }

    /// <summary>建立使用指定檔案路徑的 backup service。</summary>
    private static SqliteBackupService CreateBackupService(string databasePath, string backupDirectory)
        => new(new SqliteBackupOptions
        {
            DatabasePath = databasePath,
            BackupDirectory = backupDirectory,
            RetentionLimit = 7,
        });

    /// <summary>建立使用指定 active database 與 backup 目錄的 restore service。</summary>
    private static SqliteRestoreService CreateRestoreService(string databasePath, string backupDirectory)
        => new(new SqliteBackupOptions
        {
            DatabasePath = databasePath,
            BackupDirectory = backupDirectory,
            RetentionLimit = 7,
        });

    /// <summary>建立停在 restore 前一個 migration 且包含 owner 與代表性金融資料的 database。</summary>
    private static async Task CreateDatabaseAtSourceMigrationAsync(string databasePath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync(SourceMigration);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Users (Id, Email, PasswordHash, DisplayName, TotpSecret, IsTwoFactorEnabled, RecoveryCodes, TokenVersion, CreatedAt, UpdatedAt) " +
            "VALUES (42, 'owner@example.com', $password_hash, 'Owner', NULL, 0, NULL, 1, '2026-08-01 00:00:00', '2026-08-01 00:00:00')",
            new SqliteParameter("$password_hash", BCrypt.Net.BCrypt.HashPassword("Owner!Password123")));

        var category = new Category
        {
            Name = "測試分類",
            Type = CategoryType.Expense,
            Icon = "Test",
            Color = "#000000",
            SortOrder = 1,
        };
        var paymentMethod = new PaymentMethod
        {
            Name = "測試付款",
            Icon = "Test",
            Color = "#000000",
            SortOrder = 1,
        };
        db.Categories.Add(category);
        db.PaymentMethods.Add(paymentMethod);
        db.Transactions.Add(new Transaction
        {
            Type = TransactionType.Expense,
            Amount = 123.45m,
            Date = new DateOnly(2026, 8, 1),
            Description = "restore transaction",
            Category = category,
            PaymentMethod = paymentMethod,
        });
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "測試銀行",
            AccountNumber = "12345",
            Balance = 1000m,
            AccountType = "活期",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.CreditCards.Add(new CreditCard
        {
            BankName = "測試銀行",
            LastFourDigits = "1234",
            CardNetwork = "VISA",
            StatementDay = 15,
            DueDay = 23,
            CreditLimit = 10000m,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>建立目前 migration 的 active database，供驗證 rollback copy 內容。</summary>
    private static async Task CreateCurrentDatabaseAsync(string databasePath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        db.Users.Add(new User
        {
            Email = "current@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Current!Password123"),
            DisplayName = "Current Owner",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>讀取指定 database 的 owner email，不依賴 restore 前後的 EF model 差異。</summary>
    private static async Task<string> ReadUserEmailAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Email FROM Users ORDER BY Id LIMIT 1";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>提供每個測試獨立且自動清理的暫存目錄。</summary>
    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        /// <summary>建立測試暫存目錄。</summary>
        public TemporaryDirectory()
        {
            RootPath = Directory.CreateTempSubdirectory("myexpenses-restore-tests-").FullName;
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
