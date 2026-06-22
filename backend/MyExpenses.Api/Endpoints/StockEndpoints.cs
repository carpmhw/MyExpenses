using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Endpoints;

public static class StockEndpoints
{
    private static readonly ConcurrentDictionary<string, (string Name, decimal? CurrentPrice)> _stockCache = new();
    private static DateTime _lastFetch = DateTime.MinValue;
    private static readonly SemaphoreSlim _fetchLock = new(1, 1);

    public static void MapStockEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/stocks");

        group.MapGet("/lookup", async (string symbol, IHttpClientFactory httpFactory, AppDbContext db) =>
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

                        await UpdateMatchingStocksFromCache(db);
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

        group.MapGet("/", async (int page, int pageSize, AppDbContext db) =>
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 20;

            var query = db.Stocks.AsQueryable();
            var total = await query.CountAsync();
            var items = await query
                .OrderBy(s => s.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Results.Ok(new { items, total, page, pageSize });
        });

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

    private static async Task UpdateMatchingStocksFromCache(AppDbContext db)
    {
        var stocks = await db.Stocks.ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var stock in stocks)
        {
            var key = stock.Symbol.Trim().ToUpperInvariant();
            if (_stockCache.TryGetValue(key, out var cached) && cached.CurrentPrice.HasValue)
            {
                stock.CurrentPrice = cached.CurrentPrice.Value;
                stock.LastPriceUpdate = now;
            }
        }
        await db.SaveChangesAsync();
    }
}
