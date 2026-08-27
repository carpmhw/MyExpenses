using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>描述股票績效 metric 因資料 gate 不可用的穩定原因。</summary>
public enum StockPerformanceUnavailableReason
{
    None,
    NoHoldings,
    NoLedgerHistory,
    IncompleteLedgerCoverage,
    PeriodBeforeTrackingStart,
    InsufficientCashFlows,
    NoCashFlowSignChange,
    MissingTerminalValue,
    NoConvergence,
    NonFiniteResult,
    InsufficientHistoricalPrices,
    ZeroDenominator,
    InvalidPeriod,
}

/// <summary>以 nullable value 與 typed reason 表示一項股票績效 metric。</summary>
public sealed record StockPerformanceMetric(
    double? Value,
    StockPerformanceUnavailableReason UnavailableReason = StockPerformanceUnavailableReason.None);

/// <summary>描述績效 calculator 使用的日期、持股、Ledger 與 raw price 輸入。</summary>
public sealed record StockPerformanceInput(
    DateOnly DateStart,
    DateOnly DateEnd,
    IReadOnlyList<Stock> Stocks,
    IReadOnlyList<StockTransaction> Transactions,
    IReadOnlyList<HistoricalAdjustedPrice> Prices,
    DateOnly? AsOfDate = null);

/// <summary>描述投資人觀點的單筆日期現金流。</summary>
public sealed record StockPerformanceCashFlow(DateOnly Date, decimal Amount);

/// <summary>描述股票績效的損益摘要。</summary>
public sealed record StockPerformanceSummary(
    decimal CurrentGrossMarketValue,
    decimal RemainingCostBasis,
    decimal RealizedGainLoss,
    decimal UnrealizedGainLoss,
    decimal NetDividendIncome,
    decimal TotalGainLoss);

/// <summary>描述績效資料覆蓋、追蹤起點與價格觀測品質。</summary>
public sealed record StockPerformanceDataQuality(
    int ActiveInstrumentCount,
    int LedgerManagedInstrumentCount,
    int PriceObservationCount,
    double PriceCoverage,
    StockPerformanceUnavailableReason TrackingStartReason,
    bool HasIncompleteLedgerCoverage);

/// <summary>描述 TWR 的單日現金流時序與 cumulative chain。</summary>
public sealed record StockPerformanceTwrPoint(
    DateOnly Date,
    decimal BeginningValue,
    decimal Contributions,
    decimal Withdrawals,
    decimal EndingValue,
    double DailyReturn,
    double CumulativeReturn);

/// <summary>描述 TWR 計算結果與 raw close 覆蓋資料。</summary>
public sealed record StockPerformanceTwrResult(
    StockPerformanceMetric Metric,
    IReadOnlyList<StockPerformanceTwrPoint> Points,
    double PriceCoverage,
    int ObservationCount);

/// <summary>描述月度績效點，cumulative TWR 可因資料缺口為 null。</summary>
public sealed record StockPerformanceMonthlyPoint(
    string Month,
    decimal EndingMarketValue,
    decimal NetContribution,
    decimal RealizedGainLoss,
    decimal DividendIncome,
    double? CumulativeTwr);

/// <summary>描述單一股票的 Ledger-based P/L breakdown。</summary>
public sealed record StockPerformanceInstrumentBreakdown(
    int StockId,
    string Name,
    string Symbol,
    StockMarket Market,
    string? Broker,
    decimal CurrentShares,
    decimal GrossMarketValue,
    decimal RemainingCostBasis,
    decimal RealizedGainLoss,
    decimal UnrealizedGainLoss,
    decimal DividendIncome,
    decimal TotalGainLoss,
    bool IsClosed);

/// <summary>描述完整股票投資績效報表。</summary>
public sealed record StockPerformanceReport(
    DateOnly DateStart,
    DateOnly DateEnd,
    DateOnly? TrackingStartDate,
    bool HasSyntheticOpeningBalances,
    string TerminalValuationSource,
    StockPerformanceMetric LedgerCoverage,
    StockPerformanceSummary Summary,
    StockPerformanceMetric Twr,
    StockPerformanceMetric Xirr,
    IReadOnlyList<StockPerformanceMonthlyPoint> MonthlyPoints,
    IReadOnlyList<StockPerformanceInstrumentBreakdown> InstrumentBreakdown,
    StockPerformanceDataQuality DataQuality);

/// <summary>描述 period terminal valuation 的來源與不可用原因。</summary>
internal sealed record StockPerformanceTerminalValue(
    decimal? Value,
    string Source,
    StockPerformanceUnavailableReason Reason);

/// <summary>純計算股票 Ledger 損益、XIRR、TWR 與 breakdown 的核心。</summary>
public static class StockPerformanceCalculator
{
    private const decimal DecimalTolerance = 0.00000001m;
    private const double RateLowerBound = -0.999999d;
    private const double RateUpperBound = 1_000_000d;
    private const int MaximumIterations = 100;

    /// <summary>組合 Ledger replay、目前估值、XIRR、TWR 與月度標的報表。</summary>
    public static StockPerformanceReport Calculate(StockPerformanceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.DateEnd < input.DateStart)
            return CreateInvalidReport(input);

        var stocksById = input.Stocks.ToDictionary(stock => stock.Id);
        var replayByStock = input.Transactions
            .GroupBy(transaction => transaction.StockId)
            .Where(group => stocksById.ContainsKey(group.Key))
            .ToDictionary(
                group => group.Key,
                group => StockLedgerCalculator.Replay(group.ToList()));
        var activeStocks = input.Stocks
            .Where(stock => stock.Shares > 0m && stock.CurrentPrice > 0m)
            .ToList();
        var currentGrossMarketValue = activeStocks.Sum(stock => stock.Shares * stock.CurrentPrice);
        var ledgerManagedActiveValue = activeStocks
            .Where(stock => replayByStock.ContainsKey(stock.Id))
            .Sum(stock => stock.Shares * stock.CurrentPrice);
        var coverage = currentGrossMarketValue > 0m
            ? (double)(ledgerManagedActiveValue / currentGrossMarketValue)
            : 0d;
        var coverageMetric = currentGrossMarketValue <= 0m
            ? new StockPerformanceMetric(null, StockPerformanceUnavailableReason.NoHoldings)
            : new StockPerformanceMetric(coverage, StockPerformanceUnavailableReason.None);
        var trackingStart = input.Transactions.Count == 0
            ? (DateOnly?)null
            : input.Transactions.Min(transaction => transaction.TradeDate);
        var hasSyntheticOpening = input.Transactions.Any(
            transaction => transaction.Type == StockTransactionType.OpeningBalance);
        var summary = BuildSummary(input.Stocks, replayByStock, currentGrossMarketValue);
        var breakdown = BuildBreakdown(input.Stocks, replayByStock);
        var hasIncompleteCoverage = currentGrossMarketValue > 0m
            && coverage < 1d - 0.00000001d;
        var trackingReason = trackingStart.HasValue
            ? StockPerformanceUnavailableReason.None
            : StockPerformanceUnavailableReason.NoLedgerHistory;
        var quality = new StockPerformanceDataQuality(
            activeStocks.Count,
            replayByStock.Count,
            0,
            0d,
            trackingReason,
            hasIncompleteCoverage);
        var returnGateReason = hasIncompleteCoverage
            ? StockPerformanceUnavailableReason.IncompleteLedgerCoverage
            : trackingStart.HasValue && input.DateStart < trackingStart.Value
                ? StockPerformanceUnavailableReason.PeriodBeforeTrackingStart
                : StockPerformanceUnavailableReason.None;
        var twr = returnGateReason == StockPerformanceUnavailableReason.None
            ? CalculateTwr(input)
            : CreateUnavailableTwr(returnGateReason);
        var terminal = ResolveTerminalValue(input, activeStocks, replayByStock);
        var xirr = returnGateReason == StockPerformanceUnavailableReason.None
            ? CalculateReportXirr(input, terminal)
            : new StockPerformanceMetric(null, returnGateReason);
        var finalQuality = quality with
        {
            PriceObservationCount = twr.ObservationCount,
            PriceCoverage = twr.PriceCoverage,
        };
        var monthly = BuildMonthlyPoints(input, replayByStock, twr.Points, breakdown);

        return new StockPerformanceReport(
            input.DateStart,
            input.DateEnd,
            trackingStart,
            hasSyntheticOpening,
            terminal.Source,
            coverageMetric,
            summary,
            twr.Metric,
            xirr,
            monthly,
            breakdown,
            finalQuality);
    }

    /// <summary>依輸入資料建立 TWR daily chain，所有歷史估值只使用 raw Close。</summary>
    public static StockPerformanceTwrResult CalculateTwr(StockPerformanceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.DateEnd < input.DateStart)
            return CreateUnavailableTwr(StockPerformanceUnavailableReason.InvalidPeriod);

        var stockById = input.Stocks.ToDictionary(stock => stock.Id);
        var activeStocks = input.Stocks
            .Where(stock => stock.Shares > 0m && stock.CurrentPrice > 0m)
            .Where(stock => input.Transactions.Any(transaction => transaction.StockId == stock.Id))
            .ToList();
        if (activeStocks.Count == 0)
            return CreateUnavailableTwr(StockPerformanceUnavailableReason.NoHoldings);

        var activeKeys = activeStocks
            .Select(stock => (stock.Market, Symbol: NormalizeSymbol(stock.Symbol)))
            .ToHashSet();
        var priceByKeyAndDate = input.Prices
            .Where(price => price.TradingDate >= input.DateStart
                && price.TradingDate <= input.DateEnd
                && price.Close > 0m
                && activeKeys.Contains((price.Market, NormalizeSymbol(price.Symbol))))
            .GroupBy(price => (price.Market, Symbol: NormalizeSymbol(price.Symbol), price.TradingDate))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(price => price.FetchedAtUtc).First().Close!.Value);
        var candidateDates = priceByKeyAndDate.Keys
            .Select(key => key.TradingDate)
            .Distinct()
            .OrderBy(date => date)
            .ToList();
        var commonDates = candidateDates
            .Where(date => activeStocks.All(stock => priceByKeyAndDate.ContainsKey(
                (stock.Market, NormalizeSymbol(stock.Symbol), date))))
            .ToList();
        var priceCoverage = candidateDates.Count == 0
            ? 0d
            : (double)commonDates.Count / candidateDates.Count;
        if (commonDates.Count < 2)
        {
            return new StockPerformanceTwrResult(
                new StockPerformanceMetric(null, StockPerformanceUnavailableReason.InsufficientHistoricalPrices),
                [],
                priceCoverage,
                commonDates.Count);
        }

        var points = new List<StockPerformanceTwrPoint>();
        var cumulative = 1d;
        decimal? previousEndingValue = null;
        foreach (var date in commonDates)
        {
            var beforeValue = previousEndingValue ?? CalculateValueBeforeDate(
                date,
                activeStocks,
                input.Transactions,
                priceByKeyAndDate);
            var contributions = 0m;
            var withdrawals = 0m;
            foreach (var transaction in input.Transactions.Where(item => item.TradeDate == date))
            {
                switch (transaction.Type)
                {
                    case StockTransactionType.OpeningBalance:
                        contributions += transaction.OpeningMarketValue ?? 0m;
                        break;
                    case StockTransactionType.Buy:
                        contributions += transaction.Shares!.Value * transaction.Price!.Value
                            + transaction.Fee + transaction.Tax;
                        break;
                    case StockTransactionType.Sell:
                        withdrawals += transaction.Shares!.Value * transaction.Price!.Value
                            - transaction.Fee - transaction.Tax;
                        break;
                    case StockTransactionType.Dividend:
                        withdrawals += transaction.CashAmount!.Value
                            - transaction.Fee - transaction.Tax;
                        break;
                    case StockTransactionType.StockDividend:
                        break;
                }
            }

            var endingValue = 0m;
            foreach (var stock in activeStocks)
            {
                var shares = ReplaySharesAtDate(stock.Id, date, input.Transactions);
                var close = priceByKeyAndDate[(stock.Market, NormalizeSymbol(stock.Symbol), date)];
                endingValue += shares * close;
            }

            var denominator = beforeValue + contributions;
            if (denominator <= DecimalTolerance)
            {
                previousEndingValue = endingValue;
                continue;
            }

            var dailyReturn = (double)((endingValue + withdrawals) / denominator - 1m);
            if (!double.IsFinite(dailyReturn))
                return new StockPerformanceTwrResult(
                    new StockPerformanceMetric(null, StockPerformanceUnavailableReason.NonFiniteResult),
                    points,
                    priceCoverage,
                    commonDates.Count);
            cumulative *= 1d + dailyReturn;
            if (!double.IsFinite(cumulative))
                return new StockPerformanceTwrResult(
                    new StockPerformanceMetric(null, StockPerformanceUnavailableReason.NonFiniteResult),
                    points,
                    priceCoverage,
                    commonDates.Count);
            points.Add(new StockPerformanceTwrPoint(
                date,
                beforeValue,
                contributions,
                withdrawals,
                endingValue,
                dailyReturn,
                cumulative - 1d));
            previousEndingValue = endingValue;
        }

        if (points.Count == 0)
        {
            return new StockPerformanceTwrResult(
                new StockPerformanceMetric(null, StockPerformanceUnavailableReason.ZeroDenominator),
                points,
                priceCoverage,
                commonDates.Count);
        }

        return new StockPerformanceTwrResult(
            new StockPerformanceMetric(cumulative - 1d),
            points,
            priceCoverage,
            commonDates.Count);
    }

    /// <summary>以 bounded Newton-Raphson 與 bisection fallback 求解日期化 XIRR。</summary>
    public static StockPerformanceMetric CalculateXirr(IEnumerable<StockPerformanceCashFlow> cashFlows)
    {
        ArgumentNullException.ThrowIfNull(cashFlows);
        var flows = cashFlows.OrderBy(flow => flow.Date).ToList();
        if (flows.Count < 2)
            return new StockPerformanceMetric(null, StockPerformanceUnavailableReason.InsufficientCashFlows);
        if (!flows.Any(flow => flow.Amount < 0m) || !flows.Any(flow => flow.Amount > 0m))
            return new StockPerformanceMetric(null, StockPerformanceUnavailableReason.InsufficientCashFlows);

        var converted = new List<(DateOnly Date, double Amount)>(flows.Count);
        foreach (var flow in flows)
        {
            var amount = (double)flow.Amount;
            if (!double.IsFinite(amount))
                return new StockPerformanceMetric(null, StockPerformanceUnavailableReason.NonFiniteResult);
            converted.Add((flow.Date, amount));
        }

        if (TryNewtonRaphson(converted, out var newtonRate)
            || TryBisection(converted, out newtonRate))
        {
            return double.IsFinite(newtonRate)
                ? new StockPerformanceMetric(newtonRate)
                : new StockPerformanceMetric(null, StockPerformanceUnavailableReason.NonFiniteResult);
        }

        return new StockPerformanceMetric(null, StockPerformanceUnavailableReason.NoConvergence);
    }

    /// <summary>建立報表所需的投資人現金流並加入 period terminal value。</summary>
    private static StockPerformanceMetric CalculateReportXirr(
        StockPerformanceInput input,
        StockPerformanceTerminalValue terminal)
    {
        if (!terminal.Value.HasValue)
            return new StockPerformanceMetric(null, terminal.Reason);

        var flows = new List<StockPerformanceCashFlow>();
        foreach (var transaction in input.Transactions.Where(transaction =>
                     transaction.TradeDate >= input.DateStart && transaction.TradeDate <= input.DateEnd))
        {
            var amount = transaction.Type switch
            {
                StockTransactionType.OpeningBalance => -(transaction.OpeningMarketValue ?? 0m),
                StockTransactionType.Buy => -(transaction.Shares!.Value * transaction.Price!.Value
                    + transaction.Fee + transaction.Tax),
                StockTransactionType.Sell => transaction.Shares!.Value * transaction.Price!.Value
                    - transaction.Fee - transaction.Tax,
                StockTransactionType.Dividend => transaction.CashAmount!.Value
                    - transaction.Fee - transaction.Tax,
                StockTransactionType.StockDividend => 0m,
                _ => 0m,
            };
            flows.Add(new StockPerformanceCashFlow(transaction.TradeDate, amount));
        }

        flows.Add(new StockPerformanceCashFlow(input.DateEnd, terminal.Value.Value));
        return CalculateXirr(flows);
    }

    /// <summary>解析 period end 的目前價格或歷史 raw close terminal valuation。</summary>
    private static StockPerformanceTerminalValue ResolveTerminalValue(
        StockPerformanceInput input,
        IReadOnlyList<Stock> activeStocks,
        IReadOnlyDictionary<int, StockLedgerResult> replayByStock)
    {
        var asOfDate = input.AsOfDate ?? input.DateEnd;
        if (input.DateEnd >= asOfDate)
        {
            return new StockPerformanceTerminalValue(
                activeStocks.Sum(stock => stock.Shares * stock.CurrentPrice),
                "CurrentPrice",
                StockPerformanceUnavailableReason.None);
        }

        var value = 0m;
        foreach (var stock in activeStocks.Where(stock => replayByStock.ContainsKey(stock.Id)))
        {
            var price = input.Prices
                .Where(item => item.Market == stock.Market
                    && NormalizeSymbol(item.Symbol) == NormalizeSymbol(stock.Symbol)
                    && item.TradingDate == input.DateEnd
                    && item.Close > 0m)
                .OrderByDescending(item => item.FetchedAtUtc)
                .FirstOrDefault();
            if (price?.Close is not > 0m)
            {
                return new StockPerformanceTerminalValue(
                    null,
                    "HistoricalRawClose",
                    StockPerformanceUnavailableReason.MissingTerminalValue);
            }

            var shares = ReplaySharesAtDate(stock.Id, input.DateEnd, input.Transactions);
            value += shares * price.Close.Value;
        }

        return new StockPerformanceTerminalValue(
            value,
            "HistoricalRawClose",
            StockPerformanceUnavailableReason.None);
    }

    /// <summary>建立損益摘要並保留 Phase 1 estimated valuation 的獨立口徑。</summary>
    private static StockPerformanceSummary BuildSummary(
        IReadOnlyList<Stock> stocks,
        IReadOnlyDictionary<int, StockLedgerResult> replayByStock,
        decimal currentGrossMarketValue)
    {
        var remainingCost = replayByStock.Values.Sum(result => result.RemainingCostBasis);
        var realized = replayByStock.Values.Sum(result => result.RealizedGainLoss);
        var dividends = replayByStock.Values.Sum(result => result.NetDividendIncome);
        var unrealized = currentGrossMarketValue - remainingCost;
        return new StockPerformanceSummary(
            currentGrossMarketValue,
            remainingCost,
            realized,
            unrealized,
            dividends,
            realized + unrealized + dividends);
    }

    /// <summary>建立包含已結清標的的每檔股票績效明細。</summary>
    private static IReadOnlyList<StockPerformanceInstrumentBreakdown> BuildBreakdown(
        IReadOnlyList<Stock> stocks,
        IReadOnlyDictionary<int, StockLedgerResult> replayByStock)
    {
        return stocks
            .Where(stock => replayByStock.ContainsKey(stock.Id))
            .OrderBy(stock => stock.Id)
            .Select(stock =>
            {
                var replay = replayByStock[stock.Id];
                var currentShares = replay.RemainingShares;
                var gross = currentShares * stock.CurrentPrice;
                var unrealized = gross - replay.RemainingCostBasis;
                return new StockPerformanceInstrumentBreakdown(
                    stock.Id,
                    stock.Name,
                    stock.Symbol,
                    stock.Market,
                    stock.Broker,
                    currentShares,
                    gross,
                    replay.RemainingCostBasis,
                    replay.RealizedGainLoss,
                    unrealized,
                    replay.NetDividendIncome,
                    replay.RealizedGainLoss + unrealized + replay.NetDividendIncome,
                    currentShares <= DecimalTolerance);
            })
            .ToList();
    }

    /// <summary>建立月度點並將缺失 cumulative TWR 保留為 null。</summary>
    private static IReadOnlyList<StockPerformanceMonthlyPoint> BuildMonthlyPoints(
        StockPerformanceInput input,
        IReadOnlyDictionary<int, StockLedgerResult> replayByStock,
        IReadOnlyList<StockPerformanceTwrPoint> twrPoints,
        IReadOnlyList<StockPerformanceInstrumentBreakdown> breakdown)
    {
        var months = input.Transactions
            .Where(transaction => transaction.TradeDate >= input.DateStart && transaction.TradeDate <= input.DateEnd)
            .Select(transaction => new DateOnly(transaction.TradeDate.Year, transaction.TradeDate.Month, 1))
            .Concat(twrPoints.Select(point => new DateOnly(point.Date.Year, point.Date.Month, 1)))
            .Distinct()
            .OrderBy(month => month)
            .ToList();
        return months.Select(month =>
        {
            var monthEnd = month.AddMonths(1).AddDays(-1);
            var point = twrPoints
                .Where(item => item.Date >= month && item.Date <= monthEnd)
                .OrderBy(item => item.Date)
                .LastOrDefault();
            var monthTransactions = input.Transactions.Where(transaction =>
                transaction.TradeDate >= month && transaction.TradeDate <= monthEnd);
            var contribution = monthTransactions.Sum(transaction => transaction.Type switch
            {
                StockTransactionType.OpeningBalance => transaction.OpeningMarketValue ?? 0m,
                StockTransactionType.Buy => -(transaction.Shares!.Value * transaction.Price!.Value
                    + transaction.Fee + transaction.Tax),
                StockTransactionType.Sell => transaction.Shares!.Value * transaction.Price!.Value
                    - transaction.Fee - transaction.Tax,
                StockTransactionType.Dividend => transaction.CashAmount!.Value
                    - transaction.Fee - transaction.Tax,
                StockTransactionType.StockDividend => 0m,
                _ => 0m,
            });
            var realized = monthTransactions
                .Where(transaction => replayByStock.ContainsKey(transaction.StockId))
                .Sum(transaction => transaction.Type switch
                {
                    StockTransactionType.Sell => FindEntryResult(replayByStock[transaction.StockId], transaction.Id).RealizedGainLoss,
                    StockTransactionType.StockDividend => 0m,
                    _ => 0m,
                });
            var dividend = monthTransactions.Sum(transaction => transaction.Type switch
            {
                StockTransactionType.Dividend => transaction.CashAmount!.Value - transaction.Fee - transaction.Tax,
                StockTransactionType.StockDividend => 0m,
                _ => 0m,
            });
            return new StockPerformanceMonthlyPoint(
                $"{month.Year:D4}/{month.Month:D2}",
                point?.EndingValue ?? breakdown.Sum(item => item.GrossMarketValue),
                contribution,
                realized,
                dividend,
                point?.CumulativeReturn);
        }).ToList();
    }

    /// <summary>依交易 ID 取得 replay 後的單筆衍生結果。</summary>
    private static StockLedgerEntryResult FindEntryResult(
        StockLedgerResult replay,
        int transactionId)
        => replay.Entries.Single(entry => entry.Entry.Id == transactionId);

    /// <summary>計算指定日期前一日部位在當日 raw close 的 securities value。</summary>
    private static decimal CalculateValueBeforeDate(
        DateOnly date,
        IReadOnlyList<Stock> activeStocks,
        IReadOnlyList<StockTransaction> transactions,
        IReadOnlyDictionary<(StockMarket Market, string Symbol, DateOnly Date), decimal> prices)
    {
        var value = 0m;
        foreach (var stock in activeStocks)
        {
            var shares = ReplaySharesBeforeDate(stock.Id, date, transactions);
            if (prices.TryGetValue((stock.Market, NormalizeSymbol(stock.Symbol), date), out var close))
                value += shares * close;
        }

        return value;
    }

    /// <summary>重播單一股票至指定日期並取得日終部位股數。</summary>
    private static decimal ReplaySharesAtDate(
        int stockId,
        DateOnly date,
        IReadOnlyList<StockTransaction> transactions)
    {
        var entries = transactions
            .Where(transaction => transaction.StockId == stockId && transaction.TradeDate <= date)
            .ToList();
        return entries.Count == 0 ? 0m : StockLedgerCalculator.Replay(entries).RemainingShares;
    }

    /// <summary>重播單一股票至指定日期前並取得期初部位股數。</summary>
    private static decimal ReplaySharesBeforeDate(
        int stockId,
        DateOnly date,
        IReadOnlyList<StockTransaction> transactions)
    {
        var entries = transactions
            .Where(transaction => transaction.StockId == stockId && transaction.TradeDate < date)
            .ToList();
        return entries.Count == 0 ? 0m : StockLedgerCalculator.Replay(entries).RemainingShares;
    }

    /// <summary>建立日期無效時的安全空報表。</summary>
    private static StockPerformanceReport CreateInvalidReport(StockPerformanceInput input)
    {
        var summary = new StockPerformanceSummary(0m, 0m, 0m, 0m, 0m, 0m);
        var metric = new StockPerformanceMetric(null, StockPerformanceUnavailableReason.InvalidPeriod);
        var quality = new StockPerformanceDataQuality(0, 0, 0, 0d, StockPerformanceUnavailableReason.InvalidPeriod, false);
        return new StockPerformanceReport(
            input.DateStart,
            input.DateEnd,
            null,
            false,
            "Unavailable",
            metric,
            summary,
            metric,
            metric,
            [],
            [],
            quality);
    }

    /// <summary>建立指定原因的 TWR unavailable 結果。</summary>
    private static StockPerformanceTwrResult CreateUnavailableTwr(
        StockPerformanceUnavailableReason reason)
        => new(new StockPerformanceMetric(null, reason), [], 0d, 0);

    /// <summary>以 Newton-Raphson 嘗試在 bounded rate domain 內求根。</summary>
    private static bool TryNewtonRaphson(
        IReadOnlyList<(DateOnly Date, double Amount)> flows,
        out double rate)
    {
        rate = 0.1d;
        for (var iteration = 0; iteration < MaximumIterations; iteration++)
        {
            if (!TryCalculateNpv(flows, rate, out var value, out var derivative)
                || Math.Abs(derivative) < 1e-14d)
                return false;
            if (Math.Abs(value) < 1e-10d)
                return true;
            var next = rate - value / derivative;
            if (!double.IsFinite(next) || next <= RateLowerBound || next > RateUpperBound)
                return false;
            rate = next;
        }

        return TryCalculateNpv(flows, rate, out var finalValue, out _)
            && Math.Abs(finalValue) < 1e-8d;
    }

    /// <summary>以有限次 bisection fallback 在正負 NPV bracket 內求根。</summary>
    private static bool TryBisection(
        IReadOnlyList<(DateOnly Date, double Amount)> flows,
        out double rate)
    {
        rate = 0d;
        var low = RateLowerBound;
        var high = 1d;
        if (!TryCalculateNpv(flows, low, out var lowValue, out _))
            return false;
        if (!TryCalculateNpv(flows, high, out var highValue, out _))
            return false;
        while (Math.Sign(lowValue) == Math.Sign(highValue) && high < RateUpperBound)
        {
            high = Math.Min(RateUpperBound, high * 2d);
            if (!TryCalculateNpv(flows, high, out highValue, out _))
                return false;
        }

        if (Math.Sign(lowValue) == Math.Sign(highValue))
            return false;
        for (var iteration = 0; iteration < 200; iteration++)
        {
            rate = (low + high) / 2d;
            if (!TryCalculateNpv(flows, rate, out var value, out _))
                return false;
            if (Math.Abs(value) < 1e-10d)
                return true;
            if (Math.Sign(value) == Math.Sign(lowValue))
            {
                low = rate;
                lowValue = value;
            }
            else
            {
                high = rate;
                highValue = value;
            }
        }

        return false;
    }

    /// <summary>計算日期現金流 NPV 與其 rate 導數並執行 finite guard。</summary>
    private static bool TryCalculateNpv(
        IReadOnlyList<(DateOnly Date, double Amount)> flows,
        double rate,
        out double value,
        out double derivative)
    {
        value = 0d;
        derivative = 0d;
        if (rate <= RateLowerBound || !double.IsFinite(rate))
            return false;
        var baseDate = flows[0].Date;
        foreach (var flow in flows)
        {
            var years = (flow.Date.DayNumber - baseDate.DayNumber) / 365d;
            var denominator = Math.Pow(1d + rate, years);
            if (!double.IsFinite(denominator) || denominator == 0d)
                return false;
            value += flow.Amount / denominator;
            derivative -= years * flow.Amount / (denominator * (1d + rate));
            if (!double.IsFinite(value) || !double.IsFinite(derivative))
                return false;
        }

        return true;
    }

    /// <summary>依市場與正規化代號比較 raw price identity。</summary>
    private static string NormalizeSymbol(string symbol)
        => symbol.Trim().ToUpperInvariant();
}
