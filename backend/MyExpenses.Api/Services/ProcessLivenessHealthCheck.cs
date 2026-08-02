using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MyExpenses.Api.Services;

/// <summary>只確認 process 能執行 health check，不讀取 SQLite 或其他外部資源。</summary>
public sealed class ProcessLivenessHealthCheck : IHealthCheck
{
    /// <summary>回報 process liveness 成功，刻意不依賴任何外部服務。</summary>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
