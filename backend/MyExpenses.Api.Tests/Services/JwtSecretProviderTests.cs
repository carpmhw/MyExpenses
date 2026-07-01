using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class JwtSecretProviderTests
{
    /// <summary>Verifies non-development environments reject missing JWT secrets.</summary>
    [Fact]
    public void GetJwtSecret_ThrowsInProductionWhenSecretIsMissing()
    {
        var configuration = CreateConfiguration(null);
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Production };

        var error = Assert.Throws<InvalidOperationException>(() =>
            JwtSecretProvider.GetJwtSecret(configuration, environment));

        Assert.Contains("Jwt:Secret must be configured", error.Message);
    }

    /// <summary>Verifies non-development environments reject known placeholder JWT secrets.</summary>
    [Fact]
    public void GetJwtSecret_ThrowsInProductionWhenSecretIsPlaceholder()
    {
        var configuration = CreateConfiguration("change-this-to-a-secure-random-key-at-least-32-characters");
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Production };

        var error = Assert.Throws<InvalidOperationException>(() =>
            JwtSecretProvider.GetJwtSecret(configuration, environment));

        Assert.Contains("Jwt:Secret must be configured", error.Message);
    }

    /// <summary>Verifies development can run without local secret setup.</summary>
    [Fact]
    public void GetJwtSecret_ReturnsDevelopmentSecretWhenDevelopmentSecretIsMissing()
    {
        var configuration = CreateConfiguration(null);
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Development };

        var secret = JwtSecretProvider.GetJwtSecret(configuration, environment);

        Assert.Equal(JwtSecretProvider.DevelopmentSecret, secret);
    }

    /// <summary>Verifies valid configured secrets are returned unchanged.</summary>
    [Fact]
    public void GetJwtSecret_ReturnsConfiguredSecretWhenSecretIsValid()
    {
        const string configuredSecret = "valid-production-secret-with-at-least-32-chars";
        var configuration = CreateConfiguration(configuredSecret);
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Production };

        var secret = JwtSecretProvider.GetJwtSecret(configuration, environment);

        Assert.Equal(configuredSecret, secret);
    }

    /// <summary>Builds an in-memory configuration with an optional JWT secret.</summary>
    private static IConfiguration CreateConfiguration(string? secret)
    {
        var values = new Dictionary<string, string?>();
        if (secret is not null)
        {
            values["Jwt:Secret"] = secret;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
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
