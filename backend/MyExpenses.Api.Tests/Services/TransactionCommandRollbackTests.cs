using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Models.Requests;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class TransactionCommandRollbackTests
{
    /// <summary>驗證無 key 舊路徑保留顯式付款方式識別碼，不將錯誤參考靜默改為空值並提交。</summary>
    [Fact]
    public async Task CreateTransaction_WithoutKeyDoesNotDiscardUnknownPaymentMethodId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var fixtures = await SeedAsync(db);
        var service = new TransactionCommandService(
            db, new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions())));

        await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateAsync(new CreateTransactionRequest
        {
            Type = TransactionType.Expense,
            Amount = 100m,
            Date = new DateOnly(2026, 9, 5),
            Description = "無效付款參考",
            CategoryId = fixtures.CategoryId,
            PaymentMethodId = int.MaxValue,
        }, null));

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Transactions.CountAsync());
    }

    /// <summary>驗證普通交易 receipt 寫入失敗會回滾交易與 receipt，且可使用同 key 重試。</summary>
    [Fact]
    public async Task CreateTransaction_RollsBackReceiptFailureAndAllowsRetry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new FailingAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixtures = await SeedAsync(db);
        var service = new TransactionCommandService(
            db,
            new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions())));
        var request = new CreateTransactionRequest
        {
            Type = TransactionType.Expense,
            Amount = 100m,
            Date = new DateOnly(2026, 9, 5),
            Description = "可重試交易",
            CategoryId = fixtures.CategoryId,
            PaymentMethodId = fixtures.PaymentMethodId,
        };
        var key = Guid.NewGuid().ToString();
        db.FailOnSaveNumber(2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(request, key));

        db.DisableFailure();
        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Transactions.CountAsync());
        Assert.Equal(0, await db.IdempotencyRecords.CountAsync());

        var created = await service.CreateAsync(request, key);

        Assert.False(created.Replayed);
        Assert.Equal(1, await db.Transactions.CountAsync());
        Assert.Equal(1, await db.IdempotencyRecords.CountAsync());
    }

    /// <summary>建立普通交易命令 rollback 測試所需的分類與付款方式。</summary>
    private static async Task<(int CategoryId, int PaymentMethodId)> SeedAsync(AppDbContext db)
    {
        var category = new Category
        {
            Name = "其他",
            Type = CategoryType.Expense,
            SystemCode = "other-expense",
        };
        var paymentMethod = new PaymentMethod
        {
            Name = "現金",
            SystemCode = "cash",
        };
        db.AddRange(category, paymentMethod);
        await db.SaveChangesAsync();
        return (category.Id, paymentMethod.Id);
    }

    /// <summary>提供可注入持久化失敗的 AppDbContext。</summary>
    private sealed class FailingAppDbContext : AppDbContext
    {
        private int _saveCount;
        private int? _failOnSave;

        /// <summary>建立可注入失敗的資料庫 context。</summary>
        public FailingAppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        /// <summary>指定在第幾次 SaveChanges 前拋出失敗。</summary>
        public void FailOnSaveNumber(int saveNumber)
        {
            _saveCount = 0;
            _failOnSave = saveNumber;
        }

        /// <summary>關閉失敗注入並重設計數器。</summary>
        public void DisableFailure()
        {
            _saveCount = 0;
            _failOnSave = null;
        }

        /// <summary>在指定寫入邊界注入可預期的測試例外。</summary>
        public override async Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            _saveCount++;
            if (_failOnSave == _saveCount)
                throw new InvalidOperationException("Injected persistence failure");
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }
}
