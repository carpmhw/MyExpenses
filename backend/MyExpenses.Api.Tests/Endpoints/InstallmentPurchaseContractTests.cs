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

    /// <summary>驗證 composite endpoint 接受六十期並建立完整付款時程。</summary>
    [Fact]
    public async Task PostInstallmentPurchase_AcceptsSixtyPeriods()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/installment-purchases", new
        {
            transaction = new
            {
                amount = 6000m,
                date = "2026-06-20",
                description = "六十期 composite",
                categoryId = app.CategoryId,
                paymentMethodId = app.PaymentMethodId,
            },
            installment = new { cardId = app.CardId, periods = 60 },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await app.CountTransactionsAsync());
        Assert.Equal(1, await app.CountInstallmentsAsync());
        Assert.Equal(60, await app.CountPaymentsAsync());
        Assert.Equal(1, await app.CountIdempotencyRecordsAsync());
    }

    /// <summary>驗證 composite endpoint 接受一期並只建立一筆付款紀錄。</summary>
    [Fact]
    public async Task PostInstallmentPurchase_AcceptsOnePeriod()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync(
            "/api/installment-purchases",
            CreatePurchaseRequest(app, 100m, 1));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await app.CountTransactionsAsync());
        Assert.Equal(1, await app.CountInstallmentsAsync());
        Assert.Equal(1, await app.CountPaymentsAsync());
    }

    /// <summary>驗證 composite endpoint 拒絕零期且不留下任何交易資料或冪等收據。</summary>
    [Fact]
    public async Task PostInstallmentPurchase_RejectsZeroPeriodsAtomically()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/installment-purchases", new
        {
            transaction = new
            {
                amount = 6000m,
                date = "2026-06-20",
                description = "零期 composite",
                categoryId = app.CategoryId,
                paymentMethodId = app.PaymentMethodId,
            },
            installment = new { cardId = app.CardId, periods = 0 },
        });

        await AssertInvalidPeriodResponseAsync(response);
        await app.AssertNoInstallmentCommandRowsAsync();
    }

    /// <summary>驗證 composite endpoint 拒絕超過六十期且不留下任何交易資料或冪等收據。</summary>
    [Fact]
    public async Task PostInstallmentPurchase_RejectsMoreThanSixtyPeriodsAtomically()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/installment-purchases", new
        {
            transaction = new
            {
                amount = 6100m,
                date = "2026-06-20",
                description = "超過上限 composite",
                categoryId = app.CategoryId,
                paymentMethodId = app.PaymentMethodId,
            },
            installment = new { cardId = app.CardId, periods = 61 },
        });

        await AssertInvalidPeriodResponseAsync(response);
        await app.AssertNoInstallmentCommandRowsAsync();
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

        Assert.True(first.StatusCode == HttpStatusCode.Created,
            $"Expected first Created, got {first.StatusCode}: {await first.Content.ReadAsStringAsync()}");
        Assert.True(second.StatusCode == HttpStatusCode.Created,
            $"Expected second Created, got {second.StatusCode}: {await second.Content.ReadAsStringAsync()}");
        Assert.Equal("true", second.Headers.GetValues("X-Idempotent-Replay").Single());
        Assert.Equal(1, await app.CountTransactionsAsync());
        Assert.Equal(1, await app.CountInstallmentsAsync());
        Assert.Equal(3, await app.CountPaymentsAsync());
    }

    /// <summary>驗證 composite receipt 指向的交易被軟刪除後重播會安全回傳 410。</summary>
    [Fact]
    public async Task PostInstallmentPurchase_ReplayAfterTransactionSoftDelete_ReturnsGone()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        var key = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var first = await client.PostAsJsonAsync("/api/installment-purchases", CreatePurchaseRequest(app, 1200m));
        using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var transactionId = body.RootElement.GetProperty("transaction").GetProperty("id").GetInt32();

        await app.SoftDeleteTransactionAsync(transactionId);
        var replay = await client.PostAsJsonAsync("/api/installment-purchases", CreatePurchaseRequest(app, 1200m));

        Assert.Equal(HttpStatusCode.Gone, replay.StatusCode);
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal("result_unavailable", replayBody.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, await app.CountTransactionsIncludingDeletedAsync());
        Assert.Equal(1, await app.CountInstallmentsAsync());
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
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var payments = body.RootElement.GetProperty("payments").EnumerateArray().ToArray();
            Assert.Equal(new[] { 33m, 33m, 34m }, payments.Select(payment => payment.GetProperty("amount").GetDecimal()));
            Assert.Equal(new[] { "2026-07-23", "2026-08-23", "2026-09-23" }, payments.Select(payment => payment.GetProperty("dueDate").GetString()));
        }
        Assert.Equal(1, await app.CountInstallmentsAsync());
        Assert.Equal(3, await app.CountPaymentsAsync());
    }

    /// <summary>驗證同一 key 改用不同 financial operation 時不會建立第二筆結果。</summary>
    [Fact]
    public async Task IdempotencyKey_CannotBeReusedAcrossStandaloneAndCompositeOperations()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var standalone = await client.PostAsJsonAsync("/api/installments", new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 1,
            purchaseDate = "2026-06-20",
            description = "跨 operation",
        });
        var composite = await client.PostAsJsonAsync(
            "/api/installment-purchases",
            CreatePurchaseRequest(app, 100m, 1));

        Assert.Equal(HttpStatusCode.Created, standalone.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, composite.StatusCode);
        Assert.Equal(0, await app.CountTransactionsAsync());
        Assert.Equal(1, await app.CountInstallmentsAsync());
        Assert.Equal(1, await app.CountIdempotencyRecordsAsync());
    }

    /// <summary>驗證 standalone receipt 指向的分期被刪除後重播會安全回傳 410。</summary>
    [Fact]
    public async Task PostStandaloneInstallment_ReplayAfterInstallmentDelete_ReturnsGone()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        var key = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var request = new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 3,
            purchaseDate = "2026-06-20",
            description = "刪除後重播",
        };
        var first = await client.PostAsJsonAsync("/api/installments", request);
        using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var installmentId = body.RootElement.GetProperty("id").GetInt32();

        await app.DeleteInstallmentAsync(installmentId);
        var replay = await client.PostAsJsonAsync("/api/installments", request);

        Assert.Equal(HttpStatusCode.Gone, replay.StatusCode);
        Assert.False(replay.Headers.Contains("X-Idempotent-Replay"));
        Assert.Equal(0, await app.CountInstallmentsAsync());
        Assert.Equal(1, await app.CountIdempotencyRecordsAsync());
    }

    /// <summary>驗證 standalone 分期被編輯後重播仍回傳目前資料與原始識別碼。</summary>
    [Fact]
    public async Task PostStandaloneInstallment_ReplayAfterEditReturnsCurrentData()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        var key = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var request = new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 3,
            purchaseDate = "2026-06-20",
            description = "原始描述",
        };

        var first = await client.PostAsJsonAsync("/api/installments", request);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var installmentId = firstBody.RootElement.GetProperty("id").GetInt32();
        await app.UpdateInstallmentDescriptionAsync(installmentId, "編輯後描述");

        var replay = await client.PostAsJsonAsync("/api/installments", request);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("X-Idempotent-Replay").Single());
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(installmentId, replayBody.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("編輯後描述", replayBody.RootElement.GetProperty("description").GetString());
    }

    /// <summary>驗證 standalone endpoint 接受六十期並建立完整付款時程。</summary>
    [Fact]
    public async Task PostStandaloneInstallment_AcceptsSixtyPeriods()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/installments", new
        {
            cardId = app.CardId,
            totalAmount = 6000m,
            periods = 60,
            purchaseDate = "2026-06-20",
            description = "六十期 standalone",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(60, await app.CountPaymentsAsync());
        Assert.Equal(1, await app.CountIdempotencyRecordsAsync());
    }

    /// <summary>驗證 standalone endpoint 拒絕超過六十期且不留下分期資料或冪等收據。</summary>
    [Fact]
    public async Task PostStandaloneInstallment_RejectsMoreThanSixtyPeriodsAtomically()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/installments", new
        {
            cardId = app.CardId,
            totalAmount = 6100m,
            periods = 61,
            purchaseDate = "2026-06-20",
            description = "超過上限 standalone",
        });

        await AssertInvalidPeriodResponseAsync(response);
        await app.AssertNoInstallmentCommandRowsAsync();
    }

    /// <summary>驗證一期信用卡交易只建立一筆完整金額的待繳付款並可結清。</summary>
    [Fact]
    public async Task PostStandaloneInstallment_OnePeriodCreatesSinglePayableAndCanBePaidOff()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/installments", new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 1,
            purchaseDate = "2026-06-20",
            description = "一次付清",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var createdBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var installment = createdBody.RootElement;
        var payment = Assert.Single(installment.GetProperty("payments").EnumerateArray());
        Assert.Equal(100m, payment.GetProperty("amount").GetDecimal());
        Assert.Equal("2026-07-23", payment.GetProperty("dueDate").GetString());
        Assert.Equal(1, installment.GetProperty("remainingPeriods").GetInt32());
        Assert.Equal((int)InstallmentStatus.Active, installment.GetProperty("status").GetInt32());
        Assert.Equal(0, await app.CountTransactionsAsync());

        var paidResponse = await client.PatchAsJsonAsync(
            $"/api/installments/{installment.GetProperty("id").GetInt32()}/payments/{payment.GetProperty("id").GetInt32()}",
            new { isPaid = true, paidDate = "2026-07-20" });

        Assert.Equal(HttpStatusCode.OK, paidResponse.StatusCode);
        using var paidBody = JsonDocument.Parse(await paidResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, paidBody.RootElement.GetProperty("remainingPeriods").GetInt32());
        Assert.Equal((int)InstallmentStatus.PaidOff, paidBody.RootElement.GetProperty("status").GetInt32());
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

    /// <summary>驗證更新 endpoint 接受六十期並重建完整未繳付款時程。</summary>
    [Fact]
    public async Task PutInstallment_AcceptsSixtyPeriods()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var createResponse = await client.PostAsJsonAsync("/api/installments", new
        {
            cardId = app.CardId,
            totalAmount = 100m,
            periods = 1,
            purchaseDate = "2026-06-20",
            description = "更新至六十期",
        });
        using var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var installmentId = createdBody.RootElement.GetProperty("id").GetInt32();

        var response = await client.PutAsJsonAsync($"/api/installments/{installmentId}", new
        {
            cardId = app.CardId,
            totalAmount = 6000m,
            periods = 60,
            purchaseDate = "2026-07-20",
            description = "更新至六十期",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(60, await app.CountPaymentsAsync());
    }

    /// <summary>驗證更新 endpoint 拒絕超過六十期並保留原分期與付款時程。</summary>
    [Fact]
    public async Task PutInstallment_RejectsMoreThanSixtyPeriodsWithoutChangingSchedule()
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
            description = "保留原排程",
        });
        using var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var installmentId = createdBody.RootElement.GetProperty("id").GetInt32();

        var response = await client.PutAsJsonAsync($"/api/installments/{installmentId}", new
        {
            cardId = app.CardId,
            totalAmount = 6100m,
            periods = 61,
            purchaseDate = "2026-07-20",
            description = "不應保存",
        });

        await AssertInvalidPeriodResponseAsync(response);
        Assert.Equal(1, await app.CountInstallmentsAsync());
        Assert.Equal(3, await app.CountPaymentsAsync());
        var installment = await app.GetInstallmentValuesAsync();
        Assert.Equal(3, installment.Periods);
        Assert.Equal(100m, installment.TotalAmount);
        Assert.Equal("保留原排程", installment.Description);
    }

    /// <summary>驗證既有超過六十期的分期仍可查詢、標記付款，但不可重建超限排程。</summary>
    [Fact]
    public async Task LegacyInstallmentOverSixtyPeriods_RemainsReadableAndPayableButCannotBeRebuilt()
    {
        await using var app = await CreateAppAsync();
        var installmentId = await app.SeedLegacyInstallmentAsync(61);
        var client = app.App.GetTestClient();

        var getResponse = await client.GetAsync($"/api/installments/{installmentId}");

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using (var body = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(61, body.RootElement.GetProperty("periods").GetInt32());
            Assert.Equal(61, body.RootElement.GetProperty("payments").GetArrayLength());
        }

        using var listBody = JsonDocument.Parse(await (await client.GetAsync("/api/installments?page=1&pageSize=15")).Content.ReadAsStringAsync());
        Assert.Contains(
            listBody.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetInt32() == installmentId);

        var paymentId = await app.GetFirstPaymentIdAsync(installmentId);
        var markResponse = await client.PatchAsJsonAsync(
            $"/api/installments/{installmentId}/payments/{paymentId}",
            new { isPaid = true, paidDate = "2026-06-20" });

        Assert.Equal(HttpStatusCode.OK, markResponse.StatusCode);
        using (var markedBody = JsonDocument.Parse(await markResponse.Content.ReadAsStringAsync()))
            Assert.Equal(60, markedBody.RootElement.GetProperty("remainingPeriods").GetInt32());

        var rebuildResponse = await client.PutAsJsonAsync($"/api/installments/{installmentId}", new
        {
            cardId = app.CardId,
            totalAmount = 6100m,
            periods = 61,
            purchaseDate = "2026-06-20",
            description = "不應重建超限排程",
        });

        await AssertInvalidPeriodResponseAsync(rebuildResponse);
        Assert.Equal(61, await app.CountPaymentsAsync());
        Assert.Equal(1, await app.CountPaidPaymentsAsync());
    }

    /// <summary>驗證編輯歷史 composite 的交易不會修改分期或付款紀錄。</summary>
    [Fact]
    public async Task LegacyComposite_EditingTransactionLeavesInstallmentAndPaymentsUnchanged()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var createResponse = await client.PostAsJsonAsync(
            "/api/installment-purchases",
            CreatePurchaseRequest(app, 1200m));
        using var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var transactionId = createdBody.RootElement.GetProperty("transaction").GetProperty("id").GetInt32();
        var installmentId = createdBody.RootElement.GetProperty("installment").GetProperty("id").GetInt32();
        var originalInstallment = await app.GetInstallmentSnapshotAsync(installmentId);

        var updateResponse = await client.PutAsJsonAsync($"/api/transactions/{transactionId}", new
        {
            type = TransactionType.Expense,
            amount = 1350m,
            date = "2026-07-20",
            description = "交易已獨立修改",
            categoryId = app.CategoryId,
            paymentMethodId = app.PaymentMethodId,
        });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updatedInstallment = await app.GetInstallmentSnapshotAsync(installmentId);
        var updatedTransaction = await app.GetTransactionSnapshotAsync(transactionId);
        Assert.Equal(1350m, updatedTransaction.Amount);
        Assert.Equal(new DateOnly(2026, 7, 20), updatedTransaction.Date);
        Assert.Equal("交易已獨立修改", updatedTransaction.Description);
        AssertInstallmentSnapshotEqual(originalInstallment, updatedInstallment);
    }

    /// <summary>驗證編輯歷史 composite 的分期與重建排程不會修改關聯交易。</summary>
    [Fact]
    public async Task LegacyComposite_EditingInstallmentLeavesTransactionUnchanged()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var createResponse = await client.PostAsJsonAsync(
            "/api/installment-purchases",
            CreatePurchaseRequest(app, 1200m));
        using var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var transactionId = createdBody.RootElement.GetProperty("transaction").GetProperty("id").GetInt32();
        var installmentId = createdBody.RootElement.GetProperty("installment").GetProperty("id").GetInt32();
        var originalTransaction = await app.GetTransactionSnapshotAsync(transactionId);

        var updateResponse = await client.PutAsJsonAsync($"/api/installments/{installmentId}", new
        {
            cardId = app.CardId,
            totalAmount = 1300m,
            periods = 4,
            purchaseDate = "2026-07-20",
            description = "分期已獨立修改",
        });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updatedTransaction = await app.GetTransactionSnapshotAsync(transactionId);
        var updatedInstallment = await app.GetInstallmentSnapshotAsync(installmentId);
        Assert.Equal(originalTransaction, updatedTransaction);
        Assert.Equal(1300m, updatedInstallment.TotalAmount);
        Assert.Equal(4, updatedInstallment.Periods);
        Assert.Equal(4, updatedInstallment.Payments.Count);
        Assert.Equal("分期已獨立修改", updatedInstallment.Description);
    }

    /// <summary>驗證交易軟刪除與還原不影響歷史分期，且刪除分期只刪除自己的付款紀錄。</summary>
    [Fact]
    public async Task LegacyComposite_DeleteAndRestoreRemainIndependent()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var createResponse = await client.PostAsJsonAsync(
            "/api/installment-purchases",
            CreatePurchaseRequest(app, 1200m));
        using var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var transactionId = createdBody.RootElement.GetProperty("transaction").GetProperty("id").GetInt32();
        var installmentId = createdBody.RootElement.GetProperty("installment").GetProperty("id").GetInt32();
        var originalInstallment = await app.GetInstallmentSnapshotAsync(installmentId);

        var deleteTransactionResponse = await client.DeleteAsync($"/api/transactions/{transactionId}");

        Assert.Equal(HttpStatusCode.NoContent, deleteTransactionResponse.StatusCode);
        AssertInstallmentSnapshotEqual(originalInstallment, await app.GetInstallmentSnapshotAsync(installmentId));
        Assert.Equal(3, await app.CountPaymentsForInstallmentAsync(installmentId));
        Assert.NotNull((await app.GetTransactionSnapshotAsync(transactionId)).DeletedAt);

        var restoreResponse = await client.PostAsync($"/api/transactions/{transactionId}/undo", null);

        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);
        AssertInstallmentSnapshotEqual(originalInstallment, await app.GetInstallmentSnapshotAsync(installmentId));
        Assert.Null((await app.GetTransactionSnapshotAsync(transactionId)).DeletedAt);

        var deleteInstallmentResponse = await client.DeleteAsync($"/api/installments/{installmentId}");

        Assert.Equal(HttpStatusCode.NoContent, deleteInstallmentResponse.StatusCode);
        Assert.Equal(0, await app.CountPaymentsForInstallmentAsync(installmentId));
        var remainingTransaction = await app.GetTransactionSnapshotAsync(transactionId);
        Assert.Null(remainingTransaction.DeletedAt);
        Assert.Equal(1200m, remainingTransaction.Amount);
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
    private static object CreatePurchaseRequest(TestApp app, decimal amount, int periods = 3)
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
            installment = new { cardId = app.CardId, periods },
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
        builder.Services.AddScoped<TransactionCommandService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });

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

            app.MapTransactionEndpoints();
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

        /// <summary>計算包含軟刪除資料的交易數量，確認重播沒有重建資料。</summary>
        public async Task<int> CountTransactionsIncludingDeletedAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Transactions.IgnoreQueryFilters().CountAsync();
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

        /// <summary>計算目前資料庫中的冪等收據數量。</summary>
        public async Task<int> CountIdempotencyRecordsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.IdempotencyRecords.CountAsync();
        }

        /// <summary>將指定普通交易標記為軟刪除以測試 receipt 重播邊界。</summary>
        public async Task SoftDeleteTransactionAsync(int transactionId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transaction = await db.Transactions
                .IgnoreQueryFilters()
                .SingleAsync(item => item.Id == transactionId);
            transaction.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        /// <summary>刪除指定分期以測試 receipt 重播邊界。</summary>
        public async Task DeleteInstallmentAsync(int installmentId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var installment = await db.Installments.SingleAsync(item => item.Id == installmentId);
            db.Installments.Remove(installment);
            await db.SaveChangesAsync();
        }

        /// <summary>修改分期描述以驗證 receipt replay 使用目前 canonical 資料。</summary>
        public async Task UpdateInstallmentDescriptionAsync(int installmentId, string description)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var installment = await db.Installments.SingleAsync(item => item.Id == installmentId);
            installment.Description = description;
            await db.SaveChangesAsync();
        }

        /// <summary>確認失敗的分期命令未建立任何交易、分期、付款或冪等收據。</summary>
        public async Task AssertNoInstallmentCommandRowsAsync()
        {
            Assert.Equal(0, await CountTransactionsAsync());
            Assert.Equal(0, await CountInstallmentsAsync());
            Assert.Equal(0, await CountPaymentsAsync());
            Assert.Equal(0, await CountIdempotencyRecordsAsync());
        }

        /// <summary>讀取目前分期欄位供 endpoint 契約測試檢查保存內容。</summary>
        public async Task<(int Periods, decimal TotalAmount, string? Description)> GetInstallmentValuesAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var installment = await db.Installments.SingleAsync();
            return (installment.Periods, installment.TotalAmount, installment.Description);
        }

        /// <summary>Counts unpaid payment rows for target-state assertions.</summary>
        public async Task<int> CountUnpaidPaymentsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.InstallmentPayments.CountAsync(payment => !payment.IsPaid);
        }

        /// <summary>計算目前資料庫中的已繳付款數量。</summary>
        public async Task<int> CountPaidPaymentsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.InstallmentPayments.CountAsync(payment => payment.IsPaid);
        }

        /// <summary>讀取指定分期及付款欄位，供歷史獨立性測試比對前後狀態。</summary>
        public async Task<InstallmentSnapshot> GetInstallmentSnapshotAsync(int installmentId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var installment = await db.Installments
                .Include(item => item.Payments)
                .SingleAsync(item => item.Id == installmentId);
            return new InstallmentSnapshot(
                installment.TransactionId,
                installment.TotalAmount,
                installment.Periods,
                installment.PurchaseDate,
                installment.Description,
                installment.Payments
                    .OrderBy(payment => payment.Period)
                    .Select(payment => new InstallmentPaymentSnapshot(
                        payment.Period,
                        payment.Amount,
                        payment.PaidDate,
                        payment.IsPaid,
                        payment.DueDate))
                    .ToList());
        }

        /// <summary>讀取交易的可變欄位與軟刪除狀態，供歷史獨立性測試比對。</summary>
        public async Task<TransactionSnapshot> GetTransactionSnapshotAsync(int transactionId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transaction = await db.Transactions
                .IgnoreQueryFilters()
                .SingleAsync(item => item.Id == transactionId);
            return new TransactionSnapshot(
                transaction.Amount,
                transaction.Date,
                transaction.Description,
                transaction.Notes,
                transaction.DeletedAt);
        }

        /// <summary>計算指定分期目前仍保留的付款紀錄數量。</summary>
        public async Task<int> CountPaymentsForInstallmentAsync(int installmentId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.InstallmentPayments.CountAsync(payment => payment.InstallmentId == installmentId);
        }

        /// <summary>建立變更前的超過六十期分期資料及其完整付款紀錄。</summary>
        public async Task<int> SeedLegacyInstallmentAsync(int periods)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var installment = new Installment
            {
                CardId = CardId,
                TotalAmount = periods * 100m,
                Periods = periods,
                PerPeriod = 100m,
                PurchaseDate = new DateOnly(2026, 6, 20),
                Description = "歷史超限分期",
                CreatedAt = DateTime.UtcNow,
            };
            db.Installments.Add(installment);
            await db.SaveChangesAsync();
            db.InstallmentPayments.AddRange(Enumerable.Range(1, periods).Select(period => new InstallmentPayment
            {
                InstallmentId = installment.Id,
                Period = period,
                Amount = 100m,
                DueDate = new DateOnly(2026, 7, 23).AddMonths(period - 1),
            }));
            await db.SaveChangesAsync();
            return installment.Id;
        }

        /// <summary>讀取指定歷史分期的第一筆付款識別碼。</summary>
        public async Task<int> GetFirstPaymentIdAsync(int installmentId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.InstallmentPayments
                .Where(payment => payment.InstallmentId == installmentId)
                .OrderBy(payment => payment.Period)
                .Select(payment => payment.Id)
                .FirstAsync();
        }

        /// <summary>Stops the test host and releases its in-memory database connection.</summary>
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>保存歷史分期的可變欄位與付款快照。</summary>
    private sealed record InstallmentSnapshot(
        int? TransactionId,
        decimal TotalAmount,
        int Periods,
        DateOnly PurchaseDate,
        string? Description,
        IReadOnlyList<InstallmentPaymentSnapshot> Payments);

    /// <summary>保存單筆分期付款的欄位快照。</summary>
    private sealed record InstallmentPaymentSnapshot(
        int Period,
        decimal Amount,
        DateOnly? PaidDate,
        bool IsPaid,
        DateOnly? DueDate);

    /// <summary>保存交易可變欄位與軟刪除狀態快照。</summary>
    private sealed record TransactionSnapshot(
        decimal Amount,
        DateOnly Date,
        string? Description,
        string? Notes,
        DateTime? DeletedAt);

    /// <summary>驗證期數越界回傳一致的安全錯誤訊息。</summary>
    private static async Task AssertInvalidPeriodResponseAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("期數必須為 1 至 60 期", body.RootElement.GetProperty("detail").GetString());
    }

    /// <summary>逐欄比對歷史分期及其付款快照，避免集合參照相等造成誤判。</summary>
    private static void AssertInstallmentSnapshotEqual(
        InstallmentSnapshot expected,
        InstallmentSnapshot actual)
    {
        Assert.Equal(expected.TransactionId, actual.TransactionId);
        Assert.Equal(expected.TotalAmount, actual.TotalAmount);
        Assert.Equal(expected.Periods, actual.Periods);
        Assert.Equal(expected.PurchaseDate, actual.PurchaseDate);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Payments, actual.Payments);
    }
}
