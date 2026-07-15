using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class TimeZoneDefaultsEndpointsTests
{
    /// <summary>Verifies an omitted transaction date uses the deterministic system-local date.</summary>
    [Fact]
    public async Task PostTransaction_UsesSystemLocalDateWhenDateIsOmitted()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/transactions", new
        {
            type = TransactionType.Expense,
            amount = 100m,
            categoryId = app.CategoryId,
        });

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, responseBody);
        var transaction = await response.Content.ReadFromJsonAsync<Transaction>();
        Assert.Equal(new DateOnly(2099, 1, 1), transaction!.Date);
    }

    /// <summary>Verifies report defaults use the current year in the configured system time zone.</summary>
    [Fact]
    public async Task IncomeExpenseTrend_UsesSystemLocalCurrentYearWhenDatesAreOmitted()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();

        var response = await client.GetAsync("/api/reports/income-expense-trend");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var months = await response.Content.ReadFromJsonAsync<List<TrendResponse>>();
        Assert.Equal("2099/01", Assert.Single(months!).Month);
    }

    /// <summary>Verifies monthly summary defaults use the current system-local month.</summary>
    [Fact]
    public async Task MonthlySummary_UsesSystemLocalCurrentMonthWhenPeriodIsOmitted()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();

        var response = await client.GetFromJsonAsync<MonthlySummaryResponse>("/api/reports/monthly-summary");

        Assert.Equal(100m, response!.TotalExpense);
    }

    /// <summary>Creates a test host with a fixed future instant in the system time zone.</summary>
    private static async Task<TestApp> CreateAppAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.Configure<TimeZoneOptions>(options => options.Default = "Asia/Taipei");
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTime(2098, 12, 31, 16, 30, 0, DateTimeKind.Utc)));
        builder.Services.AddSingleton<TimeZoneService>();

        var app = builder.Build();
        app.MapTransactionEndpoints();
        app.MapReportEndpoints();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            var category = new Category
            {
                Name = "測試支出",
                Type = CategoryType.Expense,
                Icon = "Circle",
                Color = "#000000",
                SortOrder = 1,
            };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
            db.Transactions.Add(new Transaction
            {
                Type = TransactionType.Expense,
                Amount = 100m,
                Date = new DateOnly(2099, 1, 1),
                CategoryId = category.Id,
            });
            await db.SaveChangesAsync();
            var testApp = new TestApp(app, connection, category.Id);
            await app.StartAsync();
            return testApp;
        }
    }

    /// <summary>Provides a deterministic UTC clock for endpoint defaults.</summary>
    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));

        /// <summary>Returns the fixed UTC instant.</summary>
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    /// <summary>Represents the report trend response shape used by this test.</summary>
    private sealed record TrendResponse(string Month);

    /// <summary>Represents the monthly summary response shape used by this test.</summary>
    private sealed record MonthlySummaryResponse(decimal TotalIncome, decimal TotalExpense, decimal TotalBankBalance);

    /// <summary>Disposes the endpoint test host and database connection.</summary>
    private sealed record TestApp(WebApplication App, SqliteConnection Connection, int CategoryId) : IAsyncDisposable
    {
        /// <summary>Disposes test resources.</summary>
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
