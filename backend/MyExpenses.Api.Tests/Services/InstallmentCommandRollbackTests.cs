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

public class InstallmentCommandRollbackTests
{
    /// <summary>Verifies a failure while writing the payment schedule rolls back the complete purchase.</summary>
    [Fact]
    public async Task CreateInstallmentPurchase_RollsBackAfterPaymentWriteFailure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new FailingAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixtures = await SeedAsync(db);
        var service = new InstallmentCommandService(
            db,
            new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions())));
        db.FailOnSaveNumber(3);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateInstallmentPurchaseAsync(
            new InstallmentPurchaseRequest
            {
                Transaction = new CreateTransactionRequest
                {
                    Type = TransactionType.Expense,
                    Amount = 1200m,
                    Date = new DateOnly(2026, 6, 20),
                    Description = "rollback",
                    CategoryId = fixtures.CategoryId,
                    PaymentMethodId = fixtures.PaymentMethodId,
                },
                Installment = new InstallmentPurchaseDetails
                {
                    CardId = fixtures.CardId,
                    Periods = 3,
                },
            },
            Guid.NewGuid().ToString()));

        db.DisableFailure();
        Assert.Equal(0, await db.Transactions.CountAsync());
        Assert.Equal(0, await db.Installments.CountAsync());
        Assert.Equal(0, await db.InstallmentPayments.CountAsync());
        Assert.Equal(0, await db.IdempotencyRecords.CountAsync());
    }

    /// <summary>Verifies a failed standalone attempt can retry the same idempotency key in a new context state.</summary>
    [Fact]
    public async Task CreateStandaloneInstallment_FailedAttemptCanRetrySameKey()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new FailingAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixtures = await SeedAsync(db);
        var service = new InstallmentCommandService(
            db,
            new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions())));
        var request = new CreateStandaloneInstallmentRequest
        {
            CardId = fixtures.CardId,
            TotalAmount = 1200m,
            Periods = 3,
            PurchaseDate = new DateOnly(2026, 6, 20),
            Description = "retry",
        };
        var key = Guid.NewGuid().ToString();

        db.FailOnSaveNumber(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateStandaloneInstallmentAsync(request, key));
        Assert.Equal(0, await db.Installments.CountAsync());
        Assert.Equal(0, await db.InstallmentPayments.CountAsync());
        Assert.Equal(0, await db.IdempotencyRecords.CountAsync());

        db.DisableFailure();
        db.ChangeTracker.Clear();
        var first = await service.CreateStandaloneInstallmentAsync(request, key);
        var repeated = await service.CreateStandaloneInstallmentAsync(request, key);
        var conflict = await Assert.ThrowsAsync<FinancialCommandException>(() =>
            service.CreateStandaloneInstallmentAsync(
                new CreateStandaloneInstallmentRequest
                {
                    CardId = request.CardId,
                    TotalAmount = 1300m,
                    Periods = request.Periods,
                    PurchaseDate = request.PurchaseDate,
                    Description = request.Description,
                },
                key));

        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(1, await db.Installments.CountAsync());
        Assert.Equal(3, await db.InstallmentPayments.CountAsync());
        Assert.Equal(1, await db.IdempotencyRecords.CountAsync());
    }

    /// <summary>Verifies purchase writes roll back when any persisted boundary fails after the database write.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CreateInstallmentPurchase_RollsBackAtEveryWriteBoundary(int failAfterSaveNumber)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new FailingAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixtures = await SeedAsync(db);
        var service = new InstallmentCommandService(
            db,
            new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions())));
        db.FailAfterSaveNumber(failAfterSaveNumber);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateInstallmentPurchaseAsync(
            new InstallmentPurchaseRequest
            {
                Transaction = new CreateTransactionRequest
                {
                    Type = TransactionType.Expense,
                    Amount = 1200m,
                    Date = new DateOnly(2026, 6, 20),
                    Description = "boundary rollback",
                    CategoryId = fixtures.CategoryId,
                    PaymentMethodId = fixtures.PaymentMethodId,
                },
                Installment = new InstallmentPurchaseDetails
                {
                    CardId = fixtures.CardId,
                    Periods = 3,
                },
            },
            Guid.NewGuid().ToString()));

        db.DisableFailure();
        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Transactions.CountAsync());
        Assert.Equal(0, await db.Installments.CountAsync());
        Assert.Equal(0, await db.InstallmentPayments.CountAsync());
        Assert.Equal(0, await db.IdempotencyRecords.CountAsync());
    }

    /// <summary>Verifies an unpaid installment schedule can be replaced atomically.</summary>
    [Fact]
    public async Task UpdateInstallmentSchedule_ReplacesCompleteUnpaidSchedule()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new FailingAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixtures = await SeedAsync(db);
        var service = new InstallmentCommandService(
            db,
            new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions())));
        var created = await service.CreateStandaloneInstallmentAsync(new CreateStandaloneInstallmentRequest
        {
            CardId = fixtures.CardId,
            TotalAmount = 1200m,
            Periods = 3,
            PurchaseDate = new DateOnly(2026, 6, 20),
            Description = "before update",
        }, Guid.NewGuid().ToString());

        db.ChangeTracker.Clear();
        var updated = await service.UpdateInstallmentScheduleAsync(created.Id, new UpdateInstallmentScheduleRequest
        {
            CardId = fixtures.CardId,
            TotalAmount = 1300m,
            Periods = 4,
            PurchaseDate = new DateOnly(2026, 7, 20),
            Description = "after update",
        });

        Assert.Equal(1300m, updated.TotalAmount);
        Assert.Equal(4, updated.Periods);
        Assert.Equal(new DateOnly(2026, 7, 20), updated.PurchaseDate);
        Assert.Equal("after update", updated.Description);
        Assert.Equal(4, updated.Payments.Count);
        Assert.Equal(1300m, updated.Payments.Sum(payment => payment.Amount));
    }

    /// <summary>Verifies schedule regeneration failure preserves the original installment and payments.</summary>
    [Fact]
    public async Task UpdateInstallmentSchedule_RollsBackReplacementFailure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new FailingAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixtures = await SeedAsync(db);
        var service = new InstallmentCommandService(
            db,
            new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions())));
        var created = await service.CreateStandaloneInstallmentAsync(new CreateStandaloneInstallmentRequest
        {
            CardId = fixtures.CardId,
            TotalAmount = 1200m,
            Periods = 3,
            PurchaseDate = new DateOnly(2026, 6, 20),
            Description = "original",
        }, Guid.NewGuid().ToString());

        db.ChangeTracker.Clear();
        db.FailAfterSaveNumber(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateInstallmentScheduleAsync(
            created.Id,
            new UpdateInstallmentScheduleRequest
            {
                CardId = fixtures.CardId,
                TotalAmount = 1300m,
                Periods = 4,
                PurchaseDate = new DateOnly(2026, 7, 20),
                Description = "must rollback",
            }));

        db.DisableFailure();
        db.ChangeTracker.Clear();
        var original = await db.Installments
            .Include(installment => installment.Payments)
            .SingleAsync(installment => installment.Id == created.Id);
        Assert.Equal(1200m, original.TotalAmount);
        Assert.Equal(3, original.Periods);
        Assert.Equal(new DateOnly(2026, 6, 20), original.PurchaseDate);
        Assert.Equal("original", original.Description);
        Assert.Equal(3, original.Payments.Count);
        Assert.Equal(1200m, original.Payments.Sum(payment => payment.Amount));
    }

    /// <summary>Seeds the minimum compatible resources required by a composite purchase.</summary>
    private static async Task<(int CategoryId, int PaymentMethodId, int CardId)> SeedAsync(AppDbContext db)
    {
        var category = new Category { Name = "支出", Type = CategoryType.Expense };
        var paymentMethod = new PaymentMethod { Name = "信用卡", SystemCode = "credit-card" };
        var card = new CreditCard
        {
            BankName = "測試銀行",
            LastFourDigits = "1234",
            StatementDay = 15,
            DueDay = 23,
        };
        db.AddRange(category, paymentMethod, card);
        await db.SaveChangesAsync();
        return (category.Id, paymentMethod.Id, card.Id);
    }

    /// <summary>Injects a deterministic SaveChanges failure into rollback tests.</summary>
    private sealed class FailingAppDbContext : AppDbContext
    {
        private int _saveCount;
        private int? _failOnSave;
        private int? _failAfterSave;

        /// <summary>Creates a failure-injectable context with the normal application model.</summary>
        public FailingAppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        /// <summary>Schedules an exception on the selected save operation.</summary>
        public void FailOnSaveNumber(int saveNumber)
        {
            _saveCount = 0;
            _failOnSave = saveNumber;
            _failAfterSave = null;
        }

        /// <summary>Schedules an exception after the selected save operation has reached the database.</summary>
        public void FailAfterSaveNumber(int saveNumber)
        {
            _saveCount = 0;
            _failOnSave = null;
            _failAfterSave = saveNumber;
        }

        /// <summary>Disables failure injection and resets the save counter.</summary>
        public void DisableFailure()
        {
            _saveCount = 0;
            _failOnSave = null;
            _failAfterSave = null;
        }

        /// <summary>Injects the configured failure before delegating to EF Core persistence.</summary>
        public override async Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            _saveCount++;
            if (_failOnSave == _saveCount)
                throw new InvalidOperationException("Injected persistence failure");
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (_failAfterSave == _saveCount)
                throw new InvalidOperationException("Injected post-save persistence failure");
            return result;
        }
    }
}
