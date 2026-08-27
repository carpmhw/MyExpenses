using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

/// <summary>提供股票 Ledger 查詢、交易 mutation、初始化與 atomic position API。</summary>
public static class StockTransactionEndpoints
{
    /// <summary>註冊股票 Ledger 的所有 HTTP endpoints。</summary>
    public static void MapStockTransactionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/stocks/ledger");
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapPost("/transactions", CreateAsync);
        group.MapPut("/{id:int}", UpdateAsync);
        group.MapPut("/transactions/{id:int}", UpdateAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        group.MapDelete("/transactions/{id:int}", DeleteAsync);
        group.MapPost("/initialize", InitializeAsync);
        group.MapPost("/estimate-costs", EstimateCostsAsync);

        app.MapPost("/api/stocks/positions", CreatePositionAsync);
        app.MapPost("/api/stocks/ledger/position", CreatePositionAsync);
    }

    /// <summary>依股票、型別、日期與分頁條件回傳固定倒序的 Ledger rows。</summary>
    private static async Task<IResult> ListAsync(
        int? stockId,
        StockTransactionType? type,
        DateOnly? dateStart,
        DateOnly? dateEnd,
        int? page,
        int? pageSize,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (dateStart.HasValue && dateEnd.HasValue && dateEnd < dateStart)
            return Error("InvalidDateRange", "交易日期範圍無效", StatusCodes.Status400BadRequest);

        var filteredQuery = db.StockTransactions
            .AsNoTracking()
            .Include(transaction => transaction.Stock)
            .AsQueryable();
        filteredQuery = ApplyFilters(filteredQuery, stockId, type, dateStart, dateEnd);
        var filtered = await filteredQuery
            .OrderByDescending(transaction => transaction.TradeDate)
            .ThenByDescending(transaction => transaction.Sequence)
            .ThenByDescending(transaction => transaction.Id)
            .ToListAsync(cancellationToken);
        var total = filtered.Count;
        var normalizedPage = PaginationPolicy.NormalizePage(page ?? 1);
        var normalizedPageSize = PaginationPolicy.NormalizePageSize(pageSize ?? 20);
        var stockIds = filtered.Select(transaction => transaction.StockId).Distinct().ToArray();
        var allTransactions = stockIds.Length == 0
            ? []
            : await db.StockTransactions
                .AsNoTracking()
                .Include(transaction => transaction.Stock)
                .Where(transaction => stockIds.Contains(transaction.StockId))
                .ToListAsync(cancellationToken);
        var rows = BuildRows(allTransactions)
            .ToDictionary(row => row.Id);
        var items = filtered
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .Select(transaction => rows[transaction.Id])
            .ToList();

        return Results.Ok(new StockTransactionListResponse(
            items,
            total,
            normalizedPage,
            normalizedPageSize));
    }

    /// <summary>取得單筆交易及其完整歷史 replay 衍生欄位。</summary>
    private static async Task<IResult> GetAsync(
        int id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var transaction = await db.StockTransactions
            .AsNoTracking()
            .Include(item => item.Stock)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (transaction is null)
            return Results.NotFound();

        var allTransactions = await db.StockTransactions
            .AsNoTracking()
            .Include(item => item.Stock)
            .Where(item => item.StockId == transaction.StockId)
            .ToListAsync(cancellationToken);
        var row = BuildRows(allTransactions).Single(item => item.Id == id);
        return Results.Ok(row);
    }

    /// <summary>建立 Buy、Sell、Dividend 或 StockDividend，並拒絕一般 endpoint 直接建立 OpeningBalance。</summary>
    private static async Task<IResult> CreateAsync(
        CreateStockTransactionRequest request,
        StockLedgerService service,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (request.Type == StockTransactionType.OpeningBalance)
            return Error("OpeningBalanceNotAllowed", "一般交易 endpoint 不允許建立 OpeningBalance", StatusCodes.Status400BadRequest);

        try
        {
            var result = await service.CreateTransactionAsync(
                request.StockId,
                request.ToCommand(),
                cancellationToken);
            var stock = await db.Stocks.AsNoTracking().SingleAsync(
                item => item.Id == result.Transaction.StockId,
                cancellationToken);
            return Results.Created(
                $"/api/stocks/ledger/{result.Transaction.Id}",
                ToRow(result.EntryResult, stock, result.Transaction));
        }
        catch (Exception exception)
        {
            return MapException(exception);
        }
    }

    /// <summary>修改交易並讓 service 以完整 replay 驗證歷史。</summary>
    private static async Task<IResult> UpdateAsync(
        int id,
        UpdateStockTransactionRequest request,
        StockLedgerService service,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.UpdateTransactionAsync(
                id,
                request.StockId,
                request.ToCommand(),
                cancellationToken);
            var stock = await db.Stocks.AsNoTracking().SingleAsync(
                item => item.Id == result.Transaction.StockId,
                cancellationToken);
            return Results.Ok(ToRow(result.EntryResult, stock, result.Transaction));
        }
        catch (Exception exception)
        {
            return MapException(exception);
        }
    }

    /// <summary>刪除交易並在 service transaction 內重播剩餘歷史。</summary>
    private static async Task<IResult> DeleteAsync(
        int id,
        StockLedgerService service,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.DeleteTransactionAsync(id, cancellationToken);
            return Results.NoContent();
        }
        catch (Exception exception)
        {
            return MapException(exception);
        }
    }

    /// <summary>執行既有持股的整批 atomic synthetic opening 初始化。</summary>
    private static async Task<IResult> InitializeAsync(
        StockLedgerInitializationRequest request,
        StockLedgerService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await service.InitializeAsync(
                new StockLedgerInitializationCommand(request.BaselineDate),
                cancellationToken);
            return response.BlockingCount > 0
                ? Results.UnprocessableEntity(response)
                : Results.Ok(response);
        }
        catch (Exception exception)
        {
            return MapException(exception);
        }
    }

    /// <summary>建立新股票與第一筆 Buy 或 OpeningBalance 的單一 atomic command。</summary>
    private static async Task<IResult> CreatePositionAsync(
        StockPositionCommand command,
        StockLedgerService service,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedMarket(command.Market))
            return Error("InvalidMarket", "交易市場無效", StatusCodes.Status400BadRequest);

        try
        {
            var result = await service.CreatePositionAsync(command, cancellationToken);
            return Results.Created(
                $"/api/stocks/{result.Stock.Id}",
                new StockPositionResponse(result.Stock, result.Transaction, result.Replay));
        }
        catch (Exception exception)
        {
            return MapException(exception);
        }
    }

    /// <summary>查詢股票主檔並回傳不寫入資料庫的交易費稅估算。</summary>
    private static async Task<IResult> EstimateCostsAsync(
        HttpRequest httpRequest,
        AppDbContext db,
        Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        StockTransactionCostEstimateRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<StockTransactionCostEstimateRequest>(
                httpRequest.Body,
                jsonOptions.Value.SerializerOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return InvalidEstimateInput("InvalidRequestBody");
        }
        catch (OverflowException)
        {
            return InvalidEstimateInput("InvalidRequestBody");
        }

        if (request is null)
            return InvalidEstimateInput("InvalidRequestBody");
        if (request.StockId is null)
            return InvalidEstimateInput("MissingStockId");
        if (request.StockId <= 0)
            return InvalidEstimateInput("InvalidStockId");
        if (request.Type is null)
            return InvalidEstimateInput("MissingTransactionType");

        var stock = await db.Stocks
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == request.StockId.Value, cancellationToken);
        if (stock is null)
            return Error("NotFound", "股票不存在", StatusCodes.Status404NotFound);

        var result = StockTransactionCostEstimator.Estimate(
            request.Type.Value,
            request.Shares,
            request.Price,
            stock.Market,
            stock.InstrumentType);

        return result.Status switch
        {
            StockTransactionCostEstimationStatus.Success when result.Estimate is not null
                => Results.Ok(result.Estimate),
            StockTransactionCostEstimationStatus.InvalidInput
                => InvalidEstimateInput(result.Reason ?? "InvalidInput"),
            StockTransactionCostEstimationStatus.Unsupported
                => Error(
                    "TransactionCostEstimationUnsupported",
                    "此股票交易不支援自動費稅估算",
                    StatusCodes.Status422UnprocessableEntity,
                    new { reason = result.Reason }),
            _ => Error(
                "InvalidTransactionCostEstimateInput",
                "交易費稅估算輸入無效",
                StatusCodes.Status400BadRequest),
        };
    }

    /// <summary>建立交易費稅估算專用的 typed invalid input response。</summary>
    private static IResult InvalidEstimateInput(string reason)
        => Error(
            "InvalidTransactionCostEstimateInput",
            "交易費稅估算輸入無效",
            StatusCodes.Status400BadRequest,
            new { reason });

    /// <summary>套用交易 list 的所有篩選條件。</summary>
    private static IQueryable<StockTransaction> ApplyFilters(
        IQueryable<StockTransaction> query,
        int? stockId,
        StockTransactionType? type,
        DateOnly? dateStart,
        DateOnly? dateEnd)
    {
        if (stockId.HasValue)
            query = query.Where(transaction => transaction.StockId == stockId.Value);
        if (type.HasValue)
            query = query.Where(transaction => transaction.Type == type.Value);
        if (dateStart.HasValue)
            query = query.Where(transaction => transaction.TradeDate >= dateStart.Value);
        if (dateEnd.HasValue)
            query = query.Where(transaction => transaction.TradeDate <= dateEnd.Value);
        return query;
    }

    /// <summary>對每一檔股票執行完整 replay 並建立未持久化的 API rows。</summary>
    private static IReadOnlyList<StockTransactionListItem> BuildRows(
        IReadOnlyList<StockTransaction> transactions)
    {
        var rows = new List<StockTransactionListItem>(transactions.Count);
        foreach (var group in transactions.GroupBy(transaction => transaction.StockId))
        {
            var ordered = group
                .OrderBy(transaction => transaction.TradeDate)
                .ThenBy(transaction => transaction.Sequence)
                .ThenBy(transaction => transaction.Id)
                .ToList();
            var replay = StockLedgerCalculator.Replay(ordered);
            var resultById = replay.Entries.ToDictionary(entry => entry.Entry.Id);
            foreach (var transaction in group)
            {
                rows.Add(ToRow(
                    resultById[transaction.Id],
                    transaction.Stock,
                    transaction));
            }
        }

        return rows;
    }

    /// <summary>將 calculator entry result 與股票身分組合成 API contract。</summary>
    private static StockTransactionListItem ToRow(
        StockLedgerEntryResult result,
        Stock stock,
        StockTransaction transaction)
        => new(
            transaction.Id,
            transaction.StockId,
            stock.Name,
            stock.Symbol,
            stock.Market,
            stock.Broker,
            transaction.Type,
            transaction.TradeDate,
            transaction.Sequence,
            transaction.Shares,
            transaction.Price,
            transaction.Fee,
            transaction.Tax,
            transaction.CashAmount,
            transaction.OpeningMarketValue,
            transaction.Notes,
            result.GrossAmount,
            result.NetCashFlow,
            result.AllocatedCostBasis,
            result.RealizedGainLoss,
            result.NetDividend,
            result.RemainingShares,
            result.RemainingCostBasis,
            result.ExecutionAveragePrice);

    /// <summary>將 service 例外轉成不暴露 stack trace 的 typed HTTP error。</summary>
    private static IResult MapException(Exception exception)
    {
        if (exception is StockLedgerNotFoundException)
            return Error("NotFound", exception.Message, StatusCodes.Status404NotFound);
        if (exception is StockLedgerException ledgerException)
        {
            var statusCode = ledgerException.FailureCode == StockLedgerFailureCode.InsufficientShares
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return Error(ledgerException.Code, ledgerException.Message, statusCode, new
            {
                availableShares = ledgerException.AvailableShares,
                requestedShares = ledgerException.RequestedShares,
                tradeDate = ledgerException.TradeDate,
            });
        }

        return Error("LedgerMutationFailed", "股票交易無法完成", StatusCodes.Status409Conflict);
    }

    /// <summary>建立統一格式的安全錯誤 response。</summary>
    private static IResult Error(
        string code,
        string message,
        int statusCode,
        object? details = null)
        => Results.Json(new StockLedgerErrorResponse(code, message, details), statusCode: statusCode);

    /// <summary>限制 atomic position API 只接受已定義的交易市場 enum。</summary>
    private static bool IsSupportedMarket(StockMarket market)
        => market is StockMarket.Unknown or StockMarket.Twse or StockMarket.Tpex;
}

/// <summary>建立股票交易 endpoint 的 request contract。</summary>
public sealed record CreateStockTransactionRequest(
    int StockId,
    StockTransactionType Type,
    DateOnly TradeDate,
    decimal? Shares,
    decimal? Price,
    decimal Fee = 0m,
    decimal Tax = 0m,
    decimal? CashAmount = null,
    decimal? OpeningMarketValue = null,
    string? Notes = null)
{
    /// <summary>將 HTTP request 轉換成純 service command。</summary>
    public StockLedgerTransactionCommand ToCommand()
        => new(Type, TradeDate, Shares, Price, Fee, Tax, CashAmount, OpeningMarketValue, Notes);
}

/// <summary>修改股票交易 endpoint 的 request contract。</summary>
public sealed record UpdateStockTransactionRequest(
    int StockId,
    StockTransactionType Type,
    DateOnly TradeDate,
    decimal? Shares,
    decimal? Price,
    decimal Fee = 0m,
    decimal Tax = 0m,
    decimal? CashAmount = null,
    decimal? OpeningMarketValue = null,
    string? Notes = null)
{
    /// <summary>將 HTTP request 轉換成純 service command。</summary>
    public StockLedgerTransactionCommand ToCommand()
        => new(Type, TradeDate, Shares, Price, Fee, Tax, CashAmount, OpeningMarketValue, Notes);
}

/// <summary>描述 Ledger 初始化 HTTP request。</summary>
public sealed record StockLedgerInitializationRequest(DateOnly BaselineDate);

/// <summary>描述股票交易費稅估算 HTTP request。</summary>
public sealed record StockTransactionCostEstimateRequest(
    int? StockId,
    StockTransactionType? Type,
    decimal? Shares,
    decimal? Price);

/// <summary>描述單一交易及其 replay 衍生欄位。</summary>
public sealed record StockTransactionListItem(
    int Id,
    int StockId,
    string StockName,
    string Symbol,
    StockMarket Market,
    string? Broker,
    StockTransactionType Type,
    DateOnly TradeDate,
    int Sequence,
    decimal? Shares,
    decimal? Price,
    decimal Fee,
    decimal Tax,
    decimal? CashAmount,
    decimal? OpeningMarketValue,
    string? Notes,
    decimal GrossAmount,
    decimal NetCashFlow,
    decimal? AllocatedCostBasis,
    decimal RealizedGainLoss,
    decimal NetDividend,
    decimal RemainingShares,
    decimal RemainingCostBasis,
    decimal ExecutionAveragePrice);

/// <summary>描述分頁交易列表 response。</summary>
public sealed record StockTransactionListResponse(
    IReadOnlyList<StockTransactionListItem> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>描述新部位 command 的 response。</summary>
public sealed record StockPositionResponse(
    Stock Stock,
    StockTransaction Transaction,
    StockLedgerResult Replay);

/// <summary>描述股票 Ledger API 的安全錯誤。</summary>
public sealed record StockLedgerErrorResponse(
    string Code,
    string Message,
    object? Details);
