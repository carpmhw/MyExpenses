using Microsoft.Extensions.Options;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class DeploymentOptionsTests
{
    /// <summary>驗證未提供設定時使用 localhost-only 的 Local 預設值。</summary>
    [Fact]
    public void DefaultOptions_UseLocalLoopbackAndInsecureCookies()
    {
        var options = new DeploymentOptions();

        var result = Validate(options);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
        Assert.Equal(DeploymentMode.Local, options.Mode);
        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.False(options.SecureCookies);
    }

    /// <summary>驗證 Local 模式不能藉由非 loopback bind address 意外暴露服務。</summary>
    [Fact]
    public void LocalMode_RejectsNonLoopbackBindAddress()
    {
        var options = new DeploymentOptions
        {
            BindAddress = "192.168.1.20",
        };

        AssertInvalid(options, "loopback");
    }

    /// <summary>驗證 LAN 模式接受明確指定的非 loopback bind address。</summary>
    [Fact]
    public void LanMode_AcceptsExplicitNonLoopbackBindAddress()
    {
        var options = new DeploymentOptions
        {
            Mode = DeploymentMode.Lan,
            BindAddress = "192.168.1.20",
        };

        var result = Validate(options);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    /// <summary>驗證 LAN 模式必須明確使用非 loopback bind address。</summary>
    [Fact]
    public void LanMode_RejectsLoopbackBindAddress()
    {
        var options = new DeploymentOptions
        {
            Mode = DeploymentMode.Lan,
            BindAddress = "127.0.0.1",
        };

        AssertInvalid(options, "non-loopback");
    }

    /// <summary>驗證 Remote 模式接受 HTTPS public origin、Secure cookie 與 trusted proxy。</summary>
    [Fact]
    public void RemoteMode_AcceptsCompleteSecureConfiguration()
    {
        var options = new DeploymentOptions
        {
            Mode = DeploymentMode.Remote,
            PublicOrigin = "https://expenses.example.com",
            SecureCookies = true,
            TrustedProxies = ["10.0.0.2"],
        };

        var result = Validate(options);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    /// <summary>驗證 Remote 模式也接受明確指定的 trusted proxy CIDR network。</summary>
    [Fact]
    public void RemoteMode_AcceptsTrustedProxyNetwork()
    {
        var options = new DeploymentOptions
        {
            Mode = DeploymentMode.Remote,
            PublicOrigin = "https://expenses.example.com",
            SecureCookies = true,
            TrustedNetworks = ["10.0.0.0/8"],
        };

        var result = Validate(options);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    /// <summary>驗證 Remote 模式拒絕明文 public origin。</summary>
    [Fact]
    public void RemoteMode_RejectsInsecurePublicOrigin()
    {
        var options = new DeploymentOptions
        {
            Mode = DeploymentMode.Remote,
            PublicOrigin = "http://expenses.example.com",
            SecureCookies = true,
            TrustedProxies = ["10.0.0.2"],
        };

        AssertInvalid(options, "HTTPS");
    }

    /// <summary>驗證 Remote 模式拒絕缺少 Secure cookie 的設定。</summary>
    [Fact]
    public void RemoteMode_RejectsInsecureCookies()
    {
        var options = new DeploymentOptions
        {
            Mode = DeploymentMode.Remote,
            PublicOrigin = "https://expenses.example.com",
            SecureCookies = false,
            TrustedProxies = ["10.0.0.2"],
        };

        AssertInvalid(options, "SecureCookies");
    }

    /// <summary>驗證 Remote 模式拒絕沒有明確 trusted proxy 或 network allowlist 的設定。</summary>
    [Fact]
    public void RemoteMode_RejectsMissingTrustedProxyConfiguration()
    {
        var options = new DeploymentOptions
        {
            Mode = DeploymentMode.Remote,
            PublicOrigin = "https://expenses.example.com",
            SecureCookies = true,
        };

        AssertInvalid(options, "trusted proxy");
    }

    /// <summary>驗證 trusted proxy network 必須是有效 CIDR，而不是任意字串。</summary>
    [Fact]
    public void RemoteMode_RejectsInvalidTrustedNetwork()
    {
        var options = new DeploymentOptions
        {
            Mode = DeploymentMode.Remote,
            PublicOrigin = "https://expenses.example.com",
            SecureCookies = true,
            TrustedNetworks = ["not-a-network"],
        };

        AssertInvalid(options, "network");
    }

    /// <summary>使用與 startup 相同的 strongly typed validator 檢查 deployment options。</summary>
    private static ValidateOptionsResult Validate(DeploymentOptions options)
        => new DeploymentOptionsValidator().Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            options);

    /// <summary>確認 deployment options 失敗時回報不含任何 secret 的可操作錯誤。</summary>
    private static void AssertInvalid(DeploymentOptions options, string expectedMessage)
    {
        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase));
    }
}
