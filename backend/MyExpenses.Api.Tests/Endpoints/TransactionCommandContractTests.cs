using System.Net;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Models.Requests;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class TransactionCommandContractTests
{
    /// <summary>驗證 keyed 普通命令拒絕未定義型別、缺少型別及不合法金額，且不留下交易或收據。</summary>
    [Theory]
    [InlineData("999", "100")]
    [InlineData("-1", "100")]
    [InlineData("null", "100")]
    [InlineData("\"unknown\"", "100")]
    [InlineData("\"Expense\"", "0")]
    [InlineData("\"Expense\"", "-0.01")]
    [InlineData("\"Expense\"", "null")]
    [InlineData("\"Expense\"", "1e100")]
    [InlineData("\"Expense\"", "\"NaN\"")]
    public async Task PostTransaction_WithInvalidTypeOrAmount_DoesNotWrite(string type, string amount)
    {
        await using var app = await CreateAppAsync();
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var content = new StringContent(
            $$"""{"type":{{type}},"amount":{{amount}},"date":"2026-09-05","description":"輸入驗證","categoryId":{{app.CategoryId}},"paymentMethodId":{{app.CashPaymentMethodId}}}""",
            System.Text.Encoding.UTF8, "application/json");

        var response = await app.Client.PostAsync("/api/transactions", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await app.CountTransactionsAsync());
        Assert.Equal(0, await app.CountIdempotencyRecordsAsync());
    }

    /// <summary>驗證有效但與支出分類衝突的收入型別回傳語意錯誤，而非建立不一致交易。</summary>
    [Fact]
    public async Task PostTransaction_WithIncomeTypeAndExpenseCategory_DoesNotWrite()
    {
        await using var app = await CreateAppAsync();
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await app.Client.PostAsJsonAsync("/api/transactions", new
        {
            type = TransactionType.Income, amount = 100m, date = "2026-09-05",
            description = "型別不相容", categoryId = app.CategoryId, paymentMethodId = app.CashPaymentMethodId,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await app.CountTransactionsAsync());
        Assert.Equal(0, await app.CountIdempotencyRecordsAsync());
    }

    /// <summary>驗證未帶 key 的既有 API client 不受 keyed 命令契約阻擋。</summary>
    [Fact]
    public async Task PostTransaction_WithoutIdempotencyKey_UsesLegacyPath()
    {
        await using var app = await CreateAppAsync();

        var response = await app.Client.PostAsJsonAsync("/api/transactions", CreateRequest(app, date: "2026-09-05"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await app.CountTransactionsAsync());
    }

    /// <summary>驗證 keyed 普通新增拒絕格式錯誤的 UUID 並且不降級寫入。</summary>
    [Fact]
    public async Task PostTransaction_WithMalformedIdempotencyKey_RejectsWithoutWriting()
    {
        await using var app = await CreateAppAsync();
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", "not-a-uuid");

        var response = await app.Client.PostAsJsonAsync("/api/transactions", CreateRequest(app, date: "2026-09-05"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await app.CountTransactionsAsync());
    }

    /// <summary>驗證空白冪等標頭也會被視為格式錯誤而不降級寫入。</summary>
    [Fact]
    public async Task PostTransaction_WithBlankIdempotencyKey_RejectsWithoutWriting()
    {
        await using var app = await CreateAppAsync();
        app.Client.DefaultRequestHeaders.TryAddWithoutValidation("Idempotency-Key", " ");

        var response = await app.Client.PostAsJsonAsync("/api/transactions", CreateRequest(app, date: "2026-09-05"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await app.CountTransactionsAsync());
    }

    /// <summary>驗證 keyed 普通新增必須固定日期以確保重試內容穩定。</summary>
    [Fact]
    public async Task PostTransaction_WithKeyButNoDate_RejectsWithoutWriting()
    {
        await using var app = await CreateAppAsync();
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await app.Client.PostAsJsonAsync("/api/transactions", CreateRequest(app));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await app.CountTransactionsAsync());
    }

    /// <summary>驗證相同 keyed payload 重試只回傳同一筆普通交易。</summary>
    [Fact]
    public async Task PostTransaction_WithSameKeyAndPayload_ReplaysSameTransaction()
    {
        await using var app = await CreateAppAsync();
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var request = CreateRequest(app, date: "2026-09-05");

        var first = await app.Client.PostAsJsonAsync("/api/transactions", request);
        var second = await app.Client.PostAsJsonAsync("/api/transactions", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstId = await ReadIdAsync(first);
        var secondId = await ReadIdAsync(second);
        Assert.Equal(firstId, secondId);
        Assert.Equal(1, await app.CountTransactionsAsync());
        Assert.Equal(1, await app.CountIdempotencyRecordsAsync());
    }

    /// <summary>驗證同一 key 搭配不同 payload 會回傳衝突。</summary>
    [Fact]
    public async Task PostTransaction_WithSameKeyAndDifferentPayload_ReturnsConflict()
    {
        await using var app = await CreateAppAsync();
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var first = await app.Client.PostAsJsonAsync("/api/transactions", CreateRequest(app, amount: 100m, date: "2026-09-05"));
        var second = await app.Client.PostAsJsonAsync("/api/transactions", CreateRequest(app, amount: 200m, date: "2026-09-05"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await app.CountTransactionsAsync());
    }

    /// <summary>驗證相同內容使用不同 key 代表兩個獨立普通新增命令。</summary>
    [Fact]
    public async Task PostTransaction_WithDifferentKeysCreatesIndependentEntries()
    {
        await using var app = await CreateAppAsync();
        var request = CreateRequest(app, date: "2026-09-05");
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var first = await app.Client.PostAsJsonAsync("/api/transactions", request);
        app.Client.DefaultRequestHeaders.Remove("Idempotency-Key");
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var second = await app.Client.PostAsJsonAsync("/api/transactions", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.NotEqual(await ReadIdAsync(first), await ReadIdAsync(second));
        Assert.Equal(2, await app.CountTransactionsAsync());
    }

    /// <summary>驗證普通交易被編輯後重播仍回傳同一識別碼與目前資料。</summary>
    [Fact]
    public async Task PostTransaction_ReplayAfterEditReturnsCurrentCanonicalData()
    {
        await using var app = await CreateAppAsync();
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var request = CreateRequest(app, date: "2026-09-05");

        var first = await app.Client.PostAsJsonAsync("/api/transactions", request);
        var id = await ReadIdAsync(first);
        await app.UpdateTransactionDescriptionAsync(id, "已編輯交易");
        var replay = await app.Client.PostAsJsonAsync("/api/transactions", request);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("X-Idempotent-Replay").Single());
        using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(id, body.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("已編輯交易", body.RootElement.GetProperty("description").GetString());
    }

    /// <summary>驗證普通交易被軟刪除後重播回傳 410 並保留 receipt。</summary>
    [Fact]
    public async Task PostTransaction_ReplayAfterSoftDeleteReturnsUnavailable()
    {
        await using var app = await CreateAppAsync();
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var first = await app.Client.PostAsJsonAsync(
            "/api/transactions",
            CreateRequest(app, date: "2026-09-05"));
        await app.SoftDeleteTransactionAsync(await ReadIdAsync(first));

        var replay = await app.Client.PostAsJsonAsync(
            "/api/transactions",
            CreateRequest(app, date: "2026-09-05"));

        Assert.Equal(HttpStatusCode.Gone, replay.StatusCode);
        using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal("result_unavailable", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, await app.CountTransactionsIncludingDeletedAsync());
        Assert.Equal(1, await app.CountIdempotencyRecordsAsync());
    }

    /// <summary>驗證同一 key 的並行普通新增只提交一筆交易。</summary>
    [Fact]
    public async Task PostTransaction_WithSameKeyConcurrently_CreatesOneTransaction()
    {
        await using var app = await CreateAppAsync();
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var request = CreateRequest(app, date: "2026-09-05");

        var responses = await Task.WhenAll(
            app.Client.PostAsJsonAsync("/api/transactions", request),
            app.Client.PostAsJsonAsync("/api/transactions", request));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Equal(await ReadIdAsync(responses[0]), await ReadIdAsync(responses[1]));
        Assert.Equal(1, await app.CountTransactionsAsync());
        Assert.Equal(1, await app.CountIdempotencyRecordsAsync());
    }

    /// <summary>驗證 keyed 普通新增不能繞過信用卡獨立交易流程。</summary>
    [Fact]
    public async Task PostTransaction_WithCreditCardPaymentMethod_ReturnsSemanticError()
    {
        await using var app = await CreateAppAsync();
        app.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await app.Client.PostAsJsonAsync(
            "/api/transactions",
            CreateRequest(app, date: "2026-09-05", paymentMethodId: app.CreditCardPaymentMethodId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await app.CountTransactionsAsync());
    }

    /// <summary>驗證不同服務實例共用資料庫時仍由唯一收據保證同 key 只建立一筆。</summary>
    [Fact]
    public async Task PostTransaction_WithSeparateServiceInstances_ReplaysCommittedReceipt()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"myexpenses-idempotency-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .Options;
            int categoryId;
            int cashPaymentMethodId;
            await using (var seedDb = new AppDbContext(options))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var category = new Category { Name = "其他", Type = CategoryType.Expense, SystemCode = "other-expense" };
                var cash = new PaymentMethod { Name = "現金", SystemCode = "cash" };
                seedDb.AddRange(category, cash);
                await seedDb.SaveChangesAsync();
                categoryId = category.Id;
                cashPaymentMethodId = cash.Id;
            }

            var request = new CreateTransactionRequest
            {
                Type = TransactionType.Expense,
                Amount = 100m,
                Date = new DateOnly(2026, 9, 5),
                Description = "跨服務重試",
                CategoryId = categoryId,
                PaymentMethodId = cashPaymentMethodId,
            };
            var key = Guid.NewGuid().ToString();
            var barrier = new ConcurrentTransactionBarrier();
            var concurrentOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .AddInterceptors(barrier)
                .Options;
            await using var firstDb = new AppDbContext(concurrentOptions);
            await using var secondDb = new AppDbContext(concurrentOptions);
            var firstService = new TransactionCommandService(
                firstDb,
                new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { Default = "Asia/Taipei" })));
            var secondService = new TransactionCommandService(
                secondDb,
                new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { Default = "Asia/Taipei" })));

            var results = await Task.WhenAll(
                Task.Run(() => firstService.CreateAsync(request, key)),
                Task.Run(() => secondService.CreateAsync(request, key)));

            Assert.Equal(2, barrier.Arrivals);
            Assert.Single(results, result => result.Replayed);
            Assert.Equal(results[0].Transaction.Id, results[1].Transaction.Id);
            await using var verifyDb = new AppDbContext(options);
            Assert.Equal(1, await verifyDb.Transactions.CountAsync());
            Assert.Equal(1, await verifyDb.IdempotencyRecords.CountAsync());
        }
        finally
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    /// <summary>在兩個獨立連線皆完成首次收據查詢後才允許開始交易，避免同步 SQLite 造成假併發。</summary>
    private sealed class ConcurrentTransactionBarrier : DbTransactionInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => _arrivals;

        /// <summary>等待兩個命令到達交易邊界，並以有界等待避免測試卡死。</summary>
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
                _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    /// <summary>驗證未帶 key 的既有 API client 保留日期預設與新增行為。</summary>
    [Fact]
    public async Task PostTransaction_WithoutKey_PreservesLegacyCreatePath()
    {
        await using var app = await CreateAppAsync();

        var response = await app.Client.PostAsJsonAsync("/api/transactions", CreateRequest(app));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await app.CountTransactionsAsync());
    }

    /// <summary>建立普通交易測試 request，保留可選日期與付款方式覆寫。</summary>
    private static object CreateRequest(
        TestApp app,
        decimal amount = 100m,
        string? date = null,
        int? paymentMethodId = null)
        => new
        {
            type = TransactionType.Expense,
            amount,
            date,
            description = "測試交易",
            categoryId = app.CategoryId,
            paymentMethodId = paymentMethodId ?? app.CashPaymentMethodId,
        };

    /// <summary>讀取新增交易回應中的識別碼。</summary>
    private static async Task<int> ReadIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetInt32();
    }

    /// <summary>建立使用 SQLite 記憶體資料庫的交易 endpoint 測試 host。</summary>
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
        builder.Services.AddScoped<TransactionCommandService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });

        var app = builder.Build();
        app.MapTransactionEndpoints();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            var category = new Category
            {
                Name = "其他",
                Type = CategoryType.Expense,
                Icon = "MoreHorizontal",
                Color = "#64748B",
                SystemCode = "other-expense",
            };
            var cash = new PaymentMethod
            {
                Name = "現金",
                Icon = "Banknote",
                SystemCode = "cash",
            };
            var creditCard = new PaymentMethod
            {
                Name = "信用卡",
                Icon = "CreditCard",
                SystemCode = "credit-card",
            };
            db.AddRange(category, cash, creditCard);
            await db.SaveChangesAsync();

            await app.StartAsync();
            return new TestApp(app, connection, category.Id, cash.Id, creditCard.Id);
        }
    }

    /// <summary>提供交易 endpoint 測試所需的 host、資料庫與參考資料識別碼。</summary>
    private sealed record TestApp(
        WebApplication App,
        SqliteConnection Connection,
        int CategoryId,
        int CashPaymentMethodId,
        int CreditCardPaymentMethodId) : IAsyncDisposable
    {
        /// <summary>取得測試 host 的固定 HTTP client，讓 header 可跨重試保留。</summary>
        public HttpClient Client { get; } = App.GetTestClient();

        /// <summary>計算目前資料庫中的普通交易數量。</summary>
        public async Task<int> CountTransactionsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Transactions.CountAsync();
        }

        /// <summary>修改交易描述以驗證 receipt replay 使用目前 canonical 資料。</summary>
        public async Task UpdateTransactionDescriptionAsync(int transactionId, string description)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transaction = await db.Transactions.IgnoreQueryFilters().SingleAsync(item => item.Id == transactionId);
            transaction.Description = description;
            await db.SaveChangesAsync();
        }

        /// <summary>軟刪除交易以驗證 replay 不重建原始結果。</summary>
        public async Task SoftDeleteTransactionAsync(int transactionId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transaction = await db.Transactions.IgnoreQueryFilters().SingleAsync(item => item.Id == transactionId);
            transaction.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        /// <summary>計算包含軟刪除交易的總筆數。</summary>
        public async Task<int> CountTransactionsIncludingDeletedAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Transactions.IgnoreQueryFilters().CountAsync();
        }

        /// <summary>計算目前資料庫中的冪等收據數量。</summary>
        public async Task<int> CountIdempotencyRecordsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.IdempotencyRecords.CountAsync();
        }

        /// <summary>釋放交易 endpoint 測試 host 與 SQLite 連線。</summary>
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
