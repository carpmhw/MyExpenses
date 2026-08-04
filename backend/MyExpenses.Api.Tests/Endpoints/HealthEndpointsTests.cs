using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyExpenses.Api.Data;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class HealthEndpointsTests
{
    /// <summary>驗證 process liveness 不依賴 SQLite，資料庫不可用時仍回傳成功。</summary>
    [Fact]
    public async Task LiveHealth_DoesNotDependOnSqlite()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "missing", "MyExpenses.db");
        await using var app = await CreateHealthAppAsync(databasePath, startupReady: false);

        var response = await app.GetTestClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>驗證 startup coordinator 尚未 ready 時 readiness endpoint 會回傳服務不可用。</summary>
    [Fact]
    public async Task ReadyHealth_FailsWhenStartupIsIncomplete()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "MyExpenses.db");
        await using var app = await CreateHealthAppAsync(databasePath, startupReady: false);

        var response = await app.GetTestClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>驗證 startup 標記 ready 但 SQLite 不可用時 readiness endpoint 仍會失敗。</summary>
    [Fact]
    public async Task ReadyHealth_FailsWhenSqliteIsUnavailable()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "database", "MyExpenses.db");
        await using var app = await CreateHealthAppAsync(databasePath, startupReady: true);

        var response = await app.GetTestClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>驗證 migrations 尚未 current 時 readiness endpoint 不會宣告成功。</summary>
    [Fact]
    public async Task ReadyHealth_FailsWhenMigrationsArePending()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "MyExpenses.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync("20260802090418_AddAtomicFinancialCommands");
        }

        await using var app = await CreateHealthAppAsync(databasePath, startupReady: true);

        var response = await app.GetTestClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>建立只註冊健康檢查的 test host，保留與 production 相同的 endpoint mapping。</summary>
    private static async Task<WebApplication> CreateHealthAppAsync(
        string databasePath,
        bool startupReady)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath}"));
        builder.Services.AddSingleton<IStartupReadiness>(new TestStartupReadiness(startupReady));
        builder.Services.AddDeploymentHealthChecks();

        var app = builder.Build();
        app.MapDeploymentHealthChecks();
        await app.StartAsync();
        return app;
    }

    /// <summary>提供每個測試獨立且自動清理的暫存目錄。</summary>
    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        /// <summary>建立測試暫存目錄。</summary>
        public TemporaryDirectory()
        {
            RootPath = Directory.CreateTempSubdirectory("myexpenses-health-tests-").FullName;
        }

        /// <summary>取得測試暫存目錄的絕對路徑。</summary>
        public string RootPath { get; }

        /// <summary>刪除測試產生的所有資料。</summary>
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>模擬 startup coordinator 的 readiness 狀態供 endpoint 測試使用。</summary>
    private sealed class TestStartupReadiness : IStartupReadiness
    {
        /// <summary>建立指定 readiness 狀態的測試 startup state。</summary>
        public TestStartupReadiness(bool isReady)
        {
            IsReady = isReady;
        }

        /// <summary>取得測試指定的 startup readiness 狀態。</summary>
        public bool IsReady { get; }
    }
}
