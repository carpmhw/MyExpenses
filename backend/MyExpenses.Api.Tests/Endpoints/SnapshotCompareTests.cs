using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class SnapshotCompareTests
{
    /// <summary>Verifies mixed legacy and complete snapshots compare asset totals rather than incompatible net values.</summary>
    [Fact]
    public async Task CompareSnapshots_MixedBasisUsesClearlyLabeledAssetComparison()
    {
        await using var app = await CreateAppAsync();
        int legacyId;
        int completeId;
        using (var scope = app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var legacy = new SnapshotBatch
            {
                Name = "legacy",
                SnapshotDate = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
                TotalAssets = 1000m,
                TotalNetWorth = 1000m,
                NetWorthBasis = NetWorthBasis.AssetsOnly,
            };
            var complete = new SnapshotBatch
            {
                Name = "complete",
                SnapshotDate = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                TotalAssets = 1200m,
                TotalLiabilities = 100m,
                TotalNetWorth = 1100m,
                NetWorthBasis = NetWorthBasis.AssetsMinusLiabilities,
            };
            db.SnapshotBatches.AddRange(legacy, complete);
            await db.SaveChangesAsync();
            legacyId = legacy.Id;
            completeId = complete.Id;
        }

        var response = await app.App.GetTestClient().GetAsync($"/api/snapshots/{legacyId}/compare/{completeId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var differences = body.RootElement.GetProperty("differences");
        Assert.Equal("AssetsOnly", differences.GetProperty("netWorthBasis").GetString());
        Assert.Equal(1000m, differences.GetProperty("netWorth").GetProperty("old").GetDecimal());
        Assert.Equal(1200m, differences.GetProperty("netWorth").GetProperty("new").GetDecimal());
        Assert.Equal(200m, differences.GetProperty("assets").GetProperty("change").GetDecimal());
        Assert.Equal(JsonValueKind.Null, differences.GetProperty("liabilities").ValueKind);
    }

    /// <summary>驗證快照比較使用保存的 TWD 值並標示配對帳戶幣別變更。</summary>
    [Fact]
    public async Task CompareSnapshots_UsesConvertedBalanceAndCurrencyChangeFlag()
    {
        await using var app = await CreateAppAsync();
        int oldId;
        int newId;
        using (var scope = app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var oldSnapshot = new SnapshotBatch
            {
                Name = "old",
                SnapshotDate = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
                TotalAssets = 10000m,
                TotalLiabilities = 0m,
                TotalNetWorth = 10000m,
                NetWorthBasis = NetWorthBasis.AssetsMinusLiabilities,
                TotalBankBalance = 10000m,
                BankDetails =
                [
                    new BankDetail
                    {
                        BankName = "跨國銀行",
                        AccountNumber = "123",
                        AccountType = "活期",
                        CurrencyCode = "USD",
                        Balance = 310m,
                        ExchangeRate = 0.031m,
                        ConvertedBalance = 10000m,
                    },
                ],
            };
            var newSnapshot = new SnapshotBatch
            {
                Name = "new",
                SnapshotDate = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                TotalAssets = 12000m,
                TotalLiabilities = 0m,
                TotalNetWorth = 12000m,
                NetWorthBasis = NetWorthBasis.AssetsMinusLiabilities,
                TotalBankBalance = 12000m,
                BankDetails =
                [
                    new BankDetail
                    {
                        BankName = "跨國銀行",
                        AccountNumber = "123",
                        AccountType = "活期",
                        CurrencyCode = "JPY",
                        Balance = 150000m,
                        ExchangeRate = 12.5m,
                        ConvertedBalance = 12000m,
                    },
                ],
            };
            db.SnapshotBatches.AddRange(oldSnapshot, newSnapshot);
            await db.SaveChangesAsync();
            oldId = oldSnapshot.Id;
            newId = newSnapshot.Id;
        }

        var response = await app.App.GetTestClient().GetAsync($"/api/snapshots/{oldId}/compare/{newId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var bankDifference = body.RootElement
            .GetProperty("differences")
            .GetProperty("bankDetails")
            .EnumerateArray()
            .Single();
        Assert.Equal(10000m, bankDifference.GetProperty("oldConvertedBalance").GetDecimal());
        Assert.Equal(12000m, bankDifference.GetProperty("newConvertedBalance").GetDecimal());
        Assert.Equal(2000m, bankDifference.GetProperty("change").GetDecimal());
        Assert.Equal("USD", bankDifference.GetProperty("oldCurrencyCode").GetString());
        Assert.Equal("JPY", bankDifference.GetProperty("newCurrencyCode").GetString());
        Assert.True(bankDifference.GetProperty("currencyChanged").GetBoolean());
        Assert.Equal(2000m, body.RootElement.GetProperty("differences").GetProperty("bankBalance").GetProperty("change").GetDecimal());
    }

    /// <summary>Creates a minimal test host for snapshot comparison endpoint tests.</summary>
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
        builder.Services.Configure<TimeZoneOptions>(_ => { });
        builder.Services.AddSingleton<TimeZoneService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
        }
        app.MapSnapshotEndpoints();
        await app.StartAsync();
        return new TestApp(app, connection);
    }

    private sealed record TestApp(WebApplication App, SqliteConnection Connection) : IAsyncDisposable
    {
        /// <summary>Disposes the test host and its in-memory database connection.</summary>
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
