using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class BankAccountCurrencyMigrationTests
{
    /// <summary>驗證舊帳戶與 JSON 銀行明細會安全回填為 TWD 固定估值。</summary>
    [Fact]
    public async Task Migration_BackfillsLegacyBankAccountsAndSnapshotDetails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync("20260827135538_AddStockDividendTransaction");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO BankAccounts (BankName, AccountNumber, Balance, AccountType, CreatedAt, UpdatedAt) "
            + "VALUES ('舊銀行', '12345', 3000, '活期', '2026-08-01 00:00:00', '2026-08-01 00:00:00')");
        const string legacyBankDetails = "[{\"BankName\":\"舊銀行\",\"AccountNumber\":\"12345\",\"AccountType\":\"活期\",\"Balance\":\"3000.0\"}]";
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO SnapshotBatches (Name, SnapshotDate, Notes, TotalAssets, TotalLiabilities, TotalNetWorth, NetWorthBasis, TotalBankBalance, TotalStockValue, TotalStockCost, BankDetails, StockDetails) "
            + "VALUES ('legacy', '2026-08-01 00:00:00', NULL, 5000, NULL, 5000, 'AssetsOnly', 3000, 2000, 1500, {0}, '[]')",
            legacyBankDetails);

        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        var account = await db.BankAccounts.SingleAsync();
        var snapshot = await db.SnapshotBatches.SingleAsync();
        var detail = Assert.Single(snapshot.BankDetails);

        Assert.Equal("TWD", account.CurrencyCode);
        Assert.Equal(3000m, account.Balance);
        Assert.Equal(5000m, snapshot.TotalAssets);
        Assert.Equal(3000m, snapshot.TotalBankBalance);
        Assert.Equal("TWD", detail.CurrencyCode);
        Assert.Equal(1m, detail.ExchangeRate);
        Assert.Equal("TWD", detail.BaseCurrencyCode);
        Assert.Equal(3000m, detail.ConvertedBalance);
        Assert.False(snapshot.ExchangeRateIsStale);
        Assert.Null(snapshot.ExchangeRateUpdatedAt);
    }

    /// <summary>驗證 fresh database migration 會建立 required 貨幣欄位與預設值。</summary>
    [Fact]
    public async Task Migration_CreatesRequiredCurrencyColumnsOnFreshDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        await db.Database.MigrateAsync();

        Assert.Contains("CurrencyCode", await ReadColumnNamesAsync(db, "BankAccounts"));
        Assert.Contains("ExchangeRateUpdatedAt", await ReadColumnNamesAsync(db, "SnapshotBatches"));
        Assert.Contains("ExchangeRateIsStale", await ReadColumnNamesAsync(db, "SnapshotBatches"));
    }

    /// <summary>驗證升級前 verified backup 可完成 migration、保留舊總額並支援混合幣別快照。</summary>
    [Fact]
    public async Task MigrationFromVerifiedBackup_PreservesLegacyTotalsAndSupportsMixedCurrency()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var legacyPath = Path.Combine(temporaryDirectory.RootPath, "legacy.db");
        var backupDirectory = Path.Combine(temporaryDirectory.RootPath, "backups");
        var upgradedPath = Path.Combine(temporaryDirectory.RootPath, "upgraded.db");

        await using (var legacyConnection = new SqliteConnection($"Data Source={legacyPath}"))
        {
            await legacyConnection.OpenAsync();
            await using var legacyDb = CreateDb(legacyConnection);
            await legacyDb.Database.MigrateAsync("20260827135538_AddStockDividendTransaction");
            await legacyDb.Database.ExecuteSqlRawAsync(
                "INSERT INTO BankAccounts (BankName, AccountNumber, Balance, AccountType, CreatedAt, UpdatedAt) "
                + "VALUES ('舊銀行', '12345', 3000, '活期', '2026-08-01 00:00:00', '2026-08-01 00:00:00')");
            const string legacyBankDetails = "[{\"BankName\":\"舊銀行\",\"AccountNumber\":\"12345\",\"AccountType\":\"活期\",\"Balance\":\"3000.0\"}]";
            await legacyDb.Database.ExecuteSqlRawAsync(
                "INSERT INTO SnapshotBatches (Name, SnapshotDate, Notes, TotalAssets, TotalLiabilities, TotalNetWorth, NetWorthBasis, TotalBankBalance, TotalStockValue, TotalStockCost, BankDetails, StockDetails) "
                + "VALUES ('legacy', '2026-08-01 00:00:00', NULL, 5000, NULL, 5000, 'AssetsOnly', 3000, 2000, 1500, {0}, '[]')",
                legacyBankDetails);
        }

        var backupService = new SqliteBackupService(new SqliteBackupOptions
        {
            DatabasePath = legacyPath,
            BackupDirectory = backupDirectory,
            RetentionLimit = 1,
        });
        var backup = await backupService.CreateVerifiedBackupAsync("20260827135538_AddStockDividendTransaction");
        Assert.True(backup.Succeeded, backup.FailureReason);
        File.Copy(backup.BackupPath!, upgradedPath);

        await using var upgradedConnection = new SqliteConnection($"Data Source={upgradedPath}");
        await upgradedConnection.OpenAsync();
        await using var db = CreateDb(upgradedConnection);
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        var legacyAccount = await db.BankAccounts.SingleAsync();
        var legacySnapshot = await db.SnapshotBatches.SingleAsync();
        Assert.Equal("TWD", legacyAccount.CurrencyCode);
        Assert.Equal(3000m, legacyAccount.Balance);
        Assert.Equal(5000m, legacySnapshot.TotalAssets);
        Assert.Equal(3000m, legacySnapshot.TotalBankBalance);

        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "23456",
            Balance = 310m,
            AccountType = "活期",
            CurrencyCode = "USD",
        });
        await db.SaveChangesAsync();

        var mixedCurrencySnapshot = await SnapshotEndpoints.CreateSnapshotAsync(
            db,
            new ExchangeRateService(new FixedExchangeRateProvider()));
        Assert.Equal(13000m, mixedCurrencySnapshot.TotalBankBalance);
        Assert.Equal(13000m, mixedCurrencySnapshot.TotalAssets);
        Assert.Equal(3000m, Assert.Single(mixedCurrencySnapshot.BankDetails, detail => detail.CurrencyCode == "TWD").ConvertedBalance);
        Assert.Equal(10000m, Assert.Single(mixedCurrencySnapshot.BankDetails, detail => detail.CurrencyCode == "USD").ConvertedBalance);
    }

    /// <summary>建立使用開啟中 SQLite 連線的 migration 測試 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>讀取指定 SQLite table 的欄位名稱。</summary>
    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(AppDbContext db, string tableName)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info([{tableName}])";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(1));
        return names;
    }

    /// <summary>提供每個 migration smoke test 獨立且自動清理的暫存目錄。</summary>
    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        /// <summary>建立 migration smoke test 使用的暫存目錄。</summary>
        public TemporaryDirectory()
        {
            RootPath = Directory.CreateTempSubdirectory("myexpenses-migration-tests-").FullName;
        }

        /// <summary>取得暫存目錄的絕對路徑。</summary>
        public string RootPath { get; }

        /// <summary>刪除測試產生的 database、backup 與暫存檔案。</summary>
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>提供 migration smoke test 使用的固定 USD 匯率 provider。</summary>
    private sealed class FixedExchangeRateProvider : IExchangeRateProvider
    {
        /// <summary>回傳固定的 TWD 基準匯率，避免 migration 測試依賴外部網路。</summary>
        public Task<ExchangeRateProviderResult> FetchAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ExchangeRateProviderResult(
                new Dictionary<string, decimal>
                {
                    ["TWD"] = 1m,
                    ["USD"] = 0.031m,
                },
                new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc)));
    }
}
