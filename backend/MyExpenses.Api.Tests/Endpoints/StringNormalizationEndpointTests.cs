using System.Net;
using System.Net.Http.Json;
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

public class StringNormalizationEndpointTests
{
    /// <summary>Verifies category create and update responses expose normalized persisted strings.</summary>
    [Fact]
    public async Task CategoryCreateAndUpdate_ReturnNormalizedStrings()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();

        var createResponse = await client.PostAsJsonAsync("/api/categories", new
        {
            name = "  Food  and  Drinks  ",
            type = CategoryType.Expense,
            icon = "  Utensils  ",
            color = "  #FFFFFF  ",
            sortOrder = 1,
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<Category>();
        Assert.NotNull(created);
        Assert.Equal("Food  and  Drinks", created.Name);
        Assert.Equal("Utensils", created.Icon);
        Assert.Equal("#FFFFFF", created.Color);

        var updateResponse = await client.PutAsJsonAsync($"/api/categories/{created.Id}", new
        {
            name = "  Updated  Category  ",
            type = CategoryType.Income,
            icon = "  Wallet  ",
            color = " \t ",
            sortOrder = 2,
        });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<Category>();
        Assert.NotNull(updated);
        Assert.Equal("Updated  Category", updated.Name);
        Assert.Equal("Wallet", updated.Icon);
        Assert.Equal(string.Empty, updated.Color);
    }

    /// <summary>Creates an in-memory endpoint host backed by the application database context.</summary>
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

        app.MapCategoryEndpoints();
        await app.StartAsync();

        return new TestApp(app, connection);
    }

    /// <summary>Disposes the endpoint host and its in-memory database connection.</summary>
    private sealed record TestApp(WebApplication App, SqliteConnection Connection) : IAsyncDisposable
    {
        /// <summary>Stops and disposes the endpoint host and database connection.</summary>
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
