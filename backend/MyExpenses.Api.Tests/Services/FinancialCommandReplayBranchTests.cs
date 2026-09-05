using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Models.Requests;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class FinancialCommandReplayBranchTests
{
    /// <summary>驗證編輯結果並刪除原始參考後仍可重播目前資料；結果不可用時保留收據且不重建。</summary>
    [Theory]
    [InlineData("ordinary", "transaction-soft")]
    [InlineData("ordinary", "transaction-hard")]
    [InlineData("standalone", "installment")]
    [InlineData("composite", "transaction-soft")]
    [InlineData("composite", "transaction-hard")]
    [InlineData("composite", "installment")]
    public async Task Replay_AfterOriginalReferencesDeleted_PreservesEditedResultAndUnavailableReceipt(
        string operation, string deletion)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var category = new Category { Name = "原分類", Type = CategoryType.Expense };
        var payment = new PaymentMethod { Name = "原付款", SystemCode = operation == "ordinary" ? "cash" : "credit-card" };
        var card = new CreditCard { BankName = "原銀行", LastFourDigits = "1234", StatementDay = 15, DueDay = 23 };
        db.AddRange(category, payment, card);
        await db.SaveChangesAsync();
        var request = new CreateTransactionRequest
        {
            Type = TransactionType.Expense, CategoryId = category.Id, PaymentMethodId = payment.Id,
            Amount = 1200m, Date = new DateOnly(2026, 9, 5), Description = "原描述",
        };
        var key = Guid.NewGuid().ToString();
        var created = await CreateAsync(db, operation, request, card.Id, key);
        var receipt = await db.IdempotencyRecords.AsNoTracking().SingleAsync();
        var receiptSnapshot = System.Text.Json.JsonSerializer.Serialize(receipt);
        var replacementCategory = new Category { Name = "新分類", Type = CategoryType.Expense };
        var replacementPayment = new PaymentMethod { Name = "新付款" };
        var replacementCard = new CreditCard { BankName = "新銀行", LastFourDigits = "5678", StatementDay = 15, DueDay = 23 };
        db.AddRange(replacementCategory, replacementPayment, replacementCard);
        await db.SaveChangesAsync();
        if (receipt.TransactionId.HasValue)
        {
            var transaction = await db.Transactions.SingleAsync(item => item.Id == receipt.TransactionId);
            transaction.CategoryId = replacementCategory.Id;
            transaction.PaymentMethodId = replacementPayment.Id;
            transaction.Description = "修改後交易";
        }
        if (receipt.InstallmentId.HasValue)
        {
            var installment = await db.Installments.SingleAsync(item => item.Id == receipt.InstallmentId);
            installment.CardId = replacementCard.Id;
            installment.Description = "修改後信用消費";
        }
        await db.SaveChangesAsync();
        db.RemoveRange(category, payment, card);
        await db.SaveChangesAsync();
        Assert.False(await db.Categories.AnyAsync(item => item.Id == request.CategoryId));
        Assert.False(await db.PaymentMethods.AnyAsync(item => item.Id == request.PaymentMethodId));
        Assert.False(await db.CreditCards.AnyAsync(item => item.Id == card.Id));

        await using (var replayDb = new AppDbContext(options))
        {
            var replay = await CreateAsync(replayDb, operation, request, card.Id, key);
            Assert.True(replay.Replayed);
            Assert.Equal(created.Id, replay.Id);
            Assert.Equal(operation == "ordinary" ? "修改後交易" : "修改後信用消費", replay.Description);
            Assert.Equal(operation == "standalone" ? null : (int?)replacementCategory.Id, replay.CategoryId);
            Assert.Equal(operation == "standalone" ? null : (int?)replacementPayment.Id, replay.PaymentMethodId);
            Assert.Equal(operation == "ordinary" ? null : (int?)replacementCard.Id, replay.CardId);
            if (operation == "composite")
                Assert.Equal("修改後交易", replay.TransactionDescription);
            request.Amount = 1300m;
            var conflict = await Assert.ThrowsAsync<FinancialCommandException>(() => CreateAsync(replayDb, operation, request, card.Id, key));
            Assert.Equal(409, conflict.StatusCode);
            request.Amount = 1200m;
        }

        if (deletion == "installment")
            db.Installments.Remove(await db.Installments.SingleAsync());
        else
        {
            var transaction = await db.Transactions.SingleAsync();
            if (deletion == "transaction-hard")
                db.Transactions.Remove(transaction);
            else
                transaction.DeletedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        var transactionCount = await db.Transactions.IgnoreQueryFilters().CountAsync();
        var installmentCount = await db.Installments.CountAsync();
        var paymentCount = await db.InstallmentPayments.CountAsync();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var unavailableDb = new AppDbContext(options);
            var error = await Assert.ThrowsAsync<FinancialCommandException>(() => CreateAsync(unavailableDb, operation, request, card.Id, key));
            Assert.Equal(410, error.StatusCode);
            Assert.Equal("result_unavailable", error.Code);
            Assert.Equal(receiptSnapshot, System.Text.Json.JsonSerializer.Serialize(await unavailableDb.IdempotencyRecords.AsNoTracking().SingleAsync()));
            Assert.Equal(transactionCount, await unavailableDb.Transactions.IgnoreQueryFilters().CountAsync());
            Assert.Equal(installmentCount, await unavailableDb.Installments.CountAsync());
            Assert.Equal(paymentCount, await unavailableDb.InstallmentPayments.CountAsync());
            if (deletion == "transaction-soft")
                Assert.NotNull((await unavailableDb.Transactions.IgnoreQueryFilters().SingleAsync()).DeletedAt);
        }
    }

    /// <summary>逐一驗證初次查詢、交易內查詢及唯一鍵衝突後的重播旗標與結果識別碼。</summary>
    [Theory]
    [InlineData("ordinary", 0)]
    [InlineData("ordinary", 1)]
    [InlineData("ordinary", 2)]
    [InlineData("standalone", 0)]
    [InlineData("standalone", 1)]
    [InlineData("standalone", 2)]
    [InlineData("composite", 0)]
    [InlineData("composite", 1)]
    [InlineData("composite", 2)]
    public async Task Create_ReplaysEveryReceiptBranch(string operation, int hiddenReads)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var seed = new AppDbContext(options);
        await seed.Database.EnsureCreatedAsync();
        var category = new Category { Name = "支出", Type = CategoryType.Expense };
        var payment = new PaymentMethod
        {
            Name = "付款",
            SystemCode = operation == "ordinary" ? "cash" : "credit-card",
        };
        var card = new CreditCard { BankName = "銀行", LastFourDigits = "1234", StatementDay = 15, DueDay = 23 };
        seed.AddRange(category, payment, card);
        await seed.SaveChangesAsync();
        var request = new CreateTransactionRequest
        {
            Type = TransactionType.Expense,
            CategoryId = category.Id,
            PaymentMethodId = payment.Id,
            Amount = 1200m,
            Date = new DateOnly(2026, 9, 5),
            Description = "重播分支",
        };
        var key = Guid.NewGuid().ToString();
        var first = await CreateAsync(seed, operation, request, card.Id, key);
        Assert.False(first.Replayed);
        var interceptor = new HiddenReceiptInterceptor(hiddenReads);
        await using var replayDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).AddInterceptors(interceptor).Options);

        var replay = await CreateAsync(replayDb, operation, request, card.Id, key);

        Assert.Equal(hiddenReads + 1, interceptor.ReceiptReads);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(operation == "standalone" ? 0 : 1, await seed.Transactions.CountAsync());
        Assert.Equal(operation == "ordinary" ? 0 : 1, await seed.Installments.CountAsync());
        Assert.Equal(operation == "ordinary" ? 0 : 3, await seed.InstallmentPayments.CountAsync());
        Assert.Equal(1, await seed.IdempotencyRecords.CountAsync());
    }

    /// <summary>以相同固定內容呼叫指定命令，統一回傳識別碼與重播狀態。</summary>
    private static async Task<(int Id, bool Replayed, string? Description, int? CategoryId, int? PaymentMethodId, int? CardId, string? TransactionDescription)> CreateAsync(
        AppDbContext db, string operation, CreateTransactionRequest request, int cardId, string key)
    {
        var timeZone = new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions()));
        if (operation == "ordinary")
        {
            var result = await new TransactionCommandService(db, timeZone).CreateAsync(request, key);
            return (result.Transaction.Id, result.Replayed, result.Transaction.Description,
                result.Transaction.CategoryId, result.Transaction.PaymentMethodId, null, result.Transaction.Description);
        }
        var service = new InstallmentCommandService(db, timeZone);
        if (operation == "standalone")
        {
            var result = await service.CreateStandaloneInstallmentAsync(new CreateStandaloneInstallmentRequest
            {
                CardId = cardId,
                TotalAmount = request.Amount,
                Periods = 3,
                PurchaseDate = request.Date!.Value,
                Description = request.Description,
            }, key);
            return (result.Id, result.Replayed, result.Description, null, null, result.CardId, null);
        }
        var purchase = await service.CreateInstallmentPurchaseAsync(new InstallmentPurchaseRequest
        {
            Transaction = request,
            Installment = new InstallmentPurchaseDetails { CardId = cardId, Periods = 3 },
        }, key);
        return (purchase.Installment.Id, purchase.Replayed, purchase.Installment.Description,
            purchase.Transaction.CategoryId, purchase.Transaction.PaymentMethodId,
            purchase.Installment.CardId, purchase.Transaction.Description);
    }

    /// <summary>模擬先前查詢尚未看到收據；保留真實唯一鍵衝突與資料庫回滾行為。</summary>
    private sealed class HiddenReceiptInterceptor(int hiddenReads) : DbCommandInterceptor
    {
        public int ReceiptReads { get; private set; }

        /// <summary>僅隱藏指定次數的收據讀取，後續核對仍讀取真實已提交資料。</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("FROM \"IdempotencyRecords\"", StringComparison.Ordinal))
            {
                ReceiptReads++;
                if (ReceiptReads <= hiddenReads)
                    command.CommandText = command.CommandText.Replace("WHERE ", "WHERE 0 = 1 AND ", StringComparison.Ordinal);
            }
            return ValueTask.FromResult(result);
        }
    }
}
