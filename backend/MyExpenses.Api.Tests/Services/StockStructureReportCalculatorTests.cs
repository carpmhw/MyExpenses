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

    /// <summary>建立持股測試資料。</summary>
    private static Stock CreateStock(
        int id,
        string name,
        string symbol,
        StockInstrumentType instrumentType,
        decimal buyPrice,
        decimal currentPrice,
        decimal shares,
        string? broker)
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
        };
    }
}
