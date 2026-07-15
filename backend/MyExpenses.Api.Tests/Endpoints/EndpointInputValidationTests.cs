using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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

public class EndpointInputValidationTests
{
    /// <summary>Verifies category list page sizes are capped before querying.</summary>
    [Fact]
    public async Task GetCategories_CapsOversizedPageSize()
    {
        await using var app = await CreateAppAsync();
        await SeedCategoryAndTransactionDataAsync(app.App, PaginationPolicy.MaxPageSize + 5);
        var client = app.App.GetTestClient();

        var response = await client.GetAsync($"/api/categories?page=1&pageSize={PaginationPolicy.MaxPageSize + 500}");

        await AssertStatusCodeAsync(HttpStatusCode.OK, response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(PaginationPolicy.MaxPageSize, body.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(PaginationPolicy.MaxPageSize, body.RootElement.GetProperty("items").GetArrayLength());
    }

    /// <summary>Verifies transaction list limits are capped before querying.</summary>
    [Fact]
    public async Task GetTransactions_CapsOversizedLimit()
    {
        await using var app = await CreateAppAsync();
        await SeedCategoryAndTransactionDataAsync(app.App, PaginationPolicy.MaxPageSize + 5);
        var client = app.App.GetTestClient();

        var response = await client.GetAsync($"/api/transactions?limit={PaginationPolicy.MaxPageSize + 500}");

        await AssertStatusCodeAsync(HttpStatusCode.OK, response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(PaginationPolicy.MaxPageSize, body.RootElement.GetArrayLength());
    }

    /// <summary>Verifies installment creation rejects zero periods before per-period calculations.</summary>
    [Fact]
    public async Task PostInstallment_RejectsZeroPeriods()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/installments", new
        {
            totalAmount = 1200m,
            periods = 0,
            purchaseDate = "2026-01-01",
            description = "invalid installment",
        });

        await AssertStatusCodeAsync(HttpStatusCode.BadRequest, response);
    }

    /// <summary>Verifies installment updates reject zero periods before per-period calculations.</summary>
    [Fact]
    public async Task PutInstallment_RejectsZeroPeriods()
    {
        await using var app = await CreateAppAsync();
        var installmentId = await SeedInstallmentAsync(app.App);
        var client = app.App.GetTestClient();

        var response = await client.PutAsJsonAsync($"/api/installments/{installmentId}", new
        {
            totalAmount = 1200m,
            periods = 0,
            purchaseDate = "2026-01-01",
            description = "invalid installment",
        });

        await AssertStatusCodeAsync(HttpStatusCode.BadRequest, response);
    }

    /// <summary>Creates a minimal endpoint test application with an in-memory SQLite database.</summary>
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
        {
            options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        app.MapCategoryEndpoints();
        app.MapTransactionEndpoints();
        app.MapInstallmentEndpoints();
        await app.StartAsync();

        return new TestApp(app, connection);
    }

    /// <summary>Seeds categories and transactions for list endpoint pagination tests.</summary>
    private static async Task SeedCategoryAndTransactionDataAsync(WebApplication app, int itemCount)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var categories = Enumerable.Range(1, itemCount)
            .Select(index => new Category
            {
                Name = $"Category {index}",
                Type = CategoryType.Expense,
                Icon = "Circle",
                Color = "#000000",
                SortOrder = index,
            })
            .ToList();
        db.Categories.AddRange(categories);
        await db.SaveChangesAsync();

        var transactions = categories.Select((category, index) => new Transaction
        {
            Type = TransactionType.Expense,
            Amount = index + 1,
            Date = new DateOnly(2026, 1, 1).AddDays(index),
            Description = $"Transaction {index + 1}",
            CategoryId = category.Id,
        });
        db.Transactions.AddRange(transactions);
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds one valid installment for update validation tests.</summary>
    private static async Task<int> SeedInstallmentAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var installment = new Installment
        {
            TotalAmount = 1200m,
            Periods = 3,
            PerPeriod = 400m,
            RemainingPeriods = 3,
            PurchaseDate = new DateOnly(2026, 1, 1),
            CreatedAt = DateTime.UtcNow,
            Status = InstallmentStatus.Active,
            Description = "valid installment",
        };
        db.Installments.Add(installment);
        await db.SaveChangesAsync();
        return installment.Id;
    }

    /// <summary>Asserts a response status code while including the response body in failure output.</summary>
    private static async Task AssertStatusCodeAsync(HttpStatusCode expectedStatusCode, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expectedStatusCode,
            $"Expected {(int)expectedStatusCode} {expectedStatusCode}, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
    }

    private sealed record TestApp(WebApplication App, SqliteConnection Connection) : IAsyncDisposable
    {
        /// <summary>Disposes the test host and SQLite connection.</summary>
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
