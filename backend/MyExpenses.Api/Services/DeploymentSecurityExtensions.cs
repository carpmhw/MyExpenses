using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MyExpenses.Api.Options;

namespace MyExpenses.Api.Services;

/// <summary>集中註冊與啟用 Remote mode 的 HTTPS、HSTS 與 browser security headers。</summary>
public static class DeploymentSecurityExtensions
{
    /// <summary>只有 Remote mode 註冊 HSTS 與 HTTPS redirection services。</summary>
    public static IServiceCollection AddDeploymentSecurity(
        this IServiceCollection services,
        DeploymentOptions deploymentOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(deploymentOptions);

        if (deploymentOptions.Mode != DeploymentMode.Remote)
        {
            return services;
        }

        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
        });
        services.AddHttpsRedirection(options =>
        {
            options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
            options.HttpsPort = GetHttpsPort(deploymentOptions.PublicOrigin);
        });

        return services;
    }

    /// <summary>依序啟用 HTTPS redirect、HSTS 與 Remote baseline browser headers。</summary>
    public static WebApplication UseDeploymentSecurity(
        this WebApplication app,
        DeploymentOptions deploymentOptions)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(deploymentOptions);

        if (deploymentOptions.Mode != DeploymentMode.Remote)
        {
            return app;
        }

        app.UseHttpsRedirection();
        app.UseHsts();
        app.UseMiddleware<RemoteSecurityHeadersMiddleware>();
        return app;
    }

    /// <summary>從已驗證的 HTTPS public origin 取得 redirect port，預設使用 443。</summary>
    private static int GetHttpsPort(string? publicOrigin)
    {
        if (Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin)
            && !origin.IsDefaultPort)
        {
            return origin.Port;
        }

        return 443;
    }
}
