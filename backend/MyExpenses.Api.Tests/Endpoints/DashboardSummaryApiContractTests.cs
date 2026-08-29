using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public sealed class DashboardSummaryApiContractTests
{
    /// <summary>驗證 Dashboard 缺少必要外幣匯率時回傳 HTTP 503 Problem Details。</summary>
    [Fact]
    public async Task GetDashboardSummary_MissingForeignRateReturnsServiceUnavailable()
    {
        await using var app = await CreateAppAsync();
        using (var scope = app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var account = new BankAccount
            {
                BankName = "美元銀行",
                AccountNumber = "USD01",
                AccountType = "活期",
                CurrencyCode = "USD",
            };
            db.BankAccounts.Add(account);
            await db.SaveChangesAsync();
            db.Withdrawals.Add(new Withdrawal
            {
                BankAccountId = account.Id,
                Amount = 310m,
                Date = new DateOnly(2026, 6, 5),
            });
            await db.SaveChangesAsync();
        }

        var response = await app.App.GetTestClient()
            .GetAsync("/api/reports/dashboard-summary?year=2026&month=6");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("缺少 USD 匯率", body.RootElement.GetProperty("detail").GetString());
    }

    /// <summary>建立 SQLite、固定缺率服務與 Dashboard endpoint 的最小測試 host。</summary>
    private static async Task<TestApp> CreateAppAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddSingleton(new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions
        {
            Default = "Asia/Taipei",
        })));
        builder.Services.AddSingleton<IExchangeRateService>(new MissingUsdExchangeRateService());
        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
        }
        app.MapReportEndpoints();
        await app.StartAsync();
        return new TestApp(app, connection);
    }

    /// <summary>封裝測試 host 與 SQLite 連線的非同步釋放。</summary>
    private sealed record TestApp(WebApplication App, SqliteConnection Connection) : IAsyncDisposable
    {
        /// <summary>釋放測試 host 與 SQLite 連線。</summary>
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>回傳只有 TWD identity 的固定匯率服務。</summary>
    private sealed class MissingUsdExchangeRateService : IExchangeRateService
    {
        /// <summary>回傳不含 USD 的固定匯率 snapshot。</summary>
        public Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ExchangeRateSnapshot(
                CurrencyPolicy.BaseCurrencyCode,
                new Dictionary<string, decimal>
                {
                    [CurrencyPolicy.BaseCurrencyCode] = 1m,
                },
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                false));

        /// <summary>缺少 USD 匯率時只允許 TWD identity 換算。</summary>
        public decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot)
            => currencyCode == CurrencyPolicy.BaseCurrencyCode ? amount : null;

        /// <summary>回傳固定匯率服務是否能完成 TWD identity 換算。</summary>
        public bool TryConvertToBase(
            decimal amount,
            string currencyCode,
            ExchangeRateSnapshot snapshot,
            out decimal convertedAmount)
        {
            var converted = ConvertToBase(amount, currencyCode, snapshot);
            convertedAmount = converted.GetValueOrDefault();
            return converted.HasValue;
        }
    }
}
