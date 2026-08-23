using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class StockStructureReportCalculatorTests
{
    /// <summary>驗證報表彙總沿用估值結果，且零成本時損益率保持不可用。</summary>
    [Fact]
    public void Calculate_SummarizesValuationAndUsesNullableReturnWhenCostIsZero()
    {
        var holdings = new[]
        {
            CreateStock(1, "無成本", "ZERO", StockInstrumentType.Stock, 0m, 0m, 10m, "甲券商"),
        };

        var report = StockStructureReportCalculator.Calculate(holdings);

        Assert.Single(report.Holdings);
        Assert.Equal(0m, report.Summary.TotalEstimatedBuyCost);
        Assert.Equal(0m, report.Summary.TotalEstimatedNetSellValue);
        Assert.Equal(0m, report.Summary.TotalEstimatedGainLoss);
        Assert.Null(report.Summary.EstimatedGainLossPercentage);
        Assert.All(report.SymbolAllocations, allocation => Assert.Null(allocation.Percentage));
        Assert.All(report.InstrumentTypeAllocations, allocation => Assert.Null(allocation.Percentage));
        Assert.All(report.BrokerAllocations, allocation => Assert.Null(allocation.Percentage));
    }

    /// <summary>驗證同代號跨券商合併、空白代號分離、未指定券商分組與配置排序。</summary>
    [Fact]
    public void Calculate_GroupsNormalizedSymbolsAndPreservesOriginalRows()
    {
        var holdings = new[]
        {
            CreateStock(1, "台積電 A", " 2330 ", StockInstrumentType.Stock, 90m, 100m, 10m, "甲券商"),
            CreateStock(2, "台積電 B", "2330", StockInstrumentType.Stock, 90m, 100m, 5m, "乙券商"),
            CreateStock(3, "無代號 A", "", StockInstrumentType.StockEtf, 90m, 100m, 10m, null),
            CreateStock(4, "無代號 B", "  ", StockInstrumentType.BondEtf, 90m, 100m, 1m, "甲券商"),
        };

        var report = StockStructureReportCalculator.Calculate(holdings);
        var firstValuation = StockValuationCalculator.Calculate(holdings[0]);
        var secondValuation = StockValuationCalculator.Calculate(holdings[1]);

        var symbol = Assert.Single(report.SymbolAllocations, allocation => allocation.Key == "2330");
        Assert.Equal(firstValuation.EstimatedNetSellValue + secondValuation.EstimatedNetSellValue, symbol.Value);
        Assert.Equal(3, report.SymbolAllocations.Count);
        Assert.Equal(4, report.Holdings.Count);
        Assert.NotNull(report.Holdings[0].AllocationPercentage);
        Assert.Contains(report.BrokerAllocations, allocation => allocation.Label == "未指定券商");
        Assert.Equal(
            report.SymbolAllocations.OrderByDescending(allocation => allocation.Value).Select(allocation => allocation.Key),
            report.SymbolAllocations.Select(allocation => allocation.Key));

        var caseInsensitiveReport = StockStructureReportCalculator.Calculate(new[]
        {
            CreateStock(5, "大小寫一", "abc", StockInstrumentType.Stock, 90m, 100m, 10m, "甲券商"),
            CreateStock(6, "大小寫二", " ABC ", StockInstrumentType.Stock, 90m, 100m, 10m, "乙券商"),
        });
        Assert.Single(caseInsensitiveReport.SymbolAllocations);
        Assert.Equal("ABC", caseInsensitiveReport.SymbolAllocations[0].Key);
    }

    /// <summary>驗證固定集中度、類型、券商與虧損規則的邊界結果。</summary>
    [Fact]
    public void Calculate_EmitsConfiguredInsightsAtInclusiveThresholds()
    {
        var holdings = new[]
        {
            CreateStock(1, "集中標的", "AAA", StockInstrumentType.Stock, 100m, 120m, 1000m, "甲券商"),
            CreateStock(2, "第二標的", "BBB", StockInstrumentType.Stock, 100m, 80m, 200m, "甲券商"),
            CreateStock(3, "第三標的", "CCC", StockInstrumentType.Stock, 100m, 80m, 100m, "甲券商"),
            CreateStock(4, "第四標的", "DDD", StockInstrumentType.Stock, 100m, 80m, 100m, "乙券商"),
        };

        var report = StockStructureReportCalculator.Calculate(holdings);
        var codes = report.Insights.Select(insight => insight.Code).ToHashSet();

        Assert.Contains("SingleSymbolConcentration", codes);
        Assert.Contains("TopThreeConcentration", codes);
        Assert.Contains("InstrumentTypeConcentration", codes);
        Assert.Contains("BrokerConcentration", codes);
        Assert.Contains("EstimatedLosses", codes);
    }

    /// <summary>驗證分散且獲利的持股只產生無提醒資訊。</summary>
    [Fact]
    public void Calculate_ReportsNoReminderWhenNoRuleIsTriggered()
    {
        var holdings = new[]
        {
            CreateStock(1, "標的一", "AAA", StockInstrumentType.Stock, 100m, 110m, 100m, "甲券商"),
            CreateStock(2, "標的二", "BBB", StockInstrumentType.StockEtf, 100m, 110m, 100m, "乙券商"),
            CreateStock(3, "標的三", "CCC", StockInstrumentType.BondEtf, 100m, 110m, 100m, "丙券商"),
            CreateStock(4, "標的四", "DDD", StockInstrumentType.Stock, 100m, 110m, 100m, "丁券商"),
            CreateStock(5, "標的五", "EEE", StockInstrumentType.StockEtf, 100m, 110m, 100m, "戊券商"),
        };

        var report = StockStructureReportCalculator.Calculate(holdings);

        var insight = Assert.Single(report.Insights);
        Assert.Equal("NoReminder", insight.Code);
    }

    /// <summary>驗證市場配置跨券商合併、保留待辨識市場並依估值排序。</summary>
    [Fact]
    public void Calculate_GroupsMarketAllocationsAcrossBrokersAndPreservesUnknownMarket()
    {
        var holdings = new[]
        {
            CreateStock(1, "上市甲", "AAA", StockInstrumentType.Stock, 100m, 100m, 50m, "甲券商", StockMarket.Twse),
            CreateStock(2, "上市乙", "BBB", StockInstrumentType.Stock, 100m, 100m, 30m, "乙券商", StockMarket.Twse),
            CreateStock(3, "待辨識", "CCC", StockInstrumentType.Stock, 100m, 100m, 20m, "丙券商", StockMarket.Unknown),
        };

        var report = StockStructureReportCalculator.Calculate(holdings);
        var twseValue = StockValuationCalculator.Calculate(holdings[0]).EstimatedNetSellValue
            + StockValuationCalculator.Calculate(holdings[1]).EstimatedNetSellValue;
        var unknownValue = StockValuationCalculator.Calculate(holdings[2]).EstimatedNetSellValue;
        var total = twseValue + unknownValue;

        Assert.Collection(
            report.MarketAllocations,
            allocation =>
            {
                Assert.Equal("Twse", allocation.Key);
                Assert.Equal("上市", allocation.Label);
                Assert.Equal(twseValue, allocation.Value);
                Assert.Equal(twseValue / total * 100m, allocation.Percentage);
            },
            allocation =>
            {
                Assert.Equal("Unknown", allocation.Key);
                Assert.Equal("市場待辨識", allocation.Label);
                Assert.Equal(unknownValue, allocation.Value);
                Assert.Equal(unknownValue / total * 100m, allocation.Percentage);
            });
    }

    /// <summary>驗證集中度依標的配置計算且不受輸入順序影響。</summary>
    [Fact]
    public void Calculate_CalculatesConcentrationFromSymbolAllocationsIndependentlyOfInputOrder()
    {
        var holdings = new[]
        {
            CreateStock(1, "甲", "AAA", StockInstrumentType.Stock, 100m, 100m, 50m, "甲券商"),
            CreateStock(2, "乙", "BBB", StockInstrumentType.Stock, 100m, 100m, 30m, "乙券商"),
            CreateStock(3, "丙", "CCC", StockInstrumentType.Stock, 100m, 100m, 20m, "丙券商"),
        };

        var report = StockStructureReportCalculator.Calculate(holdings);
        var reversedReport = StockStructureReportCalculator.Calculate(holdings.Reverse());
        var weights = report.SymbolAllocations
            .Select(allocation => allocation.Percentage!.Value / 100m)
            .ToList();

        Assert.Equal(report.SymbolAllocations.Take(1).Sum(allocation => allocation.Percentage), report.Concentration.Top1Percentage);
        Assert.Equal(report.SymbolAllocations.Take(3).Sum(allocation => allocation.Percentage), report.Concentration.Top3Percentage);
        Assert.Equal(report.SymbolAllocations.Take(5).Sum(allocation => allocation.Percentage), report.Concentration.Top5Percentage);
        Assert.Equal(weights.Sum(weight => weight * weight), report.Concentration.Hhi);
        Assert.Equal(1m / report.Concentration.Hhi, report.Concentration.EffectiveHoldingCount);
        Assert.Equal(report.Concentration, reversedReport.Concentration);
    }

    /// <summary>驗證沒有持股或估值分母不正時不產生合成的市場比例或集中度。</summary>
    [Fact]
    public void Calculate_ReturnsUnavailableMarketPercentagesAndConcentrationForNonPositiveDenominator()
    {
        foreach (var currentPrice in new[] { 0m, -1m })
        {
            var report = StockStructureReportCalculator.Calculate(new[]
            {
                CreateStock(1, "無有效估值", "ZERO", StockInstrumentType.Stock, 100m, currentPrice, 10m, "甲券商"),
            });

            Assert.All(report.MarketAllocations, allocation => Assert.Null(allocation.Percentage));
            Assert.Null(report.Concentration.Top1Percentage);
            Assert.Null(report.Concentration.Top3Percentage);
            Assert.Null(report.Concentration.Top5Percentage);
            Assert.Null(report.Concentration.Hhi);
            Assert.Null(report.Concentration.EffectiveHoldingCount);
        }

        var emptyReport = StockStructureReportCalculator.Calculate(Array.Empty<Stock>());

        Assert.Empty(emptyReport.MarketAllocations);
        Assert.Null(emptyReport.Concentration.Hhi);
    }

    /// <summary>建立持股測試資料。</summary>
    private static Stock CreateStock(
        int id,
        string name,
        string symbol,
        StockInstrumentType instrumentType,
        decimal buyPrice,
        decimal currentPrice,
        decimal shares,
        string? broker,
        StockMarket market = StockMarket.Unknown)
    {
        return new Stock
        {
            Id = id,
            Name = name,
            Symbol = symbol,
            InstrumentType = instrumentType,
            BuyPrice = buyPrice,
            CurrentPrice = currentPrice,
            Shares = shares,
            Broker = broker,
            Market = market,
        };
    }
}
