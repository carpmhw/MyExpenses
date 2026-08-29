using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

public static class StockStructureReportCalculator
{
    private const decimal SingleSymbolThreshold = 30m;
    private const decimal TopThreeThreshold = 70m;
    private const decimal InstrumentTypeThreshold = 80m;
    private const decimal BrokerThreshold = 80m;
    private const string UnspecifiedBrokerKey = "\0unspecified";

    /// <summary>依目前篩選範圍計算持股結構報表的估值、配置與規則提醒。</summary>
    public static StockStructureReport Calculate(
        IEnumerable<Stock> stocks,
        string? brokerFilter = null,
        StockInstrumentType? instrumentTypeFilter = null)
    {
        var normalizedBrokerFilter = NormalizeText(brokerFilter);
        var selectedStocks = stocks
            .Where(stock =>
                (normalizedBrokerFilter is null || string.Equals(
                    NormalizeText(stock.Broker), normalizedBrokerFilter, StringComparison.OrdinalIgnoreCase))
                && (!instrumentTypeFilter.HasValue || stock.InstrumentType == instrumentTypeFilter.Value))
            .ToList();
        var holdings = selectedStocks.Select(ToHoldingRow).ToList();
        var totalEstimatedBuyCost = holdings.Sum(holding => holding.EstimatedBuyCost);
        var totalGrossMarketValue = holdings.Sum(holding => holding.GrossMarketValue);
        var totalEstimatedNetSellValue = holdings.Sum(holding => holding.EstimatedNetSellValue);
        var totalEstimatedGainLoss = holdings.Sum(holding => holding.EstimatedGainLoss);
        if (totalEstimatedNetSellValue > 0m)
        {
            holdings = holdings
                .Select(holding => holding with
                {
                    AllocationPercentage = CalculatePercentage(
                        holding.EstimatedNetSellValue,
                        totalEstimatedNetSellValue),
                })
                .ToList();
        }
        var symbolAllocations = BuildSymbolAllocations(holdings, totalEstimatedNetSellValue);
        var instrumentTypeAllocations = BuildAllocations(
            holdings,
            holding => holding.InstrumentType.ToString(),
            holding => FormatInstrumentType(holding.InstrumentType),
            totalEstimatedNetSellValue);
        var brokerAllocations = BuildAllocations(
            holdings,
            holding => NormalizeText(holding.Broker) ?? UnspecifiedBrokerKey,
            holding => NormalizeText(holding.Broker) ?? "未指定券商",
            totalEstimatedNetSellValue);
        var marketAllocations = BuildMarketAllocations(selectedStocks, totalEstimatedNetSellValue);
        var concentration = BuildConcentration(symbolAllocations, totalEstimatedNetSellValue);

        return new StockStructureReport(
            new StockStructureSummary(
                holdings.Count,
                totalEstimatedBuyCost,
                totalGrossMarketValue,
                totalEstimatedNetSellValue,
                totalEstimatedGainLoss,
                CalculatePercentage(totalEstimatedGainLoss, totalEstimatedBuyCost)),
            BuildInsights(
                holdings,
                symbolAllocations,
                instrumentTypeAllocations,
                brokerAllocations,
                totalEstimatedNetSellValue),
            symbolAllocations,
            instrumentTypeAllocations,
            brokerAllocations,
            marketAllocations,
            concentration,
            holdings);
    }

    /// <summary>將一筆持股轉換為包含既有費稅估值欄位的報表明細。</summary>
    private static StockStructureHolding ToHoldingRow(Stock stock)
    {
        var valuation = StockValuationCalculator.Calculate(stock);
        return new StockStructureHolding(
            stock.Id,
            stock.Name,
            stock.Symbol,
            stock.InstrumentType,
            stock.Shares,
            stock.BuyPrice,
            stock.CurrentPrice,
            stock.Broker,
            valuation.GrossMarketValue,
            valuation.BuyCommission,
            valuation.SellCommission,
            valuation.SecuritiesTransactionTax,
            valuation.EstimatedBuyCost,
            valuation.EstimatedNetSellValue,
            valuation.EstimatedGainLoss,
            null);
    }

    /// <summary>建立以正規化代號分組的配置資料，空白代號則依持股記錄分開。</summary>
    private static IReadOnlyList<StockStructureAllocation> BuildSymbolAllocations(
        IReadOnlyList<StockStructureHolding> holdings,
        decimal totalEstimatedNetSellValue)
    {
        var allocations = holdings
            .GroupBy(holding =>
            {
                var symbol = NormalizeSymbol(holding.Symbol);
                return (Symbol: symbol, HoldingId: symbol is null ? (int?)holding.Id : null);
            })
            .Select(group =>
            {
                var first = group.First();
                var symbol = group.Key.Symbol;
                var label = symbol is null
                    ? $"{first.Name} (#{first.Id})"
                    : $"{first.Name} ({symbol})";
                return new StockStructureAllocation(
                    symbol ?? $"\0holding:{group.Key.HoldingId}",
                    label,
                    group.Sum(holding => holding.EstimatedNetSellValue),
                    null);
            })
            .ToList();

        return ApplyAllocationPercentages(
            allocations,
            totalEstimatedNetSellValue,
            allocation => allocation.Key.StartsWith("\0holding:", StringComparison.Ordinal)
                ? allocation.Label
                : allocation.Key);
    }

    /// <summary>依指定欄位建立並排序配置分組。</summary>
    private static IReadOnlyList<StockStructureAllocation> BuildAllocations(
        IReadOnlyList<StockStructureHolding> holdings,
        Func<StockStructureHolding, string> keySelector,
        Func<StockStructureHolding, string> labelSelector,
        decimal totalEstimatedNetSellValue)
    {
        var allocations = holdings
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .Select(group => new StockStructureAllocation(
                group.Key,
                labelSelector(group.First()),
                group.Sum(holding => holding.EstimatedNetSellValue),
                null))
            .ToList();

        return ApplyAllocationPercentages(allocations, totalEstimatedNetSellValue);
    }

    /// <summary>依市場將篩選後持股的預估賣出淨值彙整為配置資料。</summary>
    private static IReadOnlyList<StockStructureAllocation> BuildMarketAllocations(
        IReadOnlyList<Stock> stocks,
        decimal totalEstimatedNetSellValue)
    {
        var allocations = stocks
            .GroupBy(stock => stock.Market)
            .Select(group => new StockStructureAllocation(
                group.Key.ToString(),
                FormatMarket(group.Key),
                group.Sum(stock => StockValuationCalculator.Calculate(stock).EstimatedNetSellValue),
                null))
            .ToList();

        return ApplyAllocationPercentages(allocations, totalEstimatedNetSellValue);
    }

    /// <summary>依標的配置建立集中度統計，無有效正分母時回傳不可用欄位。</summary>
    private static StockStructureConcentration BuildConcentration(
        IReadOnlyList<StockStructureAllocation> symbolAllocations,
        decimal totalEstimatedNetSellValue)
    {
        if (totalEstimatedNetSellValue <= 0m)
            return new(null, null, null, null, null);

        var weights = symbolAllocations
            .Select(allocation => allocation.Value / totalEstimatedNetSellValue)
            .ToList();
        var hhi = weights.Sum(weight => weight * weight);
        if (hhi <= 0m || hhi > 1m)
            return new(null, null, null, null, null);

        return new(
            symbolAllocations.Take(1).Sum(allocation => allocation.Percentage),
            symbolAllocations.Take(3).Sum(allocation => allocation.Percentage),
            symbolAllocations.Take(5).Sum(allocation => allocation.Percentage),
            hhi,
            1m / hhi);
    }

    /// <summary>計算配置百分比並依配置金額由大到小排序，可選擇獨立的同額次排序鍵。</summary>
    private static IReadOnlyList<StockStructureAllocation> ApplyAllocationPercentages(
        IEnumerable<StockStructureAllocation> allocations,
        decimal totalEstimatedNetSellValue,
        Func<StockStructureAllocation, string>? tieBreakerSelector = null)
    {
        return allocations
            .Select(allocation => allocation with
            {
                Percentage = CalculatePercentage(allocation.Value, totalEstimatedNetSellValue),
            })
            .OrderByDescending(allocation => allocation.Value)
            .ThenBy(
                allocation => tieBreakerSelector?.Invoke(allocation) ?? allocation.Label,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>依固定門檻產生可解釋的持股結構提醒。</summary>
    private static IReadOnlyList<StockStructureInsight> BuildInsights(
        IReadOnlyList<StockStructureHolding> holdings,
        IReadOnlyList<StockStructureAllocation> symbolAllocations,
        IReadOnlyList<StockStructureAllocation> instrumentTypeAllocations,
        IReadOnlyList<StockStructureAllocation> brokerAllocations,
        decimal totalEstimatedNetSellValue)
    {
        var insights = new List<StockStructureInsight>();
        if (totalEstimatedNetSellValue > 0m)
        {
            var largestSymbol = symbolAllocations.FirstOrDefault();
            if (largestSymbol?.Percentage >= SingleSymbolThreshold)
            {
                insights.Add(new StockStructureInsight(
                    "SingleSymbolConcentration",
                    "Warning",
                    $"單一標的 {largestSymbol.Label} 占 {largestSymbol.Percentage:F1}%，達到 {SingleSymbolThreshold:F0}% 提醒門檻。",
                    largestSymbol.Label,
                    largestSymbol.Percentage,
                    SingleSymbolThreshold,
                    null,
                    null));
            }

            var topThreePercentage = symbolAllocations.Take(3).Sum(allocation => allocation.Percentage ?? 0m);
            if (topThreePercentage >= TopThreeThreshold)
            {
                insights.Add(new StockStructureInsight(
                    "TopThreeConcentration",
                    "Warning",
                    $"前三大標的合計占 {topThreePercentage:F1}%，達到 {TopThreeThreshold:F0}% 提醒門檻。",
                    string.Join("、", symbolAllocations.Take(3).Select(allocation => allocation.Label)),
                    topThreePercentage,
                    TopThreeThreshold,
                    null,
                    null));
            }

            var concentratedInstrumentType = instrumentTypeAllocations.FirstOrDefault(
                allocation => allocation.Percentage >= InstrumentTypeThreshold);
            if (concentratedInstrumentType is not null)
            {
                insights.Add(new StockStructureInsight(
                    "InstrumentTypeConcentration",
                    "Warning",
                    $"商品類型 {concentratedInstrumentType.Label} 占 {concentratedInstrumentType.Percentage:F1}%，達到 {InstrumentTypeThreshold:F0}% 提醒門檻。",
                    concentratedInstrumentType.Label,
                    concentratedInstrumentType.Percentage,
                    InstrumentTypeThreshold,
                    null,
                    null));
            }

            var concentratedBroker = brokerAllocations.FirstOrDefault(
                allocation => allocation.Key != UnspecifiedBrokerKey && allocation.Percentage >= BrokerThreshold);
            if (concentratedBroker is not null)
            {
                insights.Add(new StockStructureInsight(
                    "BrokerConcentration",
                    "Warning",
                    $"券商 {concentratedBroker.Label} 占 {concentratedBroker.Percentage:F1}%，達到 {BrokerThreshold:F0}% 提醒門檻。",
                    concentratedBroker.Label,
                    concentratedBroker.Percentage,
                    BrokerThreshold,
                    null,
                    null));
            }
        }

        var lossHoldings = holdings.Where(holding => holding.EstimatedGainLoss < 0m).ToList();
        if (lossHoldings.Count > 0)
        {
            var lossAmount = lossHoldings.Sum(holding => holding.EstimatedGainLoss);
            insights.Add(new StockStructureInsight(
                "EstimatedLosses",
                "Info",
                $"目前有 {lossHoldings.Count} 筆持股的預估損益為負，合計 {lossAmount:N0}。",
                null,
                null,
                null,
                lossHoldings.Count,
                lossAmount));
        }

        if (insights.Count == 0)
        {
            insights.Add(new StockStructureInsight(
                "NoReminder",
                "Info",
                "目前沒有觸發已設定的持股結構提醒。",
                null,
                null,
                null,
                null,
                null));
        }

        return insights;
    }

    /// <summary>將文字欄位去除首尾空白，空白結果轉為 null。</summary>
    private static string? NormalizeText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    /// <summary>將股票代號去除空白並轉為大寫以建立穩定標的身分。</summary>
    private static string? NormalizeSymbol(string? value)
        => NormalizeText(value)?.ToUpperInvariant();

    /// <summary>將金額換算成百分比，分母不正時回傳不可用。</summary>
    private static decimal? CalculatePercentage(decimal value, decimal denominator)
        => denominator > 0m ? value / denominator * 100m : null;

    /// <summary>將商品類型轉為報表顯示名稱。</summary>
    private static string FormatInstrumentType(StockInstrumentType instrumentType)
        => instrumentType switch
        {
            StockInstrumentType.Stock => "股票",
            StockInstrumentType.StockEtf => "股票型 ETF",
            StockInstrumentType.BondEtf => "債券 ETF",
            _ => instrumentType.ToString(),
        };

    /// <summary>將市場列舉值轉為報表顯示名稱。</summary>
    private static string FormatMarket(StockMarket market)
        => market switch
        {
            StockMarket.Twse => "上市",
            StockMarket.Tpex => "上櫃",
            _ => "市場待辨識",
        };
}

public sealed record StockStructureReport(
    StockStructureSummary Summary,
    IReadOnlyList<StockStructureInsight> Insights,
    IReadOnlyList<StockStructureAllocation> SymbolAllocations,
    IReadOnlyList<StockStructureAllocation> InstrumentTypeAllocations,
    IReadOnlyList<StockStructureAllocation> BrokerAllocations,
    IReadOnlyList<StockStructureAllocation> MarketAllocations,
    StockStructureConcentration Concentration,
    IReadOnlyList<StockStructureHolding> Holdings);

public sealed record StockStructureSummary(
    int HoldingCount,
    decimal TotalEstimatedBuyCost,
    decimal TotalGrossMarketValue,
    decimal TotalEstimatedNetSellValue,
    decimal TotalEstimatedGainLoss,
    decimal? EstimatedGainLossPercentage);

public sealed record StockStructureInsight(
    string Code,
    string Severity,
    string Message,
    string? AffectedName,
    decimal? ObservedPercentage,
    decimal? ThresholdPercentage,
    int? AffectedCount,
    decimal? Amount);

public sealed record StockStructureAllocation(
    string Key,
    string Label,
    decimal Value,
    decimal? Percentage);

public sealed record StockStructureConcentration(
    decimal? Top1Percentage,
    decimal? Top3Percentage,
    decimal? Top5Percentage,
    decimal? Hhi,
    decimal? EffectiveHoldingCount);

public sealed record StockStructureHolding(
    int Id,
    string Name,
    string Symbol,
    StockInstrumentType InstrumentType,
    decimal Shares,
    decimal BuyPrice,
    decimal CurrentPrice,
    string? Broker,
    decimal GrossMarketValue,
    decimal BuyCommission,
    decimal SellCommission,
    decimal SecuritiesTransactionTax,
    decimal EstimatedBuyCost,
    decimal EstimatedNetSellValue,
    decimal EstimatedGainLoss,
    decimal? AllocationPercentage);
