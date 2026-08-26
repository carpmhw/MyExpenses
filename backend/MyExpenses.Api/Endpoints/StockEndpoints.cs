using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class StockEndpoints
{
    private static readonly HashSet<string> ProtectedStockUpdateFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Symbol",
        "Broker",
        "InstrumentType",
        "Shares",
        "BuyPrice",
    };

    public static void MapStockEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/stocks");

        group.MapGet("/lookup", async (
            string symbol,
            [FromServices] IOfficialMarketCatalogService catalogService,
            CancellationToken cancellationToken) =>
        {
            var resolution = await catalogService.LookupAsync(symbol, cancellationToken);
            var price = resolution.Record?.Price is > 0m
                ? resolution.Record.Price
                : null;
            return Results.Ok(new StockLookupResponse(
                resolution.Market == StockMarket.Unknown ? null : resolution.Record?.Name,
                price,
                resolution.Market,
                resolution.Code));
        });

        group.MapGet("/", async (int page, int pageSize, string? symbol, string? broker, bool? includeClosed, AppDbContext db) =>
            Results.Ok(await ListStocksAsync(page, pageSize, db, symbol, broker, includeClosed ?? false)));

        group.MapGet("/options", async (bool? includeClosed, AppDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(await ListStockOptionsAsync(includeClosed ?? false, db, cancellationToken)));

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
            await db.Stocks.FindAsync(id) is Stock s ? Results.Ok(s) : Results.NotFound());

        group.MapPut("/{id:int}", async (int id, UpdateStockMetadataRequest input, AppDbContext db) =>
        {
            if (!IsSupportedMarket(input.Market))
                return Results.BadRequest("交易市場無效");

            var stock = await db.Stocks.FindAsync(id);
            if (stock is null) return Results.NotFound();

            var hasLedgerHistory = await db.StockTransactions.AnyAsync(transaction => transaction.StockId == id);
            if (hasLedgerHistory)
            {
                if (input.ExtraProperties?.Keys.Any(ProtectedStockUpdateFields.Contains) == true)
                {
                    return Results.Conflict(new StockLedgerErrorResponse(
                        "LedgerManagedFieldsReadOnly",
                        "已有 Ledger 的股票不可直接修改身份與部位欄位",
                        null));
                }

                if (input.Market != stock.Market
                    && (stock.Market != StockMarket.Unknown
                        || input.Market is not (StockMarket.Twse or StockMarket.Tpex)))
                {
                    return Results.Conflict(new StockLedgerErrorResponse(
                        "LedgerManagedIdentityReadOnly",
                        "已有 Ledger 的股票身分不可直接修改",
                        null));
                }

                stock.Name = input.Name;
                stock.Market = input.Market;
                stock.CurrentPrice = input.CurrentPrice;
                stock.LastPriceUpdate = input.LastPriceUpdate;
                await db.SaveChangesAsync();
                return Results.Ok(stock);
            }

            stock.Name = input.Name;
            stock.Market = input.Market;
            stock.CurrentPrice = input.CurrentPrice;
            stock.LastPriceUpdate = input.LastPriceUpdate;

            await db.SaveChangesAsync();
            return Results.Ok(stock);
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var stock = await db.Stocks.FindAsync(id);
            if (stock is null) return Results.NotFound();

            if (await db.StockTransactions.AnyAsync(transaction => transaction.StockId == id))
            {
                return Results.Conflict(new StockLedgerErrorResponse(
                    "StockHasLedgerHistory",
                    "已有 Ledger 歷史的股票不可刪除",
                    null));
            }

            db.Stocks.Remove(stock);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    /// <summary>Returns paginated stocks with filters and all-holding valuation totals calculated before pagination.</summary>
    public static async Task<StockListResponse> ListStocksAsync(
        int page,
        int pageSize,
        AppDbContext db,
        string? symbol = null,
        string? broker = null,
        bool includeClosed = false)
    {
        page = PaginationPolicy.NormalizePage(page);
        pageSize = PaginationPolicy.NormalizePageSize(pageSize);

        var query = db.Stocks.AsQueryable();
        if (!includeClosed)
        {
            query = query.Where(stock => stock.Shares > 0m);
        }
        var trimmedSymbol = symbol?.Trim();
        if (!string.IsNullOrEmpty(trimmedSymbol))
        {
            query = query.Where(s => s.Symbol.Contains(trimmedSymbol));
        }

        var trimmedBroker = broker?.Trim();
        if (!string.IsNullOrEmpty(trimmedBroker))
        {
            query = query.Where(s => s.Broker != null && s.Broker.Contains(trimmedBroker));
        }

        var total = await query.CountAsync();
        var allStocks = await query.OrderBy(s => s.Id).ToListAsync();
        var ledgerStockIds = await db.StockTransactions
            .Where(transaction => allStocks.Select(stock => stock.Id).Contains(transaction.StockId))
            .Select(transaction => transaction.StockId)
            .Distinct()
            .ToListAsync();
        var allItems = allStocks
            .Select(stock => ToStockListItem(stock, ledgerStockIds.Contains(stock.Id)))
            .ToList();
        var items = allItems
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new StockListResponse(
            items,
            total,
            page,
            pageSize,
            allItems.Sum(s => s.EstimatedNetSellValue),
            allItems.Sum(s => s.EstimatedGainLoss));
    }

    /// <summary>回傳不受持股列表分頁限制的輕量交易股票 options。</summary>
    public static async Task<IReadOnlyList<StockOptionResponse>> ListStockOptionsAsync(
        bool includeClosed,
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        var query = db.Stocks.AsNoTracking();
        if (!includeClosed)
            query = query.Where(stock => stock.Shares > 0m);

        var stocks = await query
            .OrderBy(stock => stock.Symbol)
            .ThenBy(stock => stock.Broker ?? string.Empty)
            .ThenBy(stock => stock.Name)
            .ThenBy(stock => stock.Id)
            .ToListAsync(cancellationToken);
        var stockIds = stocks.Select(stock => stock.Id).ToArray();
        var ledgerStockIds = stockIds.Length == 0
            ? []
            : await db.StockTransactions
                .AsNoTracking()
                .Where(transaction => stockIds.Contains(transaction.StockId))
                .Select(transaction => transaction.StockId)
                .Distinct()
                .ToListAsync(cancellationToken);
        var ledgerIdSet = ledgerStockIds.ToHashSet();

        return stocks
            .Select(stock => new StockOptionResponse(
                stock.Id,
                stock.Name,
                stock.Symbol,
                stock.Broker,
                stock.Shares,
                ledgerIdSet.Contains(stock.Id)))
            .ToList();
    }

    /// <summary>Maps a stock entity to an API row that includes estimated valuation fields.</summary>
    private static StockListItem ToStockListItem(Stock stock, bool hasLedger)
    {
        var valuation = StockValuationCalculator.Calculate(stock);
        return new StockListItem(
            stock.Id,
            stock.Name,
            stock.Symbol,
            stock.Market,
            stock.InstrumentType,
            stock.Shares,
            stock.BuyPrice,
            stock.CurrentPrice,
            stock.Broker,
            stock.LastPriceUpdate,
            valuation.GrossMarketValue,
            valuation.BuyCommission,
            valuation.SellCommission,
            valuation.SecuritiesTransactionTax,
            valuation.EstimatedNetSellValue,
            valuation.EstimatedGainLoss,
            hasLedger);
    }

    /// <summary>限制股票 API 只接受已定義的交易市場 enum。</summary>
    private static bool IsSupportedMarket(StockMarket market)
        => market is StockMarket.Unknown or StockMarket.Twse or StockMarket.Tpex;

}

public sealed record StockListResponse(
    IReadOnlyList<StockListItem> Items,
    int Total,
    int Page,
    int PageSize,
    decimal TotalEstimatedNetSellValue,
    decimal TotalEstimatedGainLoss);

/// <summary>提供股票 lookup 的安全市場、名稱、價格與結果碼。</summary>
public sealed record StockLookupResponse(
    string? Name,
    decimal? CurrentPrice,
    StockMarket Market,
    string ResultCode);

/// <summary>描述股票 metadata 更新可公開接受的欄位。</summary>
public sealed record UpdateStockMetadataRequest(
    string Name,
    decimal CurrentPrice,
    DateTime? LastPriceUpdate,
    StockMarket Market)
{
    /// <summary>保留未屬於公開 contract 的欄位，讓 Ledger guard 能回傳 typed conflict。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraProperties { get; init; }
}

public sealed record StockListItem(
    int Id,
    string Name,
    string Symbol,
    StockMarket Market,
    StockInstrumentType InstrumentType,
    decimal Shares,
    decimal BuyPrice,
    decimal CurrentPrice,
    string? Broker,
    DateTime? LastPriceUpdate,
    decimal GrossMarketValue,
    decimal BuyCommission,
    decimal SellCommission,
    decimal SecuritiesTransactionTax,
    decimal EstimatedNetSellValue,
    decimal EstimatedGainLoss,
    bool HasLedger = false);

/// <summary>描述交易 selector 所需的輕量股票識別與目前部位資訊。</summary>
public sealed record StockOptionResponse(
    int Id,
    string Name,
    string Symbol,
    string? Broker,
    decimal Shares,
    bool HasLedger);
