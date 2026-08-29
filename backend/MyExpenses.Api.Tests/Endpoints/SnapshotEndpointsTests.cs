using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
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

    /// <summary>Verifies manual snapshots capture unpaid installment liabilities and complete net worth.</summary>
    [Fact]
    public async Task CreateSnapshot_CapturesUnpaidInstallmentLiabilities()
    {
        await using var db = await CreateDbContextAsync();
        var installment = new Installment
        {
            TotalAmount = 900m,
            Periods = 3,
            PerPeriod = 300m,
            RemainingPeriods = 3,
            PurchaseDate = new DateOnly(2026, 6, 1),
            Status = InstallmentStatus.Active,
            Description = "未繳分期",
        };
        db.Installments.Add(installment);
        await db.SaveChangesAsync();
        db.InstallmentPayments.AddRange(
            new InstallmentPayment
            {
                InstallmentId = installment.Id,
                Period = 1,
                Amount = 300m,
                IsPaid = false,
                DueDate = new DateOnly(2026, 6, 20),
            },
            new InstallmentPayment
            {
                InstallmentId = installment.Id,
                Period = 2,
                Amount = 300m,
                IsPaid = true,
                DueDate = new DateOnly(2026, 5, 20),
            });
        await db.SaveChangesAsync();

        var snapshot = await SnapshotEndpoints.CreateSnapshotAsync(db);

        Assert.Equal(300m, snapshot.TotalLiabilities);
        Assert.Equal(1047432m, snapshot.TotalAssets);
        Assert.Equal(1047132m, snapshot.TotalNetWorth);
        Assert.Equal(NetWorthBasis.AssetsMinusLiabilities, snapshot.NetWorthBasis);
    }

    /// <summary>驗證手動快照以同一匯率 snapshot 保存混合幣別的 TWD 固定總額。</summary>
    [Fact]
    public async Task CreateSnapshot_UsesFixedMixedCurrencyValuation()
    {
        await using var db = await CreateDbContextAsync();
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "23456",
            Balance = 310m,
            CurrencyCode = "USD",
            AccountType = "活期",
        });
        await db.SaveChangesAsync();
        var service = new FixedExchangeRateService(CreateRates(0.031m, isStale: true));

        var snapshot = await SnapshotEndpoints.CreateSnapshotAsync(db, service);

        Assert.Equal(11000m, snapshot.TotalBankBalance);
        Assert.Equal(1057432m, snapshot.TotalAssets);
        Assert.True(snapshot.ExchangeRateIsStale);
        Assert.Equal(service.Snapshot.UpdatedAtUtc, snapshot.ExchangeRateUpdatedAt);
        Assert.Equal(10000m, Assert.Single(snapshot.BankDetails, detail => detail.CurrencyCode == "USD").ConvertedBalance);
    }

    /// <summary>驗證缺少外幣匯率時手動快照不會寫入批次或半成品明細。</summary>
    [Fact]
    public async Task CreateSnapshot_RejectsMissingRateBeforePersistingAnything()
    {
        await using var db = await CreateDbContextAsync();
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "23456",
            Balance = 310m,
            CurrencyCode = "USD",
            AccountType = "活期",
        });
        await db.SaveChangesAsync();
        var service = new FixedExchangeRateService(new ExchangeRateSnapshot(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m },
            DateTime.UtcNow,
            false));

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() =>
            SnapshotEndpoints.CreateSnapshotAsync(db, service));

        Assert.Equal(0, await db.SnapshotBatches.CountAsync());
    }

    /// <summary>驗證匯率服務更新後既有快照的明細與總額保持不變。</summary>
    [Fact]
    public async Task CreateSnapshot_PreservesHistoricalValuationAfterRateUpdate()
    {
        await using var db = await CreateDbContextAsync();
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "23456",
            Balance = 310m,
            CurrencyCode = "USD",
            AccountType = "活期",
        });
        await db.SaveChangesAsync();
        var service = new MutableExchangeRateService(CreateRates(0.031m, isStale: false));

        var snapshot = await SnapshotEndpoints.CreateSnapshotAsync(db, service);
        service.Snapshot = CreateRates(0.030m, isStale: false);

        db.ChangeTracker.Clear();
        var stored = await db.SnapshotBatches.SingleAsync();
        Assert.Equal(snapshot.TotalBankBalance, stored.TotalBankBalance);
        Assert.Equal(10000m, stored.BankDetails.Single(detail => detail.CurrencyCode == "USD").ConvertedBalance);
        Assert.Equal(0.031m, stored.BankDetails.Single(detail => detail.CurrencyCode == "USD").ExchangeRate);
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

    /// <summary>建立測試用的 TWD 基準匯率 snapshot。</summary>
    private static ExchangeRateSnapshot CreateRates(decimal usdRate, bool isStale)
        => new(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m, ["USD"] = usdRate },
            new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc),
            isStale);

    /// <summary>提供固定匯率 snapshot 的測試服務。</summary>
    private sealed class FixedExchangeRateService : IExchangeRateService
    {
        /// <summary>初始化固定匯率 snapshot。</summary>
        public FixedExchangeRateService(ExchangeRateSnapshot snapshot) => Snapshot = snapshot;

        /// <summary>取得測試服務目前的 snapshot。</summary>
        public ExchangeRateSnapshot Snapshot { get; }

        /// <summary>回傳固定測試 snapshot。</summary>
        public Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshot);

        /// <summary>依指定 snapshot 執行測試換算。</summary>
        public decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot)
        {
            if (currencyCode == "TWD") return amount;
            return snapshot.Rates.TryGetValue(currencyCode, out var rate) && rate > 0m ? amount / rate : null;
        }

        /// <summary>回傳測試換算的成功狀態。</summary>
        public bool TryConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot, out decimal convertedAmount)
        {
            var result = ConvertToBase(amount, currencyCode, snapshot);
            convertedAmount = result.GetValueOrDefault();
            return result.HasValue;
        }
    }

    /// <summary>提供可更新 snapshot 的測試服務以驗證歷史不可變性。</summary>
    private sealed class MutableExchangeRateService : IExchangeRateService
    {
        /// <summary>初始化可變匯率 snapshot。</summary>
        public MutableExchangeRateService(ExchangeRateSnapshot snapshot) => Snapshot = snapshot;

        /// <summary>取得或更新目前測試 snapshot。</summary>
        public ExchangeRateSnapshot Snapshot { get; set; }

        /// <summary>回傳目前測試 snapshot。</summary>
        public Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshot);

        /// <summary>依目前 snapshot 執行換算。</summary>
        public decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot)
            => currencyCode == "TWD"
                ? amount
                : snapshot.Rates.TryGetValue(currencyCode, out var rate) && rate > 0m ? amount / rate : null;

        /// <summary>回傳目前 snapshot 的換算成功狀態。</summary>
        public bool TryConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot, out decimal convertedAmount)
        {
            var result = ConvertToBase(amount, currencyCode, snapshot);
            convertedAmount = result.GetValueOrDefault();
            return result.HasValue;
        }
    }
}
