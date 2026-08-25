using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class StockLedgerCalculatorTests
{
    /// <summary>驗證期初市值只作為報酬追蹤基準，不會混入實際成本基礎。</summary>
    [Fact]
    public void Replay_OpeningBalance_SeparatesMarketValueFromCostBasis()
    {
        var result = StockLedgerCalculator.Replay(new StockLedgerInput(
        [
            new StockLedgerEntry(
                1,
                StockTransactionType.OpeningBalance,
                new DateOnly(2026, 1, 1),
                1,
                10m,
                100m,
                0m,
                0m,
                OpeningMarketValue: 1200m),
        ]));

        Assert.Equal(10m, result.RemainingShares);
        Assert.Equal(1000m, result.RemainingCostBasis);
        Assert.Equal(100m, result.ExecutionAveragePrice);
        Assert.Equal(-1200m, Assert.Single(result.Entries).NetCashFlow);
    }

    /// <summary>驗證多筆買入使用 moving weighted execution average 且實際成本包含費稅。</summary>
    [Fact]
    public void Replay_Buys_UsesWeightedExecutionAverageAndActualCosts()
    {
        var result = StockLedgerCalculator.Replay(new StockLedgerInput(
        [
            new StockLedgerEntry(1, StockTransactionType.Buy, new DateOnly(2026, 1, 1), 1, 10m, 100m, 5m, 2m),
            new StockLedgerEntry(2, StockTransactionType.Buy, new DateOnly(2026, 2, 1), 1, 20m, 130m, 7m, 3m),
        ]));

        Assert.Equal(30m, result.RemainingShares);
        Assert.Equal(3617m, result.RemainingCostBasis);
        Assert.Equal(120m, result.ExecutionAveragePrice);
        Assert.Equal(-1007m, result.Entries[0].NetCashFlow);
        Assert.Equal(-2610m, result.Entries[1].NetCashFlow);
    }

    /// <summary>驗證部分賣出按實際成本基礎分攤並扣除賣出費稅。</summary>
    [Fact]
    public void Replay_PartialSell_AllocatesCostAndRealizesNetProceeds()
    {
        var result = StockLedgerCalculator.Replay(new StockLedgerInput(
        [
            new StockLedgerEntry(1, StockTransactionType.OpeningBalance, new DateOnly(2026, 1, 1), 1, 10m, 100m, 0m, 0m, OpeningMarketValue: 1200m),
            new StockLedgerEntry(2, StockTransactionType.Buy, new DateOnly(2026, 1, 2), 1, 10m, 120m, 10m, 5m),
            new StockLedgerEntry(3, StockTransactionType.Sell, new DateOnly(2026, 1, 3), 1, 5m, 150m, 3m, 2m),
        ]));

        var sell = result.Entries[2];
        Assert.Equal(553.75m, sell.AllocatedCostBasis);
        Assert.Equal(745m, sell.NetCashFlow);
        Assert.Equal(191.25m, sell.RealizedGainLoss);
        Assert.Equal(15m, result.RemainingShares);
        Assert.Equal(1661.25m, result.RemainingCostBasis);
        Assert.Equal(110m, result.ExecutionAveragePrice);
    }

    /// <summary>驗證全數賣出接近 decimal tolerance 時會清除剩餘殘值。</summary>
    [Fact]
    public void Replay_FullSellWithinTolerance_ZeroesProjection()
    {
        var result = StockLedgerCalculator.Replay(new StockLedgerInput(
        [
            new StockLedgerEntry(1, StockTransactionType.Buy, new DateOnly(2026, 1, 1), 1, 1m, 100m, 0m, 0m),
            new StockLedgerEntry(2, StockTransactionType.Sell, new DateOnly(2026, 1, 2), 1, 1.000000001m, 100m, 0m, 0m),
        ]));

        Assert.Equal(0m, result.RemainingShares);
        Assert.Equal(0m, result.RemainingCostBasis);
        Assert.Equal(0m, result.ExecutionAveragePrice);
    }

    /// <summary>驗證股息只增加淨股息，不改變部位與成本。</summary>
    [Fact]
    public void Replay_Dividend_AddsNetIncomeWithoutChangingPosition()
    {
        var result = StockLedgerCalculator.Replay(new StockLedgerInput(
        [
            new StockLedgerEntry(1, StockTransactionType.Buy, new DateOnly(2026, 1, 1), 1, 10m, 100m, 0m, 0m),
            new StockLedgerEntry(2, StockTransactionType.Dividend, new DateOnly(2026, 2, 1), 1, null, null, 2m, 3m, CashAmount: 100m),
        ]));

        Assert.Equal(95m, result.NetDividendIncome);
        Assert.Equal(10m, result.RemainingShares);
        Assert.Equal(1000m, result.RemainingCostBasis);
        Assert.Equal(95m, result.Entries[1].NetDividend);
    }

    /// <summary>驗證 replay 會按 TradeDate、Sequence、Id 排序而不依賴輸入列舉順序。</summary>
    [Fact]
    public void Replay_UsesStableDateSequenceAndIdOrdering()
    {
        var entries = new[]
        {
            new StockLedgerEntry(3, StockTransactionType.Sell, new DateOnly(2026, 1, 2), 1, 1m, 120m, 0m, 0m),
            new StockLedgerEntry(2, StockTransactionType.Buy, new DateOnly(2026, 1, 1), 2, 1m, 110m, 0m, 0m),
            new StockLedgerEntry(1, StockTransactionType.Buy, new DateOnly(2026, 1, 1), 1, 1m, 100m, 0m, 0m),
        };

        var result = StockLedgerCalculator.Replay(entries.Reverse());

        Assert.Equal(1m, result.RemainingShares);
        Assert.Equal(105m, result.RemainingCostBasis);
        Assert.Equal(15m, result.RealizedGainLoss);
        Assert.Equal([1, 2, 3], result.Entries.Select(entry => entry.Entry.Id));
    }

    /// <summary>驗證型別專屬欄位錯誤會產生 stable validation code 且不進行部分 replay。</summary>
    [Fact]
    public void Replay_InvalidTypeFields_ThrowsTypedValidationFailure()
    {
        var exception = Assert.Throws<StockLedgerException>(() =>
            StockLedgerCalculator.Replay(new StockLedgerInput(
            [
                new StockLedgerEntry(1, StockTransactionType.Dividend, new DateOnly(2026, 1, 1), 1, 1m, null, 0m, 0m, CashAmount: 100m),
            ])));

        Assert.Equal(StockLedgerFailureCode.InvalidTransaction, exception.FailureCode);
    }

    /// <summary>驗證 oversell 會回傳 typed InsufficientShares failure 與交易上下文。</summary>
    [Fact]
    public void Replay_Oversell_ThrowsInsufficientShares()
    {
        var exception = Assert.Throws<InsufficientSharesException>(() =>
            StockLedgerCalculator.Replay(new StockLedgerInput(
            [
                new StockLedgerEntry(1, StockTransactionType.Buy, new DateOnly(2026, 1, 1), 1, 1m, 100m, 0m, 0m),
                new StockLedgerEntry(2, StockTransactionType.Sell, new DateOnly(2026, 1, 2), 1, 2m, 100m, 0m, 0m),
            ])));

        Assert.Equal("InsufficientShares", exception.Code);
        Assert.Equal(1m, exception.AvailableShares);
        Assert.Equal(2m, exception.RequestedShares);
    }

    /// <summary>驗證 decimal overflow 不會產生無限迴圈或未定義結果。</summary>
    [Fact]
    public void Replay_DecimalOverflow_ThrowsNonFiniteResult()
    {
        var exception = Assert.Throws<StockLedgerException>(() =>
            StockLedgerCalculator.Replay(new StockLedgerInput(
            [
                new StockLedgerEntry(1, StockTransactionType.Buy, new DateOnly(2026, 1, 1), 1, decimal.MaxValue, 2m, 0m, 0m),
            ])));

        Assert.Equal(StockLedgerFailureCode.NonFiniteResult, exception.FailureCode);
    }

    /// <summary>驗證歷史交易被建立、修改或刪除時，完整 replay 會產生一致的最新 projection。</summary>
    [Fact]
    public void Replay_HistoricalMutation_RebuildsDerivedProjection()
    {
        var opening = new StockLedgerEntry(1, StockTransactionType.OpeningBalance, new DateOnly(2026, 1, 1), 1, 10m, 100m, 0m, 0m, OpeningMarketValue: 1000m);
        var buy = new StockLedgerEntry(2, StockTransactionType.Buy, new DateOnly(2026, 1, 2), 1, 10m, 120m, 0m, 0m);
        var sell = new StockLedgerEntry(3, StockTransactionType.Sell, new DateOnly(2026, 1, 3), 1, 5m, 150m, 0m, 0m);

        var created = StockLedgerCalculator.Replay([opening, buy]);
        var edited = StockLedgerCalculator.Replay([opening, buy with { Price = 130m }, sell]);
        var deleted = StockLedgerCalculator.Replay([opening, sell]);

        Assert.Equal(20m, created.RemainingShares);
        Assert.Equal(175m, edited.RealizedGainLoss);
        Assert.Equal(15m, edited.RemainingShares);
        Assert.Equal(5m, deleted.RemainingShares);
    }
}
