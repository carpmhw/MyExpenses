using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MyExpenses.Api.Data;

namespace MyExpenses.Api.Services;

/// <summary>確認 startup ready、migration current 且 SQLite 可實際執行查詢。</summary>
public sealed class DatabaseReadinessHealthCheck : IHealthCheck
{
    private readonly IStartupReadiness _startupReadiness;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>建立使用 startup state 與 scoped DbContext 的 readiness health check。</summary>
    public DatabaseReadinessHealthCheck(
        IStartupReadiness startupReadiness,
        IServiceScopeFactory scopeFactory)
    {
        _startupReadiness = startupReadiness ?? throw new ArgumentNullException(nameof(startupReadiness));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <summary>依序檢查 startup、pending migrations、連線與 SQLite probe，且不回傳敏感錯誤細節。</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_startupReadiness.IsReady)
            return HealthCheckResult.Unhealthy("Application startup is incomplete.");

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (!await db.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("SQLite database is unavailable.");

            var pendingMigrations = await db.Database
                .GetPendingMigrationsAsync(cancellationToken);
            if (pendingMigrations.Any())
                return HealthCheckResult.Unhealthy("SQLite database migrations are pending.");

            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT 1";
                _ = await command.ExecuteScalarAsync(cancellationToken);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("SQLite database is unavailable.");
        }
    }
}
