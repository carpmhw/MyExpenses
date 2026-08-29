using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>以單一短 transaction 建立自動財務快照的 typed workflow。</summary>
public sealed class AutomaticSnapshotWorkflow
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IExchangeRateService? _exchangeRateService;

    /// <summary>初始化使用 scoped database context、UTC 時間來源與共用匯率服務的快照 workflow。</summary>
    public AutomaticSnapshotWorkflow(
        AppDbContext db,
        TimeProvider? timeProvider = null,
        IExchangeRateService? exchangeRateService = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _exchangeRateService = exchangeRateService;
    }

    /// <summary>建立快照並在 transaction 失敗時回傳可分類的安全結果。</summary>
    public async Task<ScheduledJobWorkflowResult> RunAsync(
        DateTime scheduledForUtc,
        DateOnly scheduledLocalDate,
        CancellationToken cancellationToken = default)
    {
        var targetKey = "automatic-snapshot";
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var config = await _db.AutoSnapshotConfigs.FirstOrDefaultAsync(cancellationToken);
            if (config is null || !config.IsEnabled)
                return ScheduledJobWorkflowResult.NoWork("NotDue");
            var validConfig = config;
            var bankAccounts = await _db.BankAccounts.ToListAsync(cancellationToken);
            var stocks = await _db.Stocks.ToListAsync(cancellationToken);
            var totalLiabilities = await _db.InstallmentPayments
                .Where(payment => !payment.IsPaid)
                .SumAsync(payment => (decimal?)payment.Amount, cancellationToken) ?? 0m;
            var exchangeRateSnapshot = await ExchangeRateSnapshotResolver.ResolveForAccountsAsync(
                bankAccounts,
                _exchangeRateService,
                cancellationToken);
            var nowUtc = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
            var scheduledTime = TimeOnly.TryParseExact(
                validConfig.TimeOfDay,
                "HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsedTime)
                ? parsedTime
                : TimeOnly.MinValue;
            var localScheduledAt = DateTime.SpecifyKind(
                scheduledLocalDate.ToDateTime(scheduledTime),
                DateTimeKind.Unspecified);
            var snapshot = FinancialSnapshotBuilder.Build(
                SnapshotBackgroundService.BuildAutomaticSnapshotName(localScheduledAt),
                "系統自動建立",
                nowUtc,
                bankAccounts,
                stocks,
                exchangeRateSnapshot,
                totalLiabilities);

            _db.SnapshotBatches.Add(snapshot);
            validConfig.LastRunAt = DateTime.SpecifyKind(scheduledForUtc, DateTimeKind.Utc);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ScheduledJobWorkflowResult
            {
                Outcome = ScheduledJobWorkflowOutcome.Succeeded,
                Retryability = ScheduledJobRetryClassification.None,
                TargetsEnumerated = true,
                TargetKeys = [targetKey],
                SucceededTargetKeys = [targetKey],
                AffectedRowKeys = [$"snapshot-{snapshot.Id}"],
                ResultCode = "Completed",
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ExchangeRateUnavailableException exception)
        {
            _db.ChangeTracker.Clear();
            return new ScheduledJobWorkflowResult
            {
                Outcome = ScheduledJobWorkflowOutcome.Failed,
                Retryability = exception.IsRetryable
                    ? ScheduledJobRetryClassification.Retryable
                    : ScheduledJobRetryClassification.Permanent,
                TargetsEnumerated = true,
                TargetCount = 1,
                TargetKeys = [targetKey],
                FailedTargetCodes = new Dictionary<string, string>
                {
                    [targetKey] = "ExchangeRateUnavailable",
                },
                ResultCode = "ExchangeRateUnavailable",
                SafeMessage = "自動快照匯率不可用",
            };
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            var retryable = RetryClassification.IsRetryable(exception);
            return new ScheduledJobWorkflowResult
            {
                Outcome = ScheduledJobWorkflowOutcome.Failed,
                Retryability = retryable
                    ? ScheduledJobRetryClassification.Retryable
                    : ScheduledJobRetryClassification.Permanent,
                TargetsEnumerated = true,
                TargetCount = 1,
                TargetKeys = [targetKey],
                FailedTargetCodes = new Dictionary<string, string>
                {
                    [targetKey] = retryable ? "DatabaseBusy" : "DatabaseFailure",
                },
                ResultCode = retryable ? "DatabaseBusy" : "DatabaseFailure",
                SafeMessage = "自動快照建立失敗",
            };
        }
    }
}
