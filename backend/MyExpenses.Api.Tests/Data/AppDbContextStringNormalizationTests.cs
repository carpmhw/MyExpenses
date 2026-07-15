using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using Xunit;

namespace MyExpenses.Api.Tests.Data;

public class AppDbContextStringNormalizationTests
{
    /// <summary>Verifies added allowlisted strings are trimmed while interior whitespace is preserved.</summary>
    [Fact]
    public async Task SaveChanges_TrimsAddedAllowlistedStrings()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var category = new Category
        {
            Name = "  Food  and  Drinks  ",
            Icon = "  Utensils  ",
            Color = "  #FFFFFF  ",
            SystemCode = "  meal  ",
            SortOrder = 1,
        };

        db.Categories.Add(category);
        db.SaveChanges();

        Assert.Equal("Food  and  Drinks", category.Name);
        Assert.Equal("Utensils", category.Icon);
        Assert.Equal("#FFFFFF", category.Color);
        Assert.Equal("  meal  ", category.SystemCode);

        db.ChangeTracker.Clear();
        var stored = db.Categories.Single(c => c.Id == category.Id);
        Assert.Equal("Food  and  Drinks", stored.Name);
        Assert.Equal("Utensils", stored.Icon);
        Assert.Equal("#FFFFFF", stored.Color);
        Assert.Equal("  meal  ", stored.SystemCode);
    }

    /// <summary>Verifies modified strings and whitespace-only optional values are normalized on asynchronous saves.</summary>
    [Fact]
    public async Task SaveChangesAsync_TrimsModifiedStringsAndNormalizesBlankValues()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var category = new Category
        {
            Name = "Original",
            Icon = "Circle",
            Color = "#000000",
            SortOrder = 1,
        };
        var transaction = new Transaction
        {
            Category = category,
            Type = TransactionType.Expense,
            Amount = 100m,
            Date = new DateOnly(2026, 1, 1),
            Description = "Original description",
            Notes = "Original notes",
        };

        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        category.Name = "  Updated  name  ";
        category.Color = " \t ";
        transaction.Description = "  Coffee  shop  ";
        transaction.Notes = " \r\n ";

        await db.SaveChangesAsync();

        Assert.Equal("Updated  name", category.Name);
        Assert.Equal(string.Empty, category.Color);
        Assert.Equal("Coffee  shop", transaction.Description);
        Assert.Null(transaction.Notes);

        db.ChangeTracker.Clear();
        var storedCategory = await db.Categories.SingleAsync(c => c.Id == category.Id);
        var storedTransaction = await db.Transactions.SingleAsync(t => t.Id == transaction.Id);
        Assert.Equal("Updated  name", storedCategory.Name);
        Assert.Equal(string.Empty, storedCategory.Color);
        Assert.Equal("Coffee  shop", storedTransaction.Description);
        Assert.Null(storedTransaction.Notes);
    }

    /// <summary>Verifies excluded strings remain unchanged and unrelated historical values are not rewritten.</summary>
    [Fact]
    public async Task SaveChangesAsync_PreservesExcludedStringsAndUnmodifiedHistoricalValues()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var user = new User
        {
            Email = "  user@example.com  ",
            PasswordHash = "  password-hash  ",
            DisplayName = "  Display Name  ",
            TotpSecret = "  totp-secret  ",
            RecoveryCodes = "  recovery-json  ",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = new ApiToken
        {
            UserId = user.Id,
            Name = "  Automation token  ",
            TokenHash = "  token-hash  ",
            Prefix = "  token-prefix  ",
            Scopes = "  [\"transactions:read\"]  ",
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        Assert.Equal("Display Name", user.DisplayName);
        Assert.Equal("  user@example.com  ", user.Email);
        Assert.Equal("  password-hash  ", user.PasswordHash);
        Assert.Equal("  totp-secret  ", user.TotpSecret);
        Assert.Equal("  recovery-json  ", user.RecoveryCodes);
        Assert.Equal("Automation token", token.Name);
        Assert.Equal("  token-hash  ", token.TokenHash);
        Assert.Equal("  token-prefix  ", token.Prefix);
        Assert.Equal("  [\"transactions:read\"]  ", token.Scopes);

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Categories (Name, Type, Icon, Color, SortOrder, SystemCode) VALUES ({0}, {1}, {2}, {3}, {4}, NULL)",
            "  Legacy  ",
            (int)CategoryType.Expense,
            "LegacyIcon",
            "#000000",
            99);
        db.ChangeTracker.Clear();
        var legacy = await db.Categories.SingleAsync(c => c.SortOrder == 99);
        legacy.SortOrder = 100;

        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var storedUser = await db.Users.SingleAsync(u => u.Id == user.Id);
        var storedToken = await db.ApiTokens.SingleAsync(t => t.Id == token.Id);
        var storedLegacy = await db.Categories.SingleAsync(c => c.Id == legacy.Id);
        Assert.Equal("  user@example.com  ", storedUser.Email);
        Assert.Equal("  password-hash  ", storedUser.PasswordHash);
        Assert.Equal("  totp-secret  ", storedUser.TotpSecret);
        Assert.Equal("  recovery-json  ", storedUser.RecoveryCodes);
        Assert.Equal("  token-hash  ", storedToken.TokenHash);
        Assert.Equal("  token-prefix  ", storedToken.Prefix);
        Assert.Equal("  [\"transactions:read\"]  ", storedToken.Scopes);
        Assert.Equal("  Legacy  ", storedLegacy.Name);
    }

    /// <summary>Verifies fixed-format and generated snapshot strings remain outside the normalization allowlist.</summary>
    [Fact]
    public async Task SaveChangesAsync_PreservesFixedFormatAndGeneratedStrings()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var card = new CreditCard
        {
            BankName = "Test Bank",
            LastFourDigits = "1234",
        };
        var setting = new SystemSetting { TimeZoneId = "  Asia/Taipei  " };
        var autoSnapshotConfig = new AutoSnapshotConfig
        {
            Frequency = " Daily ",
            TimeOfDay = " 08:00 ",
        };
        var bill = new CreditCardBill
        {
            Card = card,
            Period = " 2026-07 ",
            TotalAmount = 100m,
            DueDate = new DateOnly(2026, 7, 20),
        };
        var snapshot = new SnapshotBatch
        {
            Name = "  Generated snapshot  ",
            Notes = "  Generated notes  ",
            SnapshotDate = DateTime.UtcNow,
            BankDetails =
            [
                new BankDetail
                {
                    BankName = "  Generated bank  ",
                    AccountNumber = " 12345 ",
                    AccountType = " Current ",
                },
            ],
            StockDetails =
            [
                new StockDetail
                {
                    Name = "  Generated stock  ",
                    Symbol = " 2330 ",
                },
            ],
        };

        db.AddRange(card, setting, autoSnapshotConfig, bill, snapshot);
        await db.SaveChangesAsync();

        Assert.Equal("Asia/Taipei", setting.TimeZoneId);
        Assert.Equal(" Daily ", autoSnapshotConfig.Frequency);
        Assert.Equal(" 08:00 ", autoSnapshotConfig.TimeOfDay);
        Assert.Equal(" 2026-07 ", bill.Period);
        Assert.Equal("  Generated snapshot  ", snapshot.Name);
        Assert.Equal("  Generated notes  ", snapshot.Notes);
        Assert.Equal("  Generated bank  ", snapshot.BankDetails[0].BankName);
        Assert.Equal(" 12345 ", snapshot.BankDetails[0].AccountNumber);
        Assert.Equal(" Current ", snapshot.BankDetails[0].AccountType);
        Assert.Equal("  Generated stock  ", snapshot.StockDetails[0].Name);
        Assert.Equal(" 2330 ", snapshot.StockDetails[0].Symbol);
    }

    /// <summary>Opens a shared in-memory SQLite connection for persistence tests.</summary>
    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Creates and initializes an application database context for persistence tests.</summary>
    private static async Task<AppDbContext> CreateDbContextAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
