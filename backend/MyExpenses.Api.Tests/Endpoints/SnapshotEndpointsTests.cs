using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class SnapshotEndpointsTests
{
    /// <summary>Verifies snapshot creation stores estimated stock values and includes them in net worth.</summary>
    [Fact]
    public async Task CreateSnapshot_UsesEstimatedNetSellValueForStockTotals()
    {
        await using var db = await CreateDbContextAsync();

        var snapshot = await SnapshotEndpoints.CreateSnapshotAsync(db);

        Assert.Equal(1046432m, snapshot.TotalStockValue);
        Assert.Equal(1047432m, snapshot.TotalNetWorth);

        var stockDetail = Assert.Single(snapshot.StockDetails);
        Assert.Equal(StockInstrumentType.Stock, stockDetail.InstrumentType);
        Assert.Equal(1046432m, stockDetail.MarketValue);
        Assert.Equal(46033m, stockDetail.GainLoss);
    }

    /// <summary>Verifies snapshot list date filtering includes snapshots on both boundary dates.</summary>
    [Fact]
    public async Task ListSnapshots_FiltersDateRangeInclusively()
    {
        await using var db = await CreateSnapshotListDbContextAsync();

        var result = await SnapshotEndpoints.ListSnapshotsAsync(
            page: 1,
            pageSize: 10,
            dateStart: new DateOnly(2026, 1, 1),
            dateEnd: new DateOnly(2026, 12, 31),
            db);

        Assert.Equal(3, result.Total);
        Assert.Equal(new[] { "year-end", "mid-year", "year-start" }, result.Items.Select(s => s.Name).ToArray());
    }

    /// <summary>Verifies filtered snapshot totals are counted before pagination is applied.</summary>
    [Fact]
    public async Task ListSnapshots_CountsFilteredTotalBeforePagination()
    {
        await using var db = await CreateSnapshotListDbContextAsync();

        var result = await SnapshotEndpoints.ListSnapshotsAsync(
            page: 1,
            pageSize: 1,
            dateStart: new DateOnly(2026, 1, 1),
            dateEnd: new DateOnly(2026, 12, 31),
            db);

        Assert.Single(result.Items);
        Assert.Equal(3, result.Total);
    }

    /// <summary>Verifies snapshot trend date filtering returns matching points in chronological order.</summary>
    [Fact]
    public async Task ListSnapshotTrend_FiltersDateRangeAndKeepsChronologicalOrdering()
    {
        await using var db = await CreateSnapshotListDbContextAsync();

        var result = await SnapshotEndpoints.ListSnapshotTrendAsync(
            dateStart: new DateOnly(2026, 1, 1),
            dateEnd: new DateOnly(2026, 12, 31),
            db);

        Assert.Equal(new[] { "year-start", "mid-year", "year-end" }, result.Select(s => s.Name).ToArray());
    }

    /// <summary>Verifies invalid snapshot date ranges are rejected before querying.</summary>
    [Fact]
    public async Task ListSnapshots_RejectsEndDateEarlierThanStartDate()
    {
        await using var db = await CreateSnapshotListDbContextAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => SnapshotEndpoints.ListSnapshotsAsync(
            page: 1,
            pageSize: 10,
            dateStart: new DateOnly(2026, 12, 31),
            dateEnd: new DateOnly(2026, 1, 1),
            db));

        Assert.Equal("迄日不能小於起日", error.Message);
    }

    /// <summary>Verifies invalid snapshot trend date ranges are rejected before querying.</summary>
    [Fact]
    public async Task ListSnapshotTrend_RejectsEndDateEarlierThanStartDate()
    {
        await using var db = await CreateSnapshotListDbContextAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => SnapshotEndpoints.ListSnapshotTrendAsync(
            dateStart: new DateOnly(2026, 12, 31),
            dateEnd: new DateOnly(2026, 1, 1),
            db));

        Assert.Equal("迄日不能小於起日", error.Message);
    }

    /// <summary>Verifies snapshot list rejects date ranges longer than five calendar years.</summary>
    [Fact]
    public async Task ListSnapshotsAsync_RejectsDateRangesLongerThanFiveYears()
    {
        await using var db = await CreateSnapshotListDbContextAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => SnapshotEndpoints.ListSnapshotsAsync(
            page: 1,
            pageSize: 20,
            dateStart: new DateOnly(2020, 6, 27),
            dateEnd: new DateOnly(2026, 6, 28),
            db));

        Assert.Equal("日期區間最多只能查詢 5 年", ex.Message);
    }

    /// <summary>Verifies snapshot trend rejects date ranges longer than five calendar years.</summary>
    [Fact]
    public async Task ListSnapshotTrendAsync_RejectsDateRangesLongerThanFiveYears()
    {
        await using var db = await CreateSnapshotListDbContextAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => SnapshotEndpoints.ListSnapshotTrendAsync(
            dateStart: new DateOnly(2020, 6, 27),
            dateEnd: new DateOnly(2026, 6, 28),
            db));

        Assert.Equal("日期區間最多只能查詢 5 年", ex.Message);
    }

    /// <summary>Verifies snapshot list accepts a date range of exactly five calendar years.</summary>
    [Fact]
    public async Task ListSnapshotsAsync_AllowsExactlyFiveYears()
    {
        await using var db = await CreateDbContextAsync();
        db.SnapshotBatches.Add(CreateSnapshot("Inside range", new DateTime(2021, 6, 28, 12, 0, 0, DateTimeKind.Utc), 100m));
        await db.SaveChangesAsync();

        var result = await SnapshotEndpoints.ListSnapshotsAsync(
            page: 1,
            pageSize: 20,
            dateStart: new DateOnly(2021, 6, 28),
            dateEnd: new DateOnly(2026, 6, 28),
            db);

        Assert.Equal(1, result.Total);
        Assert.Single(result.Items);
    }

    /// <summary>Verifies snapshot list accepts a leap-day end range of exactly five calendar years.</summary>
    [Fact]
    public async Task ListSnapshotsAsync_AllowsLeapDayEndExactlyFiveYears()
    {
        await using var db = await CreateDbContextAsync();
        db.SnapshotBatches.Add(CreateSnapshot("Leap day", new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Utc), 100m));
        await db.SaveChangesAsync();

        var result = await SnapshotEndpoints.ListSnapshotsAsync(
            page: 1,
            pageSize: 20,
            dateStart: new DateOnly(2019, 2, 28),
            dateEnd: new DateOnly(2024, 2, 29),
            db);

        Assert.Equal(1, result.Total);
        Assert.Single(result.Items);
    }

    /// <summary>Verifies snapshot list rejects leap-day end ranges longer than five calendar years.</summary>
    [Fact]
    public async Task ListSnapshotsAsync_RejectsLeapDayEndLongerThanFiveYears()
    {
        await using var db = await CreateSnapshotListDbContextAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => SnapshotEndpoints.ListSnapshotsAsync(
            page: 1,
            pageSize: 20,
            dateStart: new DateOnly(2019, 2, 27),
            dateEnd: new DateOnly(2024, 2, 29),
            db));

        Assert.Equal("日期區間最多只能查詢 5 年", ex.Message);
    }

    /// <summary>Creates a SQLite-backed context with one bank account and one stock holding.</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.BankAccounts.Add(new BankAccount { BankName = "測試銀行", AccountNumber = "12345", Balance = 1000m, AccountType = "活期" });
        db.Stocks.Add(new Stock { Name = "台積電", Symbol = "2330", InstrumentType = StockInstrumentType.Stock, Shares = 1000m, BuyPrice = 1000m, CurrentPrice = 1050m });
        await db.SaveChangesAsync();

        return db;
    }

    /// <summary>Creates a SQLite-backed context with snapshots across multiple dates.</summary>
    private static async Task<AppDbContext> CreateSnapshotListDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.SnapshotBatches.AddRange(
            CreateSnapshot("before-range", new DateTime(2025, 12, 31, 23, 59, 0, DateTimeKind.Utc), 100m),
            CreateSnapshot("year-start", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 200m),
            CreateSnapshot("mid-year", new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc), 300m),
            CreateSnapshot("year-end", new DateTime(2026, 12, 31, 23, 59, 0, DateTimeKind.Utc), 400m),
            CreateSnapshot("after-range", new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), 500m));
        await db.SaveChangesAsync();

        return db;
    }

    /// <summary>Creates a minimal snapshot batch for date range tests.</summary>
    private static SnapshotBatch CreateSnapshot(string name, DateTime snapshotDate, decimal totalNetWorth)
    {
        return new SnapshotBatch
        {
            Name = name,
            SnapshotDate = snapshotDate,
            TotalNetWorth = totalNetWorth,
            TotalBankBalance = totalNetWorth,
            TotalStockValue = 0m,
            TotalStockCost = 0m,
        };
    }
}
