using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class SnapshotEndpoints
{
    private const int MaximumSnapshotRangeYears = 5;

    public static void MapSnapshotEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/snapshots");

        group.MapPost("/", async (AppDbContext db) =>
        {
            var snapshot = await CreateSnapshotAsync(db);

            return Results.Created($"/api/snapshots/{snapshot.Id}", snapshot);
        });

        group.MapGet("/", async (int? page, int? pageSize, DateOnly? dateStart, DateOnly? dateEnd, AppDbContext db) =>
        {
            try
            {
                return Results.Ok(await ListSnapshotsAsync(page, pageSize, dateStart, dateEnd, db));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
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

        group.MapGet("/trend", async (DateOnly? dateStart, DateOnly? dateEnd, AppDbContext db) =>
        {
            try
            {
                return Results.Ok(await ListSnapshotTrendAsync(dateStart, dateEnd, db));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
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

    /// <summary>Creates and persists a manual financial snapshot using estimated net sell value for stock holdings.</summary>
    public static async Task<SnapshotBatch> CreateSnapshotAsync(AppDbContext db)
    {
        var bankAccounts = await db.BankAccounts.ToListAsync();
        var stocks = await db.Stocks.ToListAsync();
        var now = DateTime.UtcNow;
        var snapshot = FinancialSnapshotBuilder.Build($"快照 {now:yyyy-MM-dd HH:mm}", null, now, bankAccounts, stocks);

        db.SnapshotBatches.Add(snapshot);
        await db.SaveChangesAsync();

        return snapshot;
    }

    /// <summary>Returns paginated snapshot history filtered by an optional inclusive date range.</summary>
    public static async Task<SnapshotListResponse> ListSnapshotsAsync(int? page, int? pageSize, DateOnly? dateStart, DateOnly? dateEnd, AppDbContext db)
    {
        ValidateDateRange(dateStart, dateEnd);

        var p = page is > 0 ? page.Value : 1;
        var ps = pageSize is > 0 ? pageSize.Value : 20;
        var query = ApplyDateRange(db.SnapshotBatches.AsQueryable(), dateStart, dateEnd);
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(s => s.SnapshotDate)
            .Skip((p - 1) * ps)
            .Take(ps)
            .ToListAsync();

        return new SnapshotListResponse(items, total, p, ps);
    }

    /// <summary>Returns snapshot trend points filtered by an optional inclusive date range in chronological order.</summary>
    public static async Task<IReadOnlyList<SnapshotTrendPoint>> ListSnapshotTrendAsync(DateOnly? dateStart, DateOnly? dateEnd, AppDbContext db)
    {
        ValidateDateRange(dateStart, dateEnd);

        return await ApplyDateRange(db.SnapshotBatches.AsQueryable(), dateStart, dateEnd)
            .OrderBy(s => s.SnapshotDate)
            .Select(s => new SnapshotTrendPoint(
                s.Id,
                s.SnapshotDate,
                s.Name,
                s.TotalNetWorth,
                s.TotalBankBalance,
                s.TotalStockValue,
                s.TotalStockCost))
            .ToListAsync();
    }

    /// <summary>Applies inclusive whole-day date range filters to a snapshot query.</summary>
    private static IQueryable<SnapshotBatch> ApplyDateRange(IQueryable<SnapshotBatch> query, DateOnly? dateStart, DateOnly? dateEnd)
    {
        if (dateStart.HasValue)
        {
            var start = dateStart.Value.ToDateTime(TimeOnly.MinValue);
            query = query.Where(s => s.SnapshotDate >= start);
        }

        if (dateEnd.HasValue)
        {
            var endExclusive = dateEnd.Value.AddDays(1).ToDateTime(TimeOnly.MinValue);
            query = query.Where(s => s.SnapshotDate < endExclusive);
        }

        return query;
    }

    /// <summary>Rejects invalid snapshot date ranges before database queries are executed.</summary>
    private static void ValidateDateRange(DateOnly? dateStart, DateOnly? dateEnd)
    {
        if (dateStart.HasValue && dateEnd.HasValue && dateEnd.Value < dateStart.Value)
        {
            throw new ArgumentException("迄日不能小於起日");
        }

        if (dateStart.HasValue && dateEnd.HasValue && dateStart.Value < dateEnd.Value.AddYears(-MaximumSnapshotRangeYears))
        {
            throw new ArgumentException("日期區間最多只能查詢 5 年");
        }
    }

}

public sealed record SnapshotListResponse(
    IReadOnlyList<SnapshotBatch> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record SnapshotTrendPoint(
    int Id,
    DateTime Date,
    string Name,
    decimal TotalNetWorth,
    decimal TotalBankBalance,
    decimal TotalStockValue,
    decimal TotalStockCost);
