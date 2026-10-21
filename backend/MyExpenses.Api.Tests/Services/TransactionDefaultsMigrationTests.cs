using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class TransactionDefaultsMigrationTests
{
    /// <summary>驗證舊資料庫升級後不推定預設，並保留時區、帳戶與卡片資料。</summary>
    [Fact]
    public async Task Migration_PreservesExistingDataAndLeavesDefaultsUnset()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync("20260829102909_AddBankAccountCurrency");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO SystemSettings (Id, TimeZoneId) VALUES (1, 'America/New_York')");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO BankAccounts (BankName, AccountNumber, Balance, AccountType, CurrencyCode, CreatedAt, UpdatedAt)
            VALUES ('既有銀行', '12345', '123.456', '活期', 'USD', '2026-08-01', '2026-08-01');
            INSERT INTO CreditCards (BankName, LastFourDigits, StatementDay, DueDay, CreditLimit, CreatedAt, UpdatedAt)
            VALUES ('既有卡片', '1234', 1, 15, '50000', '2026-08-01', '2026-08-01');
            INSERT INTO Withdrawals (Amount, Date, BankAccountId, Description)
            VALUES ('12.34', '2026-08-02', 1, '既有提款');
            """);

        await db.Database.MigrateAsync();

        await using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(SystemSettings)";
        var names = new List<string>();
        await using (var reader = await columns.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add(reader.GetString(1));
        Assert.Contains("DefaultBankAccountId", names);
        Assert.Contains("DefaultCreditCardId", names);

        await using var settings = connection.CreateCommand();
        settings.CommandText = "SELECT TimeZoneId, DefaultBankAccountId, DefaultCreditCardId FROM SystemSettings WHERE Id = 1";
        await using (var reader = await settings.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("America/New_York", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
        }
        Assert.Equal(123.456m, (await db.BankAccounts.SingleAsync()).Balance);
        Assert.Equal("USD", (await db.BankAccounts.SingleAsync()).CurrencyCode);
        Assert.Equal("1234", (await db.CreditCards.SingleAsync()).LastFourDigits);
        Assert.Equal(12.34m, (await db.Withdrawals.SingleAsync()).Amount);
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await integrity.ExecuteScalarAsync());
        integrity.CommandText = "PRAGMA foreign_key_check";
        Assert.Null(await integrity.ExecuteScalarAsync());
    }
}
