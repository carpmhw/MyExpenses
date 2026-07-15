using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class StockEndpoints
{
    private static readonly ConcurrentDictionary<string, (string Name, decimal? CurrentPrice)> _stockCache = new();
    private static DateTime _lastFetch = DateTime.MinValue;
    private static readonly SemaphoreSlim _fetchLock = new(1, 1);

    public static void MapStockEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/stocks");

        group.MapGet("/lookup", async (string symbol, IHttpClientFactory httpFactory) =>
        {
            if (string.IsNullOrWhiteSpace(symbol)) return Results.Ok(new { name = (string?)null });

            var key = symbol.Trim().ToUpperInvariant();

            if (_stockCache.TryGetValue(key, out var cached))
                return Results.Ok(new { name = cached.Name, currentPrice = cached.CurrentPrice });

            // Refresh cache from TWSE if stale (once per hour)
            if (DateTime.UtcNow - _lastFetch > TimeSpan.FromHours(1))
            {
                await _fetchLock.WaitAsync();
                try
                {
                    if (DateTime.UtcNow - _lastFetch > TimeSpan.FromHours(1))
                    {
                        var http = httpFactory.CreateClient();
                        var response = await http.GetStringAsync("https://openapi.twse.com.tw/v1/exchangeReport/STOCK_DAY_ALL");
                        using var doc = JsonDocument.Parse(response);
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var code = item.GetProperty("Code").GetString();
                            var name = item.GetProperty("Name").GetString();
                            decimal? closingPrice = null;
                            if (item.TryGetProperty("ClosingPrice", out var cp) && cp.ValueKind == JsonValueKind.String)
                            {
                                var cpStr = cp.GetString();
                                if (decimal.TryParse(cpStr, out var parsed))
                                    closingPrice = parsed;
                            }
                            if (code != null && name != null)
                                _stockCache[code.Trim().ToUpperInvariant()] = (name, closingPrice);
                        }
                        _lastFetch = DateTime.UtcNow;
                    }
                }
                catch
                {
                    // TWSE API unavailable, proceed with existing cache
                }
                finally
                {
                    _fetchLock.Release();
                }

                if (_stockCache.TryGetValue(key, out var refreshed))
                    return Results.Ok(new { name = refreshed.Name, currentPrice = refreshed.CurrentPrice });
            }

            return Results.Ok(new { name = (string?)null });
        });

        group.MapGet("/", async (int page, int pageSize, string? symbol, string? broker, AppDbContext db) =>
            Results.Ok(await ListStocksAsync(page, pageSize, db, symbol, broker)));

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
            await db.Stocks.FindAsync(id) is Stock s ? Results.Ok(s) : Results.NotFound());

        group.MapPost("/", async (Stock stock, AppDbContext db) =>
        {
            db.Stocks.Add(stock);
            await db.SaveChangesAsync();
            return Results.Created($"/api/stocks/{stock.Id}", stock);
        });

        group.MapPut("/{id:int}", async (int id, Stock input, AppDbContext db) =>
        {
            var stock = await db.Stocks.FindAsync(id);
            if (stock is null) return Results.NotFound();

            stock.Name = input.Name;
            stock.Symbol = input.Symbol;
            stock.InstrumentType = input.InstrumentType;
            stock.Shares = input.Shares;
            stock.BuyPrice = input.BuyPrice;
            stock.CurrentPrice = input.CurrentPrice;
            stock.Broker = input.Broker;
            if (input.LastPriceUpdate.HasValue)
                stock.LastPriceUpdate = input.LastPriceUpdate;

            await db.SaveChangesAsync();
            return Results.Ok(stock);
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var stock = await db.Stocks.FindAsync(id);
            if (stock is null) return Results.NotFound();

            db.Stocks.Remove(stock);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    /// <summary>Returns paginated stocks with filters and all-holding valuation totals calculated before pagination.</summary>
    public static async Task<StockListResponse> ListStocksAsync(int page, int pageSize, AppDbContext db, string? symbol = null, string? broker = null)
    {
        page = PaginationPolicy.NormalizePage(page);
        pageSize = PaginationPolicy.NormalizePageSize(pageSize);

        var query = db.Stocks.AsQueryable();
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
        var allItems = allStocks.Select(ToStockListItem).ToList();
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

    /// <summary>Maps a stock entity to an API row that includes estimated valuation fields.</summary>
    private static StockListItem ToStockListItem(Stock stock)
    {
        var valuation = StockValuationCalculator.Calculate(stock);
        return new StockListItem(
            stock.Id,
            stock.Name,
            stock.Symbol,
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
            valuation.EstimatedGainLoss);
    }

}

public sealed record StockListResponse(
    IReadOnlyList<StockListItem> Items,
    int Total,
    int Page,
    int PageSize,
    decimal TotalEstimatedNetSellValue,
    decimal TotalEstimatedGainLoss);

public sealed record StockListItem(
    int Id,
    string Name,
    string Symbol,
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
    decimal EstimatedGainLoss);
