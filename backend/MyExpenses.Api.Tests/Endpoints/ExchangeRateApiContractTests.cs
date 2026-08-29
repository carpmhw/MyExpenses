using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public sealed class ExchangeRateApiContractTests
{
    /// <summary>驗證已認證匯率 API 回傳 TWD 基準與 stale 狀態。</summary>
    [Fact]
    public async Task GetExchangeRates_ReturnsTwdBaseAndStaleMetadata()
    {
        await using var app = await CreateAppAsync(new Dictionary<string, decimal>
        {
            ["TWD"] = 1m,
            ["USD"] = 0.031m,
        });

        var response = await app.App.GetTestClient().GetAsync("/api/exchange-rates");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TWD", body.RootElement.GetProperty("base").GetString());
        Assert.Equal(0.031m, body.RootElement.GetProperty("rates").GetProperty("USD").GetDecimal());
        Assert.False(body.RootElement.GetProperty("isStale").GetBoolean());
        Assert.True(body.RootElement.TryGetProperty("updatedAt", out _));
    }

    /// <summary>驗證匯率 provider 沒有快取時 API 回傳服務不可用。</summary>
    [Fact]
    public async Task GetExchangeRates_ReturnsServiceUnavailableWhenProviderFailsWithoutCache()
    {
        await using var app = await CreateAppAsync(new InvalidOperationException("provider failed"));

        var response = await app.App.GetTestClient().GetAsync("/api/exchange-rates");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>建立使用 fake authentication 與固定匯率 provider 的最小 API host。</summary>
    private static async Task<TestApp> CreateAppAsync(object providerResponse)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName,
                _ => { });
        builder.Services.AddAuthorization();
        var provider = providerResponse is Exception exception
            ? new FixedExchangeRateProvider(exception)
            : new FixedExchangeRateProvider(new ExchangeRateProviderResult(
                (IReadOnlyDictionary<string, decimal>)providerResponse));
        builder.Services.AddSingleton<IExchangeRateProvider>(provider);
        builder.Services.AddSingleton<IExchangeRateService>(services =>
            new ExchangeRateService(
                services.GetRequiredService<IExchangeRateProvider>(),
                new FixedTimeProvider(DateTime.UtcNow)));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapExchangeRateEndpoints();
        await app.StartAsync();
        return new TestApp(app);
    }

    /// <summary>封裝 API host 的非同步釋放。</summary>
    private sealed record TestApp(WebApplication App) : IAsyncDisposable
    {
        /// <summary>釋放測試 API host。</summary>
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>回傳固定 provider 結果或模擬 provider 失敗。</summary>
    private sealed class FixedExchangeRateProvider : IExchangeRateProvider
    {
        private readonly ExchangeRateProviderResult? _result;
        private readonly Exception? _exception;

        /// <summary>初始化 provider 結果。</summary>
        public FixedExchangeRateProvider(ExchangeRateProviderResult result) => _result = result;

        /// <summary>初始化 provider 例外。</summary>
        public FixedExchangeRateProvider(Exception exception) => _exception = exception;

        /// <summary>回傳固定結果或拋出測試例外。</summary>
        public Task<ExchangeRateProviderResult> FetchAsync(CancellationToken cancellationToken = default)
            => _exception is not null ? Task.FromException<ExchangeRateProviderResult>(_exception) : Task.FromResult(_result!);
    }

    /// <summary>提供固定 UTC 時間供服務快取測試使用。</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        /// <summary>初始化固定 UTC 時間。</summary>
        public FixedTimeProvider(DateTime utcNow)
        {
            _utcNow = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
        }

        /// <summary>回傳固定 UTC instant。</summary>
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    /// <summary>永遠以測試 user 建立認證 principal 的 handler。</summary>
    private sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "ExchangeRateTest";

        /// <summary>初始化測試認證 handler。</summary>
        public TestAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        /// <summary>回傳固定已認證 principal。</summary>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "1")],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
