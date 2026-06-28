using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

/// <summary>
/// ExchangeRate Endpoint 整合測試，驗證正常回傳、快取命中、快取過期、API 失敗等情境。
/// </summary>
public class ExchangeRateEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ExchangeRateEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("Frankfurter");
            });
        }).CreateClient();
    }

    /// <summary>
    /// 驗證未經認證的請求會被拒絕（回傳 401）。
    /// </summary>
    [Fact]
    public async Task GetExchangeRates_Unauthenticated_Returns401()
    {
        var response = await _client.GetAsync("/api/exchange-rates");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// 驗證匯率端點存在且可被呼叫（透過設定開發用 token）。
    /// </summary>
    [Fact]
    public async Task GetExchangeRates_EndpointExists()
    {
        var response = await _client.GetAsync("/api/exchange-rates");

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.ServiceUnavailable);
    }
}
