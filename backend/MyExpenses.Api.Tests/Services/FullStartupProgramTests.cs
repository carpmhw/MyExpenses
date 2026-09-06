using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

/// <summary>以真正 Program startup path 驗證 migration、seed、recovery、hosted service 與 readiness。</summary>
public sealed class FullStartupProgramTests
{
    private const string JwtSecret = "full-startup-test-jwt-secret-0123456789";
    private const string BootstrapSecret = "full-startup-test-bootstrap-0123456789";

    /// <summary>驗證非 Production 空資料庫會完成 migration、reference seed、recovery 與 readiness。</summary>
    [Fact]
    public async Task StagingEmptyDatabase_CompletesStartupAndReadiness()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var logs = new CapturingLoggerProvider();
        var externalRequests = new ExternalRequestCounter();
        using var factory = CreateFactory(
            temporaryDirectory.RootPath,
            Environments.Staging,
            logs,
            externalRequests);
        using var client = factory.CreateClient();

        await AssertReadyAsync(client);

        await using var db = CreateDbContext(temporaryDirectory.RootPath, isProduction: false);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await db.Categories.AnyAsync());
        Assert.True(await db.PaymentMethods.AnyAsync());
        Assert.Single(await db.SystemSettings.ToListAsync());
        Assert.Single(await db.AutoSnapshotConfigs.ToListAsync());
        Assert.Empty(await db.Transactions.ToListAsync());
        AssertHostedServicesStarted(logs);
        AssertNoTargetWarning(logs);
        Assert.Equal(0, externalRequests.Count);
    }

    /// <summary>驗證實際 Program 在 Staging 對無篩選及排序的 First 查詢套用 Query 10103 throw policy。</summary>
    [Fact]
    public async Task StagingProgram_ThrowsForUnsafeFirstQuery()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var logs = new CapturingLoggerProvider();
        var externalRequests = new ExternalRequestCounter();
        using var factory = CreateFactory(
            temporaryDirectory.RootPath,
            Environments.Staging,
            logs,
            externalRequests);
        using var client = factory.CreateClient();

        await AssertReadyAsync(client);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.Categories.FirstOrDefaultAsync());

        Assert.Contains("FirstWithoutOrderByAndFilterWarning", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, externalRequests.Count);
    }

    /// <summary>驗證既有資料庫重啟會保持資料語意並復原遺留的 Running execution。</summary>
    [Fact]
    public async Task StagingRestart_RecoversRunningExecutionIdempotently()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using (var firstLogs = new CapturingLoggerProvider())
        {
            var firstRequests = new ExternalRequestCounter();
            using var firstFactory = CreateFactory(
                temporaryDirectory.RootPath,
                Environments.Staging,
                firstLogs,
                firstRequests);
            using var firstClient = firstFactory.CreateClient();
            await AssertReadyAsync(firstClient);
        }

        await using (var seededDb = CreateDbContext(temporaryDirectory.RootPath, isProduction: false))
        {
            seededDb.ScheduledJobExecutions.Add(new ScheduledJobExecution
            {
                JobKey = ScheduledJobKey.AutomaticSnapshot,
                ScheduledForUtc = DateTime.UtcNow.AddHours(-1),
                ScheduleTimeZoneId = "Asia/Taipei",
                ScheduledLocalDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-1)),
                Status = ScheduledJobExecutionStatus.Running,
                StartedAtUtc = DateTime.UtcNow.AddHours(-1),
                AttemptCount = 1,
            });
            await seededDb.SaveChangesAsync();
        }

        using var logs = new CapturingLoggerProvider();
        var externalRequests = new ExternalRequestCounter();
        using var factory = CreateFactory(
            temporaryDirectory.RootPath,
            Environments.Staging,
            logs,
            externalRequests);
        using var client = factory.CreateClient();

        await AssertReadyAsync(client);

        await using var db = CreateDbContext(temporaryDirectory.RootPath, isProduction: false);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await db.Categories.AnyAsync());
        Assert.Empty(await db.Transactions.ToListAsync());
        var execution = await db.ScheduledJobExecutions.SingleAsync();
        Assert.Equal(ScheduledJobExecutionStatus.Interrupted, execution.Status);
        Assert.NotNull(execution.CompletedAtUtc);
        AssertNoTargetWarning(logs);
        Assert.Equal(0, externalRequests.Count);
    }

    /// <summary>驗證 Development 會建立 sample data 且仍完成真實 startup 與 readiness。</summary>
    [Fact]
    public async Task DevelopmentEmptyDatabase_SeedsSampleDataWithoutTargetWarning()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var logs = new CapturingLoggerProvider();
        var externalRequests = new ExternalRequestCounter();
        using var factory = CreateFactory(
            temporaryDirectory.RootPath,
            Environments.Development,
            logs,
            externalRequests);
        using var client = factory.CreateClient();

        await AssertReadyAsync(client);

        await using var db = CreateDbContext(temporaryDirectory.RootPath, isProduction: false);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await db.Transactions.AnyAsync());
        Assert.True(await db.BankAccounts.AnyAsync());
        Assert.True(await db.CreditCards.AnyAsync());
        Assert.Single(await db.AutoSnapshotConfigs.ToListAsync());
        AssertNoTargetWarning(logs);
        Assert.Equal(0, externalRequests.Count);
    }

    /// <summary>驗證 Production 空資料庫只建立 reference data，不寫入 Development sample data。</summary>
    [Fact]
    public async Task ProductionEmptyDatabase_DoesNotSeedDevelopmentSamples()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var logs = new CapturingLoggerProvider();
        var externalRequests = new ExternalRequestCounter();
        using var factory = CreateFactory(
            temporaryDirectory.RootPath,
            Environments.Production,
            logs,
            externalRequests);
        using var client = factory.CreateClient();

        await AssertReadyAsync(client);

        await using var db = CreateDbContext(temporaryDirectory.RootPath, isProduction: true);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await db.Categories.AnyAsync());
        Assert.True(await db.PaymentMethods.AnyAsync());
        Assert.Empty(await db.Transactions.ToListAsync());
        Assert.Empty(await db.BankAccounts.ToListAsync());
        Assert.Empty(await db.CreditCards.ToListAsync());
        AssertNoTargetWarning(logs);
        Assert.Equal(0, externalRequests.Count);
    }

    /// <summary>建立使用暫存檔案 SQLite、有效 secrets、隔離 key path 與外部 HTTP 替身的完整 Program factory。</summary>
    private static WebApplicationFactory<Program> CreateFactory(
        string rootPath,
        string environmentName,
        CapturingLoggerProvider logs,
        ExternalRequestCounter externalRequests)
    {
        var databasePath = Path.Combine(rootPath, "MyExpenses.db");
        var keyPath = Path.Combine(rootPath, "keys");
        var backupPath = Path.Combine(rootPath, "backups");
        var factory = new WebApplicationFactory<Program>();
        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={databasePath}");
            builder.UseSetting("DataProtection:ApplicationName", "MyExpenses.FullStartup.Tests");
            builder.UseSetting("DataProtection:KeyDirectory", keyPath);
            builder.UseSetting("Jwt:Secret", JwtSecret);
            builder.UseSetting("Jwt:Issuer", "MyExpenses");
            builder.UseSetting("Jwt:Audience", "MyExpenses");
            builder.UseSetting("Bootstrap:Secret", BootstrapSecret);
            builder.UseSetting("Deployment:Mode", "Local");
            builder.UseSetting("Deployment:BindAddress", "127.0.0.1");
            builder.UseSetting("Deployment:PublicOrigin", "http://127.0.0.1");
            builder.UseSetting("Deployment:SecureCookies", "false");
            builder.UseSetting("SqliteBackup:BackupDirectory", backupPath);
            builder.UseSetting("TimeZone:Default", "Asia/Taipei");
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services =>
            {
                foreach (var clientName in new[]
                         {
                             "exchange-rates",
                             "historical-market-data",
                             "twse-current-price",
                             "tpex-current-price",
                         })
                {
                    services.Configure<HttpClientFactoryOptions>(clientName, options =>
                        options.HttpMessageHandlerBuilderActions.Add(handlerBuilder =>
                            handlerBuilder.PrimaryHandler = new StubHttpMessageHandler(externalRequests)));
                }
            });
        });
    }

    /// <summary>建立連到測試檔案 SQLite 的 context，並沿用本次環境的 warning policy。</summary>
    private static AppDbContext CreateDbContext(string rootPath, bool isProduction)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(rootPath, "MyExpenses.db")}");
        EfCoreQueryWarningPolicy.Configure(optionsBuilder, isProduction);
        return new AppDbContext(optionsBuilder.Options);
    }

    /// <summary>確認 liveness 與 readiness endpoint 均回傳 HTTP 200。</summary>
    private static async Task AssertReadyAsync(HttpClient client)
    {
        using var liveResponse = await client.GetAsync("/health/live");
        using var readyResponse = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
    }

    /// <summary>確認三個正式註冊的 hosted services 已進入執行迴圈。</summary>
    private static void AssertHostedServicesStarted(CapturingLoggerProvider logs)
    {
        Assert.True(logs.Contains("Snapshot background service started"));
        Assert.True(logs.Contains("Stock price update service started"));
        Assert.True(logs.Contains("Historical market data sync service started"));
    }

    /// <summary>確認完整 host log 沒有目標 warning 或被捕捉後繼續執行的目標例外。</summary>
    private static void AssertNoTargetWarning(CapturingLoggerProvider logs)
    {
        Assert.DoesNotContain(
            logs.Messages,
            message => message.Contains("FirstWithoutOrderByAndFilterWarning", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Messages,
            message => message.Contains("Microsoft.EntityFrameworkCore.Query[10103]", StringComparison.Ordinal));
    }

    /// <summary>保存測試產生的檔案並在測試完成後移除。</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>建立本次測試專用的暫存目錄。</summary>
        public TemporaryDirectory()
        {
            RootPath = Directory.CreateTempSubdirectory("myexpenses-full-startup-tests-").FullName;
        }

        /// <summary>取得測試專用目錄的絕對路徑。</summary>
        public string RootPath { get; }

        /// <summary>遞迴移除測試產生的 database、backup 與 key files。</summary>
        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }

    /// <summary>統計測試 host 是否意外呼叫外部行情或匯率 endpoint。</summary>
    private sealed class ExternalRequestCounter
    {
        private int _count;

        /// <summary>取得目前替身 handler 收到的 request 數量。</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>增加外部 request 計數。</summary>
        public void Increment() => Interlocked.Increment(ref _count);
    }

    /// <summary>阻擋完整 startup 測試對外部 HTTP 服務的依賴。</summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly ExternalRequestCounter _counter;

        /// <summary>建立連結指定 request counter 的 HTTP 替身。</summary>
        public StubHttpMessageHandler(ExternalRequestCounter counter)
        {
            _counter = counter;
        }

        /// <summary>記錄意外外部呼叫並回傳固定失敗 response。</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _counter.Increment();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = request,
            });
        }
    }

    /// <summary>保存完整 Program host 的 log，讓測試能觀察背景例外而非只看 process 存活。</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        /// <summary>取得目前所有已格式化的 log message。</summary>
        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        /// <summary>建立寫入共用測試佇列的 logger。</summary>
        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(categoryName, _messages);

        /// <summary>判斷 log 中是否包含指定文字。</summary>
        public bool Contains(string value)
            => _messages.Any(message => message.Contains(value, StringComparison.Ordinal));

        /// <summary>釋放測試 logger provider 的資源。</summary>
        public void Dispose()
        {
        }
    }

    /// <summary>將 logger state 與例外保存為可檢查的單一文字訊息。</summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly ConcurrentQueue<string> _messages;

        /// <summary>建立指定 category 與共用訊息佇列的 logger。</summary>
        public CapturingLogger(string categoryName, ConcurrentQueue<string> messages)
        {
            _categoryName = categoryName;
            _messages = messages;
        }

        /// <summary>不建立 scope，因為測試只需要完整訊息內容。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        /// <summary>啟用所有 level，確保目標 warning 與背景 error 不會被 log filter 過濾。</summary>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <summary>保存格式化後的 category、level、message 與例外內容。</summary>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception is not null)
                message = $"{message} {exception}";
            _messages.Enqueue($"{_categoryName} [{logLevel}] {message}");
        }

        /// <summary>提供不執行任何動作的 logger scope。</summary>
        private sealed class NullScope : IDisposable
        {
            /// <summary>取得共用空 scope。</summary>
            public static NullScope Instance { get; } = new();

            /// <summary>結束空 scope。</summary>
            public void Dispose()
            {
            }
        }
    }
}
