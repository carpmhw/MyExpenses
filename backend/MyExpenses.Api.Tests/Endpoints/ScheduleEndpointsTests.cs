using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Options;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public sealed class ScheduleEndpointsTests
{
    /// <summary>驗證自動快照設定允許零或一筆，多筆時拒絕任意選取且不改寫資料。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AutoSnapshotConfig_RequiresAtMostOneRow(int count)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        for (var index = 0; index < count; index++)
            db.AutoSnapshotConfigs.Add(new AutoSnapshotConfig { IsEnabled = false });
        await db.SaveChangesAsync();
        var repository = new ScheduledJobExecutionRepository(db);

        if (count > 1)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ScheduleEndpoints.GetOverviewAsync(
                db, repository, CreateTimeZoneService(), TimeProvider.System));
        }
        else
        {
            var overview = await ScheduleEndpoints.GetOverviewAsync(
                db, repository, CreateTimeZoneService(), TimeProvider.System);
            Assert.False(Assert.Single(overview, item => item.JobKey == ScheduledJobKey.AutomaticSnapshot).IsEnabled);
        }

        var result = await new AutomaticSnapshotWorkflow(db).RunAsync(
            new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 9, 6));
        Assert.Equal(count > 1 ? ScheduledJobWorkflowOutcome.Failed : ScheduledJobWorkflowOutcome.NoWork, result.Outcome);
        Assert.Equal(count, await db.AutoSnapshotConfigs.CountAsync());
        Assert.Equal(0, await db.SnapshotBatches.CountAsync());
    }

    /// <summary>驗證總覽回傳三個 descriptor 並對停用快照省略 next run。</summary>
    [Fact]
    public async Task GetOverviewAsync_ReturnsThreeDescriptorsAndLatestExecution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.AutoSnapshotConfigs.Add(new AutoSnapshotConfig
        {
            IsEnabled = false,
            Frequency = "Daily",
            TimeOfDay = "08:00",
        });
        await db.SaveChangesAsync();
        var repository = new ScheduledJobExecutionRepository(db);
        var running = await repository.CreateRunningAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            new DateTime(2026, 8, 8, 15, 0, 1, DateTimeKind.Utc));
        await repository.CompleteAsync(
            running.Id,
            ScheduledJobExecutionStatus.Succeeded,
            new DateTime(2026, 8, 8, 15, 0, 2, DateTimeKind.Utc),
            1,
            1,
            0,
            1,
            "Completed",
            "完成");

        var result = await ScheduleEndpoints.GetOverviewAsync(
            db,
            repository,
            CreateTimeZoneService(),
            new FixedTimeProvider(new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(3, result.Count);
        var snapshot = Assert.Single(result, item => item.JobKey == ScheduledJobKey.AutomaticSnapshot);
        Assert.False(snapshot.IsEnabled);
        Assert.Null(snapshot.NextRunAtUtc);
        var stock = Assert.Single(result, item => item.JobKey == ScheduledJobKey.StockPriceUpdate);
        Assert.Equal(ScheduledJobExecutionStatus.Succeeded, stock.LatestExecution?.Status);
    }

    /// <summary>驗證歷史 API 使用 total-before-pagination 與穩定降冪排序。</summary>
    [Fact]
    public async Task ListExecutionsAsync_ReturnsStablePaginatedHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var repository = new ScheduledJobExecutionRepository(db);
        for (var index = 0; index < 3; index++)
        {
            await repository.CreateRunningAsync(
                ScheduledJobKey.StockPriceUpdate,
                new DateTime(2026, 8, 8, 15, index, 0, DateTimeKind.Utc),
                "Asia/Taipei",
                new DateOnly(2026, 8, 8),
                new DateTime(2026, 8, 8, 15, index, 1, DateTimeKind.Utc));
        }

        var response = await ScheduleEndpoints.ListExecutionsAsync(
            new ScheduleExecutionQuery(
                ScheduledJobKey.StockPriceUpdate,
                null,
                1,
                2,
                null,
                null),
            repository);

        Assert.Equal(3, response.Total);
        Assert.Equal(2, response.Items.Count);
        Assert.True(response.Items[0].StartedAtUtc > response.Items[1].StartedAtUtc);
    }

    /// <summary>驗證 execution query 會正規化分頁並忽略空白 filter。</summary>
    [Fact]
    public void NormalizeExecutionQuery_ClampsPagingAndOmitsBlankFilters()
    {
        var query = ScheduleEndpoints.NormalizeExecutionQuery(
            "  ",
            " ",
            null,
            null,
            page: 0,
            pageSize: 500,
            new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { Default = "Asia/Taipei" })));

        Assert.Null(query.JobKey);
        Assert.Null(query.Status);
        Assert.Equal(1, query.Page);
        Assert.Equal(100, query.PageSize);
        Assert.Null(query.StartedAtUtcInclusive);
        Assert.Null(query.StartedAtUtcExclusive);
    }

    /// <summary>驗證未知 job/status、單邊日期與反向日期會回傳安全 validation error。</summary>
    [Fact]
    public void NormalizeExecutionQuery_RejectsInvalidFilters()
    {
        Assert.Throws<ScheduleQueryValidationException>(() => ScheduleEndpoints.NormalizeExecutionQuery(
            "UnknownJob",
            null,
            null,
            null,
            1,
            20,
            CreateTimeZoneService()));
        Assert.Throws<ScheduleQueryValidationException>(() => ScheduleEndpoints.NormalizeExecutionQuery(
            null,
            "UnknownStatus",
            null,
            null,
            1,
            20,
            CreateTimeZoneService()));
        Assert.Throws<ScheduleQueryValidationException>(() => ScheduleEndpoints.NormalizeExecutionQuery(
            null,
            null,
            new DateOnly(2026, 8, 1),
            null,
            1,
            20,
            CreateTimeZoneService()));
        Assert.Throws<ScheduleQueryValidationException>(() => ScheduleEndpoints.NormalizeExecutionQuery(
            null,
            null,
            new DateOnly(2026, 8, 2),
            new DateOnly(2026, 8, 1),
            1,
            20,
            CreateTimeZoneService()));
        Assert.Throws<ScheduleQueryValidationException>(() => ScheduleEndpoints.NormalizeExecutionQuery(
            "1",
            null,
            null,
            null,
            1,
            20,
            CreateTimeZoneService()));
        Assert.Throws<ScheduleQueryValidationException>(() => ScheduleEndpoints.NormalizeExecutionQuery(
            null,
            "1",
            null,
            null,
            1,
            20,
            CreateTimeZoneService()));
    }

    /// <summary>驗證本地日期篩選會轉成系統時區 UTC 半開區間。</summary>
    [Fact]
    public void NormalizeExecutionQuery_ConvertsLocalDateRangeToUtcHalfOpenInterval()
    {
        var query = ScheduleEndpoints.NormalizeExecutionQuery(
            ScheduledJobKey.StockPriceUpdate.ToString(),
            ScheduledJobExecutionStatus.Succeeded.ToString(),
            new DateOnly(2026, 8, 8),
            new DateOnly(2026, 8, 8),
            1,
            20,
            CreateTimeZoneService());

        Assert.Equal(new DateTime(2026, 8, 7, 16, 0, 0, DateTimeKind.Utc), query.StartedAtUtcInclusive);
        Assert.Equal(new DateTime(2026, 8, 8, 16, 0, 0, DateTimeKind.Utc), query.StartedAtUtcExclusive);
    }

    /// <summary>建立台灣系統時區服務。</summary>
    private static TimeZoneService CreateTimeZoneService()
        => new(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { Default = "Asia/Taipei" }));

    /// <summary>建立使用已開啟 SQLite 連線的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>提供固定 UTC 時間供排程總覽測試使用。</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        /// <summary>初始化固定 UTC instant。</summary>
        public FixedTimeProvider(DateTime utcNow)
        {
            _utcNow = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
        }

        /// <summary>回傳測試指定的 UTC instant。</summary>
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
