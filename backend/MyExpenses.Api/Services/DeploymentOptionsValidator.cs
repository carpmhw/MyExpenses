using System.Net;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Options;

namespace MyExpenses.Api.Services;

/// <summary>驗證部署模式、bind address、cookie 與 trusted proxy 的安全組合。</summary>
public sealed class DeploymentOptionsValidator : IValidateOptions<DeploymentOptions>
{
    /// <summary>驗證 deployment options 並回傳不含敏感值的設定錯誤。</summary>
    public ValidateOptionsResult Validate(string? name, DeploymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidateMode(options, failures);
        ValidateBindAddress(options, failures);
        ValidatePublicOrigin(options, failures);
        ValidateTrustedProxyEntries(options, failures);

        if (options.Mode == DeploymentMode.Local && options.SecureCookies)
        {
            failures.Add(
                "Deployment:SecureCookies must be false in Local mode because Local mode uses HTTP.");
        }

        if (options.Mode == DeploymentMode.Remote)
        {
            if (string.IsNullOrWhiteSpace(options.PublicOrigin))
            {
                failures.Add("Deployment:PublicOrigin is required in Remote mode.");
            }
            else if (!IsHttpsOrigin(options.PublicOrigin))
            {
                failures.Add(
                    "Deployment:PublicOrigin must be an absolute HTTPS origin in Remote mode.");
            }

            if (!options.SecureCookies)
            {
                failures.Add("Deployment:SecureCookies must be true in Remote mode.");
            }

            if ((options.TrustedProxies?.Count ?? 0) == 0 &&
                (options.TrustedNetworks?.Count ?? 0) == 0)
            {
                failures.Add(
                    "Deployment:TrustedProxies or Deployment:TrustedNetworks must contain at least one explicit trusted proxy entry in Remote mode.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>在 application build 前執行 deployment validation，失敗時停止啟動。</summary>
    public static void ThrowIfInvalid(DeploymentOptions options)
    {
        var result = new DeploymentOptionsValidator().Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            options);
        if (result.Failed)
        {
            throw new InvalidOperationException(
                $"Invalid deployment configuration: {string.Join(" ", result.Failures ?? [])}");
        }
    }

    /// <summary>驗證 enum mode 並拒絕未定義的設定值。</summary>
    private static void ValidateMode(DeploymentOptions options, ICollection<string> failures)
    {
        if (!Enum.IsDefined(typeof(DeploymentMode), options.Mode))
        {
            failures.Add("Deployment:Mode must be Local, Lan, or Remote.");
        }
    }

    /// <summary>驗證 bind address 是明確 IP，並依模式限制 loopback 暴露範圍。</summary>
    private static void ValidateBindAddress(
        DeploymentOptions options,
        ICollection<string> failures)
    {
        if (!IPAddress.TryParse(options.BindAddress, out var bindAddress))
        {
            failures.Add("Deployment:BindAddress must be a valid IP address.");
            return;
        }

        if (options.Mode == DeploymentMode.Local && !IPAddress.IsLoopback(bindAddress))
        {
            failures.Add("Deployment:BindAddress must be a loopback address in Local mode.");
        }

        if (options.Mode == DeploymentMode.Lan && IPAddress.IsLoopback(bindAddress))
        {
            failures.Add("Deployment:BindAddress must be a non-loopback address in LAN mode.");
        }
    }

    /// <summary>驗證非 Remote public origin 若存在也必須是沒有 path 的 absolute origin。</summary>
    private static void ValidatePublicOrigin(
        DeploymentOptions options,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.PublicOrigin) || options.Mode == DeploymentMode.Remote)
        {
            return;
        }

        if (!IsOrigin(options.PublicOrigin))
        {
            failures.Add("Deployment:PublicOrigin must be an absolute HTTP or HTTPS origin.");
        }
    }

    /// <summary>驗證所有 proxy IP 與 CIDR network，避免 forwarding registration 接受隱含信任。</summary>
    private static void ValidateTrustedProxyEntries(
        DeploymentOptions options,
        ICollection<string> failures)
    {
        foreach (var proxy in options.TrustedProxies ?? [])
        {
            if (!IPAddress.TryParse(proxy, out var address) ||
                address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.IPv6Any))
            {
                failures.Add("Deployment:TrustedProxies contains an invalid or unrestricted IP address.");
            }
        }

        foreach (var network in options.TrustedNetworks ?? [])
        {
            if (!TryParseNetwork(network, out _, out var prefixLength, out var addressFamilyBits) ||
                prefixLength == 0 && addressFamilyBits > 0)
            {
                failures.Add("Deployment:TrustedNetworks contains an invalid or unrestricted network.");
            }
        }
    }

    /// <summary>判斷文字是否為沒有 path、query 或 fragment 的 absolute origin。</summary>
    private static bool IsOrigin(string value)
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

    /// <summary>判斷文字是否為 Remote 模式所需的 HTTPS origin。</summary>
    private static bool IsHttpsOrigin(string value)
    {
        return IsOrigin(value)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>解析 CIDR network，並回傳 forwarding middleware 可直接使用的 IPNetwork。</summary>
    internal static bool TryParseNetwork(
        string? value,
        out IPNetwork network,
        out int prefixLength,
        out int addressFamilyBits)
    {
        network = default!;
        prefixLength = -1;
        addressFamilyBits = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var prefix))
        {
            return false;
        }

        addressFamilyBits = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? 32
            : prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? 128
                : 0;
        if (addressFamilyBits == 0 ||
            !int.TryParse(parts[1], out prefixLength) ||
            prefixLength < 0 ||
            prefixLength > addressFamilyBits)
        {
            return false;
        }

        network = new IPNetwork(prefix, prefixLength);
        return true;
    }
}
