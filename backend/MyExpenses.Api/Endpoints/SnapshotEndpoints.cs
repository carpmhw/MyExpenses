using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Endpoints;

public static class SnapshotEndpoints
{
    public static void MapSnapshotEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/snapshots");

        group.MapPost("/", async (AppDbContext db) =>
        {
            var bankAccounts = await db.BankAccounts.ToListAsync();
            var stocks = await db.Stocks.ToListAsync();

            var bankDetails = bankAccounts.Select(b => new BankDetail
            {
                BankName = b.BankName,
                AccountNumber = b.AccountNumber,
                AccountType = b.AccountType,
                Balance = b.Balance,
            }).ToList();

            var totalBankBalance = bankDetails.Sum(b => b.Balance);

            var stockDetails = stocks.Select(s => new StockDetail
            {
                Name = s.Name,
                Symbol = s.Symbol,
                Shares = s.Shares,
                BuyPrice = s.BuyPrice,
                CurrentPrice = s.CurrentPrice,
                MarketValue = s.CurrentPrice * s.Shares,
                GainLoss = (s.CurrentPrice - s.BuyPrice) * s.Shares,
            }).ToList();

            var totalStockValue = stockDetails.Sum(s => s.MarketValue);
            var totalStockCost = stockDetails.Sum(s => s.BuyPrice * s.Shares);
            var totalNetWorth = totalBankBalance + totalStockValue;

            var now = DateTime.UtcNow;
            var name = $"快照 {now:yyyy-MM-dd HH:mm}";

            var snapshot = new SnapshotBatch
            {
                Name = name,
                SnapshotDate = now,
                Notes = null,
                TotalNetWorth = totalNetWorth,
                TotalBankBalance = totalBankBalance,
                TotalStockValue = totalStockValue,
                TotalStockCost = totalStockCost,
                BankDetails = bankDetails,
                StockDetails = stockDetails,
            };

            db.SnapshotBatches.Add(snapshot);
            await db.SaveChangesAsync();

            return Results.Created($"/api/snapshots/{snapshot.Id}", snapshot);
        });

        group.MapGet("/", async (int? page, int? pageSize, AppDbContext db) =>
        {
            var query = db.SnapshotBatches.AsQueryable();

            var total = await query.CountAsync();
            var p = page ?? 1;
            var ps = pageSize ?? 20;

            var items = await query
                .OrderByDescending(s => s.SnapshotDate)
                .Skip((p - 1) * ps)
                .Take(ps)
                .ToListAsync();

            return Results.Ok(new { items, total, page = p, pageSize = ps });
        });

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
        {
            var snapshot = await db.SnapshotBatches.FindAsync(id);
            return snapshot is not null ? Results.Ok(snapshot) : Results.NotFound();
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var snapshot = await db.SnapshotBatches.FindAsync(id);
            if (snapshot is null) return Results.NotFound();

            db.SnapshotBatches.Remove(snapshot);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        group.MapGet("/{id1:int}/compare/{id2:int}", async (int id1, int id2, AppDbContext db) =>
        {
            var s1 = await db.SnapshotBatches.FindAsync(id1);
            var s2 = await db.SnapshotBatches.FindAsync(id2);
            if (s1 is null || s2 is null) return Results.NotFound();

            var bankDetails = s1.BankDetails.Select(b1 =>
            {
                var b2 = s2.BankDetails.FirstOrDefault(b =>
                    b.BankName == b1.BankName && b.AccountNumber == b1.AccountNumber);
                var oldBal = b1.Balance;
                var newBal = b2?.Balance ?? 0;
                var change = newBal - oldBal;
                var changePercent = oldBal != 0 ? Math.Round(change / oldBal * 100, 2) : 0;
                return new
                {
                    bankName = b1.BankName,
                    accountNumber = b1.AccountNumber,
                    oldBalance = oldBal,
                    newBalance = newBal,
                    change,
                    changePercent,
                };
            }).ToList();

            var stockDetails = s1.StockDetails.Select(s1d =>
            {
                var s2d = s2.StockDetails.FirstOrDefault(s =>
                    s.Symbol == s1d.Symbol);
                var oldVal = s1d.MarketValue;
                var newVal = s2d?.MarketValue ?? 0;
                var change = newVal - oldVal;
                var changePercent = oldVal != 0 ? Math.Round(change / oldVal * 100, 2) : 0;
                return new
                {
                    name = s1d.Name,
                    symbol = s1d.Symbol,
                    oldShares = s1d.Shares,
                    newShares = s2d?.Shares ?? 0,
                    oldPrice = s1d.CurrentPrice,
                    newPrice = s2d?.CurrentPrice ?? 0,
                    oldValue = oldVal,
                    newValue = newVal,
                    change,
                    changePercent,
                };
            }).ToList();

            var nwChange = s2.TotalNetWorth - s1.TotalNetWorth;
            var nwChangePercent = s1.TotalNetWorth != 0
                ? Math.Round(nwChange / s1.TotalNetWorth * 100, 2) : 0;

            var bbChange = s2.TotalBankBalance - s1.TotalBankBalance;
            var bbChangePercent = s1.TotalBankBalance != 0
                ? Math.Round(bbChange / s1.TotalBankBalance * 100, 2) : 0;

            var svChange = s2.TotalStockValue - s1.TotalStockValue;
            var svChangePercent = s1.TotalStockValue != 0
                ? Math.Round(svChange / s1.TotalStockValue * 100, 2) : 0;

            return Results.Ok(new
            {
                snapshot1 = new
                {
                    id = s1.Id,
                    date = s1.SnapshotDate,
                    name = s1.Name,
                    totalNetWorth = s1.TotalNetWorth,
                    totalBankBalance = s1.TotalBankBalance,
                    totalStockValue = s1.TotalStockValue,
                    totalStockCost = s1.TotalStockCost,
                },
                snapshot2 = new
                {
                    id = s2.Id,
                    date = s2.SnapshotDate,
                    name = s2.Name,
                    totalNetWorth = s2.TotalNetWorth,
                    totalBankBalance = s2.TotalBankBalance,
                    totalStockValue = s2.TotalStockValue,
                    totalStockCost = s2.TotalStockCost,
                },
                differences = new
                {
                    netWorth = new
                    {
                        old = s1.TotalNetWorth,
                        @new = s2.TotalNetWorth,
                        change = nwChange,
                        changePercent = nwChangePercent,
                    },
                    bankBalance = new
                    {
                        old = s1.TotalBankBalance,
                        @new = s2.TotalBankBalance,
                        change = bbChange,
                        changePercent = bbChangePercent,
                    },
                    stockValue = new
                    {
                        old = s1.TotalStockValue,
                        @new = s2.TotalStockValue,
                        change = svChange,
                        changePercent = svChangePercent,
                    },
                    bankDetails,
                    stockDetails,
                },
            });
        });

        group.MapGet("/trend", async (AppDbContext db) =>
        {
            var snapshots = await db.SnapshotBatches
                .OrderBy(s => s.SnapshotDate)
                .Select(s => new
                {
                    id = s.Id,
                    date = s.SnapshotDate,
                    name = s.Name,
                    totalNetWorth = s.TotalNetWorth,
                    totalBankBalance = s.TotalBankBalance,
                    totalStockValue = s.TotalStockValue,
                    totalStockCost = s.TotalStockCost,
                })
                .ToListAsync();

            return Results.Ok(snapshots);
        });

        group.MapGet("/auto-schedule", async (AppDbContext db) =>
        {
            var config = await db.AutoSnapshotConfigs.FirstOrDefaultAsync();
            return config is not null ? Results.Ok(config) : Results.NotFound();
        });

        group.MapPut("/auto-schedule", async (AutoSnapshotConfig input, AppDbContext db) =>
        {
            var config = await db.AutoSnapshotConfigs.FirstOrDefaultAsync();
            if (config is null) return Results.NotFound();

            config.IsEnabled = input.IsEnabled;
            config.Frequency = input.Frequency;
            config.DayOfWeek = input.DayOfWeek;
            config.DayOfMonth = input.DayOfMonth;
            config.TimeOfDay = input.TimeOfDay;

            await db.SaveChangesAsync();
            return Results.Ok(config);
        });
    }
}
