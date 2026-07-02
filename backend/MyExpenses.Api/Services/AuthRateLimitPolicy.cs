using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace MyExpenses.Api.Services;

public static class AuthRateLimitPolicy
{
    public const string SensitiveAuthPolicy = "sensitive-auth";
    public const int PermitLimit = 5;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Configures rate limiting for sensitive authentication endpoints.</summary>
    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(SensitiveAuthPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                GetPartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = PermitLimit,
                    QueueLimit = 0,
                    Window = Window,
                }));
    }

    /// <summary>Builds an IP-and-path partition key so each sensitive endpoint is limited independently.</summary>
    private static string GetPartitionKey(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"{remoteIp}:{context.Request.Path}";
    }
}
