using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class BankAccountEndpointsTests
{
    /// <summary>Verifies bank account list queries return only accounts matching the bank name filter.</summary>
    [Fact]
    public async Task ListBankAccounts_FiltersByBankNameContainsText()
    {
        await using var db = await CreateDbContextAsync();

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 10, "國泰", db);

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, account => Assert.Contains("國泰", account.BankName));
    }

    /// <summary>Verifies filtered totals are counted before pagination is applied.</summary>
    [Fact]
    public async Task ListBankAccounts_CountsAllMatchingAccountsBeforePagination()
    {
        await using var db = await CreateDbContextAsync();

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 1, "國泰", db);

        Assert.Single(result.Items);
        Assert.Equal(2, result.Total);
    }

    /// <summary>Verifies filtered balance totals are summed before pagination is applied.</summary>
    [Fact]
    public async Task ListBankAccounts_SumsAllMatchingBalancesBeforePagination()
    {
        await using var db = await CreateDbContextAsync();

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 1, "國泰", db);

        Assert.Equal(3000m, result.TotalBalance);
    }

    /// <summary>Verifies blank and missing bank name filters return all accounts and full balance totals.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListBankAccounts_BlankBankNameReturnsAllAccounts(string? bankName)
    {
        await using var db = await CreateDbContextAsync();

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 10, bankName, db);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(3, result.Total);
        Assert.Equal(6000m, result.TotalBalance);
    }

    /// <summary>Creates a seeded SQLite-backed context for bank account list query tests.</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.BankAccounts.AddRange(
            new BankAccount { BankName = "國泰世華", AccountNumber = "12345", Balance = 1000m, AccountType = "活期存款", CreatedAt = new DateTime(2026, 1, 3), UpdatedAt = new DateTime(2026, 1, 3) },
            new BankAccount { BankName = "國泰銀行", AccountNumber = "23456", Balance = 2000m, AccountType = "數位帳戶", CreatedAt = new DateTime(2026, 1, 2), UpdatedAt = new DateTime(2026, 1, 2) },
            new BankAccount { BankName = "玉山銀行", AccountNumber = "34567", Balance = 3000m, AccountType = "定期存款", CreatedAt = new DateTime(2026, 1, 1), UpdatedAt = new DateTime(2026, 1, 1) });
        await db.SaveChangesAsync();

        return db;
    }
}
