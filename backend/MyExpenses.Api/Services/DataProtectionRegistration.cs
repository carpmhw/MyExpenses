using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MyExpenses.Api.Options;

namespace MyExpenses.Api.Services;

/// <summary>集中註冊 MyExpenses 使用的 Data Protection application discriminator 與 key ring。</summary>
public static class DataProtectionRegistration
{
    /// <summary>依環境註冊穩定 application name，以及 Production 的持久化 key directory。</summary>
    public static void Add(
        IServiceCollection services,
        PersistentDataProtectionOptions options,
        bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var dataProtection = services
            .AddDataProtection()
            .SetApplicationName(options.ApplicationName);

        if (isProduction)
        {
            if (string.IsNullOrWhiteSpace(options.KeyDirectory))
            {
                throw new InvalidOperationException(
                    "DataProtection:KeyDirectory must be configured in Production.");
            }

            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(options.KeyDirectory));
        }

        services.AddSingleton(new DataProtectionStartupValidator(options, isProduction));
    }
}
