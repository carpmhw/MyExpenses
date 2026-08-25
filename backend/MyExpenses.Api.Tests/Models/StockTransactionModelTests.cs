using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using Xunit;

namespace MyExpenses.Api.Tests.Models;

public sealed class StockTransactionModelTests
{
    /// <summary>驗證股票交易型別只包含規格定義的四種穩定字串值。</summary>
    [Fact]
    public void StockTransactionType_ContainsTheFourLedgerKinds()
    {
        Assert.Equal(
            ["OpeningBalance", "Buy", "Sell", "Dividend"],
            Enum.GetNames<StockTransactionType>());
    }

    /// <summary>驗證交易資料表的欄位、索引、外鍵刪除行為與 UTC 稽核欄位。</summary>
    [Fact]
    public async Task StockTransactionMapping_UsesLedgerPersistenceContract()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var entity = db.Model.FindEntityType(typeof(StockTransaction));
        Assert.NotNull(entity);
        Assert.Equal("StockTransactions", entity!.GetTableName());
        Assert.Equal(typeof(string), entity.FindProperty(nameof(StockTransaction.Type))!.GetProviderClrType());
        Assert.Equal(18, entity.FindProperty(nameof(StockTransaction.Shares))!.GetPrecision());
        Assert.Equal(4, entity.FindProperty(nameof(StockTransaction.Shares))!.GetScale());
        Assert.Equal(18, entity.FindProperty(nameof(StockTransaction.Price))!.GetPrecision());
        Assert.Equal(2, entity.FindProperty(nameof(StockTransaction.Price))!.GetScale());
        Assert.Equal(18, entity.FindProperty(nameof(StockTransaction.CashAmount))!.GetPrecision());
        Assert.Equal(2, entity.FindProperty(nameof(StockTransaction.CashAmount))!.GetScale());
        Assert.True(entity.FindProperty(nameof(StockTransaction.Shares))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(StockTransaction.Price))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(StockTransaction.CashAmount))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(StockTransaction.OpeningMarketValue))!.IsNullable);
        Assert.Contains(
            entity.GetIndexes(),
            index => index.IsUnique
                && index.Properties.Select(property => property.Name).SequenceEqual(
                    [nameof(StockTransaction.StockId), nameof(StockTransaction.TradeDate), nameof(StockTransaction.Sequence)]));

        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);

        var stock = new Stock
        {
            Name = "測試標的",
            Symbol = "2330",
            Shares = 10m,
            BuyPrice = 100m,
            CurrentPrice = 110m,
        };
        var transaction = new StockTransaction
        {
            Stock = stock,
            Type = StockTransactionType.Buy,
            TradeDate = new DateOnly(2026, 8, 25),
            Sequence = 1,
            Shares = 1m,
            Price = 100m,
            Fee = 2m,
            Tax = 1m,
            Notes = "  first buy  ",
        };

        db.Add(transaction);
        await db.SaveChangesAsync();

        Assert.Equal("first buy", transaction.Notes);
        Assert.Equal(DateTimeKind.Utc, transaction.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, transaction.UpdatedAtUtc.Kind);
    }
}
