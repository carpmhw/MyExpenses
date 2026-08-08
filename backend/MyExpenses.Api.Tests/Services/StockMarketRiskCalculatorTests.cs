using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class StockMarketRiskCalculatorTests
{
    /// <summary>驗證簡單日報酬只使用連續有效還原價格與指定期間邊界。</summary>
    [Fact]
    public void CalculateDailyReturns_UsesAdjustedPriceAndPeriodBoundary()
    {
        var points = new[]
        {
            Price(StockMarket.Twse, "2330", new DateOnly(2026, 1, 1), 100m),
            Price(StockMarket.Twse, "2330", new DateOnly(2026, 1, 2), 110m),
            Price(StockMarket.Twse, "2330", new DateOnly(2026, 1, 3), 0m),
            Price(StockMarket.Twse, "2330", new DateOnly(2026, 1, 4), 121m),
        };

        var returns = StockMarketRiskCalculator.CalculateDailyReturns(
            points,
            new DateOnly(2026, 1, 2),
            new DateOnly(2026, 1, 4));

        Assert.Equal(0.1d, returns[new DateOnly(2026, 1, 2)], 10);
        Assert.Equal(0.1d, returns[new DateOnly(2026, 1, 4)], 10);
        Assert.Equal(2, returns.Count);
    }

    /// <summary>驗證 3、6、12 個月分別採用 50、100、200 筆報酬門檻。</summary>
    [Fact]
    public void MinimumObservations_UsesConfiguredPeriodThresholds()
    {
        Assert.Equal(50, StockMarketRiskCalculator.MinimumObservations(3));
        Assert.Equal(100, StockMarketRiskCalculator.MinimumObservations(6));
        Assert.Equal(200, StockMarketRiskCalculator.MinimumObservations(12));
    }

    /// <summary>驗證跨券商相同市場代號會合併毛市值並排除未知市場。</summary>
    [Fact]
    public void Calculate_MergesSameInstrumentAndReportsExclusionReasons()
    {
        var calculationDate = new DateOnly(2026, 8, 7);
        var holdings = new[]
        {
            Stock("甲", "2330", StockMarket.Twse, 10m, 100m),
            Stock("乙", " 2330 ", StockMarket.Twse, 5m, 100m),
            Stock("未知", "00679B", StockMarket.Unknown, 10m, 50m),
            Stock("空白", "  ", StockMarket.Twse, 10m, 50m),
            Stock("負值", "9999", StockMarket.Twse, -1m, 100m),
        };
        var prices = CreatePriceSeries(StockMarket.Twse, "2330", 60, 100m, 1.01m);

        var result = StockMarketRiskCalculator.Calculate(holdings, prices, 3, calculationDate);

        var included = Assert.Single(result.IncludedInstruments);
        Assert.Equal("2330", included.Symbol);
        Assert.Equal(1500m, included.GrossMarketValue);
        Assert.Equal(3, result.ExcludedInstruments.Count);
        Assert.Contains(result.ExcludedInstruments, item => item.ExclusionReason == StockMarketRiskUnavailableReason.UnknownMarket);
        Assert.Contains(result.ExcludedInstruments, item => item.ExclusionReason == StockMarketRiskUnavailableReason.BlankSymbol);
        Assert.Contains(result.ExcludedInstruments, item => item.ExclusionReason == StockMarketRiskUnavailableReason.NonPositiveGrossValue);
    }

    /// <summary>驗證市值覆蓋率達到 90% 時可使用合格標的重新正規化權重。</summary>
    [Fact]
    public void Calculate_AllowsExactNinetyPercentCoverageAndRenormalizesWeights()
    {
        var holdings = new[]
        {
            Stock("合格", "2330", StockMarket.Twse, 9m, 100m),
            Stock("不足", "00679B", StockMarket.Tpex, 1m, 100m),
        };
        var prices = CreatePriceSeries(StockMarket.Twse, "2330", 60, 100m, 1.01m);
        var result = StockMarketRiskCalculator.Calculate(holdings, prices, 3, new DateOnly(2026, 8, 7));

        Assert.Equal(0.9d, result.EligibleMarketValueCoverage, 10);
        Assert.NotNull(result.PortfolioAnnualizedVolatility.Value);
        Assert.Equal(1d, Assert.Single(result.IncludedInstruments).RenormalizedWeight, 10);
        Assert.Equal(StockMarketRiskUnavailableReason.InsufficientHistory,
            Assert.Single(result.ExcludedInstruments).ExclusionReason);
    }

    /// <summary>驗證覆蓋率低於 90% 時組合波動度不可用而不冒充完整組合。</summary>
    [Fact]
    public void Calculate_MarksPortfolioUnavailableBelowCoverageThreshold()
    {
        var holdings = new[]
        {
            Stock("合格", "2330", StockMarket.Twse, 899m, 1m),
            Stock("缺少", "00679B", StockMarket.Tpex, 101m, 1m),
        };
        var prices = CreatePriceSeries(StockMarket.Twse, "2330", 60, 100m, 1.01m);

        var result = StockMarketRiskCalculator.Calculate(holdings, prices, 3, new DateOnly(2026, 8, 7));

        Assert.Equal(0.899d, result.EligibleMarketValueCoverage, 10);
        Assert.Null(result.PortfolioAnnualizedVolatility.Value);
        Assert.Equal(StockMarketRiskUnavailableReason.CoverageBelowThreshold,
            result.PortfolioAnnualizedVolatility.UnavailableReason);
    }

    /// <summary>驗證個別與組合波動度使用樣本標準差及 sqrt(252) 年化。</summary>
    [Fact]
    public void Calculate_AnnualizesSampleVolatilityFromCommonReturns()
    {
        var holdings = new[]
        {
            Stock("甲", "AAA", StockMarket.Twse, 1m, 100m),
            Stock("乙", "BBB", StockMarket.Twse, 1m, 100m),
        };
        var prices = CreateAlternatingSeries(StockMarket.Twse, "AAA", 50, 100m, 1.01m, -0.01m)
            .Concat(CreateAlternatingSeries(StockMarket.Twse, "BBB", 50, 100m, 1.02m, -0.02m))
            .ToList();

        var result = StockMarketRiskCalculator.Calculate(holdings, prices, 3, new DateOnly(2026, 8, 7));

        Assert.All(result.VolatilityRanking, item => Assert.True(double.IsFinite(item.AnnualizedVolatility)));
        Assert.NotNull(result.PortfolioAnnualizedVolatility.Value);
        Assert.Equal(50, result.CommonObservationCount);
        Assert.True(result.VolatilityRanking[0].AnnualizedVolatility >= result.VolatilityRanking[1].AnnualizedVolatility);
    }

    /// <summary>驗證相關矩陣使用市值前十檔、共同日期、對稱值與一對角線。</summary>
    [Fact]
    public void Calculate_BuildsTopTenSymmetricCorrelationMatrix()
    {
        var holdings = Enumerable.Range(0, 11)
            .Select(index => Stock($"標的{index}", $"{index:0000}", StockMarket.Twse, 11 - index, 100m))
            .ToList();
        var prices = holdings
            .SelectMany((stock, index) => CreatePriceSeries(StockMarket.Twse, stock.Symbol, 60, 100m, 1m + (index + 1) / 100m))
            .ToList();

        var result = StockMarketRiskCalculator.Calculate(holdings, prices, 3, new DateOnly(2026, 8, 7));

        var matrix = result.CorrelationMatrix;
        Assert.NotNull(matrix);
        Assert.Null(matrix.UnavailableReason);
        Assert.Equal(10, matrix.Labels.Count);
        Assert.Equal(60, matrix.CommonObservationCount);
        for (var row = 0; row < matrix.Values.Count; row++)
        {
            Assert.Equal(1d, matrix.Values[row][row]);
            for (var column = 0; column < matrix.Values.Count; column++)
                Assert.Equal(matrix.Values[row][column], matrix.Values[column][row]);
        }
    }

    /// <summary>驗證少於兩檔合格標的時只停用相關矩陣並保留其他統計。</summary>
    [Fact]
    public void Calculate_MarksCorrelationUnavailableWithOneEligibleInstrument()
    {
        var holdings = new[] { Stock("唯一", "2330", StockMarket.Twse, 1m, 100m) };
        var prices = CreatePriceSeries(StockMarket.Twse, "2330", 60, 100m, 1.01m);

        var result = StockMarketRiskCalculator.Calculate(holdings, prices, 3, new DateOnly(2026, 8, 7));

        Assert.NotNull(result.PortfolioAnnualizedVolatility.Value);
        Assert.Equal(StockMarketRiskUnavailableReason.NotEnoughEligibleInstruments,
            result.CorrelationMatrix!.UnavailableReason);
    }

    /// <summary>驗證報表同步警告只包含目前持股的市場代號。</summary>
    [Fact]
    public void Calculate_ExcludesWarningsForDeletedOrSupersededInstruments()
    {
        var holdings = new[] { Stock("目前持股", "2330", StockMarket.Twse, 1m, 100m) };
        var states = new[]
        {
            new HistoricalPriceSyncState
            {
                Market = StockMarket.Twse,
                Symbol = "2330",
                Status = HistoricalPriceSyncStatus.ProviderError,
                SafeMessage = "目前警告",
            },
            new HistoricalPriceSyncState
            {
                Market = StockMarket.Unknown,
                Symbol = "2330",
                Status = HistoricalPriceSyncStatus.AmbiguousMarket,
                SafeMessage = "過期未知狀態",
            },
            new HistoricalPriceSyncState
            {
                Market = StockMarket.Tpex,
                Symbol = "9999",
                Status = HistoricalPriceSyncStatus.ProviderError,
                SafeMessage = "已刪除標的",
            },
        };

        var result = StockMarketRiskCalculator.Calculate(
            holdings,
            [],
            3,
            new DateOnly(2026, 8, 7),
            states);

        var warning = Assert.Single(result.SyncWarnings);
        Assert.Equal(StockMarket.Twse, warning.Market);
        Assert.Equal("2330", warning.Symbol);
    }

    /// <summary>建立指定市場、代號與價格的歷史點。</summary>
    private static HistoricalAdjustedPrice Price(StockMarket market, string symbol, DateOnly date, decimal value)
        => new()
        {
            Market = market,
            Symbol = symbol,
            TradingDate = date,
            AdjustedClose = value,
            Provider = "fixture",
            FetchedAtUtc = DateTime.UtcNow,
        };

    /// <summary>建立供風險計算使用的目前持股。</summary>
    private static Stock Stock(string name, string symbol, StockMarket market, decimal shares, decimal currentPrice)
        => new()
        {
            Name = name,
            Symbol = symbol,
            Market = market,
            Shares = shares,
            BuyPrice = currentPrice,
            CurrentPrice = currentPrice,
            InstrumentType = StockInstrumentType.Stock,
        };

    /// <summary>建立固定成長率的連續歷史價格序列。</summary>
    private static IReadOnlyList<HistoricalAdjustedPrice> CreatePriceSeries(
        StockMarket market,
        string symbol,
        int returnCount,
        decimal initialPrice,
        decimal dailyMultiplier)
    {
        var points = new List<HistoricalAdjustedPrice>();
        var price = initialPrice;
        var startDate = new DateOnly(2026, 1, 1);
        for (var index = 0; index <= returnCount; index++)
        {
            points.Add(Price(market, symbol, startDate.AddDays(index), price));
            price *= dailyMultiplier;
        }

        return points;
    }

    /// <summary>建立兩種固定日報酬交替的手算波動度 fixture。</summary>
    private static IReadOnlyList<HistoricalAdjustedPrice> CreateAlternatingSeries(
        StockMarket market,
        string symbol,
        int returnCount,
        decimal initialPrice,
        decimal positiveMultiplier,
        decimal negativeReturn)
    {
        var points = new List<HistoricalAdjustedPrice>();
        var price = initialPrice;
        var startDate = new DateOnly(2026, 1, 1);
        for (var index = 0; index <= returnCount; index++)
        {
            points.Add(Price(market, symbol, startDate.AddDays(index), price));
            price *= index % 2 == 0 ? positiveMultiplier : 1m + negativeReturn;
        }

        return points;
    }
}
