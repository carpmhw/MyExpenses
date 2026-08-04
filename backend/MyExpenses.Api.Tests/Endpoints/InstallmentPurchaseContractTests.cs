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
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class InstallmentPurchaseContractTests
{
    /// <summary>Verifies a missing idempotency key is rejected before a purchase is created.</summary>
    [Fact]
    public async Task PostInstallmentPurchase_RequiresIdempotencyKey()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/installment-purchases", new
        {
            transaction = new
            {
                amount = 1200m,
                date = "2026-06-20",
                description = "測試分期",
                categoryId = app.CategoryId,
                paymentMethodId = app.PaymentMethodId,
            },
            installment = new { cardId = app.CardId, periods = 3 },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            Assert.Equal("Invalid financial command", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(0, await app.CountTransactionsAsync());
    }

    /// <summary>Verifies malformed composite input is mapped to a safe validation response.</summary>
    [Fact]
    public async Task PostInstallmentPurchase_RejectsNullInstallmentPayload()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/installment-purchases", new
        {
            transaction = new
            {
                amount = 1200m,
                date = "2026-06-20",
                description = "缺少分期資料",
                categoryId = app.CategoryId,
                paymentMethodId = app.PaymentMethodId,
            },
            installment = (object?)null,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await app.CountTransactionsAsync());
    }

    /// <summary>Verifies one installment purchase creates the complete aggregate in one response.</summary>
    [Fact]
    public async Task PostInstallmentPurchase_CreatesTransactionInstallmentAndPayments()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        var request = CreatePurchaseRequest(app, 1200m);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/installment-purchases", request);

        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"Expected Created, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.True(body.RootElement.TryGetProperty("transaction", out _));
            var installment = body.RootElement.GetProperty("installment");
            Assert.Equal(3, installment.GetProperty("payments").GetArrayLength());
            Assert.Equal(12_00m, installment.GetProperty("totalAmount").GetDecimal());
        }
        Assert.Equal(1, await app.CountTransactionsAsync());
        Assert.Equal(1, await app.CountInstallmentsAsync());
        Assert.Equal(3, await app.CountPaymentsAsync());
    }

    /// <summary>Verifies retrying an identical idempotent purchase does not duplicate financial records.</summary>
    [Fact]
    public async Task PostInstallmentPurchase_RetryReturnsExistingAggregate()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        var key = Guid.NewGuid().ToString();
        var request = CreatePurchaseRequest(app, 1200m);
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var first = await client.PostAsJsonAsync("/api/installment-purchases", request);
        var second = await client.PostAsJsonAsync("/api/installment-purchases", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(1, await app.CountTransactionsAsync());
        Assert.Equal(1, await app.CountInstallmentsAsync());
        Assert.Equal(3, await app.CountPaymentsAsync());
    }

    /// <summary>Verifies reusing an idempotency key with a different payload returns a conflict.</summary>
    [Fact]
    public async Task PostInstallmentPurchase_RejectsChangedPayloadForExistingKey()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        var key = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var first = await client.PostAsJsonAsync("/api/installment-purchases", CreatePurchaseRequest(app, 1200m));
        var second = await client.PostAsJsonAsync("/api/installment-purchases", CreatePurchaseRequest(app, 1300m));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await app.CountTransactionsAsync());
    }

    /// <summary>Verifies standalone installment creation commits the complete schedule.</summary>
    [Fact]
    public async Task PostStandaloneInstallment_CreatesCompleteSchedule()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/installments", new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 3,
            purchaseDate = "2026-06-20",
            description = "獨立分期",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await app.CountInstallmentsAsync());
        Assert.Equal(3, await app.CountPaymentsAsync());
    }

    /// <summary>Verifies schedule updates preserve the original aggregate when validation fails.</summary>
    [Fact]
    public async Task PutInstallment_RejectsMissingCardWithoutChangingSchedule()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var createResponse = await client.PostAsJsonAsync("/api/installments", new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 3,
            purchaseDate = "2026-06-20",
            description = "保留排程",
        });
        using var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var installmentId = createdBody.RootElement.GetProperty("id").GetInt32();

        var response = await client.PutAsJsonAsync($"/api/installments/{installmentId}", new
        {
            cardId = 999,
            totalAmount = 120m,
            periods = 3,
            purchaseDate = "2026-06-21",
            description = "不應保存",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await app.CountInstallmentsAsync());
        Assert.Equal(3, await app.CountPaymentsAsync());
    }

    /// <summary>Verifies payment PATCH accepts an explicit target state and repeated requests are no-ops.</summary>
    [Fact]
    public async Task PatchPayment_SetsTargetStateIdempotently()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var createResponse = await client.PostAsJsonAsync("/api/installments", new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 3,
            purchaseDate = "2026-06-20",
            description = "付款狀態",
        });
        using var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var installmentId = createdBody.RootElement.GetProperty("id").GetInt32();
        var paymentId = createdBody.RootElement.GetProperty("payments")[0].GetProperty("id").GetInt32();

        var first = await client.PatchAsJsonAsync(
            $"/api/installments/{installmentId}/payments/{paymentId}",
            new { isPaid = true, paidDate = "2026-06-20" });
        var repeated = await client.PatchAsJsonAsync(
            $"/api/installments/{installmentId}/payments/{paymentId}",
            new { isPaid = true, paidDate = "2026-06-20" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        using var repeatedBody = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
        Assert.Equal(2, repeatedBody.RootElement.GetProperty("remainingPeriods").GetInt32());
    }

    /// <summary>Verifies repeated unpaid target-state requests do not toggle an unpaid payment.</summary>
    [Fact]
    public async Task PatchPayment_RepeatedUnpaidTargetIsNoOp()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var createResponse = await client.PostAsJsonAsync("/api/installments", new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 3,
            purchaseDate = "2026-06-20",
            description = "未繳狀態",
        });
        using var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var installmentId = createdBody.RootElement.GetProperty("id").GetInt32();
        var paymentId = createdBody.RootElement.GetProperty("payments")[0].GetProperty("id").GetInt32();

        var first = await client.PatchAsJsonAsync(
            $"/api/installments/{installmentId}/payments/{paymentId}",
            new { isPaid = false });
        var repeated = await client.PatchAsJsonAsync(
            $"/api/installments/{installmentId}/payments/{paymentId}",
            new { isPaid = false });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        using var repeatedBody = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
        Assert.Equal(3, repeatedBody.RootElement.GetProperty("remainingPeriods").GetInt32());
        Assert.Equal(3, await app.CountUnpaidPaymentsAsync());
    }

    /// <summary>Verifies a payment PATCH without a target state leaves the payment schedule unchanged.</summary>
    [Fact]
    public async Task PatchPayment_RequiresTargetState()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var createResponse = await client.PostAsJsonAsync("/api/installments", new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 3,
            purchaseDate = "2026-06-20",
            description = "缺少狀態",
        });
        using var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var installmentId = createdBody.RootElement.GetProperty("id").GetInt32();
        var paymentId = createdBody.RootElement.GetProperty("payments")[0].GetProperty("id").GetInt32();

        var response = await client.PatchAsJsonAsync(
            $"/api/installments/{installmentId}/payments/{paymentId}",
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(3, await app.CountUnpaidPaymentsAsync());
    }

    /// <summary>Verifies marking a payment paid without a date leaves the payment unchanged.</summary>
    [Fact]
    public async Task PatchPayment_RequiresDateWhenSettingPaid()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var createResponse = await client.PostAsJsonAsync("/api/installments", new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 3,
            purchaseDate = "2026-06-20",
            description = "付款日期",
        });
        using var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var installmentId = createdBody.RootElement.GetProperty("id").GetInt32();
        var paymentId = createdBody.RootElement.GetProperty("payments")[0].GetProperty("id").GetInt32();

        var response = await client.PatchAsJsonAsync(
            $"/api/installments/{installmentId}/payments/{paymentId}",
            new { isPaid = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(3, await app.CountUnpaidPaymentsAsync());
    }

    /// <summary>Verifies concurrent identical purchase requests resolve to one aggregate.</summary>
    [Fact]
    public async Task PostInstallmentPurchase_ConcurrentIdenticalRequestsCreateOneAggregate()
    {
        await using var app = await CreateAppAsync();
        var key = Guid.NewGuid().ToString();
        var request = CreatePurchaseRequest(app, 1200m);

        var firstClient = app.App.GetTestClient();
        var secondClient = app.App.GetTestClient();
        firstClient.DefaultRequestHeaders.Add("Idempotency-Key", key);
        secondClient.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync("/api/installment-purchases", request),
            secondClient.PostAsJsonAsync("/api/installment-purchases", request));

        foreach (var response in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Created,
                $"Expected Created, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        Assert.Equal(1, await app.CountTransactionsAsync());
        Assert.Equal(1, await app.CountInstallmentsAsync());
    }

    /// <summary>Verifies concurrent standalone retries resolve to one installment and schedule.</summary>
    [Fact]
    public async Task PostStandaloneInstallment_ConcurrentIdenticalRequestsCreateOneAggregate()
    {
        await using var app = await CreateAppAsync();
        var key = Guid.NewGuid().ToString();
        var request = new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 3,
            purchaseDate = "2026-06-20",
            description = "並行獨立分期",
        };
        var firstClient = app.App.GetTestClient();
        var secondClient = app.App.GetTestClient();
        firstClient.DefaultRequestHeaders.Add("Idempotency-Key", key);
        secondClient.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync("/api/installments", request),
            secondClient.PostAsJsonAsync("/api/installments", request));

        foreach (var response in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Created,
                $"Expected Created, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        Assert.Equal(1, await app.CountInstallmentsAsync());
        Assert.Equal(3, await app.CountPaymentsAsync());
    }

    /// <summary>Builds a valid composite purchase request for endpoint tests.</summary>
    private static object CreatePurchaseRequest(TestApp app, decimal amount)
        => new
        {
            transaction = new
            {
                type = TransactionType.Expense,
                amount,
                date = "2026-06-20",
                description = "測試分期",
                categoryId = app.CategoryId,
                paymentMethodId = app.PaymentMethodId,
            },
            installment = new { cardId = app.CardId, periods = 3 },
        };

    /// <summary>Creates a minimal endpoint host for installment purchase contract tests.</summary>
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
        builder.Services.AddScoped<InstallmentCommandService>();

        var app = builder.Build();
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
            };
            var paymentMethod = new PaymentMethod
            {
                Name = "信用卡",
                Icon = "CreditCard",
                SystemCode = "credit-card",
            };
            var card = new CreditCard
            {
                BankName = "測試銀行",
                LastFourDigits = "1234",
                StatementDay = 15,
                DueDay = 23,
            };
            db.AddRange(category, paymentMethod, card);
            await db.SaveChangesAsync();

            app.MapInstallmentEndpoints();
            await app.StartAsync();
            return new TestApp(app, connection, category.Id, paymentMethod.Id, card.Id);
        }
    }

    /// <summary>Provides the resources used by an endpoint contract test.</summary>
    private sealed record TestApp(
        WebApplication App,
        SqliteConnection Connection,
        int CategoryId,
        int PaymentMethodId,
        int CardId) : IAsyncDisposable
    {
        /// <summary>Counts persisted transactions for atomicity assertions.</summary>
        public async Task<int> CountTransactionsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Transactions.CountAsync();
        }

        /// <summary>Counts persisted installments for atomicity assertions.</summary>
        public async Task<int> CountInstallmentsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Installments.CountAsync();
        }

        /// <summary>Counts persisted installment payments for schedule assertions.</summary>
        public async Task<int> CountPaymentsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.InstallmentPayments.CountAsync();
        }

        /// <summary>Counts unpaid payment rows for target-state assertions.</summary>
        public async Task<int> CountUnpaidPaymentsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.InstallmentPayments.CountAsync(payment => !payment.IsPaid);
        }

        /// <summary>Stops the test host and releases its in-memory database connection.</summary>
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
