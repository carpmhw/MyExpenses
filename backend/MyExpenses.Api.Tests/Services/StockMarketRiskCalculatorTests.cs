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

    /// <summary>驗證無正毛市值分母時保留舊 coverage 零值，但另以 typed metric 標示不可用。</summary>
    [Fact]
    public void Calculate_MarksCoverageMetricUnavailableWithoutPositiveGrossValue()
    {
        var result = StockMarketRiskCalculator.Calculate(
            [Stock("零市值", "AAA", StockMarket.Twse, 0m, 100m)],
            [],
            3,
            new DateOnly(2026, 8, 7));

        Assert.Equal(0d, result.EligibleMarketValueCoverage);
        Assert.Null(result.EligibleMarketValueCoverageMetric.Value);
        Assert.Equal(StockMarketRiskUnavailableReason.NoEligibleInstruments,
            result.EligibleMarketValueCoverageMetric.UnavailableReason);
    }

    /// <summary>驗證不相關標的較新的本機行情不會改變目前持股的資料截止日。</summary>
    [Fact]
    public void Calculate_UsesOnlyCurrentRecognizedHoldingPricesForDataCutoffDate()
    {
        var holdingPrices = CreatePriceSeries(StockMarket.Twse, "AAA", 60, 100m, 1.01m);
        var prices = holdingPrices
            .Append(Price(StockMarket.Twse, "UNRELATED", new DateOnly(2026, 8, 7), 100m))
            .ToList();

        var result = StockMarketRiskCalculator.Calculate(
            [Stock("目前持股", " aaa ", StockMarket.Twse, 1m, 100m)],
            prices,
            3,
            new DateOnly(2026, 8, 7));

        Assert.Equal(holdingPrices.Max(price => price.TradingDate), result.DataCutoffDate);
        Assert.Single(result.IncludedInstruments);
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

    /// <summary>驗證單調上升的組合累積路徑沒有最大回撤。</summary>
    [Fact]
    public void Calculate_ReturnsZeroMaximumDrawdownForMonotonicGrowth()
    {
        var result = StockMarketRiskCalculator.Calculate(
            [Stock("上升", "AAA", StockMarket.Twse, 1m, 100m)],
            CreateReturnSeries(StockMarket.Twse, "AAA", Enumerable.Repeat(0.01d, 50)),
            3,
            new DateOnly(2026, 8, 7));

        Assert.Equal(0d, result.PortfolioMaximumDrawdown.Value!.Value, 10);
        Assert.Null(result.PortfolioMaximumDrawdown.UnavailableReason);
    }

    /// <summary>驗證最大回撤會取峰值至谷底的最大損失。</summary>
    [Fact]
    public void Calculate_ReturnsPeakToTroughMaximumDrawdown()
    {
        var returns = new[] { 0.10d, -0.20d }.Concat(Enumerable.Repeat(0d, 48));
        var result = StockMarketRiskCalculator.Calculate(
            [Stock("回撤", "AAA", StockMarket.Twse, 1m, 100m)],
            CreateReturnSeries(StockMarket.Twse, "AAA", returns),
            3,
            new DateOnly(2026, 8, 7));

        Assert.Equal(-0.20d, result.PortfolioMaximumDrawdown.Value!.Value, 10);
    }

    /// <summary>驗證日組合報酬為負百分之百時，最大回撤為負一而不是非有限結果。</summary>
    [Fact]
    public void CalculateMaximumDrawdown_ReturnsNegativeOneForCompleteLoss()
    {
        var result = InvokeMaximumDrawdown([-1d]);

        Assert.Equal(-1d, result.Value);
        Assert.Null(result.UnavailableReason);
    }

    /// <summary>驗證非有限日組合報酬會使最大回撤安全地標示為不可用。</summary>
    [Fact]
    public void CalculateMaximumDrawdown_MarksNonFiniteDailyReturnUnavailable()
    {
        var result = InvokeMaximumDrawdown([double.PositiveInfinity]);

        Assert.Null(result.Value);
        Assert.Equal(StockMarketRiskUnavailableReason.NonFiniteResult, result.UnavailableReason);
    }

    /// <summary>驗證創新高後的回撤以新峰值重新計算。</summary>
    [Fact]
    public void Calculate_ResetsMaximumDrawdownPeakAfterNewHigh()
    {
        var returns = new[] { 0.10d, -0.10d, 0.20d, -0.25d }.Concat(Enumerable.Repeat(0d, 46));
        var result = StockMarketRiskCalculator.Calculate(
            [Stock("重設峰值", "AAA", StockMarket.Twse, 1m, 100m)],
            CreateReturnSeries(StockMarket.Twse, "AAA", returns),
            3,
            new DateOnly(2026, 8, 7));

        Assert.Equal(-0.25d, result.PortfolioMaximumDrawdown.Value!.Value, 10);
    }

    /// <summary>驗證最大回撤沿用覆蓋率、共同日期與空持股的不可用 gate。</summary>
    [Theory]
    [InlineData(StockMarketRiskUnavailableReason.NoHoldings)]
    [InlineData(StockMarketRiskUnavailableReason.CoverageBelowThreshold)]
    [InlineData(StockMarketRiskUnavailableReason.InsufficientCommonDates)]
    public void Calculate_MarksMaximumDrawdownUnavailableWhenPortfolioGateFails(StockMarketRiskUnavailableReason expectedReason)
    {
        IReadOnlyList<Stock> holdings = expectedReason switch
        {
            StockMarketRiskUnavailableReason.NoHoldings => [],
            StockMarketRiskUnavailableReason.CoverageBelowThreshold =>
            [
                Stock("合格", "AAA", StockMarket.Twse, 899m, 1m),
                Stock("不足", "BBB", StockMarket.Twse, 101m, 1m),
            ],
            _ =>
            [
                Stock("共同日期不足甲", "AAA", StockMarket.Twse, 1m, 100m),
                Stock("共同日期不足乙", "BBB", StockMarket.Twse, 1m, 100m),
            ],
        };
        IReadOnlyList<HistoricalAdjustedPrice> prices = expectedReason switch
        {
            StockMarketRiskUnavailableReason.NoHoldings => [],
            StockMarketRiskUnavailableReason.InsufficientCommonDates => CreateReturnSeries(StockMarket.Twse, "AAA", Enumerable.Repeat(0.01d, 50))
                .Concat(CreateReturnSeries(StockMarket.Twse, "BBB", Enumerable.Repeat(0.01d, 50), new DateOnly(2026, 1, 2))).ToList(),
            _ => CreateReturnSeries(StockMarket.Twse, "AAA", Enumerable.Repeat(0.01d, 50)),
        };

        var result = StockMarketRiskCalculator.Calculate(holdings, prices, 3, new DateOnly(2026, 8, 7));

        Assert.Null(result.PortfolioMaximumDrawdown.Value);
        Assert.Equal(expectedReason, result.PortfolioMaximumDrawdown.UnavailableReason);
    }

    /// <summary>驗證單一標的承擔全部組合風險貢獻。</summary>
    [Fact]
    public void Calculate_ReturnsOneHundredPercentRiskContributionForSingleInstrument()
    {
        var result = StockMarketRiskCalculator.Calculate(
            [Stock("唯一", "AAA", StockMarket.Twse, 1m, 100m)],
            CreateReturnSeries(StockMarket.Twse, "AAA", CreateAlternatingReturns(50, 0.01d, -0.01d)),
            3,
            new DateOnly(2026, 8, 7));

        var contribution = Assert.Single(result.RiskContributions);
        Assert.Equal("AAA", contribution.Symbol);
        Assert.Equal(1d, contribution.Weight, 10);
        Assert.Equal(result.PortfolioAnnualizedVolatility.Value!.Value, contribution.ComponentVolatilityContribution, 10);
        Assert.Equal(1d, contribution.ContributionPercentage, 10);
    }

    /// <summary>驗證等權且完全相關的標的各承擔一半風險。</summary>
    [Fact]
    public void Calculate_ReturnsEqualRiskContributionsForEqualCorrelatedInstruments()
    {
        var returns = CreateAlternatingReturns(50, 0.01d, -0.01d);
        var result = StockMarketRiskCalculator.Calculate(
            [Stock("甲", "AAA", StockMarket.Twse, 1m, 100m), Stock("乙", "BBB", StockMarket.Twse, 1m, 100m)],
            CreateReturnSeries(StockMarket.Twse, "AAA", returns)
                .Concat(CreateReturnSeries(StockMarket.Twse, "BBB", returns)).ToList(),
            3,
            new DateOnly(2026, 8, 7));

        Assert.All(result.RiskContributions, contribution => Assert.Equal(0.5d, contribution.ContributionPercentage, 10));
        Assert.Equal(1d, result.RiskContributions.Sum(contribution => contribution.ContributionPercentage), 10);
    }

    /// <summary>驗證不等市值權重的完全相關標的依權重分配風險且總和近似一。</summary>
    [Fact]
    public void Calculate_ReturnsWeightedRiskContributionsThatSumToOne()
    {
        var returns = CreateAlternatingReturns(50, 0.01d, -0.01d);
        var result = StockMarketRiskCalculator.Calculate(
            [Stock("大", "AAA", StockMarket.Twse, 3m, 100m), Stock("小", "BBB", StockMarket.Twse, 1m, 100m)],
            CreateReturnSeries(StockMarket.Twse, "AAA", returns)
                .Concat(CreateReturnSeries(StockMarket.Twse, "BBB", returns)).ToList(),
            3,
            new DateOnly(2026, 8, 7));

        Assert.Equal(["AAA", "BBB"], result.RiskContributions.Select(contribution => contribution.Symbol));
        Assert.Equal(0.75d, result.RiskContributions[0].ContributionPercentage, 10);
        Assert.Equal(0.25d, result.RiskContributions[1].ContributionPercentage, 10);
        Assert.InRange(result.RiskContributions.Sum(contribution => contribution.ContributionPercentage), 0.999999999d, 1.000000001d);
    }

    /// <summary>驗證分散標的的負風險貢獻不會被截斷為零。</summary>
    [Fact]
    public void Calculate_PreservesNegativeDiversificationRiskContribution()
    {
        var firstReturns = CreateAlternatingReturns(50, 0.02d, -0.01d);
        var inverseReturns = firstReturns.Select(value => -value).ToList();
        var result = StockMarketRiskCalculator.Calculate(
            [Stock("主風險", "AAA", StockMarket.Twse, 4m, 100m), Stock("分散", "BBB", StockMarket.Twse, 1m, 100m)],
            CreateReturnSeries(StockMarket.Twse, "AAA", firstReturns)
                .Concat(CreateReturnSeries(StockMarket.Twse, "BBB", inverseReturns)).ToList(),
            3,
            new DateOnly(2026, 8, 7));

        Assert.Contains(result.RiskContributions, contribution => contribution.Symbol == "BBB" && contribution.ContributionPercentage < 0d);
        Assert.Equal(1d, result.RiskContributions.Sum(contribution => contribution.ContributionPercentage), 10);
    }

    /// <summary>驗證不可用 gate 與零變異數時不建立合成風險貢獻。</summary>
    [Fact]
    public void Calculate_ReturnsNoRiskContributionsWhenPortfolioOrVarianceIsUnavailable()
    {
        var unavailable = StockMarketRiskCalculator.Calculate(
            [Stock("不足", "AAA", StockMarket.Twse, 1m, 100m)],
            CreateReturnSeries(StockMarket.Twse, "AAA", Enumerable.Repeat(0.01d, 49)),
            3,
            new DateOnly(2026, 8, 7));
        var zeroVariance = StockMarketRiskCalculator.Calculate(
            [Stock("固定", "AAA", StockMarket.Twse, 1m, 100m)],
            CreateReturnSeries(StockMarket.Twse, "AAA", Enumerable.Repeat(0d, 50)),
            3,
            new DateOnly(2026, 8, 7));

        Assert.Empty(unavailable.RiskContributions);
        Assert.Empty(zeroVariance.RiskContributions);
    }

    /// <summary>驗證非有限共變異數不會產生風險貢獻。</summary>
    [Fact]
    public void CalculateRiskContributionPercentages_ReturnsEmptyForNonFiniteCovariance()
    {
        IReadOnlyList<IReadOnlyList<double>> covariance = [[double.NaN]];

        var result = InvokeRiskContributionPercentages(covariance, [1d]);

        Assert.Empty(result);
    }

    /// <summary>驗證溢位成非有限組合變異數時不會產生風險貢獻。</summary>
    [Fact]
    public void CalculateRiskContributionPercentages_ReturnsEmptyForNonFiniteVariance()
    {
        IReadOnlyList<IReadOnlyList<double>> covariance =
        [
            [double.MaxValue, double.MaxValue],
            [double.MaxValue, double.MaxValue],
        ];

        var result = InvokeRiskContributionPercentages(covariance, [1d, 1d]);

        Assert.Empty(result);
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

    /// <summary>由指定日報酬建立連續還原價格序列。</summary>
    private static IReadOnlyList<HistoricalAdjustedPrice> CreateReturnSeries(
        StockMarket market,
        string symbol,
        IEnumerable<double> returns,
        DateOnly? startDate = null)
    {
        var points = new List<HistoricalAdjustedPrice>();
        var price = 100m;
        var firstDate = startDate ?? new DateOnly(2026, 1, 1);
        points.Add(Price(market, symbol, firstDate, price));
        foreach (var dailyReturn in returns)
        {
            price *= (decimal)(1d + dailyReturn);
            points.Add(Price(market, symbol, firstDate.AddDays(points.Count), price));
        }

        return points;
    }

    /// <summary>建立固定兩種日報酬交替的共同日期測試資料。</summary>
    private static IReadOnlyList<double> CreateAlternatingReturns(int count, double first, double second)
        => Enumerable.Range(0, count).Select(index => index % 2 == 0 ? first : second).ToList();

    /// <summary>呼叫由市場風險計算器實際使用的最大回撤純數學 helper。</summary>
    private static StockMarketRiskMetric InvokeMaximumDrawdown(IReadOnlyList<double> returns)
    {
        var method = typeof(StockMarketRiskCalculator).GetMethod(
            "CalculateMaximumDrawdown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<StockMarketRiskMetric>(method!.Invoke(null, [returns]));
    }

    /// <summary>呼叫由市場風險計算器實際使用的風險貢獻純數學 helper。</summary>
    private static IReadOnlyList<double> InvokeRiskContributionPercentages(
        IReadOnlyList<IReadOnlyList<double>> covariance,
        IReadOnlyList<double> weights)
    {
        var method = typeof(StockMarketRiskCalculator).GetMethod(
            "CalculateRiskContributionPercentages",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IReadOnlyList<double>>(method!.Invoke(null, [covariance, weights]));
    }
}
