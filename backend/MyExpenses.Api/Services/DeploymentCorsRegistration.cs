using Microsoft.Extensions.DependencyInjection;

namespace MyExpenses.Api.Services;

/// <summary>提供只限 Development frontend origin 的 CORS policy。</summary>
public static class DeploymentCorsRegistration
{
    /// <summary>註冊一個明確 origin，不允許 Production 使用 wildcard CORS。</summary>
    public static IServiceCollection AddDevelopmentCors(
        this IServiceCollection services,
        string allowedOrigin)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!IsValidOrigin(allowedOrigin))
        {
            throw new InvalidOperationException(
                "Cors:DevelopmentOrigin must be an absolute HTTP or HTTPS origin.");
        }

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy => policy
                .WithOrigins(allowedOrigin)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials());
        });

        return services;
    }

    /// <summary>確認 CORS 設定是沒有 path、query 或 fragment 的 HTTP origin。</summary>
    private static bool IsValidOrigin(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.AbsolutePath == "/"
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }
}
