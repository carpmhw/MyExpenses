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
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class ConsumptionQueryContractTests
{
    /// <summary>驗證重新命名的生活分類仍排除描述中的卡費，但備註關鍵字不影響消費或卡費原始查詢。</summary>
    [Fact]
    public async Task GetConsumption_RepaymentPredicateUsesSystemCodeAndDescriptionOnly()
    {
        await using var app = await CreateAppAsync();
        var living = await app.SeedCategoryAsync("生活", "living");
        var notesOnly = await app.SeedOrdinaryExpenseAsync(100m, new DateOnly(2026, 9, 5), "日用品", living, "信用卡帳單");
        var repayment = await app.SeedOrdinaryExpenseAsync(3000m, new DateOnly(2026, 9, 5), "九月信用卡帳單", living);
        var other = await app.SeedOrdinaryExpenseAsync(200m, new DateOnly(2026, 9, 5), "信用卡帳單", app.CategoryId);
        await app.SeedTransactionAsync(900m, new DateOnly(2026, 9, 5), "信用卡帳單", living, TransactionType.Income);
        await app.RenameCategoryAsync(living, "重新命名的生活分類");

        var consumption = await app.Client.GetAsync("/api/consumption?startDate=2026-09-01&endDate=2026-09-30");
        Assert.Equal(HttpStatusCode.OK, consumption.StatusCode);
        using var body = JsonDocument.Parse(await consumption.Content.ReadAsStringAsync());
        Assert.Equal(300m, body.RootElement.GetProperty("summary").GetProperty("totalAmount").GetDecimal());
        Assert.Equal(new[] { other, notesOnly }, body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("sourceId").GetInt32()).ToArray());

        var raw = await app.Client.GetAsync("/api/transactions?startDate=2026-09-01&endDate=2026-09-30&repaymentOnly=true");
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        using var rawBody = JsonDocument.Parse(await raw.Content.ReadAsStringAsync());
        Assert.Equal(repayment, Assert.Single(rawBody.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal(3000m, rawBody.RootElement.GetProperty("summary").GetProperty("totalExpense").GetDecimal());
    }

    /// <summary>驗證信用消費不受付款狀態、付款日期及本期到期的期間外分期影響，查詢也不改動付款。</summary>
    [Fact]
    public async Task GetConsumption_IsIndependentOfPaidStateAndDoesNotMutatePayments()
    {
        await using var app = await CreateAppAsync();
        await using var scope = app.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = new InstallmentCommandService(db, scope.ServiceProvider.GetRequiredService<TimeZoneService>());
        foreach (var date in new[] { new DateOnly(2026, 9, 5), new DateOnly(2026, 8, 5) })
        {
            await service.CreateStandaloneInstallmentAsync(new MyExpenses.Api.Models.Requests.CreateStandaloneInstallmentRequest
            {
                CardId = app.CardId, TotalAmount = 1200m, Periods = 3,
                PurchaseDate = date, Description = "付款獨立性",
            }, Guid.NewGuid().ToString());
        }
        db.ChangeTracker.Clear();
        Assert.Equal(6, await db.InstallmentPayments.CountAsync());
        foreach (var paid in new[] { false, true, false })
        {
            var payments = await db.InstallmentPayments.ToListAsync();
            foreach (var payment in payments)
            {
                payment.IsPaid = paid;
                payment.PaidDate = paid ? new DateOnly(2026, 9, 10) : null;
                payment.DueDate = new DateOnly(2026, 9, 15);
            }
            await db.SaveChangesAsync();
            var before = JsonSerializer.Serialize(payments.OrderBy(item => item.Id).Select(item =>
                new { item.Id, item.Amount, item.DueDate, item.IsPaid, item.PaidDate }));

            var response = await app.Client.GetAsync("/api/consumption?startDate=2026-09-01&endDate=2026-09-30");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1200m, body.RootElement.GetProperty("summary").GetProperty("totalAmount").GetDecimal());
            Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1200m, Assert.Single(body.RootElement.GetProperty("items").EnumerateArray()).GetProperty("amount").GetDecimal());
            db.ChangeTracker.Clear();
            var after = await db.InstallmentPayments.OrderBy(item => item.Id).Select(item =>
                new { item.Id, item.Amount, item.DueDate, item.IsPaid, item.PaidDate }).ToListAsync();
            Assert.Equal(before, JsonSerializer.Serialize(after));
        }
    }

    /// <summary>驗證分類查詢只彙總符合分類的普通消費，不納入信用消費或歷史關聯端。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("&source=all")]
    [InlineData("&source=ordinary")]
    public async Task GetConsumption_CategoryFilterIncludesOnlyOrdinary(string source)
    {
        await using var app = await CreateAppAsync();
        var other = await app.SeedCategoryAsync("飲食", "food");
        await app.SeedOrdinaryExpenseAsync(100m, new DateOnly(2026, 9, 5), "符合分類");
        await app.SeedOrdinaryExpenseAsync(200m, new DateOnly(2026, 9, 5), "其他分類", other);
        await app.SeedInstallmentAsync(300m, 1, new DateOnly(2026, 9, 5), "信用消費");
        await app.SeedLinkedInstallmentAsync(400m, new DateOnly(2026, 9, 5), new DateOnly(2026, 9, 5), "歷史關聯");

        var response = await app.Client.GetAsync(
            $"/api/consumption?startDate=2026-09-01&endDate=2026-09-30&categoryId={app.CategoryId}{source}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ordinary", Assert.Single(body.RootElement.GetProperty("items").EnumerateArray()).GetProperty("sourceType").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
        var summary = body.RootElement.GetProperty("summary");
        Assert.Equal(100m, summary.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(100m, summary.GetProperty("ordinaryAmount").GetDecimal());
        Assert.Equal(0m, summary.GetProperty("creditCardAmount").GetDecimal());
        Assert.False(body.RootElement.GetProperty("coverage").GetProperty("creditCardCategoriesAvailable").GetBoolean());
    }

    /// <summary>驗證分類識別碼格式錯誤時回傳 400，而非成功的空集合。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public async Task GetConsumption_RejectsInvalidCategoryId(string categoryId)
    {
        await using var app = await CreateAppAsync();
        var response = await app.Client.GetAsync(
            $"/api/consumption?startDate=2026-09-01&endDate=2026-09-30&categoryId={categoryId}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>驗證極大頁碼不會溢位回到第一頁，完整摘要仍保持不變。</summary>
    [Theory]
    [InlineData(2147483647, 100)]
    [InlineData(1073741825, 4)]
    public async Task GetConsumption_LargePageReturnsEmptyWithoutOverflow(int page, int pageSize)
    {
        await using var app = await CreateAppAsync();
        await app.SeedOrdinaryExpenseAsync(100m, new DateOnly(2026, 9, 5), "測試分頁");
        var response = await app.Client.GetAsync(
            $"/api/consumption?startDate=2026-09-01&endDate=2026-09-30&page={page}&pageSize={pageSize}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(100m, body.RootElement.GetProperty("summary").GetProperty("totalAmount").GetDecimal());
    }

    /// <summary>驗證同日跨來源及相同數值識別碼的排序，在跨頁與重複查詢時保持穩定。</summary>
    [Fact]
    public async Task GetConsumption_PaginatesWithStableSourceAwareOrdering()
    {
        await using var app = await CreateAppAsync();
        await app.SeedOrdinaryExpenseAsync(10m, new DateOnly(2026, 9, 5), "普通一");
        await app.SeedOrdinaryExpenseAsync(20m, new DateOnly(2026, 9, 5), "普通二");
        await app.SeedInstallmentAsync(30m, 1, new DateOnly(2026, 9, 5), "信用一");
        await app.SeedInstallmentAsync(40m, 1, new DateOnly(2026, 9, 5), "信用二");
        await app.SeedOrdinaryExpenseAsync(50m, new DateOnly(2026, 9, 6), "最新");
        var expected = new[] { "ordinary:3", "credit_card:2", "credit_card:1", "ordinary:2", "ordinary:1" };
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var actual = new List<string>();
            for (var page = 1; page <= 3; page++)
            {
                var response = await app.Client.GetAsync(
                    $"/api/consumption?startDate=2026-09-01&endDate=2026-09-30&page={page}&pageSize=2");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                actual.AddRange(body.RootElement.GetProperty("items").EnumerateArray().Select(item =>
                    $"{item.GetProperty("sourceType").GetString()}:{item.GetProperty("sourceId").GetInt32()}"));
                Assert.Equal(150m, body.RootElement.GetProperty("summary").GetProperty("totalAmount").GetDecimal());
            }
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>驗證消費查詢按購買日把普通支出與信用卡總額合併。</summary>
    [Fact]
    public async Task GetConsumption_CombinesOrdinaryAndCreditPurchaseFullAmount()
    {
        await using var app = await CreateAppAsync();
        await app.SeedOrdinaryExpenseAsync(150m, new DateOnly(2026, 9, 5), "現金午餐");
        await app.SeedInstallmentAsync(24000m, 12, new DateOnly(2026, 9, 5), "刷卡手機");

        var response = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30");

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Expected OK, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var summary = body.RootElement.GetProperty("summary");
        Assert.Equal(24150m, summary.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(150m, summary.GetProperty("ordinaryAmount").GetDecimal());
        Assert.Equal(24000m, summary.GetProperty("creditCardAmount").GetDecimal());
        Assert.Equal(2, summary.GetProperty("count").GetInt32());
    }

    /// <summary>驗證卡費繳款排除於消費，且期間外分期不受本期未付款影響。</summary>
    [Fact]
    public async Task GetConsumption_ExcludesCardRepaymentAndOutOfPeriodInstallment()
    {
        await using var app = await CreateAppAsync();
        var livingCategoryId = await app.SeedCategoryAsync("生活", "living");
        await app.SeedOrdinaryExpenseAsync(100m, new DateOnly(2026, 9, 5), "生活用品");
        await app.SeedOrdinaryExpenseAsync(
            3000m,
            new DateOnly(2026, 9, 6),
            "信用卡帳單 9 月",
            livingCategoryId);
        await app.SeedInstallmentAsync(3000m, 3, new DateOnly(2026, 8, 20), "八月刷卡");

        var response = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var summary = body.RootElement.GetProperty("summary");
        Assert.Equal(100m, summary.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(1, summary.GetProperty("count").GetInt32());
    }

    /// <summary>驗證歷史複合交易依明確關聯去重且信用端以購買日認列。</summary>
    [Fact]
    public async Task GetConsumption_DeduplicatesLinkedTransactionBeforeDateFiltering()
    {
        await using var app = await CreateAppAsync();
        var linked = await app.SeedLinkedInstallmentAsync(
            500m,
            new DateOnly(2026, 10, 5),
            new DateOnly(2026, 9, 28),
            "歷史複合");

        var september = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30");
        var october = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-10-01&endDate=2026-10-31");

        Assert.Equal(0m, await ReadSummaryAmountAsync(september));
        Assert.Equal(500m, await ReadSummaryAmountAsync(october));
        await app.DeleteInstallmentAsync(linked.InstallmentId);
        var afterDelete = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30");
        Assert.Equal(500m, await ReadSummaryAmountAsync(afterDelete));
    }

    /// <summary>驗證普通關聯端軟刪除不會讓仍存在的信用消費消失，且 ordinary filter 不會回補關聯端。</summary>
    [Fact]
    public async Task GetConsumption_PreservesCreditAfterLinkedTransactionSoftDelete()
    {
        await using var app = await CreateAppAsync();
        var linked = await app.SeedLinkedInstallmentAsync(
            800m,
            new DateOnly(2026, 9, 5),
            new DateOnly(2026, 9, 5),
            "關聯手機");
        await app.SoftDeleteTransactionAsync(linked.TransactionId);

        var credit = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30&source=credit_card");
        var ordinary = await app.Client.GetAsync(
            $"/api/consumption?startDate=2026-09-01&endDate=2026-09-30&source=ordinary&categoryId={app.CategoryId}&search=關聯手機");

        Assert.Equal(800m, await ReadSummaryAmountAsync(credit));
        Assert.Equal(0m, await ReadSummaryAmountAsync(ordinary));
    }

    /// <summary>驗證無明確關聯的相同金額紀錄仍各自計入，空集合也保留完整期間 metadata。</summary>
    [Fact]
    public async Task GetConsumption_PreservesUnrelatedEqualRecordsAndEmptyMetadata()
    {
        await using var app = await CreateAppAsync();
        await app.SeedOrdinaryExpenseAsync(500m, new DateOnly(2026, 9, 5), "相同消費");
        await app.SeedInstallmentAsync(500m, 1, new DateOnly(2026, 9, 5), "相同消費");

        var both = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30");
        Assert.Equal(1000m, await ReadSummaryAmountAsync(both));

        var empty = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-10-01&endDate=2026-10-31");
        using var emptyBody = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        Assert.Equal(0, emptyBody.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("2026-10-01", emptyBody.RootElement.GetProperty("period").GetProperty("startDate").GetString());
        Assert.Equal(0m, emptyBody.RootElement.GetProperty("summary").GetProperty("totalAmount").GetDecimal());
    }

    /// <summary>驗證消費查詢回傳完整集合摘要、coverage 與參數錯誤。</summary>
    [Fact]
    public async Task GetConsumption_ReturnsCompleteSummaryAndValidatesFilters()
    {
        await using var app = await CreateAppAsync();
        await app.SeedOrdinaryExpenseAsync(100m, new DateOnly(2026, 9, 1), "第一筆");
        await app.SeedOrdinaryExpenseAsync(200m, new DateOnly(2026, 9, 2), "第二筆");
        await app.SeedOrdinaryExpenseAsync(300m, new DateOnly(2026, 9, 3), "第三筆");

        var paged = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30&page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, paged.StatusCode);
        using (var body = JsonDocument.Parse(await paged.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, body.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(3, body.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(600m, body.RootElement.GetProperty("summary").GetProperty("totalAmount").GetDecimal());
            Assert.False(body.RootElement.GetProperty("coverage").GetProperty("creditCardCategoriesAvailable").GetBoolean());
        }

        var invalidSource = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30&source=unknown");
        var missingDate = await app.Client.GetAsync("/api/consumption?startDate=2026-09-01");
        var unsupportedCategory = await app.Client.GetAsync(
            $"/api/consumption?startDate=2026-09-01&endDate=2026-09-30&source=credit_card&categoryId={app.CategoryId}");

        Assert.Equal(HttpStatusCode.BadRequest, invalidSource.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingDate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedCategory.StatusCode);
    }

    /// <summary>驗證信用卡來源可查詢描述，且多重歷史關聯會回傳資料品質警告。</summary>
    [Fact]
    public async Task GetConsumption_SearchesCreditDescriptionAndReportsMultipleLinks()
    {
        await using var app = await CreateAppAsync();
        var linked = await app.SeedLinkedInstallmentAsync(
            600m,
            new DateOnly(2026, 9, 5),
            new DateOnly(2026, 9, 5),
            "手機分期");
        await app.SeedInstallmentForExistingTransactionAsync(
            linked.TransactionId,
            700m,
            new DateOnly(2026, 9, 5),
            "另一筆關聯");

        var response = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30&source=credit_card&search=手機");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(600m, body.RootElement.GetProperty("summary").GetProperty("totalAmount").GetDecimal());
        Assert.NotEmpty(body.RootElement.GetProperty("warnings").EnumerateArray());
    }

    /// <summary>驗證收入、軟刪除與卡費 predicate 邊界不會污染 consumption。</summary>
    [Fact]
    public async Task GetConsumption_ExcludesIncomeDeletedAndNotesOnlyRepaymentMarker()
    {
        await using var app = await CreateAppAsync();
        var livingCategoryId = await app.SeedCategoryAsync("原生活名稱", "living");
        await app.SeedOrdinaryExpenseAsync(100m, new DateOnly(2026, 9, 5), "生活用品", livingCategoryId);
        await app.SeedOrdinaryExpenseAsync(
            50m,
            new DateOnly(2026, 9, 5),
            "備註含關鍵字",
            app.CategoryId,
            "信用卡帳單");
        await app.SeedTransactionAsync(
            999m,
            new DateOnly(2026, 9, 5),
            "薪資",
            app.CategoryId,
            TransactionType.Income);
        var deletedId = await app.SeedOrdinaryExpenseAsync(
            777m,
            new DateOnly(2026, 9, 5),
            "已刪除",
            app.CategoryId);
        await app.SoftDeleteTransactionAsync(deletedId);
        await app.RenameCategoryAsync(livingCategoryId, "生活重新命名");

        var response = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30");

        Assert.Equal(150m, await ReadSummaryAmountAsync(response));
    }

    /// <summary>驗證資料超過單頁時 summary 仍涵蓋完整篩選集合。</summary>
    [Fact]
    public async Task GetConsumption_SummarizesOneHundredTwentyFiveRecordsAcrossPages()
    {
        await using var app = await CreateAppAsync();
        await app.SeedOrdinaryExpensesAsync(125, new DateOnly(2026, 9, 5));

        var response = await app.Client.GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30&page=2&pageSize=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(20, body.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(125, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(125, body.RootElement.GetProperty("summary").GetProperty("count").GetInt32());
        Assert.Equal(1250m, body.RootElement.GetProperty("summary").GetProperty("totalAmount").GetDecimal());
    }

    /// <summary>讀取 consumption 回應中的完整摘要金額。</summary>
    private static async Task<decimal> ReadSummaryAmountAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("summary").GetProperty("totalAmount").GetDecimal();
    }

    /// <summary>建立 consumption endpoint 測試 host 與空的 SQLite 記憶體資料庫。</summary>
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
        builder.Services.Configure<TimeZoneOptions>(options => options.Default = "Asia/Taipei");
        builder.Services.AddSingleton<TimeZoneService>();
        builder.Services.AddScoped<ConsumptionQueryService>();
        builder.Services.AddScoped<TransactionCommandService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });

        var app = builder.Build();
        app.MapConsumptionEndpoints();
        app.MapTransactionEndpoints();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            var category = new Category
            {
                Name = "其他",
                Type = CategoryType.Expense,
                SystemCode = "other-expense",
            };
            var cash = new PaymentMethod
            {
                Name = "現金",
                SystemCode = "cash",
            };
            var card = new CreditCard
            {
                BankName = "測試銀行",
                LastFourDigits = "1234",
                StatementDay = 15,
                DueDay = 23,
            };
            db.AddRange(category, cash, card);
            await db.SaveChangesAsync();
            await app.StartAsync();
            return new TestApp(app, connection, category.Id, cash.Id, card.Id);
        }
    }

    /// <summary>保存 consumption 測試 host 與參考資料識別碼。</summary>
    private sealed record TestApp(
        WebApplication App,
        SqliteConnection Connection,
        int CategoryId,
        int CashPaymentMethodId,
        int CardId) : IAsyncDisposable
    {
        /// <summary>取得測試 host 的 HTTP client。</summary>
        public HttpClient Client { get; } = App.GetTestClient();

        /// <summary>新增一筆普通支出 fixture。</summary>
        public async Task<int> SeedOrdinaryExpenseAsync(decimal amount, DateOnly date, string description)
            => await SeedOrdinaryExpenseAsync(amount, date, description, CategoryId);

        /// <summary>新增指定分類的普通支出 fixture。</summary>
        public async Task<int> SeedOrdinaryExpenseAsync(
            decimal amount,
            DateOnly date,
            string description,
            int categoryId,
            string? notes = null)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transaction = new Transaction
            {
                Type = TransactionType.Expense,
                Amount = amount,
                Date = date,
                Description = description,
                Notes = notes,
                CategoryId = categoryId,
                PaymentMethodId = CashPaymentMethodId,
            };
            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();
            return transaction.Id;
        }

        /// <summary>新增指定數量的普通支出 fixture，測試跨頁 summary。</summary>
        public async Task SeedOrdinaryExpensesAsync(int count, DateOnly date)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Transactions.AddRange(Enumerable.Range(1, count).Select(index => new Transaction
            {
                Type = TransactionType.Expense,
                Amount = 10m,
                Date = date,
                Description = $"跨頁支出 {index}",
                CategoryId = CategoryId,
                PaymentMethodId = CashPaymentMethodId,
            }));
            await db.SaveChangesAsync();
        }

        /// <summary>新增收入 fixture 以確認 consumption 僅納入 Expense。</summary>
        public async Task<int> SeedTransactionAsync(
            decimal amount,
            DateOnly date,
            string description,
            int categoryId,
            TransactionType type,
            string? notes = null)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transaction = new Transaction
            {
                Type = type,
                Amount = amount,
                Date = date,
                Description = description,
                Notes = notes,
                CategoryId = categoryId,
                PaymentMethodId = CashPaymentMethodId,
            };
            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();
            return transaction.Id;
        }

        /// <summary>新增一個 consumption 測試分類並回傳其識別碼。</summary>
        public async Task<int> SeedCategoryAsync(string name, string systemCode)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var category = new Category
            {
                Name = name,
                Type = CategoryType.Expense,
                SystemCode = systemCode,
            };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
            return category.Id;
        }

        /// <summary>新增一筆獨立信用卡消費 fixture。</summary>
        public async Task SeedInstallmentAsync(decimal amount, int periods, DateOnly date, string description)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Installments.Add(new Installment
            {
                CardId = CardId,
                TotalAmount = amount,
                Periods = periods,
                PerPeriod = amount / periods,
                PurchaseDate = date,
                Description = description,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>新增一筆帶歷史普通交易關聯的複合 consumption fixture。</summary>
        public async Task<(int TransactionId, int InstallmentId)> SeedLinkedInstallmentAsync(
            decimal amount,
            DateOnly purchaseDate,
            DateOnly transactionDate,
            string description)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transaction = new Transaction
            {
                Type = TransactionType.Expense,
                Amount = amount,
                Date = transactionDate,
                Description = description,
                CategoryId = CategoryId,
                PaymentMethodId = CashPaymentMethodId,
            };
            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();
            var installment = new Installment
            {
                TransactionId = transaction.Id,
                CardId = CardId,
                TotalAmount = amount,
                Periods = 1,
                PerPeriod = amount,
                PurchaseDate = purchaseDate,
                Description = description,
            };
            db.Installments.Add(installment);
            await db.SaveChangesAsync();
            return (transaction.Id, installment.Id);
        }

        /// <summary>新增另一筆指向既有交易的信用卡 fixture。</summary>
        public async Task SeedInstallmentForExistingTransactionAsync(
            int transactionId,
            decimal amount,
            DateOnly purchaseDate,
            string description)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Installments.Add(new Installment
            {
                TransactionId = transactionId,
                CardId = CardId,
                TotalAmount = amount,
                Periods = 1,
                PerPeriod = amount,
                PurchaseDate = purchaseDate,
                Description = description,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>刪除指定分期，驗證刪除後普通關聯端可重新參與消費。</summary>
        public async Task DeleteInstallmentAsync(int installmentId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var installment = await db.Installments.SingleAsync(item => item.Id == installmentId);
            db.Installments.Remove(installment);
            await db.SaveChangesAsync();
        }

        /// <summary>軟刪除普通交易以驗證 consumption 遵循原始資料生命週期。</summary>
        public async Task SoftDeleteTransactionAsync(int transactionId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transaction = await db.Transactions.IgnoreQueryFilters().SingleAsync(item => item.Id == transactionId);
            transaction.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        /// <summary>修改分類顯示名稱，確認 consumption 依 systemCode 判斷卡費。</summary>
        public async Task RenameCategoryAsync(int categoryId, string name)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var category = await db.Categories.SingleAsync(item => item.Id == categoryId);
            category.Name = name;
            await db.SaveChangesAsync();
        }

        /// <summary>釋放 consumption 測試 host 與 SQLite 連線。</summary>
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
