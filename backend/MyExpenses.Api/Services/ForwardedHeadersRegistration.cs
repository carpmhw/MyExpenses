using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using MyExpenses.Api.Options;

namespace MyExpenses.Api.Services;

/// <summary>註冊只信任明確 allowlist 的 X-Forwarded-For 與 X-Forwarded-Proto。</summary>
public static class ForwardedHeadersRegistration
{
    /// <summary>註冊 trusted proxy IP/network，並清除 framework 的 implicit loopback trust。</summary>
    public static IServiceCollection AddTrustedForwardedHeaders(
        this IServiceCollection services,
        DeploymentOptions deploymentOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(deploymentOptions);

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in deploymentOptions.TrustedProxies ?? [])
            {
                if (IPAddress.TryParse(proxy, out var address))
                {
                    options.KnownProxies.Add(address);
                }
            }

            foreach (var network in deploymentOptions.TrustedNetworks ?? [])
            {
                if (DeploymentOptionsValidator.TryParseNetwork(
                        network,
                        out var parsedNetwork,
                        out _,
                        out _))
                {
                    options.KnownIPNetworks.Add(parsedNetwork);
                }
            }
        });

        return services;
    }
}
