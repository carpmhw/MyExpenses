using System.Net;
using System.Net.Http.Json;
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
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public sealed class BankAccountCrudApiTests
{
    /// <summary>驗證建立帳戶省略貨幣代碼時會保存 TWD。</summary>
    [Fact]
    public async Task CreateBankAccount_DefaultsMissingCurrencyToTwd()
    {
        await using var app = await CreateAppAsync();

        var response = await app.App.GetTestClient().PostAsJsonAsync("/api/bank-accounts", new
        {
            bankName = "測試銀行",
            accountNumber = "12345",
            balance = 100m,
            accountType = "活期",
        });

        await AssertStatusCodeAsync(HttpStatusCode.Created, response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TWD", body.RootElement.GetProperty("currencyCode").GetString());
    }

    /// <summary>驗證建立帳戶會 trim 並轉換小寫貨幣代碼。</summary>
    [Fact]
    public async Task CreateBankAccount_NormalizesCurrencyCode()
    {
        await using var app = await CreateAppAsync();

        var response = await app.App.GetTestClient().PostAsJsonAsync("/api/bank-accounts", new
        {
            bankName = "美元銀行",
            accountNumber = "12345",
            balance = 3000m,
            accountType = "活期",
            currencyCode = " usd ",
        });

        await AssertStatusCodeAsync(HttpStatusCode.Created, response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("USD", body.RootElement.GetProperty("currencyCode").GetString());
    }

    /// <summary>驗證建立帳戶拒絕不支援貨幣且不留下資料列。</summary>
    [Fact]
    public async Task CreateBankAccount_RejectsUnsupportedCurrency()
    {
        await using var app = await CreateAppAsync();

        var response = await app.App.GetTestClient().PostAsJsonAsync("/api/bank-accounts", new
        {
            bankName = "歐元銀行",
            accountNumber = "12345",
            balance = 3000m,
            accountType = "活期",
            currencyCode = "EUR",
        });

        await AssertStatusCodeAsync(HttpStatusCode.BadRequest, response);
        using var scope = app.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.BankAccounts.ToListAsync());
    }

    /// <summary>驗證更新貨幣只變更代碼而不自動改寫原幣餘額。</summary>
    [Fact]
    public async Task UpdateBankAccount_ChangingCurrencyPreservesBalance()
    {
        await using var app = await CreateAppAsync();
        int accountId;
        using (var scope = app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var account = new BankAccount
            {
                BankName = "美元銀行",
                AccountNumber = "12345",
                Balance = 3000m,
                CurrencyCode = "USD",
                AccountType = "活期",
            };
            db.BankAccounts.Add(account);
            await db.SaveChangesAsync();
            accountId = account.Id;
        }

        var response = await app.App.GetTestClient().PutAsJsonAsync($"/api/bank-accounts/{accountId}", new
        {
            bankName = "美元銀行",
            accountNumber = "12345",
            balance = 3000m,
            accountType = "活期",
            currencyCode = " jpy ",
        });

        await AssertStatusCodeAsync(HttpStatusCode.OK, response);
        using var scopeAfter = app.App.Services.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await dbAfter.BankAccounts.SingleAsync();
        Assert.Equal("JPY", stored.CurrencyCode);
        Assert.Equal(3000m, stored.Balance);
    }

    /// <summary>建立使用 SQLite 與銀行帳戶 endpoint 的最小測試 host。</summary>
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
        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
        }
        app.MapBankAccountEndpoints();
        await app.StartAsync();
        return new TestApp(app, connection);
    }

    /// <summary>驗證 response status 並在失敗時保留安全 response body。</summary>
    private static async Task AssertStatusCodeAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected,
            $"預期 {(int)expected} {expected}，實際 {(int)response.StatusCode} {response.StatusCode}。Body: {body}");
    }

    /// <summary>封裝測試 host 與 SQLite 連線的非同步釋放。</summary>
    private sealed record TestApp(WebApplication App, SqliteConnection Connection) : IAsyncDisposable
    {
        /// <summary>釋放測試 host 與資料庫連線。</summary>
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
