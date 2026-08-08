using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

/// <summary>提供唯讀業務排程總覽與 execution 歷史 API。</summary>
public static class ScheduleEndpoints
{
    /// <summary>註冊 browser-owner-only 的業務排程 endpoints。</summary>
    public static void MapScheduleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/schedules");
        group.MapGet("", async (
            AppDbContext db,
            ScheduledJobExecutionRepository repository,
            TimeZoneService timeZoneService,
            TimeProvider timeProvider) =>
        {
            return Results.Ok(await GetOverviewAsync(db, repository, timeZoneService, timeProvider));
        });

        group.MapGet("/executions", async (
            string? jobKey,
            string? status,
            DateOnly? dateStart,
            DateOnly? dateEnd,
            int? page,
            int? pageSize,
            ScheduledJobExecutionRepository repository,
            TimeZoneService timeZoneService) =>
        {
            try
            {
                var query = NormalizeExecutionQuery(
                    jobKey,
                    status,
                    dateStart,
                    dateEnd,
                    page,
                    pageSize,
                    timeZoneService);
                return Results.Ok(await ListExecutionsAsync(query, repository));
            }
            catch (ScheduleQueryValidationException exception)
            {
                return Results.ValidationProblem(exception.Errors);
            }
        });
    }

    /// <summary>查詢三個排程 descriptor 與每個工作最近一次 execution。</summary>
    public static async Task<IReadOnlyList<ScheduleOverviewItem>> GetOverviewAsync(
        AppDbContext db,
        ScheduledJobExecutionRepository repository,
        TimeZoneService timeZoneService,
        TimeProvider timeProvider)
    {
        var config = await db.AutoSnapshotConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync();
        var nowUtc = DateTime.SpecifyKind(timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        var descriptors = BusinessScheduleDescriptorFactory.Create(
            config,
            nowUtc,
            timeZoneService.GetTimeZoneInfo());
        var response = new List<ScheduleOverviewItem>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            var latest = await repository.GetLatestAsync(descriptor.JobKey);
            response.Add(new ScheduleOverviewItem(
                descriptor.JobKey,
                descriptor.DisplayName,
                descriptor.ConfigurationSource,
                descriptor.IsEnabled,
                descriptor.FrequencyDescription,
                descriptor.ScheduleTimeZoneId,
                descriptor.NextRunAtUtc,
                latest is null ? null : ToSummary(latest)));
        }

        return response;
    }

    /// <summary>依已正規化 query 回傳穩定排序的 execution 分頁歷史。</summary>
    public static async Task<ScheduleExecutionHistoryResponse> ListExecutionsAsync(
        ScheduleExecutionQuery query,
        ScheduledJobExecutionRepository repository)
    {
        var total = await repository.CountAsync(
            query.JobKey,
            query.Status,
            query.StartedAtUtcInclusive,
            query.StartedAtUtcExclusive);
        var executions = await repository.QueryAsync(
            query.JobKey,
            query.Status,
            query.StartedAtUtcInclusive,
            query.StartedAtUtcExclusive,
            (query.Page - 1) * query.PageSize,
            query.PageSize);
        return new ScheduleExecutionHistoryResponse(
            executions.Select(ToSummary).ToList(),
            total,
            query.Page,
            query.PageSize);
    }

    /// <summary>將 query filter 正規化為 bounded enum、UTC 半開日期區間與分頁參數。</summary>
    public static ScheduleExecutionQuery NormalizeExecutionQuery(
        string? jobKey,
        string? status,
        DateOnly? dateStart,
        DateOnly? dateEnd,
        int? page,
        int? pageSize,
        TimeZoneService timeZoneService)
    {
        ArgumentNullException.ThrowIfNull(timeZoneService);
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var parsedJobKey = ParseEnumFilter<ScheduledJobKey>(jobKey, nameof(jobKey), errors);
        var parsedStatus = ParseEnumFilter<ScheduledJobExecutionStatus>(status, nameof(status), errors);
        if (dateStart.HasValue != dateEnd.HasValue)
            errors["dateRange"] = ["dateStart 與 dateEnd 必須成對提供。"];
        if (dateStart.HasValue && dateEnd.HasValue && dateEnd.Value < dateStart.Value)
            errors["dateRange"] = ["dateEnd 不能早於 dateStart。"];
        if (errors.Count > 0)
            throw new ScheduleQueryValidationException(errors);

        DateTime? startUtc = null;
        DateTime? endUtc = null;
        if (dateStart.HasValue && dateEnd.HasValue)
        {
            var range = timeZoneService.ConvertLocalDateRangeToUtc(dateStart.Value, dateEnd.Value);
            startUtc = range.StartUtc;
            endUtc = range.EndExclusiveUtc;
        }

        return new ScheduleExecutionQuery(
            parsedJobKey,
            parsedStatus,
            PaginationPolicy.NormalizePage(page),
            PaginationPolicy.NormalizePageSize(pageSize),
            startUtc,
            endUtc);
    }

    /// <summary>將 execution entity 映射為不含技術細節的安全 DTO。</summary>
    private static ScheduledJobExecutionSummary ToSummary(ScheduledJobExecution execution)
        => new(
            execution.Id,
            execution.JobKey,
            execution.ScheduledForUtc,
            execution.ScheduleTimeZoneId,
            execution.ScheduledLocalDate,
            execution.Status,
            execution.StartedAtUtc,
            execution.CompletedAtUtc,
            execution.AttemptCount,
            execution.TargetCount,
            execution.SucceededCount,
            execution.FailedCount,
            execution.AffectedCount,
            execution.ResultCode,
            ScheduledJobExecutionSafety.SanitizeSafeMessage(execution.SafeMessage));

    /// <summary>解析忽略大小寫且拒絕未知值的 bounded enum filter。</summary>
    private static TEnum? ParseEnumFilter<TEnum>(
        string? value,
        string fieldName,
        IDictionary<string, string[]> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        var name = Enum.GetNames<TEnum>()
            .FirstOrDefault(candidate => string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
        if (name is not null)
            return Enum.Parse<TEnum>(name, true);
        errors[fieldName] = [$"{fieldName} 值無效。"];
        return null;
    }
}

/// <summary>保存已正規化的 execution query。</summary>
public sealed record ScheduleExecutionQuery(
    ScheduledJobKey? JobKey,
    ScheduledJobExecutionStatus? Status,
    int Page,
    int PageSize,
    DateTime? StartedAtUtcInclusive,
    DateTime? StartedAtUtcExclusive);

/// <summary>表示安全的 schedule query validation error。</summary>
public sealed class ScheduleQueryValidationException : ArgumentException
{
    /// <summary>建立帶有欄位錯誤的 validation exception。</summary>
    public ScheduleQueryValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("排程查詢條件無效")
    {
        Errors = new Dictionary<string, string[]>(errors, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>取得可直接回傳 ProblemDetails 的欄位錯誤。</summary>
    public IDictionary<string, string[]> Errors { get; }
}

/// <summary>排程總覽單一卡片的安全 response。</summary>
public sealed record ScheduleOverviewItem(
    ScheduledJobKey JobKey,
    string DisplayName,
    string ConfigurationSource,
    bool IsEnabled,
    string FrequencyDescription,
    string ScheduleTimeZoneId,
    DateTime? NextRunAtUtc,
    ScheduledJobExecutionSummary? LatestExecution);

/// <summary>execution 歷史分頁 response。</summary>
public sealed record ScheduleExecutionHistoryResponse(
    IReadOnlyList<ScheduledJobExecutionSummary> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>不含 stack trace、payload 與逐標的資料的 execution 摘要。</summary>
public sealed record ScheduledJobExecutionSummary(
    long Id,
    ScheduledJobKey JobKey,
    DateTime ScheduledForUtc,
    string ScheduleTimeZoneId,
    DateOnly ScheduledLocalDate,
    ScheduledJobExecutionStatus Status,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    int AttemptCount,
    int? TargetCount,
    int SucceededCount,
    int FailedCount,
    int AffectedCount,
    string? ResultCode,
    string? SafeMessage);
