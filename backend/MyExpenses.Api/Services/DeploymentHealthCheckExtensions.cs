using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MyExpenses.Api.Services;

/// <summary>註冊並映射不洩漏內部資料的 liveness 與 readiness endpoints。</summary>
public static class DeploymentHealthCheckExtensions
{
    /// <summary>取得 liveness health check 的 tag。</summary>
    public const string LivenessTag = "liveness";

    /// <summary>取得 readiness health check 的 tag。</summary>
    public const string ReadinessTag = "readiness";

    /// <summary>註冊只包含 process liveness 與 SQLite readiness 的 health checks。</summary>
    public static IServiceCollection AddDeploymentHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHealthChecks()
            .AddCheck<ProcessLivenessHealthCheck>(
                "process",
                tags: [LivenessTag])
            .AddCheck<DatabaseReadinessHealthCheck>(
                "database",
                tags: [ReadinessTag]);
        return services;
    }

    /// <summary>映射匿名 liveness 與 readiness endpoints，供 reverse proxy 直接探測。</summary>
    public static void MapDeploymentHealthChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = HasLivenessTag,
        }).AllowAnonymous();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = HasReadinessTag,
        }).AllowAnonymous();
    }

    /// <summary>選取 liveness tag，確保此 probe 不會執行 SQLite readiness。</summary>
    private static bool HasLivenessTag(HealthCheckRegistration registration)
        => registration.Tags.Contains(LivenessTag, StringComparer.Ordinal);

    /// <summary>選取 readiness tag，確保 endpoint 只回報完整 startup 與 storage 狀態。</summary>
    private static bool HasReadinessTag(HealthCheckRegistration registration)
        => registration.Tags.Contains(ReadinessTag, StringComparer.Ordinal);
}
