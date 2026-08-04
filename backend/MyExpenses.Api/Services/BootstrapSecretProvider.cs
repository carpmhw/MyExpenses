using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using MyExpenses.Api.Options;

namespace MyExpenses.Api.Services;

public static class BootstrapSecretProvider
{
    public const string HeaderName = "X-MyExpenses-Bootstrap-Secret";
    public const int MinimumSecretLength = 32;

    private static readonly HashSet<string> PlaceholderSecrets = new(StringComparer.Ordinal)
    {
        "change-this-to-a-secure-random-bootstrap-secret",
        "placeholder-bootstrap-secret",
        "replace-with-a-random-bootstrap-secret",
    };

    /// <summary>只在 Production 的未初始化安裝檢查 operator 提供的 bootstrap secret。</summary>
    public static void ValidateForStartup(
        BootstrapOptions options,
        bool initialized,
        IHostEnvironment environment)
    {
        if (initialized || !environment.IsProduction())
            return;

        if (IsUnsafeSecret(options.Secret))
        {
            throw new InvalidOperationException(
                "Bootstrap:Secret must be configured with a strong, non-placeholder value before an uninitialized Production installation can start.");
        }
    }

    /// <summary>判斷 secret 是否缺漏、過短或使用已知 placeholder。</summary>
    public static bool IsUnsafeSecret(string? secret)
    {
        return string.IsNullOrWhiteSpace(secret)
            || secret.Length < MinimumSecretLength
            || PlaceholderSecrets.Contains(secret);
    }

    /// <summary>以固定時間比較驗證 request header 內容，避免記錄或保存 bootstrap secret。</summary>
    public static bool Matches(string? configuredSecret, string? presentedSecret)
    {
        if (IsUnsafeSecret(configuredSecret) || string.IsNullOrEmpty(presentedSecret))
            return false;

        var configuredBytes = Encoding.UTF8.GetBytes(configuredSecret!);
        var presentedBytes = Encoding.UTF8.GetBytes(presentedSecret);
        return CryptographicOperations.FixedTimeEquals(configuredBytes, presentedBytes);
    }
}
