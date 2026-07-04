using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MyExpenses.Api.Services;

public static class JwtSecretProvider
{
    public const string DevelopmentSecret = "dev-only-myexpenses-jwt-secret-not-for-production-32chars";

    private static readonly HashSet<string> PlaceholderSecrets = new(StringComparer.Ordinal)
    {
        "change-this-to-a-secure-random-key-at-least-32-characters",
        "placeholder-key-replace-in-production",
    };

    /// <summary>Returns a safe JWT secret for the current environment or fails closed outside development.</summary>
    public static string GetJwtSecret(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredSecret = configuration["Jwt:Secret"];
        if (!IsUnsafeSecret(configuredSecret))
        {
            return configuredSecret!;
        }

        if (environment.IsDevelopment())
        {
            return DevelopmentSecret;
        }

        throw new InvalidOperationException(
            "Jwt:Secret must be configured for non-development environments and must not use a placeholder value.");
    }

    /// <summary>Detects missing, short, or known placeholder JWT secrets.</summary>
    public static bool IsUnsafeSecret(string? secret)
    {
        return string.IsNullOrWhiteSpace(secret)
            || secret.Length < 32
            || PlaceholderSecrets.Contains(secret);
    }
}
