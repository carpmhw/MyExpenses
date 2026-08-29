using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Data;

public sealed class BankAccountCurrencyPersistenceTests
{
    /// <summary>驗證銀行帳戶模型的貨幣預設值為 TWD。</summary>
    [Fact]
    public void BankAccount_DefaultCurrencyIsTwd()
    {
        var account = new BankAccount();

        Assert.Equal(CurrencyPolicy.BaseCurrencyCode, account.CurrencyCode);
    }

    /// <summary>驗證同步儲存會正規化銀行帳戶貨幣代碼。</summary>
    [Fact]
    public async Task SaveChanges_NormalizesBankAccountCurrencyCode()
    {
        await using var db = await CreateDbContextAsync();
        var account = new BankAccount
        {
            BankName = "測試銀行",
            AccountNumber = "12345",
            AccountType = "活期",
            CurrencyCode = " usd ",
            Balance = 3000m,
        };

        db.BankAccounts.Add(account);
        db.SaveChanges();

        db.ChangeTracker.Clear();
        var stored = db.BankAccounts.Single(item => item.Id == account.Id);
        Assert.Equal("USD", stored.CurrencyCode);
        Assert.Equal(3000m, stored.Balance);
    }

    /// <summary>驗證異步儲存拒絕不支援的銀行帳戶貨幣代碼。</summary>
    [Fact]
    public async Task SaveChangesAsync_RejectsUnsupportedBankAccountCurrencyCode()
    {
        await using var db = await CreateDbContextAsync();
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "測試銀行",
            AccountNumber = "12345",
            AccountType = "活期",
            CurrencyCode = "EUR",
        });

        await Assert.ThrowsAsync<ArgumentException>(() => db.SaveChangesAsync());
        Assert.Equal(0, await db.BankAccounts.CountAsync());
    }

    /// <summary>驗證持久化貨幣資料變更不會改寫銀行帳戶原幣餘額。</summary>
    [Fact]
    public async Task SaveChangesAsync_PreservesOriginalBalanceWhenCurrencyChanges()
    {
        await using var db = await CreateDbContextAsync();
        var account = new BankAccount
        {
            BankName = "測試銀行",
            AccountNumber = "12345",
            AccountType = "活期",
            CurrencyCode = "USD",
            Balance = 3000m,
        };
        db.BankAccounts.Add(account);
        await db.SaveChangesAsync();

        account.CurrencyCode = "JPY";
        await db.SaveChangesAsync();

        Assert.Equal("JPY", account.CurrencyCode);
        Assert.Equal(3000m, account.Balance);
    }

    /// <summary>建立使用開啟中 SQLite 連線的持久化測試 context。</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
