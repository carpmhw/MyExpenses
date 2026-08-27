using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class StockPerformanceCalculatorTests
{
    /// <summary>驗證目前毛市值、剩餘實際成本、已實現、未實現、股息與總損益口徑。</summary>
    [Fact]
    public void Calculate_ReportsGrossProfitAndLossFromLedgerReplay()
    {
        var stock = CreateStock(1, shares: 5m, buyPrice: 100m, currentPrice: 150m);
        var report = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 12, 31),
            [stock],
            [
                Buy(1, 1, new DateOnly(2026, 1, 1), 10m, 100m),
                Sell(2, 1, new DateOnly(2026, 6, 1), 5m, 120m, fee: 2m, tax: 1m),
                Dividend(3, 1, new DateOnly(2026, 7, 1), 100m, fee: 5m, tax: 5m),
            ],
            []));

        Assert.Equal(750m, report.Summary.CurrentGrossMarketValue);
        Assert.Equal(500m, report.Summary.RemainingCostBasis);
        Assert.Equal(97m, report.Summary.RealizedGainLoss);
        Assert.Equal(250m, report.Summary.UnrealizedGainLoss);
        Assert.Equal(90m, report.Summary.NetDividendIncome);
        Assert.Equal(437m, report.Summary.TotalGainLoss);
        Assert.Equal(1d, report.LedgerCoverage.Value);
    }

    /// <summary>驗證未初始化 active holding 會阻擋 TWR 與 XIRR，但不阻擋損益摘要。</summary>
    [Fact]
    public void Calculate_IncompleteLedgerCoverage_GatesOnlyReturnMetrics()
    {
        var initialized = CreateStock(1, shares: 10m, buyPrice: 100m, currentPrice: 110m);
        var uninitialized = CreateStock(2, shares: 5m, buyPrice: 50m, currentPrice: 60m);
        var report = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 12, 31),
            [initialized, uninitialized],
            [Buy(1, 1, new DateOnly(2026, 1, 1), 10m, 100m)],
            []));

        Assert.Equal(1100m / 1400m, (decimal)report.LedgerCoverage.Value!, 6);
        Assert.Null(report.Twr.Value);
        Assert.Equal(StockPerformanceUnavailableReason.IncompleteLedgerCoverage, report.Twr.UnavailableReason);
        Assert.Null(report.Xirr.Value);
        Assert.Equal(StockPerformanceUnavailableReason.IncompleteLedgerCoverage, report.Xirr.UnavailableReason);
        Assert.Equal(1100m, report.Summary.CurrentGrossMarketValue - 300m);
    }

    /// <summary>驗證已完全賣出的標的仍保留歷史 realized P/L 與 breakdown。</summary>
    [Fact]
    public void Calculate_FullySoldHolding_RemainsInBreakdown()
    {
        var stock = CreateStock(1, shares: 0m, buyPrice: 100m, currentPrice: 120m);
        var report = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 12, 31),
            [stock],
            [
                Buy(1, 1, new DateOnly(2026, 1, 1), 10m, 100m),
                Sell(2, 1, new DateOnly(2026, 2, 1), 10m, 120m, fee: 2m, tax: 3m),
            ],
            []));

        var breakdown = Assert.Single(report.InstrumentBreakdown);
        Assert.True(breakdown.IsClosed);
        Assert.Equal(0m, breakdown.CurrentShares);
        Assert.Equal(0m, breakdown.GrossMarketValue);
        Assert.Equal(0m, breakdown.RemainingCostBasis);
        Assert.Equal(195m, breakdown.RealizedGainLoss);
        Assert.Equal(195m, report.Summary.RealizedGainLoss);
        Assert.Equal(195m, report.Summary.TotalGainLoss);
    }

    /// <summary>驗證 synthetic opening 的成本與報酬 tracking start 使用不同數值。</summary>
    [Fact]
    public void Calculate_SyntheticOpening_SeparatesCostBasisAndTrackingStart()
    {
        var stock = CreateStock(1, shares: 10m, buyPrice: 100m, currentPrice: 120m);
        var baseline = new DateOnly(2026, 3, 1);
        var report = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            baseline,
            new DateOnly(2026, 3, 2),
            [stock],
            [new StockTransaction
            {
                Id = 1,
                StockId = 1,
                Type = StockTransactionType.OpeningBalance,
                TradeDate = baseline,
                Sequence = 1,
                Shares = 10m,
                Price = 100m,
                OpeningMarketValue = 1200m,
            }],
            [Price(1, "2330", baseline, 100m, 120m), Price(1, "2330", baseline.AddDays(1), 110m, 130m)],
            AsOfDate: baseline.AddDays(1)));

        Assert.True(report.HasSyntheticOpeningBalances);
        Assert.Equal(baseline, report.TrackingStartDate);
        Assert.Equal(1000m, report.Summary.RemainingCostBasis);
        Assert.Equal(StockPerformanceUnavailableReason.None, report.DataQuality.TrackingStartReason);
        Assert.NotNull(report.Twr.Value);
        Assert.InRange(report.Twr.Value!.Value, 0.083332d, 0.083334d);
    }

    /// <summary>驗證 synthetic opening 的 XIRR 初始投入使用期初毛市值而非歷史成本。</summary>
    [Fact]
    public void Calculate_SyntheticOpening_UsesOpeningMarketValueForXirr()
    {
        var stock = CreateStock(1, shares: 10m, buyPrice: 100m, currentPrice: 120m);
        var baseline = new DateOnly(2026, 3, 1);
        var report = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            baseline,
            baseline.AddDays(1),
            [stock],
            [new StockTransaction
            {
                Id = 1,
                StockId = 1,
                Type = StockTransactionType.OpeningBalance,
                TradeDate = baseline,
                Sequence = 1,
                Shares = 10m,
                Price = 100m,
                OpeningMarketValue = 1200m,
            }],
            [],
            AsOfDate: baseline.AddDays(1)));

        Assert.NotNull(report.Xirr.Value);
        Assert.InRange(report.Xirr.Value!.Value, -0.000001d, 0.000001d);
    }

    /// <summary>驗證 requested period 早於 tracking start 時只阻擋 return metrics。</summary>
    [Fact]
    public void Calculate_PeriodBeforeTrackingStart_GatesReturnMetrics()
    {
        var baseline = new DateOnly(2026, 3, 1);
        var stock = CreateStock(1, shares: 10m, buyPrice: 100m, currentPrice: 120m);
        var report = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            baseline.AddDays(-1),
            baseline.AddDays(1),
            [stock],
            [new StockTransaction
            {
                Id = 1,
                StockId = 1,
                Type = StockTransactionType.OpeningBalance,
                TradeDate = baseline,
                Sequence = 1,
                Shares = 10m,
                Price = 100m,
                OpeningMarketValue = 1200m,
            }],
            []));

        Assert.Equal(StockPerformanceUnavailableReason.PeriodBeforeTrackingStart, report.Twr.UnavailableReason);
        Assert.Equal(StockPerformanceUnavailableReason.PeriodBeforeTrackingStart, report.Xirr.UnavailableReason);
        Assert.Equal(1200m, report.Summary.CurrentGrossMarketValue);
    }

    /// <summary>驗證 XIRR 對一年期 -1000 與 +1100 現金流求得約 10%。</summary>
    [Fact]
    public void CalculateXirr_SolvesSimpleAnnualReturn()
    {
        var metric = StockPerformanceCalculator.CalculateXirr(
        [
            new StockPerformanceCashFlow(new DateOnly(2026, 1, 1), -1000m),
            new StockPerformanceCashFlow(new DateOnly(2027, 1, 1), 1100m),
        ]);

        Assert.NotNull(metric.Value);
        Assert.InRange(metric.Value!.Value, 0.099d, 0.101d);
    }

    /// <summary>驗證缺少正負現金流或 terminal value 時 XIRR 會回傳 typed unavailable。</summary>
    [Fact]
    public void CalculateXirr_RejectsInsufficientCashFlowSigns()
    {
        var metric = StockPerformanceCalculator.CalculateXirr(
        [new StockPerformanceCashFlow(new DateOnly(2026, 1, 1), -1000m)]);

        Assert.Null(metric.Value);
        Assert.Equal(StockPerformanceUnavailableReason.InsufficientCashFlows, metric.UnavailableReason);
    }

    /// <summary>驗證 XIRR 可處理不規則日期及多筆投資人現金流。</summary>
    [Fact]
    public void CalculateXirr_SolvesIrregularCashFlows()
    {
        var metric = StockPerformanceCalculator.CalculateXirr(
        [
            new StockPerformanceCashFlow(new DateOnly(2026, 1, 1), -1000m),
            new StockPerformanceCashFlow(new DateOnly(2026, 2, 15), -500m),
            new StockPerformanceCashFlow(new DateOnly(2026, 6, 1), 700m),
            new StockPerformanceCashFlow(new DateOnly(2026, 12, 31), 1100m),
        ]);

        Assert.NotNull(metric.Value);
        Assert.True(double.IsFinite(metric.Value!.Value));
    }

    /// <summary>驗證報表 XIRR 將買入、部分賣出、股息及 terminal value 使用正確符號。</summary>
    [Fact]
    public void Calculate_UsesInvestorCashFlowSignsForXirr()
    {
        var stock = CreateStock(1, shares: 5m, buyPrice: 100m, currentPrice: 130m);
        var report = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 3, 1),
            [stock],
            [
                Buy(1, 1, new DateOnly(2026, 1, 1), 10m, 100m),
                Sell(2, 1, new DateOnly(2026, 2, 1), 5m, 120m),
                Dividend(3, 1, new DateOnly(2026, 3, 1), 50m),
            ],
            [],
            AsOfDate: new DateOnly(2026, 3, 1)));

        Assert.NotNull(report.Xirr.Value);
        Assert.True(double.IsFinite(report.Xirr.Value!.Value));
    }

    /// <summary>驗證歷史 period 缺少 raw terminal close 時 XIRR 回傳穩定 unavailable reason。</summary>
    [Fact]
    public void Calculate_MissingHistoricalTerminalValue_ReturnsTypedXirrReason()
    {
        var stock = CreateStock(1, shares: 10m, buyPrice: 100m, currentPrice: 120m);
        var report = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            [stock],
            [Buy(1, 1, new DateOnly(2026, 1, 1), 10m, 100m)],
            [],
            AsOfDate: new DateOnly(2026, 2, 1)));

        Assert.Null(report.Xirr.Value);
        Assert.Equal(StockPerformanceUnavailableReason.MissingTerminalValue, report.Xirr.UnavailableReason);
        Assert.Equal("HistoricalRawClose", report.TerminalValuationSource);
    }

    /// <summary>驗證有正負現金流但沒有可辨識根時 solver 有界結束並回傳 NoConvergence。</summary>
    [Fact]
    public void CalculateXirr_NoConvergence_ReturnsTypedReason()
    {
        var metric = StockPerformanceCalculator.CalculateXirr(
        [
            new StockPerformanceCashFlow(new DateOnly(2026, 1, 1), -1000m),
            new StockPerformanceCashFlow(new DateOnly(2026, 1, 1), 1000m),
        ]);

        Assert.Null(metric.Value);
        Assert.Equal(StockPerformanceUnavailableReason.NoConvergence, metric.UnavailableReason);
    }

    /// <summary>驗證 TWR 使用 raw Close 並將買入視為期初投入。</summary>
    [Fact]
    public void CalculateTwr_UsesRawCloseAndBeginningContribution()
    {
        var stock = CreateStock(1, shares: 10m, buyPrice: 10m, currentPrice: 11m);
        var result = StockPerformanceCalculator.CalculateTwr(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 2),
            [stock],
            [Buy(1, 1, new DateOnly(2026, 1, 1), 10m, 10m)],
            [Price(1, "2330", new DateOnly(2026, 1, 1), 100m, 10m), Price(1, "2330", new DateOnly(2026, 1, 2), 200m, 11m)]));

        Assert.NotNull(result.Metric.Value);
        Assert.InRange(result.Metric.Value!.Value, 0.099d, 0.101d);
        Assert.Equal(2, result.ObservationCount);
        Assert.Equal(1d, result.PriceCoverage);
    }

    /// <summary>驗證沒有期間內外部現金流時 TWR 只反映 securities value 變化。</summary>
    [Fact]
    public void CalculateTwr_WithoutPeriodFlow_ChainsDailyReturns()
    {
        var stock = CreateStock(1, shares: 10m, buyPrice: 10m, currentPrice: 12.1m);
        var result = StockPerformanceCalculator.CalculateTwr(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 3),
            [stock],
            [Buy(1, 1, new DateOnly(2025, 12, 31), 10m, 10m)],
            [
                Price(1, "2330", new DateOnly(2026, 1, 1), 90m, 10m),
                Price(1, "2330", new DateOnly(2026, 1, 2), 99m, 11m),
                Price(1, "2330", new DateOnly(2026, 1, 3), 108.9m, 12.1m),
            ]));

        Assert.NotNull(result.Metric.Value);
        Assert.InRange(result.Metric.Value!.Value, 0.209999d, 0.210001d);
        Assert.Equal(3, result.Points.Count);
        Assert.InRange(result.Points[^1].CumulativeReturn, 0.209999d, 0.210001d);
    }

    /// <summary>驗證股息以期末提領加入 TWR 分子，而不被當成 securities loss。</summary>
    [Fact]
    public void CalculateTwr_TreatsDividendAsEndOfDayWithdrawal()
    {
        var stock = CreateStock(1, shares: 10m, buyPrice: 10m, currentPrice: 10m);
        var result = StockPerformanceCalculator.CalculateTwr(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 2),
            [stock],
            [
                Buy(1, 1, new DateOnly(2025, 12, 31), 10m, 10m),
                Dividend(2, 1, new DateOnly(2026, 1, 2), 10m),
            ],
            [Price(1, "2330", new DateOnly(2026, 1, 1), 10m, 10m), Price(1, "2330", new DateOnly(2026, 1, 2), 10m, 10m)]));

        Assert.NotNull(result.Metric.Value);
        Assert.InRange(result.Metric.Value!.Value, 0.099999d, 0.100001d);
        Assert.Equal(10m, result.Points[^1].Withdrawals);
    }

    /// <summary>驗證多標的 TWR 只使用所有 active instrument 都有 raw close 的共同日期。</summary>
    [Fact]
    public void CalculateTwr_MultipleStocks_ReportsCommonPriceCoverage()
    {
        var first = CreateStock(1, shares: 10m, buyPrice: 10m, currentPrice: 11m);
        var second = CreateStock(2, shares: 10m, buyPrice: 20m, currentPrice: 22m);
        var result = StockPerformanceCalculator.CalculateTwr(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 2),
            [first, second],
            [
                Buy(1, 1, new DateOnly(2025, 12, 31), 10m, 10m),
                Buy(2, 2, new DateOnly(2025, 12, 31), 10m, 20m),
            ],
            [
                Price(1, "2330", new DateOnly(2026, 1, 1), 10m, 10m),
                Price(1, "2330", new DateOnly(2026, 1, 2), 11m, 11m),
                Price(2, "0050", new DateOnly(2026, 1, 1), 20m, 20m),
            ]));

        Assert.Null(result.Metric.Value);
        Assert.Equal(StockPerformanceUnavailableReason.InsufficientHistoricalPrices, result.Metric.UnavailableReason);
        Assert.Equal(0.5d, result.PriceCoverage);
        Assert.Equal(1, result.ObservationCount);
    }

    /// <summary>驗證同日買入與賣出分別套用期初投入及期末提領 timing。</summary>
    [Fact]
    public void CalculateTwr_SameDayBuyAndSell_UsesExplicitFlowTiming()
    {
        var stock = CreateStock(1, shares: 10m, buyPrice: 10m, currentPrice: 11m);
        var result = StockPerformanceCalculator.CalculateTwr(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 2),
            [stock],
            [
                Buy(1, 1, new DateOnly(2025, 12, 31), 10m, 10m),
                Buy(2, 1, new DateOnly(2026, 1, 2), 5m, 12m),
                Sell(3, 1, new DateOnly(2026, 1, 2), 5m, 12m),
            ],
            [Price(1, "2330", new DateOnly(2026, 1, 1), 10m, 10m), Price(1, "2330", new DateOnly(2026, 1, 2), 11m, 11m)]));

        Assert.NotNull(result.Metric.Value);
        Assert.InRange(result.Metric.Value!.Value, 0.062499d, 0.062501d);
        Assert.Equal(60m, result.Points[^1].Contributions);
        Assert.Equal(60m, result.Points[^1].Withdrawals);
    }

    /// <summary>驗證全期間沒有有效部位與現金流時 TWR 不建立假報酬點。</summary>
    [Fact]
    public void CalculateTwr_ZeroDenominator_ReturnsTypedReason()
    {
        var stock = CreateStock(1, shares: 10m, buyPrice: 10m, currentPrice: 10m);
        var result = StockPerformanceCalculator.CalculateTwr(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 2),
            [stock],
            [Buy(1, 1, new DateOnly(2026, 1, 3), 10m, 10m)],
            [Price(1, "2330", new DateOnly(2026, 1, 1), 10m, 10m), Price(1, "2330", new DateOnly(2026, 1, 2), 10m, 10m)]));

        Assert.Null(result.Metric.Value);
        Assert.Equal(StockPerformanceUnavailableReason.ZeroDenominator, result.Metric.UnavailableReason);
    }

    /// <summary>驗證賣出與股息的期末提領會加回 TWR 分子而不誤算投資虧損。</summary>
    [Fact]
    public void CalculateTwr_TreatsSellAndDividendAsEndOfDayWithdrawals()
    {
        var stock = CreateStock(1, shares: 5m, buyPrice: 10m, currentPrice: 11m);
        var result = StockPerformanceCalculator.CalculateTwr(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 2),
            [stock],
            [
                Buy(1, 1, new DateOnly(2026, 1, 1), 10m, 10m),
                Sell(2, 1, new DateOnly(2026, 1, 2), 5m, 11m),
            ],
            [Price(1, "2330", new DateOnly(2026, 1, 1), 10m, 10m), Price(1, "2330", new DateOnly(2026, 1, 2), 11m, 11m)]));

        Assert.InRange(result.Metric.Value!.Value, 0.099d, 0.101d);
    }

    /// <summary>驗證任一 active instrument 缺 raw close 時 TWR 不會補 0 或 adjusted close。</summary>
    [Fact]
    public void CalculateTwr_MissingRawClose_ReturnsUnavailable()
    {
        var stock = CreateStock(1, shares: 10m, buyPrice: 10m, currentPrice: 11m);
        var result = StockPerformanceCalculator.CalculateTwr(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 2),
            [stock],
            [Buy(1, 1, new DateOnly(2026, 1, 1), 10m, 10m)],
            [Price(1, "2330", new DateOnly(2026, 1, 1), 100m, 10m)]));

        Assert.Null(result.Metric.Value);
        Assert.Equal(StockPerformanceUnavailableReason.InsufficientHistoricalPrices, result.Metric.UnavailableReason);
    }

    /// <summary>驗證月度點與標的 breakdown 由同一組 Ledger replay 及估值資料產生。</summary>
    [Fact]
    public void Calculate_BuildsMonthlyPointsAndInstrumentBreakdown()
    {
        var stock = CreateStock(1, shares: 5m, buyPrice: 10m, currentPrice: 14m);
        var report = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 2, 28),
            [stock],
            [
                Buy(1, 1, new DateOnly(2025, 12, 31), 10m, 10m),
                Sell(2, 1, new DateOnly(2026, 1, 15), 5m, 12m),
                Dividend(3, 1, new DateOnly(2026, 1, 20), 20m),
            ],
            [
                Price(1, "2330", new DateOnly(2026, 1, 31), 13m, 13m),
                Price(1, "2330", new DateOnly(2026, 2, 28), 14m, 14m),
            ]));

        var january = Assert.Single(report.MonthlyPoints, point => point.Month == "2026/01");
        Assert.Equal(65m, january.EndingMarketValue);
        Assert.Equal(80m, january.NetContribution);
        Assert.Equal(10m, january.RealizedGainLoss);
        Assert.Equal(20m, january.DividendIncome);
        Assert.Equal(0d, january.CumulativeTwr);

        var february = Assert.Single(report.MonthlyPoints, point => point.Month == "2026/02");
        Assert.Equal(70m, february.EndingMarketValue);
        Assert.Equal(0m, february.NetContribution);
        Assert.InRange(february.CumulativeTwr!.Value, 0.076922d, 0.076924d);

        var breakdown = Assert.Single(report.InstrumentBreakdown);
        Assert.Equal(5m, breakdown.CurrentShares);
        Assert.Equal(70m, breakdown.GrossMarketValue);
        Assert.Equal(50m, breakdown.RemainingCostBasis);
        Assert.Equal(10m, breakdown.RealizedGainLoss);
        Assert.Equal(20m, breakdown.UnrealizedGainLoss);
        Assert.Equal(20m, breakdown.DividendIncome);
        Assert.Equal(50m, breakdown.TotalGainLoss);
        Assert.False(breakdown.IsClosed);
    }

    /// <summary>驗證股票股利不形成 XIRR 現金流，且 TWR 與月度外部流量欄位保持零。</summary>
    [Fact]
    public void Calculate_StockDividend_UsesZeroExternalCashFlowSemantics()
    {
        var input = CreateStockDividendPerformanceInput();
        var report = StockPerformanceCalculator.Calculate(input);
        var twr = StockPerformanceCalculator.CalculateTwr(input);
        var stockDividendDate = new DateOnly(2026, 6, 1);
        var stockDividendPoint = Assert.Single(twr.Points, point => point.Date == stockDividendDate);
        var june = Assert.Single(report.MonthlyPoints, point => point.Month == "2026/06");

        Assert.InRange(report.Xirr.Value!.Value, 0.099d, 0.101d);
        Assert.Equal(0m, stockDividendPoint.Contributions);
        Assert.Equal(0m, stockDividendPoint.Withdrawals);
        Assert.InRange(stockDividendPoint.EndingValue, 1099.99m, 1100.01m);
        Assert.Equal(0m, june.NetContribution);
        Assert.Equal(0m, june.RealizedGainLoss);
        Assert.Equal(0m, june.DividendIncome);
        Assert.InRange(june.CumulativeTwr!.Value, 0.099d, 0.101d);
    }

    /// <summary>驗證股票股利透過 Ledger replay 增加估值股數，但不改變成本、損益或股息收入。</summary>
    [Fact]
    public void Calculate_StockDividend_UsesReplaySharesAndPreservesCostAndIncome()
    {
        var report = StockPerformanceCalculator.Calculate(CreateStockDividendPerformanceInput());
        var breakdown = Assert.Single(report.InstrumentBreakdown);

        Assert.Equal(110m, breakdown.CurrentShares);
        Assert.Equal(1100m, breakdown.GrossMarketValue);
        Assert.Equal(1000m, breakdown.RemainingCostBasis);
        Assert.Equal(0m, breakdown.RealizedGainLoss);
        Assert.Equal(100m, breakdown.UnrealizedGainLoss);
        Assert.Equal(0m, breakdown.DividendIncome);
        Assert.Equal(100m, breakdown.TotalGainLoss);
        Assert.Equal(1000m, report.Summary.RemainingCostBasis);
        Assert.Equal(0m, report.Summary.NetDividendIncome);
    }

    /// <summary>驗證歷史期間結束於股票股利前時，terminal value 只使用當日 replay 股數。</summary>
    [Fact]
    public void Calculate_HistoricalTerminalValue_ExcludesFutureStockDividendShares()
    {
        var stock = CreateStock(1, shares: 110m, buyPrice: 10m, currentPrice: 12m);
        var dateEnd = new DateOnly(2026, 1, 31);
        var report = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            dateEnd,
            [stock],
            [
                Buy(1, 1, new DateOnly(2026, 1, 1), 100m, 10m),
                StockDividend(2, 1, new DateOnly(2026, 2, 1), 10m),
            ],
            [Price(1, "2330", dateEnd, 10m, 10m)],
            AsOfDate: new DateOnly(2026, 2, 2)));

        Assert.Equal("HistoricalRawClose", report.TerminalValuationSource);
        Assert.InRange(report.Xirr.Value!.Value, -0.000001d, 0.000001d);
    }

    /// <summary>驗證交易與價格輸入順序變更不會改變績效結果。</summary>
    [Fact]
    public void Calculate_IsIndependentOfInputOrder()
    {
        var stock = CreateStock(1, shares: 5m, buyPrice: 10m, currentPrice: 14m);
        var transactions = new List<StockTransaction>
        {
            Buy(1, 1, new DateOnly(2025, 12, 31), 10m, 10m),
            Sell(2, 1, new DateOnly(2026, 1, 15), 5m, 12m),
            Dividend(3, 1, new DateOnly(2026, 1, 20), 20m),
        };
        var prices = new List<HistoricalAdjustedPrice>
        {
            Price(1, "2330", new DateOnly(2026, 1, 31), 13m, 13m),
            Price(1, "2330", new DateOnly(2026, 2, 28), 14m, 14m),
        };
        var first = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 28), [stock], transactions, prices));
        var second = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 28), [stock], transactions.AsEnumerable().Reverse().ToList(), prices.AsEnumerable().Reverse().ToList()));

        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.LedgerCoverage, second.LedgerCoverage);
        Assert.Equal(first.Twr, second.Twr);
        Assert.Equal(first.Xirr, second.Xirr);
        Assert.Equal(first.MonthlyPoints, second.MonthlyPoints);
        Assert.Equal(first.InstrumentBreakdown, second.InstrumentBreakdown);
    }

    /// <summary>驗證 public double 結果在 decimal 邊界輸入下不會產生 NaN 或 Infinity。</summary>
    [Fact]
    public void Calculate_PublicDoubleResultsRemainFinite()
    {
        var stock = CreateStock(1, shares: 1_000_000m, buyPrice: 0.000001m, currentPrice: 0.000002m);
        var report = StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 2),
            [stock],
            [Buy(1, 1, new DateOnly(2026, 1, 1), 1_000_000m, 0.000001m)],
            [Price(1, "2330", new DateOnly(2026, 1, 1), 0.000001m, 0.000001m), Price(1, "2330", new DateOnly(2026, 1, 2), 0.000002m, 0.000002m)]));

        AssertFinite(report.LedgerCoverage.Value);
        AssertFinite(report.DataQuality.PriceCoverage);
        AssertFinite(report.Twr.Value);
        AssertFinite(report.Xirr.Value);
        foreach (var point in report.MonthlyPoints)
            AssertFinite(point.CumulativeTwr);
        var twrResult = StockPerformanceCalculator.CalculateTwr(new StockPerformanceInput(
            report.DateStart,
            report.DateEnd,
            [stock],
            [Buy(1, 1, new DateOnly(2026, 1, 1), 1_000_000m, 0.000001m)],
            [Price(1, "2330", new DateOnly(2026, 1, 1), 0.000001m, 0.000001m), Price(1, "2330", new DateOnly(2026, 1, 2), 0.000002m, 0.000002m)]));
        AssertFinite(twrResult.Metric.Value);
        AssertFinite(twrResult.PriceCoverage);
        foreach (var point in twrResult.Points)
        {
            AssertFinite(point.DailyReturn);
            AssertFinite(point.CumulativeReturn);
        }
    }

    /// <summary>建立純測試用股票主檔。</summary>
    private static Stock CreateStock(int id, decimal shares, decimal buyPrice, decimal currentPrice)
        => new()
        {
            Id = id,
            Name = $"標的{id}",
            Symbol = id == 1 ? "2330" : "0050",
            Market = StockMarket.Twse,
            Shares = shares,
            BuyPrice = buyPrice,
            CurrentPrice = currentPrice,
            Broker = "測試券商",
        };

    /// <summary>建立買入交易 fixture。</summary>
    private static StockTransaction Buy(int id, int stockId, DateOnly date, decimal shares, decimal price, decimal fee = 0m, decimal tax = 0m)
        => new()
        {
            Id = id,
            StockId = stockId,
            Type = StockTransactionType.Buy,
            TradeDate = date,
            Sequence = id,
            Shares = shares,
            Price = price,
            Fee = fee,
            Tax = tax,
        };

    /// <summary>建立賣出交易 fixture。</summary>
    private static StockTransaction Sell(int id, int stockId, DateOnly date, decimal shares, decimal price, decimal fee = 0m, decimal tax = 0m)
        => new()
        {
            Id = id,
            StockId = stockId,
            Type = StockTransactionType.Sell,
            TradeDate = date,
            Sequence = id,
            Shares = shares,
            Price = price,
            Fee = fee,
            Tax = tax,
        };

    /// <summary>建立股息交易 fixture。</summary>
    private static StockTransaction Dividend(int id, int stockId, DateOnly date, decimal cashAmount, decimal fee = 0m, decimal tax = 0m)
        => new()
        {
            Id = id,
            StockId = stockId,
            Type = StockTransactionType.Dividend,
            TradeDate = date,
            Sequence = id,
            CashAmount = cashAmount,
            Fee = fee,
            Tax = tax,
        };

    /// <summary>建立股票股利交易 fixture，固定為正股數與零費稅。</summary>
    private static StockTransaction StockDividend(int id, int stockId, DateOnly date, decimal shares)
        => new()
        {
            Id = id,
            StockId = stockId,
            Type = StockTransactionType.StockDividend,
            TradeDate = date,
            Sequence = id,
            Shares = shares,
            Fee = 0m,
            Tax = 0m,
        };

    /// <summary>建立涵蓋 XIRR、TWR、月度點與 instrument breakdown 的股票股利績效 fixture。</summary>
    private static StockPerformanceInput CreateStockDividendPerformanceInput()
    {
        var start = new DateOnly(2026, 1, 1);
        var stockDividendDate = new DateOnly(2026, 6, 1);
        var end = new DateOnly(2026, 12, 31);
        return new StockPerformanceInput(
            start,
            end,
            [CreateStock(1, shares: 110m, buyPrice: 10m, currentPrice: 10m)],
            [
                Buy(1, 1, start, 100m, 10m),
                StockDividend(2, 1, stockDividendDate, 10m),
            ],
            [
                Price(1, "2330", start, 10m, 10m),
                Price(1, "2330", stockDividendDate, 10m, 10m),
                Price(1, "2330", end, 10m, 10m),
            ],
            AsOfDate: end);
    }

    /// <summary>建立同時含 adjusted 與 raw close 的歷史價格 fixture。</summary>
    private static HistoricalAdjustedPrice Price(int stockId, string symbol, DateOnly date, decimal adjustedClose, decimal close)
        => new()
        {
            Id = stockId,
            Market = StockMarket.Twse,
            Symbol = symbol,
            TradingDate = date,
            AdjustedClose = adjustedClose,
            Close = close,
            Provider = "fixture",
            FetchedAtUtc = DateTime.UtcNow,
        };

    /// <summary>驗證 nullable double 有值時必須是有限數字。</summary>
    private static void AssertFinite(double? value)
    {
        if (value.HasValue)
            Assert.True(double.IsFinite(value.Value));
    }
}
