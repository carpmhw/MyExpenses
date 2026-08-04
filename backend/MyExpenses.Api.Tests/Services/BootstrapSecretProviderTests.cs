using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class BootstrapSecretProviderTests
{
    /// <summary>驗證未初始化的 Production installation 會拒絕缺少 bootstrap 設定。</summary>
    [Fact]
    public void ValidateForStartup_RejectsMissingSecretWhenUninitialized()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BootstrapSecretProvider.ValidateForStartup(
                new BootstrapOptions(),
                initialized: false,
                new TestHostEnvironment { EnvironmentName = Environments.Production }));

        Assert.Contains("Bootstrap:Secret", error.Message);
        Assert.DoesNotContain("secret-value", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>驗證過短或 placeholder bootstrap value 會被拒絕。</summary>
    [Theory]
    [InlineData("short")]
    [InlineData("change-this-to-a-secure-random-bootstrap-secret")]
    [InlineData("placeholder-bootstrap-secret")]
    public void ValidateForStartup_RejectsUnsafeSecretWhenUninitialized(string secret)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BootstrapSecretProvider.ValidateForStartup(
                new BootstrapOptions { Secret = secret },
                initialized: false,
                new TestHostEnvironment { EnvironmentName = Environments.Production }));

        Assert.Contains("Bootstrap:Secret", error.Message);
    }

    /// <summary>驗證初始化前可接受 operator 提供的強度足夠 bootstrap value。</summary>
    [Fact]
    public void ValidateForStartup_AllowsValidSecretWhenUninitialized()
    {
        var options = new BootstrapOptions
        {
            Secret = "bootstrap-secret-generated-by-the-operator-123456",
        };

        BootstrapSecretProvider.ValidateForStartup(
            options,
            initialized: false,
            new TestHostEnvironment { EnvironmentName = Environments.Production });
    }

    /// <summary>驗證已初始化 installation 移除 bootstrap 設定後仍可啟動。</summary>
    [Fact]
    public void ValidateForStartup_AllowsMissingSecretWhenInitialized()
    {
        BootstrapSecretProvider.ValidateForStartup(
            new BootstrapOptions(),
            initialized: true,
            new TestHostEnvironment { EnvironmentName = Environments.Production });
    }

    /// <summary>驗證 Development startup 不要求 Production bootstrap 設定。</summary>
    [Fact]
    public void ValidateForStartup_AllowsMissingSecretInDevelopment()
    {
        BootstrapSecretProvider.ValidateForStartup(
            new BootstrapOptions(),
            initialized: false,
            new TestHostEnvironment { EnvironmentName = Environments.Development });
    }

    /// <summary>驗證 bootstrap comparison 只接受完全相同的 configured value。</summary>
    [Fact]
    public void Matches_RequiresExactConfiguredSecret()
    {
        const string secret = "bootstrap-secret-generated-by-the-operator-123456";

        Assert.True(BootstrapSecretProvider.Matches(secret, secret));
        Assert.False(BootstrapSecretProvider.Matches(secret, secret + "-wrong"));
        Assert.False(BootstrapSecretProvider.Matches(secret, null));
    }

    private sealed class TestHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MyExpenses.Api.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
