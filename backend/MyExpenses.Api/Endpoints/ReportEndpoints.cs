using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class ReportEndpoints
{
    /// <summary>註冊收入支出、資產負債、提款摘要與其他報表端點。</summary>
    public static void MapReportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/reports");

        group.MapGet("/income-expense-trend", async (DateOnly? dateStart, DateOnly? dateEnd, AppDbContext db, TimeZoneService timeZoneService) =>
        {
            var localNow = timeZoneService.GetLocalNow();
            var start = dateStart ?? new DateOnly(localNow.Year, 1, 1);
            var end = dateEnd ?? new DateOnly(localNow.Year, 12, 31);

            var data = await db.Transactions
                .Where(t => t.Date >= start && t.Date <= end)
                .GroupBy(t => new { t.Date.Year, t.Date.Month })
                .Select(g => new
                {
                    Year = g.Key.Year,
                    Month = g.Key.Month,
                    Income = g.Where(t => t.Type == TransactionType.Income).Sum(t => t.Amount),
                    Expense = g.Where(t => t.Type == TransactionType.Expense).Sum(t => t.Amount)
                })
                .OrderBy(x => x.Year).ThenBy(x => x.Month)
                .ToListAsync();

            var result = data.Select(x => new
            {
                Month = $"{x.Year:D4}/{x.Month:D2}",
                Income = x.Income,
                Expense = x.Expense
            });

            return Results.Ok(result);
        });

        group.MapGet("/category-distribution", async (DateOnly? dateStart, DateOnly? dateEnd, AppDbContext db, TimeZoneService timeZoneService) =>
        {
            var localNow = timeZoneService.GetLocalNow();
            var start = dateStart ?? new DateOnly(localNow.Year, localNow.Month, 1);
            var end = dateEnd ?? start.AddMonths(1).AddDays(-1);

            var totalExpense = await db.Transactions
                .Where(t => t.Type == TransactionType.Expense && t.Date >= start && t.Date <= end)
                .SumAsync(t => t.Amount);

            if (totalExpense == 0)
                return Results.Ok(Array.Empty<object>());

            var data = await db.Transactions
                .Where(t => t.Type == TransactionType.Expense && t.Date >= start && t.Date <= end)
                .GroupBy(t => new { t.CategoryId, t.Category.Name, t.Category.Color, t.Category.Icon })
                .Select(g => new
                {
                    CategoryId = g.Key.CategoryId,
                    CategoryName = g.Key.Name,
                    Color = g.Key.Color,
                    Icon = g.Key.Icon,
                    Total = g.Sum(t => t.Amount),
                    Percentage = g.Sum(t => t.Amount) / totalExpense * 100
                })
                .OrderByDescending(x => x.Total)
                .ToListAsync();

            return Results.Ok(data);
        });

        group.MapGet("/stock-structure", async (string? broker, StockInstrumentType? instrumentType, AppDbContext db) =>
            Results.Ok(await GetStockStructureAsync(db, broker, instrumentType)))
            .RequireApiTokenScope(ApiTokenScopes.ReportsRead);

        group.MapGet("/stock-market-risk", async (int? periodMonths, AppDbContext db, TimeZoneService timeZoneService) =>
        {
            var selectedPeriod = periodMonths ?? 12;
            if (selectedPeriod is not (3 or 6 or 12))
                return Results.BadRequest("觀察期只支援 3、6 或 12 個月");

            return Results.Ok(await GetStockMarketRiskAsync(selectedPeriod, db, timeZoneService));
        })
        .RequireApiTokenScope(ApiTokenScopes.ReportsRead);

        group.MapGet("/stock-performance", GetStockPerformanceHttpAsync)
            .RequireApiTokenScope(ApiTokenScopes.ReportsRead);

        group.MapGet("/stock-value-trend", async (int? months, AppDbContext db, TimeZoneService timeZoneService) =>
        {
            try
            {
                return Results.Ok(await GetStockValueTrendAsync(months, db, timeZoneService));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
        .RequireApiTokenScope(ApiTokenScopes.ReportsRead);

        group.MapGet("/net-worth", async (AppDbContext db, IServiceProvider services) =>
        {
            try
            {
                return Results.Ok(await GetNetWorthAsync(db, services.GetService<IExchangeRateService>()));
            }
            catch (ExchangeRateUnavailableException exception)
            {
                return Results.Problem(
                    detail: exception.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/installment-forecast", async (int? months, AppDbContext db, TimeZoneService timeZoneService) =>
        {
            var forecastMonths = months ?? 6;
            var today = timeZoneService.GetLocalDate();

            var unpaidPayments = await db.InstallmentPayments
                .Include(p => p.Installment).ThenInclude(i => i!.Card)
                .Where(p => !p.IsPaid && p.DueDate != null)
                .ToListAsync();

            var forecast = new List<object>();
            for (var i = 0; i < forecastMonths; i++)
            {
                var monthStartDate = new DateOnly(today.Year, today.Month, 1).AddMonths(i);
                var monthEndDate = monthStartDate.AddMonths(1).AddDays(-1);

                var monthPayments = unpaidPayments
                    .Where(p => p.DueDate!.Value >= monthStartDate && p.DueDate.Value <= monthEndDate)
                    .ToList();

                forecast.Add(new
                {
                    Month = $"{monthStartDate.Year:D4}/{monthStartDate.Month:D2}",
                    TotalAmount = monthPayments.Sum(p => p.Amount),
                    Payments = monthPayments.Select(p => new
                    {
                        CardBankName = p.Installment.Card?.BankName ?? "",
                        Description = p.Installment.Description,
                        Period = p.Period,
                        Amount = p.Amount,
                        DueDate = p.DueDate!.Value.ToString("yyyy-MM-dd")
                    })
                });
            }

            return Results.Ok(forecast);
        });

        group.MapGet("/dashboard-summary", async (
            int? year,
            int? month,
            AppDbContext db,
            TimeZoneService timeZoneService,
            IServiceProvider services) =>
        {
            try
            {
                return Results.Ok(await GetDashboardSummaryAsync(
                    year,
                    month,
                    db,
                    timeZoneService,
                    services.GetService<IExchangeRateService>()));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (ExchangeRateUnavailableException ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .RequireApiTokenScope(ApiTokenScopes.ReportsRead);

        group.MapGet("/net-worth-trend", async (int? months, AppDbContext db, TimeZoneService timeZoneService) =>
        {
            try
            {
                return Results.Ok(await GetNetWorthTrendAsync(months, db, timeZoneService));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
        .RequireApiTokenScope(ApiTokenScopes.ReportsRead);

        group.MapGet("/monthly-summary", async (int? year, int? month, AppDbContext db, TimeZoneService timeZoneService, IServiceProvider services) =>
        {
            try
            {
                return Results.Ok(await GetMonthlySummaryAsync(
                    year,
                    month,
                    db,
                    timeZoneService,
                    services.GetService<IExchangeRateService>()));
            }
            catch (ExchangeRateUnavailableException exception)
            {
                return Results.Problem(
                    detail: exception.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .RequireApiTokenScope(ApiTokenScopes.ReportsRead);
    }

    /// <summary>建立 Dashboard 選定月份與前一月份的完整彙總。</summary>
    public static async Task<DashboardSummaryResponse> GetDashboardSummaryAsync(
        int? year,
        int? month,
        AppDbContext db,
        TimeZoneService timeZoneService,
        IExchangeRateService? exchangeRateService = null)
    {
        var current = timeZoneService.GetLocalDate();
        var selectedYear = year ?? current.Year;
        var selectedMonth = month ?? current.Month;
        if (selectedYear is < 1 or > 9999)
            throw new ArgumentException("年份不在支援範圍內");
        if (selectedMonth is < 1 or > 12)
            throw new ArgumentException("月份必須介於 1 到 12 之間");

        var selectedStart = new DateOnly(selectedYear, selectedMonth, 1);
        var selectedEnd = selectedStart.AddMonths(1).AddDays(-1);
        var previousStart = selectedStart.AddMonths(-1);
        var previousEnd = selectedStart.AddDays(-1);
        var withdrawals = await db.Withdrawals
            .Include(withdrawal => withdrawal.BankAccount)
            .Where(withdrawal =>
                withdrawal.Date >= previousStart
                && withdrawal.Date <= selectedEnd)
            .ToListAsync();
        var exchangeRateSnapshot = await ExchangeRateSnapshotResolver.ResolveForAccountsAsync(
            withdrawals.Select(withdrawal => withdrawal.BankAccount),
            exchangeRateService);
        var selected = await GetDashboardPeriodAsync(
            db,
            selectedStart,
            selectedEnd,
            withdrawals,
            exchangeRateService,
            exchangeRateSnapshot);
        var previous = await GetDashboardPeriodAsync(
            db,
            previousStart,
            previousEnd,
            withdrawals,
            exchangeRateService,
            exchangeRateSnapshot);
        var activeInstallmentCount = await db.Installments
            .CountAsync(installment => installment.Payments.Any(payment => !payment.IsPaid));

        return new DashboardSummaryResponse(
            selected.TotalWithdrawals,
            selected.WithdrawalCount,
            selected.TotalExpenses,
            selected.ExpenseCount,
            selected.TotalWithdrawals - selected.TotalExpenses,
            selected.InstallmentDueAmount,
            selected.InstallmentDuePaymentCount,
            activeInstallmentCount,
            previous.TotalWithdrawals - previous.TotalExpenses,
            CurrencyPolicy.BaseCurrencyCode,
            exchangeRateSnapshot.UpdatedAtUtc == DateTime.UnixEpoch
                ? null
                : exchangeRateSnapshot.UpdatedAtUtc,
            exchangeRateSnapshot.IsStale,
            true);
    }

    /// <summary>回傳完整淨值快照趨勢，並選取各系統本地月份的最新資料點。</summary>
    public static async Task<IReadOnlyList<NetWorthTrendPoint>> GetNetWorthTrendAsync(
        int? months,
        AppDbContext db,
        TimeZoneService timeZoneService,
        DateOnly? asOfDate = null)
    {
        var monthCount = months ?? 6;
        if (monthCount is < 1 or > 60)
            throw new ArgumentException("月份數必須介於 1 到 60 之間");

        var localEndDate = asOfDate ?? timeZoneService.GetLocalDate();
        var currentMonthStart = new DateOnly(localEndDate.Year, localEndDate.Month, 1);
        var firstMonthStart = currentMonthStart.AddMonths(-(monthCount - 1));
        var utcStart = timeZoneService.ConvertLocalToUtc(
            firstMonthStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified));
        var utcEndExclusive = timeZoneService.ConvertLocalToUtc(
            currentMonthStart.AddMonths(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified));

        var snapshots = await db.SnapshotBatches
            .Where(snapshot =>
                snapshot.NetWorthBasis == NetWorthBasis.AssetsMinusLiabilities
                && snapshot.TotalLiabilities.HasValue
                && snapshot.SnapshotDate >= utcStart
                && snapshot.SnapshotDate < utcEndExclusive)
            .Select(snapshot => new
            {
                snapshot.Id,
                snapshot.Name,
                snapshot.SnapshotDate,
                snapshot.TotalAssets,
                TotalLiabilities = snapshot.TotalLiabilities!.Value,
                snapshot.TotalNetWorth,
            })
            .ToListAsync();

        return snapshots
            .GroupBy(snapshot =>
            {
                var localDate = timeZoneService.ConvertUtcToLocal(snapshot.SnapshotDate);
                return (localDate.Year, localDate.Month);
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                Snapshot = group.OrderByDescending(snapshot => snapshot.SnapshotDate).First(),
            })
            .OrderBy(item => item.Snapshot.SnapshotDate)
            .Select(item => new NetWorthTrendPoint(
                $"{item.Year:D4}/{item.Month:D2}",
                item.Snapshot.SnapshotDate,
                item.Snapshot.Name,
                item.Snapshot.TotalAssets,
                item.Snapshot.TotalLiabilities,
                item.Snapshot.TotalNetWorth))
            .ToList();
    }

    /// <summary>彙總一個含起迄日之本地期間的提款、支出與未繳應付款。</summary>
    private static async Task<DashboardPeriodAggregate> GetDashboardPeriodAsync(
        AppDbContext db,
        DateOnly start,
        DateOnly end,
        IReadOnlyCollection<Withdrawal> withdrawals,
        IExchangeRateService? exchangeRateService,
        ExchangeRateSnapshot exchangeRateSnapshot)
    {
        var periodWithdrawals = withdrawals
            .Where(withdrawal => withdrawal.Date >= start && withdrawal.Date <= end)
            .ToList();
        var totalWithdrawals = periodWithdrawals.Sum(withdrawal =>
            ConvertDashboardWithdrawalAmount(
                withdrawal,
                exchangeRateService,
                exchangeRateSnapshot));
        var withdrawalCount = periodWithdrawals.Count;
        var totalExpenses = await db.Transactions
            .Where(transaction =>
                transaction.Type == TransactionType.Expense
                && transaction.Date >= start
                && transaction.Date <= end)
            .SumAsync(transaction => (decimal?)transaction.Amount) ?? 0m;
        var expenseCount = await db.Transactions
            .CountAsync(transaction =>
                transaction.Type == TransactionType.Expense
                && transaction.Date >= start
                && transaction.Date <= end);
        var duePayments = db.InstallmentPayments.Where(payment =>
            !payment.IsPaid
            && payment.DueDate.HasValue
            && payment.DueDate.Value >= start
            && payment.DueDate.Value <= end);
        var installmentDueAmount = await duePayments
            .SumAsync(payment => (decimal?)payment.Amount) ?? 0m;
        var installmentDuePaymentCount = await duePayments.CountAsync();

        return new DashboardPeriodAggregate(
            totalWithdrawals,
            withdrawalCount,
            totalExpenses,
            expenseCount,
            installmentDueAmount,
            installmentDuePaymentCount);
    }

    /// <summary>依提款關聯帳戶幣別將原幣提款換算為 TWD。</summary>
    private static decimal ConvertDashboardWithdrawalAmount(
        Withdrawal withdrawal,
        IExchangeRateService? exchangeRateService,
        ExchangeRateSnapshot exchangeRateSnapshot)
    {
        var currencyCode = CurrencyPolicy.NormalizeOrDefault(withdrawal.BankAccount.CurrencyCode);
        if (currencyCode == CurrencyPolicy.BaseCurrencyCode)
            return withdrawal.Amount;
        if (exchangeRateService is null)
            throw new ExchangeRateUnavailableException("存在外幣提款但未設定匯率服務");

        return exchangeRateService.ConvertToBase(
                   withdrawal.Amount,
                   currencyCode,
                   exchangeRateSnapshot)
               ?? throw new ExchangeRateUnavailableException(
                   $"缺少 {currencyCode} 匯率，無法產生 Dashboard 摘要");
    }

    /// <summary>建立使用股票預估賣出淨值與 TWD 銀行估值的資產負債報表。</summary>
    public static async Task<NetWorthReportResponse> GetNetWorthAsync(
        AppDbContext db,
        IExchangeRateService? exchangeRateService = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var bankAccounts = await db.BankAccounts.ToListAsync();
        var stocks = await db.Stocks.ToListAsync();
        var containsForeignCurrency = bankAccounts.Any(account =>
            CurrencyPolicy.NormalizeOrDefault(account.CurrencyCode) != CurrencyPolicy.BaseCurrencyCode);
        var exchangeRateSnapshot = await ExchangeRateSnapshotResolver.ResolveForAccountsAsync(
            bankAccounts,
            exchangeRateService);

        var bankRows = bankAccounts
            .Select(account => new NetWorthBankAccountRow(
                account.BankName,
                account.AccountNumber,
                CurrencyPolicy.NormalizeOrDefault(account.CurrencyCode),
                account.Balance,
                ConvertBankBalance(account, exchangeRateService, exchangeRateSnapshot)))
            .ToList();
        var stockRows = stocks.Select(ToNetWorthStockRow).ToList();
        var totalBankBalance = bankRows.Sum(b => b.ConvertedBalance);
        var totalStockValue = stockRows.Sum(s => s.EstimatedNetSellValue);
        var totalAssets = totalBankBalance + totalStockValue;

        var unpaidInstallments = await db.InstallmentPayments
            .Where(p => !p.IsPaid)
            .SumAsync(p => p.Amount);

        return new NetWorthReportResponse(
            totalAssets,
            unpaidInstallments,
            totalAssets - unpaidInstallments,
            totalBankBalance,
            bankRows,
            stockRows,
            CurrencyPolicy.BaseCurrencyCode,
            containsForeignCurrency ? exchangeRateSnapshot.UpdatedAtUtc : null,
            exchangeRateSnapshot.IsStale,
            true);
    }

    /// <summary>以共用匯率 snapshot 將單一銀行帳戶換算為 TWD。</summary>
    private static decimal ConvertBankBalance(
        BankAccount account,
        IExchangeRateService? exchangeRateService,
        ExchangeRateSnapshot exchangeRateSnapshot)
    {
        var currencyCode = CurrencyPolicy.NormalizeOrDefault(account.CurrencyCode);
        if (currencyCode == CurrencyPolicy.BaseCurrencyCode)
            return account.Balance;
        if (exchangeRateService is null)
            throw new ExchangeRateUnavailableException("存在外幣帳戶但未設定匯率服務");

        var convertedBalance = exchangeRateService.ConvertToBase(
            account.Balance,
            currencyCode,
            exchangeRateSnapshot);
        return convertedBalance
            ?? throw new ExchangeRateUnavailableException($"缺少 {currencyCode} 匯率，無法產生報表");
    }

    /// <summary>建立指定月份的收入、支出與 TWD 銀行資產摘要。</summary>
    public static async Task<MonthlySummaryReportResponse> GetMonthlySummaryAsync(
        int? year,
        int? month,
        AppDbContext db,
        TimeZoneService timeZoneService,
        IExchangeRateService? exchangeRateService = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeZoneService);
        var now = timeZoneService.GetLocalNow();
        var selectedYear = year ?? now.Year;
        var selectedMonth = month ?? now.Month;
        var start = new DateOnly(selectedYear, selectedMonth, 1);
        var end = start.AddMonths(1).AddDays(-1);

        var totalIncome = await db.Transactions
            .Where(transaction => transaction.Type == TransactionType.Income
                && transaction.Date >= start
                && transaction.Date <= end)
            .SumAsync(transaction => transaction.Amount);
        var totalExpense = await db.Transactions
            .Where(transaction => transaction.Type == TransactionType.Expense
                && transaction.Date >= start
                && transaction.Date <= end)
            .SumAsync(transaction => transaction.Amount);
        var bankAccounts = await db.BankAccounts.ToListAsync();
        var containsForeignCurrency = bankAccounts.Any(account =>
            CurrencyPolicy.NormalizeOrDefault(account.CurrencyCode) != CurrencyPolicy.BaseCurrencyCode);
        var exchangeRateSnapshot = await ExchangeRateSnapshotResolver.ResolveForAccountsAsync(
            bankAccounts,
            exchangeRateService);
        var totalBankBalance = bankAccounts
            .Sum(account => ConvertBankBalance(account, exchangeRateService, exchangeRateSnapshot));

        return new MonthlySummaryReportResponse(
            totalIncome,
            totalExpense,
            totalBankBalance,
            CurrencyPolicy.BaseCurrencyCode,
            containsForeignCurrency ? exchangeRateSnapshot.UpdatedAtUtc : null,
            exchangeRateSnapshot.IsStale,
            true);
    }

    /// <summary>載入持股並建立目前篩選範圍的持股結構報表。</summary>
    public static async Task<StockStructureReportResponse> GetStockStructureAsync(
        AppDbContext db,
        string? broker = null,
        StockInstrumentType? instrumentType = null,
        DateTime? asOfUtc = null)
    {
        var stocks = await db.Stocks
            .OrderBy(stock => stock.Id)
            .ToListAsync();
        var availableBrokers = stocks
            .Select(stock => stock.Broker?.Trim())
            .Where(brokerName => !string.IsNullOrWhiteSpace(brokerName))
            .Select(brokerName => brokerName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(brokerName => brokerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var availableInstrumentTypes = stocks
            .Select(stock => stock.InstrumentType)
            .Distinct()
            .OrderBy(instrument => instrument)
            .ToList();
        var report = StockStructureReportCalculator.Calculate(stocks, broker, instrumentType);
        var selectedHoldingIds = report.Holdings.Select(holding => holding.Id).ToHashSet();
        var selectedStocks = stocks.Where(stock => selectedHoldingIds.Contains(stock.Id));
        var generatedAt = StockReportDataQualityCalculator.NormalizeUtc(asOfUtc ?? DateTime.UtcNow);
        var dataQuality = StockReportDataQualityCalculator.Calculate(selectedStocks, generatedAt);

        return new StockStructureReportResponse(
            report.Summary,
            report.Insights,
            report.SymbolAllocations,
            report.InstrumentTypeAllocations,
            report.BrokerAllocations,
            report.MarketAllocations,
            report.Concentration,
            dataQuality,
            report.Holdings,
            availableBrokers,
            availableInstrumentTypes,
            generatedAt);
    }

    /// <summary>只讀本機持股、歷史價格與同步狀態建立市場風險報表。</summary>
    public static async Task<StockMarketRiskReport> GetStockMarketRiskAsync(
        int? periodMonths,
        AppDbContext db,
        TimeZoneService timeZoneService,
        DateOnly? asOfDate = null)
    {
        var selectedPeriod = periodMonths ?? 12;
        if (selectedPeriod is not (3 or 6 or 12))
            throw new ArgumentException("觀察期只支援 3、6 或 12 個月", nameof(periodMonths));

        var stocks = await db.Stocks.AsNoTracking().ToListAsync();
        var prices = await db.HistoricalAdjustedPrices.AsNoTracking().ToListAsync();
        var syncStates = await db.HistoricalPriceSyncStates.AsNoTracking().ToListAsync();
        var calculationDate = asOfDate ?? timeZoneService.GetLocalDate();
        return StockMarketRiskCalculator.Calculate(
            stocks,
            prices,
            selectedPeriod,
            calculationDate,
            syncStates);
    }

    /// <summary>只讀本機股票、Ledger 與 raw close 建立績效報表，不呼叫外部行情 provider。</summary>
    public static async Task<StockPerformanceReport> GetStockPerformanceAsync(
        DateOnly? dateStart,
        DateOnly? dateEnd,
        AppDbContext db,
        TimeZoneService timeZoneService)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeZoneService);

        var localToday = timeZoneService.GetLocalDate();
        var requestedEnd = dateEnd ?? localToday;
        if (requestedEnd > localToday)
            throw new ArgumentException("dateEnd 不可晚於今天", nameof(dateEnd));

        var transactions = await db.StockTransactions
            .AsNoTracking()
            .OrderBy(transaction => transaction.TradeDate)
            .ThenBy(transaction => transaction.Sequence)
            .ThenBy(transaction => transaction.Id)
            .ToListAsync();
        var requestedStart = dateStart
            ?? transactions.Select(transaction => (DateOnly?)transaction.TradeDate).Min()
            ?? requestedEnd;
        if (requestedEnd < requestedStart)
            throw new ArgumentException("dateEnd 不可早於 dateStart", nameof(dateEnd));

        var stocks = await db.Stocks
            .AsNoTracking()
            .OrderBy(stock => stock.Id)
            .ToListAsync();
        var priceLookbackStart = requestedStart.AddDays(-31);
        var prices = await db.HistoricalAdjustedPrices
            .AsNoTracking()
            .Where(price => price.TradingDate >= priceLookbackStart && price.TradingDate <= requestedEnd)
            .ToListAsync();

        return StockPerformanceCalculator.Calculate(new StockPerformanceInput(
            requestedStart,
            requestedEnd,
            stocks,
            transactions,
            prices,
            localToday));
    }

    /// <summary>處理績效報表 HTTP request 並將日期錯誤轉成安全的 typed response。</summary>
    private static async Task<IResult> GetStockPerformanceHttpAsync(
        DateOnly? dateStart,
        DateOnly? dateEnd,
        AppDbContext db,
        TimeZoneService timeZoneService)
    {
        try
        {
            return Results.Ok(await GetStockPerformanceAsync(dateStart, dateEnd, db, timeZoneService));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new
            {
                Code = "InvalidDateRange",
                Message = exception.Message,
            });
        }
    }

    /// <summary>依系統時區彙整指定月份數的全部持股實際快照價值。</summary>
    public static async Task<IReadOnlyList<StockValueTrendPoint>> GetStockValueTrendAsync(
        int? months,
        AppDbContext db,
        TimeZoneService timeZoneService,
        DateOnly? asOfDate = null)
    {
        var monthCount = months ?? 6;
        if (monthCount is < 1 or > 60)
            throw new ArgumentException("月份數必須介於 1 到 60 之間");

        var localEndDate = asOfDate ?? timeZoneService.GetLocalDate();
        var currentMonthStart = new DateOnly(localEndDate.Year, localEndDate.Month, 1);
        var firstMonthStart = currentMonthStart.AddMonths(-(monthCount - 1));
        var utcStart = timeZoneService.ConvertLocalToUtc(
            firstMonthStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified));
        var utcEndExclusive = timeZoneService.ConvertLocalToUtc(
            currentMonthStart.AddMonths(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified));
        var snapshots = await db.SnapshotBatches
            .AsNoTracking()
            .Where(snapshot => snapshot.SnapshotDate >= utcStart && snapshot.SnapshotDate < utcEndExclusive)
            .Select(snapshot => new
            {
                snapshot.Name,
                snapshot.SnapshotDate,
                snapshot.TotalStockValue,
                snapshot.NetWorthBasis,
            })
            .ToListAsync();

        return snapshots
            .GroupBy(snapshot =>
            {
                var localDate = timeZoneService.ConvertUtcToLocal(snapshot.SnapshotDate);
                return (localDate.Year, localDate.Month);
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                Snapshot = group.OrderByDescending(snapshot => snapshot.SnapshotDate).First(),
            })
            .OrderBy(item => item.Snapshot.SnapshotDate)
            .Select(item => new StockValueTrendPoint(
                $"{item.Year:D4}/{item.Month:D2}",
                item.Snapshot.SnapshotDate,
                item.Snapshot.Name,
                item.Snapshot.TotalStockValue,
                item.Snapshot.NetWorthBasis))
            .ToList();
    }

    /// <summary>將股票持倉映射為包含預估價值欄位的淨值報表資料列。</summary>
    private static NetWorthStockRow ToNetWorthStockRow(Stock stock)
    {
        var valuation = StockValuationCalculator.Calculate(stock);
        return new NetWorthStockRow(
            stock.Name,
            stock.Symbol,
            stock.InstrumentType,
            stock.Shares,
            stock.CurrentPrice,
            valuation.GrossMarketValue,
            valuation.EstimatedNetSellValue);
    }
}

public sealed record NetWorthReportResponse(
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal NetWorth,
    decimal TotalBankBalance,
    IReadOnlyList<NetWorthBankAccountRow> BankAccounts,
    IReadOnlyList<NetWorthStockRow> Stocks,
    string BaseCurrency,
    DateTime? ExchangeRateUpdatedAt,
    bool ExchangeRateIsStale,
    bool ConversionAvailable);

public sealed record NetWorthBankAccountRow(
    string BankName,
    string AccountNumber,
    string CurrencyCode,
    decimal Balance,
    decimal ConvertedBalance);

public sealed record MonthlySummaryReportResponse(
    decimal TotalIncome,
    decimal TotalExpense,
    decimal TotalBankBalance,
    string BaseCurrency,
    DateTime? ExchangeRateUpdatedAt,
    bool ExchangeRateIsStale,
    bool ConversionAvailable);

public sealed record NetWorthStockRow(
    string Name,
    string Symbol,
    StockInstrumentType InstrumentType,
    decimal Shares,
    decimal CurrentPrice,
    decimal GrossMarketValue,
    decimal EstimatedNetSellValue);

public sealed record StockStructureReportResponse(
    StockStructureSummary Summary,
    IReadOnlyList<StockStructureInsight> Insights,
    IReadOnlyList<StockStructureAllocation> SymbolAllocations,
    IReadOnlyList<StockStructureAllocation> InstrumentTypeAllocations,
    IReadOnlyList<StockStructureAllocation> BrokerAllocations,
    IReadOnlyList<StockStructureAllocation> MarketAllocations,
    StockStructureConcentration Concentration,
    StockReportDataQuality DataQuality,
    IReadOnlyList<StockStructureHolding> Holdings,
    IReadOnlyList<string> AvailableBrokers,
    IReadOnlyList<StockInstrumentType> AvailableInstrumentTypes,
    DateTime GeneratedAt);

public sealed record StockValueTrendPoint(
    string Month,
    DateTime SnapshotDate,
    string Name,
    decimal TotalStockValue,
    NetWorthBasis Basis);

public sealed record DashboardSummaryResponse(
    decimal TotalWithdrawals,
    int WithdrawalCount,
    decimal TotalExpenses,
    int ExpenseCount,
    decimal DisposableBalance,
    decimal InstallmentDueAmount,
    int InstallmentDuePaymentCount,
    int ActiveInstallmentCount,
    decimal PreviousDisposableBalance,
    string BaseCurrency,
    DateTime? ExchangeRateUpdatedAt,
    bool ExchangeRateIsStale,
    bool ConversionAvailable);

public sealed record NetWorthTrendPoint(
    string Month,
    DateTime SnapshotDate,
    string Name,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal NetWorth);

/// <summary>保存建立 Dashboard response 使用的單一本地期間彙總。</summary>
internal sealed record DashboardPeriodAggregate(
    decimal TotalWithdrawals,
    int WithdrawalCount,
    decimal TotalExpenses,
    int ExpenseCount,
    decimal InstallmentDueAmount,
    int InstallmentDuePaymentCount);
