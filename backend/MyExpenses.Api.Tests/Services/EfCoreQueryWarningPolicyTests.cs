using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyExpenses.Api.Data;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

/// <summary>驗證 EF Core Query 10103 在診斷環境的失敗政策與 Production 可觀測性。</summary>
public sealed class EfCoreQueryWarningPolicyTests
{
    /// <summary>驗證非 Production 環境遇到無條件 First 查詢時會直接拋出目標 warning 例外。</summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    [InlineData("Staging")]
    public async Task NonProduction_UnsafeFirstQuery_ThrowsWarningException(string environmentName)
    {
        var messages = new List<string>();
        await using var connection = await OpenSqliteConnectionAsync();
        var options = CreateOptions(connection, environmentName == Environments.Production, messages);
        await using var db = new AppDbContext(
            options);
        await db.Database.EnsureCreatedAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.Categories.FirstOrDefaultAsync());

        Assert.Contains("FirstWithoutOrderByAndFilterWarning", error.Message, StringComparison.Ordinal);
    }

    /// <summary>驗證 Production 不因 Query 10103 拋出例外且仍能捕捉到 warning 紀錄。</summary>
    [Fact]
    public async Task Production_UnsafeFirstQuery_LogsWarningWithoutThrowing()
    {
        var messages = new List<string>();
        await using var connection = await OpenSqliteConnectionAsync();
        await using var db = new AppDbContext(CreateOptions(connection, isProduction: true, messages));
        await db.Database.EnsureCreatedAsync();

        _ = await db.Categories.FirstOrDefaultAsync();

        Assert.Contains(
            messages,
            message => message.Contains("FirstWithoutOrderByAndFilterWarning", StringComparison.Ordinal));
    }

    /// <summary>建立隔離 SQLite options 並套用指定環境的 Query 10103 政策。</summary>
    private static DbContextOptions<AppDbContext> CreateOptions(
        SqliteConnection connection,
        bool isProduction,
        ICollection<string> messages)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .LogTo(messages.Add, LogLevel.Warning);
        EfCoreQueryWarningPolicy.Configure(builder, isProduction);
        return builder.Options;
    }

    /// <summary>開啟只存在於目前測試生命週期的 SQLite 記憶體連線。</summary>
    private static async Task<SqliteConnection> OpenSqliteConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }
}
