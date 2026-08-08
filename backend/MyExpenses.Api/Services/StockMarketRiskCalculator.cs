using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>描述市場風險統計不可用的穩定原因代碼。</summary>
public enum StockMarketRiskUnavailableReason
{
    NoHoldings,
    UnknownMarket,
    BlankSymbol,
    NonPositiveGrossValue,
    InsufficientHistory,
    NoEligibleInstruments,
    CoverageBelowThreshold,
    InsufficientCommonDates,
    NotEnoughEligibleInstruments,
    NonFiniteResult,
    InvalidPeriod,
}

/// <summary>以 nullable value 與 typed reason 表示一項可用或不可用統計。</summary>
public sealed record StockMarketRiskMetric(
    double? Value,
    StockMarketRiskUnavailableReason? UnavailableReason);

/// <summary>描述風險計算中的納入或排除標的。</summary>
public sealed record StockMarketRiskInstrument(
    string Name,
    string Symbol,
    StockMarket Market,
    decimal GrossMarketValue,
    double OriginalWeight,
    double RenormalizedWeight,
    int Observations,
    double? AnnualizedVolatility,
    StockMarketRiskUnavailableReason? ExclusionReason);

/// <summary>描述依年化波動度由高到低排列的標的。</summary>
public sealed record StockMarketRiskVolatilityRanking(
    string Name,
    string Symbol,
    StockMarket Market,
    decimal GrossMarketValue,
    double Weight,
    double AnnualizedVolatility,
    int Observations);

/// <summary>描述相關矩陣中的列欄標籤。</summary>
public sealed record StockMarketRiskCorrelationLabel(
    string Name,
    string Symbol,
    StockMarket Market);

/// <summary>描述共同日期 Pearson 相關係數矩陣。</summary>
public sealed record StockMarketRiskCorrelationMatrix(
    IReadOnlyList<StockMarketRiskCorrelationLabel> Labels,
    IReadOnlyList<IReadOnlyList<double?>> Values,
    int CommonObservationCount,
    StockMarketRiskUnavailableReason? UnavailableReason);

/// <summary>將非成功同步狀態安全呈現在市場風險報表中的警告。</summary>
public sealed record StockMarketRiskSyncWarning(
    string Symbol,
    StockMarket Market,
    HistoricalPriceSyncStatus Status,
    string? SafeMessage,
    DateTime? LastAttemptedAtUtc,
    DateTime? LastSucceededAtUtc,
    DateOnly? LatestTradingDate);

/// <summary>市場風險報表的完整本機計算結果。</summary>
public sealed record StockMarketRiskReport(
    int PeriodMonths,
    string ScenarioDescription,
    DateOnly CalculationDate,
    DateOnly? DataCutoffDate,
    StockMarketRiskMetric PortfolioAnnualizedVolatility,
    double EligibleMarketValueCoverage,
    double CoverageThreshold,
    int CommonObservationCount,
    int TotalHoldingCount,
    IReadOnlyList<StockMarketRiskInstrument> IncludedInstruments,
    IReadOnlyList<StockMarketRiskInstrument> ExcludedInstruments,
    IReadOnlyList<StockMarketRiskVolatilityRanking> VolatilityRanking,
    StockMarketRiskCorrelationMatrix CorrelationMatrix,
    IReadOnlyList<StockMarketRiskSyncWarning> SyncWarnings);

/// <summary>不依賴 EF Core、HTTP 或外部狀態的市場風險計算單元。</summary>
public static class StockMarketRiskCalculator
{
    private const double CoverageThreshold = 0.90d;
    private const double AnnualizationFactor = 252d;
    private const string ScenarioDescription = "目前持股歷史情境：以目前毛市值權重套用歷史還原日報酬，不代表實際歷史績效或未來損失預測。";

    /// <summary>回傳指定觀察期所需的最低日報酬數。</summary>
    public static int MinimumObservations(int periodMonths)
        => periodMonths switch
        {
            3 => 50,
            6 => 100,
            12 => 200,
            _ => throw new ArgumentException("觀察期只支援 3、6 或 12 個月", nameof(periodMonths)),
        };

    /// <summary>以相鄰有效還原價格計算指定期間的簡單日報酬。</summary>
    public static IReadOnlyDictionary<DateOnly, double> CalculateDailyReturns(
        IEnumerable<HistoricalAdjustedPrice> prices,
        DateOnly startDate,
        DateOnly endDate)
    {
        var points = prices
            .Where(price => price.TradingDate <= endDate && price.AdjustedClose > 0m)
            .GroupBy(price => price.TradingDate)
            .Select(group => group.OrderByDescending(price => price.FetchedAtUtc).First())
            .OrderBy(price => price.TradingDate)
            .ToList();
        var returns = new Dictionary<DateOnly, double>();
        for (var index = 1; index < points.Count; index++)
        {
            var previous = (double)points[index - 1].AdjustedClose;
            var current = (double)points[index].AdjustedClose;
            if (points[index].TradingDate < startDate
                || !double.IsFinite(previous)
                || !double.IsFinite(current)
                || previous <= 0d
                || current <= 0d)
                continue;

            var dailyReturn = current / previous - 1d;
            if (double.IsFinite(dailyReturn))
                returns[points[index].TradingDate] = dailyReturn;
        }

        return returns;
    }

    /// <summary>依目前持股、還原價格與同步狀態建立市場風險報表。</summary>
    public static StockMarketRiskReport Calculate(
        IReadOnlyList<Stock> stocks,
        IReadOnlyList<HistoricalAdjustedPrice> prices,
        int periodMonths,
        DateOnly calculationDate,
        IReadOnlyList<HistoricalPriceSyncState>? syncStates = null)
    {
        int minimumObservations;
        try
        {
            minimumObservations = MinimumObservations(periodMonths);
        }
        catch (ArgumentException)
        {
            return CreateUnavailableReport(
                periodMonths,
                calculationDate,
                stocks,
                StockMarketRiskUnavailableReason.InvalidPeriod,
                syncStates);
        }

        var usablePrices = prices
            .Where(price => price.TradingDate <= calculationDate && price.AdjustedClose > 0m)
            .GroupBy(price => (price.Market, Symbol: NormalizeSymbol(price.Symbol)))
            .SelectMany(group => group)
            .ToList();
        var dataCutoffDate = usablePrices.Count == 0
            ? (DateOnly?)null
            : usablePrices.Max(price => price.TradingDate);
        var periodStart = dataCutoffDate?.AddMonths(-periodMonths);
        var periodEnd = dataCutoffDate;

        var positionGroups = stocks
            .GroupBy(stock => (stock.Market, Symbol: NormalizeSymbol(stock.Symbol)))
            .Select(group => CreatePosition(group.Key.Market, group.Key.Symbol, group.ToList(), usablePrices, periodStart, periodEnd, minimumObservations))
            .ToList();
        var totalPositiveGrossValue = stocks
            .Select(GetGrossMarketValue)
            .Where(value => value > 0m)
            .Sum();
        var includedPositions = positionGroups
            .Where(position => position.ExclusionReason is null)
            .ToList();
        var eligibleGrossValue = includedPositions.Sum(position => position.GrossMarketValue);
        var coverage = totalPositiveGrossValue > 0m
            ? (double)(eligibleGrossValue / totalPositiveGrossValue)
            : 0d;
        var normalizedDenominator = eligibleGrossValue > 0m ? eligibleGrossValue : 1m;

        var includedInstruments = includedPositions
            .Select(position => position.ToInstrument(
                totalPositiveGrossValue > 0m
                    ? (double)(position.GrossMarketValue / totalPositiveGrossValue)
                    : 0d,
                (double)(position.GrossMarketValue / normalizedDenominator),
                AnnualizedVolatility(position.Returns.Values)))
            .ToList();
        var excludedInstruments = positionGroups
            .Where(position => position.ExclusionReason is not null)
            .Select(position => position.ToInstrument(
                totalPositiveGrossValue > 0m && position.GrossMarketValue > 0m
                    ? (double)(position.GrossMarketValue / totalPositiveGrossValue)
                    : 0d,
                0d,
                null))
            .ToList();
        var volatilityRanking = includedInstruments
            .Where(instrument => instrument.AnnualizedVolatility.HasValue)
            .OrderByDescending(instrument => instrument.AnnualizedVolatility)
            .Select(instrument => new StockMarketRiskVolatilityRanking(
                instrument.Name,
                instrument.Symbol,
                instrument.Market,
                instrument.GrossMarketValue,
                instrument.OriginalWeight,
                instrument.AnnualizedVolatility!.Value,
                instrument.Observations))
            .ToList();

        var commonDates = IntersectReturnDates(includedPositions);
        var portfolioMetric = CalculatePortfolioMetric(
            includedPositions,
            commonDates,
            coverage,
            minimumObservations);
        var correlationMatrix = CalculateCorrelationMatrix(
            includedPositions,
            minimumObservations);
        var warnings = BuildSyncWarnings(stocks, syncStates);

        if (stocks.Count == 0)
            portfolioMetric = new StockMarketRiskMetric(null, StockMarketRiskUnavailableReason.NoHoldings);
        else if (includedPositions.Count == 0)
            portfolioMetric = new StockMarketRiskMetric(null, StockMarketRiskUnavailableReason.NoEligibleInstruments);

        return new StockMarketRiskReport(
            periodMonths,
            ScenarioDescription,
            calculationDate,
            dataCutoffDate,
            portfolioMetric,
            double.IsFinite(coverage) ? coverage : 0d,
            CoverageThreshold,
            commonDates.Count,
            stocks.Count,
            includedInstruments,
            excludedInstruments,
            volatilityRanking,
            correlationMatrix,
            warnings);
    }

    /// <summary>正規化股票代號以合併跨券商持股與本機歷史行情。</summary>
    private static string NormalizeSymbol(string symbol)
        => symbol.Trim().ToUpperInvariant();

    /// <summary>建立一個市場代號風險位置並決定排除原因。</summary>
    private static RiskPosition CreatePosition(
        StockMarket market,
        string symbol,
        IReadOnlyList<Stock> stocks,
        IReadOnlyList<HistoricalAdjustedPrice> prices,
        DateOnly? periodStart,
        DateOnly? periodEnd,
        int minimumObservations)
    {
        var grossMarketValue = stocks.Sum(GetGrossMarketValue);
        var firstStock = stocks[0];
        if (market is not (StockMarket.Twse or StockMarket.Tpex))
            return new RiskPosition(firstStock.Name, symbol, market, grossMarketValue, new Dictionary<DateOnly, double>(), StockMarketRiskUnavailableReason.UnknownMarket);
        if (string.IsNullOrWhiteSpace(symbol))
            return new RiskPosition(firstStock.Name, symbol, market, grossMarketValue, new Dictionary<DateOnly, double>(), StockMarketRiskUnavailableReason.BlankSymbol);
        if (grossMarketValue <= 0m || !double.IsFinite((double)grossMarketValue))
            return new RiskPosition(firstStock.Name, symbol, market, grossMarketValue, new Dictionary<DateOnly, double>(), StockMarketRiskUnavailableReason.NonPositiveGrossValue);
        if (periodStart is null || periodEnd is null)
            return new RiskPosition(firstStock.Name, symbol, market, grossMarketValue, new Dictionary<DateOnly, double>(), StockMarketRiskUnavailableReason.InsufficientHistory);

        var series = prices
            .Where(price => price.Market == market && NormalizeSymbol(price.Symbol) == symbol)
            .ToList();
        var returns = CalculateDailyReturns(series, periodStart.Value, periodEnd.Value);
        return returns.Count < minimumObservations
            ? new RiskPosition(firstStock.Name, symbol, market, grossMarketValue, returns, StockMarketRiskUnavailableReason.InsufficientHistory)
            : new RiskPosition(firstStock.Name, symbol, market, grossMarketValue, returns, null);
    }

    /// <summary>取得單筆持股的目前毛市值，不扣除任何費稅。</summary>
    private static decimal GetGrossMarketValue(Stock stock)
        => stock.Shares * stock.CurrentPrice;

    /// <summary>取得所有合格標的共同存在的交易日集合。</summary>
    private static IReadOnlyList<DateOnly> IntersectReturnDates(IReadOnlyList<RiskPosition> positions)
    {
        if (positions.Count == 0)
            return [];
        var dates = positions[0].Returns.Keys.ToHashSet();
        foreach (var position in positions.Skip(1))
            dates.IntersectWith(position.Returns.Keys);
        return dates.OrderBy(date => date).ToList();
    }

    /// <summary>依覆蓋率與共同交易日計算組合年化波動度。</summary>
    private static StockMarketRiskMetric CalculatePortfolioMetric(
        IReadOnlyList<RiskPosition> positions,
        IReadOnlyList<DateOnly> commonDates,
        double coverage,
        int minimumObservations)
    {
        if (positions.Count == 0)
            return new StockMarketRiskMetric(null, StockMarketRiskUnavailableReason.NoEligibleInstruments);
        if (coverage < CoverageThreshold)
            return new StockMarketRiskMetric(null, StockMarketRiskUnavailableReason.CoverageBelowThreshold);
        if (commonDates.Count < minimumObservations)
            return new StockMarketRiskMetric(null, StockMarketRiskUnavailableReason.InsufficientCommonDates);

        var totalGross = positions.Sum(position => position.GrossMarketValue);
        var returns = commonDates.Select(date => positions.Sum(position =>
            (double)(position.GrossMarketValue / totalGross) * position.Returns[date])).ToList();
        var volatility = AnnualizedVolatility(returns);
        return volatility.HasValue
            ? new StockMarketRiskMetric(volatility.Value, null)
            : new StockMarketRiskMetric(null, StockMarketRiskUnavailableReason.NonFiniteResult);
    }

    /// <summary>依目前毛市值前十大的合格標的建立共同日期相關矩陣。</summary>
    private static StockMarketRiskCorrelationMatrix CalculateCorrelationMatrix(
        IReadOnlyList<RiskPosition> positions,
        int minimumObservations)
    {
        var selected = positions.OrderByDescending(position => position.GrossMarketValue).Take(10).ToList();
        var labels = selected
            .Select(position => new StockMarketRiskCorrelationLabel(position.Name, position.Symbol, position.Market))
            .ToList();
        if (selected.Count < 2)
            return new StockMarketRiskCorrelationMatrix(labels, [], 0, StockMarketRiskUnavailableReason.NotEnoughEligibleInstruments);

        var commonDates = IntersectReturnDates(selected);
        if (commonDates.Count < minimumObservations)
            return new StockMarketRiskCorrelationMatrix(labels, [], commonDates.Count, StockMarketRiskUnavailableReason.InsufficientCommonDates);

        var values = new List<IReadOnlyList<double?>>();
        for (var row = 0; row < selected.Count; row++)
        {
            var rowValues = new List<double?>();
            for (var column = 0; column < selected.Count; column++)
            {
                if (row == column)
                {
                    rowValues.Add(1d);
                    continue;
                }

                rowValues.Add(Pearson(
                    commonDates.Select(date => selected[row].Returns[date]),
                    commonDates.Select(date => selected[column].Returns[date])));
            }

            values.Add(rowValues);
        }

        return new StockMarketRiskCorrelationMatrix(labels, values, commonDates.Count, null);
    }

    /// <summary>計算兩組共同日期報酬的 Pearson 相關係數。</summary>
    private static double? Pearson(IEnumerable<double> first, IEnumerable<double> second)
    {
        var firstValues = first.ToArray();
        var secondValues = second.ToArray();
        if (firstValues.Length != secondValues.Length || firstValues.Length < 2)
            return null;
        var firstMean = firstValues.Average();
        var secondMean = secondValues.Average();
        var covariance = 0d;
        var firstVariance = 0d;
        var secondVariance = 0d;
        for (var index = 0; index < firstValues.Length; index++)
        {
            var firstDelta = firstValues[index] - firstMean;
            var secondDelta = secondValues[index] - secondMean;
            covariance += firstDelta * secondDelta;
            firstVariance += firstDelta * firstDelta;
            secondVariance += secondDelta * secondDelta;
        }

        var denominator = Math.Sqrt(firstVariance * secondVariance);
        if (denominator <= 0d || !double.IsFinite(denominator))
            return null;
        var result = covariance / denominator;
        return double.IsFinite(result) ? result : null;
    }

    /// <summary>計算樣本標準差並以 sqrt(252) 年化。</summary>
    private static double? AnnualizedVolatility(IEnumerable<double> returns)
    {
        var values = returns.ToArray();
        if (values.Length < 2 || values.Any(value => !double.IsFinite(value)))
            return null;
        var mean = values.Average();
        var variance = values.Sum(value => Math.Pow(value - mean, 2)) / (values.Length - 1);
        var volatility = Math.Sqrt(variance) * Math.Sqrt(AnnualizationFactor);
        return double.IsFinite(volatility) ? volatility : null;
    }

    /// <summary>建立沒有足夠資料時仍可供 endpoint 回傳的安全報表。</summary>
    private static StockMarketRiskReport CreateUnavailableReport(
        int periodMonths,
        DateOnly calculationDate,
        IReadOnlyList<Stock> stocks,
        StockMarketRiskUnavailableReason reason,
        IReadOnlyList<HistoricalPriceSyncState>? syncStates)
        => new(
            periodMonths,
            ScenarioDescription,
            calculationDate,
            null,
            new StockMarketRiskMetric(null, reason),
            0d,
            CoverageThreshold,
            0,
            stocks.Count,
            [],
            [],
            [],
            new StockMarketRiskCorrelationMatrix([], [], 0, reason),
            BuildSyncWarnings(stocks, syncStates));

    /// <summary>只回傳目前持股對應的非成功同步狀態，排除已刪除標的殘留警告。</summary>
    private static IReadOnlyList<StockMarketRiskSyncWarning> BuildSyncWarnings(
        IReadOnlyList<Stock> stocks,
        IReadOnlyList<HistoricalPriceSyncState>? syncStates)
    {
        var currentKeys = stocks
            .Where(stock => !string.IsNullOrWhiteSpace(stock.Symbol))
            .Select(stock => (stock.Market, Symbol: NormalizeSymbol(stock.Symbol)))
            .ToHashSet();
        return (syncStates ?? [])
            .Where(state => state.Status != HistoricalPriceSyncStatus.Success
                && currentKeys.Contains((state.Market, Symbol: NormalizeSymbol(state.Symbol))))
            .OrderBy(state => state.Symbol, StringComparer.Ordinal)
            .Select(state => new StockMarketRiskSyncWarning(
                state.Symbol,
                state.Market,
                state.Status,
                state.SafeMessage,
                state.LastAttemptedAtUtc,
                state.LastSucceededAtUtc,
                state.LatestTradingDate))
            .ToList();
    }

    /// <summary>保存計算中尚未轉成公開 DTO 的標的報酬資料。</summary>
    private sealed record RiskPosition(
        string Name,
        string Symbol,
        StockMarket Market,
        decimal GrossMarketValue,
        IReadOnlyDictionary<DateOnly, double> Returns,
        StockMarketRiskUnavailableReason? ExclusionReason)
    {
        /// <summary>將內部風險位置轉成報表標的資料。</summary>
        public StockMarketRiskInstrument ToInstrument(
            double originalWeight,
            double renormalizedWeight,
            double? volatility)
            => new(
                Name,
                Symbol,
                Market,
                GrossMarketValue,
                originalWeight,
                renormalizedWeight,
                Returns.Count,
                volatility,
                ExclusionReason);
    }
}
