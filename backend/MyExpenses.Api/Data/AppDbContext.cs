using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Data;

public class AppDbContext : DbContext
{
    private static readonly IReadOnlyDictionary<Type, IReadOnlySet<string>> PersistedStringProperties =
        new Dictionary<Type, IReadOnlySet<string>>
        {
            [typeof(Category)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(Category.Name),
                nameof(Category.Icon),
                nameof(Category.Color),
            },
            [typeof(Transaction)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(Transaction.Description),
                nameof(Transaction.Notes),
            },
            [typeof(Installment)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(Installment.Description),
            },
            [typeof(CreditCard)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CreditCard.BankName),
                nameof(CreditCard.LastFourDigits),
                nameof(CreditCard.CardNetwork),
                nameof(CreditCard.Notes),
            },
            [typeof(BankAccount)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(BankAccount.BankName),
                nameof(BankAccount.AccountNumber),
                nameof(BankAccount.AccountType),
            },
            [typeof(Stock)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(Stock.Name),
                nameof(Stock.Symbol),
                nameof(Stock.Broker),
            },
            [typeof(StockTransaction)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(StockTransaction.Notes),
            },
            [typeof(Withdrawal)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(Withdrawal.Description),
            },
            [typeof(PaymentMethod)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(PaymentMethod.Name),
                nameof(PaymentMethod.Icon),
                nameof(PaymentMethod.Color),
            },
            [typeof(User)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(User.DisplayName),
            },
            [typeof(ApiToken)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(ApiToken.Name),
            },
            [typeof(SystemSetting)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(SystemSetting.TimeZoneId),
            },
            [typeof(ScheduledJobExecution)] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(ScheduledJobExecution.ScheduleTimeZoneId),
                nameof(ScheduledJobExecution.ResultCode),
                nameof(ScheduledJobExecution.SafeMessage),
            },
        };

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Installment> Installments => Set<Installment>();
    public DbSet<InstallmentPayment> InstallmentPayments => Set<InstallmentPayment>();
    public DbSet<CreditCard> CreditCards => Set<CreditCard>();
    public DbSet<CreditCardBill> CreditCardBills => Set<CreditCardBill>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<Stock> Stocks => Set<Stock>();
    public DbSet<StockTransaction> StockTransactions => Set<StockTransaction>();
    public DbSet<HistoricalAdjustedPrice> HistoricalAdjustedPrices => Set<HistoricalAdjustedPrice>();
    public DbSet<HistoricalPriceSyncState> HistoricalPriceSyncStates => Set<HistoricalPriceSyncState>();
    public DbSet<Withdrawal> Withdrawals => Set<Withdrawal>();
    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();
    public DbSet<SnapshotBatch> SnapshotBatches => Set<SnapshotBatch>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AutoSnapshotConfig> AutoSnapshotConfigs => Set<AutoSnapshotConfig>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<ScheduledJobExecution> ScheduledJobExecutions => Set<ScheduledJobExecution>();

    /// <summary>Normalizes allowlisted tracked strings before synchronously saving changes.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        NormalizeTrackedStrings();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>Normalizes allowlisted tracked strings before asynchronously saving changes.</summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        NormalizeTrackedStrings();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>建立 financial entities、authentication entities 與 single-owner database constraints。</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(e =>
        {
            e.ToTable("Categories");
            e.Property(c => c.Name).HasMaxLength(100).IsRequired();
            e.Property(c => c.Icon).HasMaxLength(50);
            e.Property(c => c.Color).HasMaxLength(20);
            e.HasIndex(c => c.SortOrder);
            e.HasIndex(c => c.SystemCode);
        });

        modelBuilder.Entity<Transaction>(e =>
        {
            e.ToTable("Transactions");
            e.Property(t => t.Amount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(t => t.Description).HasMaxLength(500);
            e.Property(t => t.Notes).HasMaxLength(1000);
            e.HasOne(t => t.PaymentMethod)
                .WithMany()
                .HasForeignKey(t => t.PaymentMethodId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.Category)
                .WithMany(c => c.Transactions)
                .HasForeignKey(t => t.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => t.Date);
            e.HasIndex(t => t.CategoryId);
            e.HasIndex(t => t.PaymentMethodId);
            e.HasQueryFilter(t => t.DeletedAt == null);
        });

        modelBuilder.Entity<Installment>(e =>
        {
            e.ToTable("Installments");
            e.Property(i => i.TotalAmount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(i => i.PerPeriod).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(i => i.PurchaseDate).HasColumnType("TEXT").IsRequired();
            e.Property(i => i.Description).HasMaxLength(500);
            e.HasIndex(i => i.PurchaseDate);
            e.HasOne(i => i.Transaction)
                .WithMany()
                .HasForeignKey(i => i.TransactionId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(i => i.Card)
                .WithMany()
                .HasForeignKey(i => i.CardId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<InstallmentPayment>(e =>
        {
            e.ToTable("InstallmentPayments");
            e.Property(p => p.Amount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(p => p.DueDate).HasColumnType("TEXT");
            e.HasOne(p => p.Installment)
                .WithMany(i => i.Payments)
                .HasForeignKey(p => p.InstallmentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.InstallmentId, p.Period }).IsUnique();
            e.HasIndex(p => new { p.InstallmentId, p.IsPaid });
        });

        modelBuilder.Entity<CreditCard>(e =>
        {
            e.ToTable("CreditCards");
            e.Property(c => c.BankName).HasMaxLength(100).IsRequired();
            e.Property(c => c.LastFourDigits).HasMaxLength(4).IsRequired();
            e.Property(c => c.CardNetwork).HasMaxLength(50);
            e.Property(c => c.CreditLimit).HasColumnType("decimal(18,2)");
            e.Property(c => c.Notes).HasMaxLength(200);
        });

        modelBuilder.Entity<CreditCardBill>(e =>
        {
            e.ToTable("CreditCardBills");
            e.Property(b => b.Period).HasMaxLength(20).IsRequired();
            e.Property(b => b.TotalAmount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(b => b.PaidAmount).HasColumnType("decimal(18,2)");
            e.HasOne(b => b.Card)
                .WithMany(c => c.Bills)
                .HasForeignKey(b => b.CardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BankAccount>(e =>
        {
            e.ToTable("BankAccounts");
            e.Property(b => b.BankName).HasMaxLength(100).IsRequired();
            e.Property(b => b.AccountNumber).HasMaxLength(50).IsRequired();
            e.Property(b => b.Balance).HasColumnType("decimal(18,2)");
            e.Property(b => b.AccountType).HasMaxLength(50);
        });

        modelBuilder.Entity<Stock>(e =>
        {
            e.ToTable("Stocks");
            e.Property(s => s.Name).HasMaxLength(100).IsRequired();
            e.Property(s => s.Symbol).HasMaxLength(20).IsRequired();
            e.Property(s => s.Market)
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasDefaultValue(StockMarket.Unknown)
                .IsRequired();
            e.Property(s => s.InstrumentType).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(s => s.Shares).HasColumnType("decimal(18,4)");
            e.Property(s => s.BuyPrice).HasColumnType("decimal(18,2)");
            e.Property(s => s.CurrentPrice).HasColumnType("decimal(18,2)");
            e.Property(s => s.Broker).HasMaxLength(100);
        });

        modelBuilder.Entity<StockTransaction>(e =>
        {
            e.ToTable("StockTransactions", table =>
            {
                table.HasCheckConstraint(
                    "CK_StockTransactions_FeeTax_NonNegative",
                    "Fee >= 0 AND Tax >= 0");
                table.HasCheckConstraint(
                    "CK_StockTransactions_TypeFields",
                     "(Type = 'OpeningBalance' AND Shares > 0 AND Price > 0 AND OpeningMarketValue > 0 AND CashAmount IS NULL) "
                     + "OR (Type IN ('Buy', 'Sell') AND Shares > 0 AND Price > 0 AND OpeningMarketValue IS NULL AND CashAmount IS NULL) "
                     + "OR (Type = 'Dividend' AND CashAmount > 0 AND Shares IS NULL AND Price IS NULL AND OpeningMarketValue IS NULL) "
                     + "OR (Type = 'StockDividend' AND Shares IS NOT NULL AND Shares > 0 AND Price IS NULL AND CashAmount IS NULL AND OpeningMarketValue IS NULL AND Fee = 0 AND Tax = 0)");
            });
            e.Property(t => t.Type)
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
            e.Property(t => t.TradeDate).HasColumnType("TEXT").IsRequired();
            e.Property(t => t.Shares).HasColumnType("decimal(18,4)").HasPrecision(18, 4);
            e.Property(t => t.Price).HasColumnType("decimal(18,2)").HasPrecision(18, 2);
            e.Property(t => t.Fee).HasColumnType("decimal(18,2)").HasPrecision(18, 2).IsRequired();
            e.Property(t => t.Tax).HasColumnType("decimal(18,2)").HasPrecision(18, 2).IsRequired();
            e.Property(t => t.CashAmount).HasColumnType("decimal(18,2)").HasPrecision(18, 2);
            e.Property(t => t.OpeningMarketValue).HasColumnType("decimal(18,2)").HasPrecision(18, 2);
            e.Property(t => t.Notes).HasMaxLength(1000);
            e.Property(t => t.CreatedAtUtc).HasColumnType("TEXT").IsRequired();
            e.Property(t => t.UpdatedAtUtc).HasColumnType("TEXT").IsRequired();
            e.HasOne(t => t.Stock)
                .WithMany(s => s.Transactions)
                .HasForeignKey(t => t.StockId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => new { t.StockId, t.TradeDate, t.Sequence }).IsUnique();
            e.HasIndex(t => new { t.StockId, t.Type, t.TradeDate });
        });

        modelBuilder.Entity<HistoricalAdjustedPrice>(e =>
        {
            e.ToTable("HistoricalAdjustedPrices", table =>
            {
                table.HasCheckConstraint(
                    "CK_HistoricalAdjustedPrices_AdjustedClose_Positive",
                    "AdjustedClose > 0");
                table.HasCheckConstraint(
                    "CK_HistoricalAdjustedPrices_Close_Positive",
                    "Close IS NULL OR Close > 0");
            });
            e.Property(price => price.Market).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(price => price.Symbol).HasMaxLength(20).IsRequired();
            e.Property(price => price.TradingDate).HasColumnType("TEXT").IsRequired();
            e.Property(price => price.AdjustedClose).HasColumnType("decimal(18,6)").IsRequired();
            e.Property(price => price.Close).HasColumnType("decimal(18,6)").HasPrecision(18, 6);
            e.Property(price => price.Provider).HasMaxLength(50).IsRequired();
            e.Property(price => price.FetchedAtUtc).HasColumnType("TEXT").IsRequired();
            e.HasIndex(price => new { price.Market, price.Symbol, price.TradingDate })
                .IsUnique();
        });

        modelBuilder.Entity<HistoricalPriceSyncState>(e =>
        {
            e.ToTable("HistoricalPriceSyncStates");
            e.Property(state => state.Market).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(state => state.Symbol).HasMaxLength(20).IsRequired();
            e.Property(state => state.LatestTradingDate).HasColumnType("TEXT");
            e.Property(state => state.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(state => state.SafeMessage).HasMaxLength(500);
            e.HasIndex(state => new { state.Market, state.Symbol })
                .IsUnique();
        });

        modelBuilder.Entity<Withdrawal>(e =>
        {
            e.ToTable("Withdrawals");
            e.Property(w => w.Amount).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(w => w.Description).HasMaxLength(500);
            e.HasOne(w => w.BankAccount)
                .WithMany()
                .HasForeignKey(w => w.BankAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(w => w.Date);
        });

        modelBuilder.Entity<PaymentMethod>(e =>
        {
            e.ToTable("PaymentMethods");
            e.Property(p => p.Name).HasMaxLength(100).IsRequired();
            e.Property(p => p.Icon).HasMaxLength(50);
            e.Property(p => p.Color).HasMaxLength(20);
            e.HasIndex(p => p.SortOrder);
            e.HasIndex(p => p.SystemCode);
        });

        modelBuilder.Entity<SnapshotBatch>(e =>
        {
            e.ToTable("SnapshotBatches");
            e.Property(s => s.Name).HasMaxLength(200).IsRequired();
            e.Property(s => s.Notes).HasMaxLength(1000);
            e.Property(s => s.TotalAssets).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(s => s.TotalLiabilities).HasColumnType("decimal(18,2)");
            e.Property(s => s.TotalNetWorth).HasColumnType("decimal(18,2)");
            e.Property(s => s.NetWorthBasis).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(s => s.TotalBankBalance).HasColumnType("decimal(18,2)");
            e.Property(s => s.TotalStockValue).HasColumnType("decimal(18,2)");
            e.Property(s => s.TotalStockCost).HasColumnType("decimal(18,2)");
            e.OwnsMany(s => s.BankDetails, b =>
            {
                b.ToJson();
                b.Property(d => d.BankName).HasMaxLength(100);
                b.Property(d => d.AccountNumber).HasMaxLength(50);
                b.Property(d => d.AccountType).HasMaxLength(50);
                b.Property(d => d.Balance).HasColumnType("decimal(18,2)");
            });
            e.OwnsMany(s => s.StockDetails, s =>
            {
                s.ToJson();
                s.Property(d => d.Name).HasMaxLength(100);
                s.Property(d => d.Symbol).HasMaxLength(20);
                s.Property(d => d.InstrumentType).HasConversion<string>().HasMaxLength(20);
                s.Property(d => d.Shares).HasColumnType("decimal(18,4)");
                s.Property(d => d.BuyPrice).HasColumnType("decimal(18,2)");
                s.Property(d => d.CurrentPrice).HasColumnType("decimal(18,2)");
                s.Property(d => d.MarketValue).HasColumnType("decimal(18,2)");
                s.Property(d => d.GainLoss).HasColumnType("decimal(18,2)");
            });
            e.HasIndex(s => s.SnapshotDate);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("Users", table => table.HasCheckConstraint(
                "CK_Users_InstallationOwnerMarker",
                $"InstallationOwnerMarker = '{User.SingletonOwnerMarkerValue}'"));
            e.Property(u => u.InstallationOwnerMarker)
                .HasMaxLength(50)
                .HasDefaultValue(User.SingletonOwnerMarkerValue)
                .IsRequired();
            e.HasIndex(u => u.InstallationOwnerMarker).IsUnique();
            e.Property(u => u.Email).HasMaxLength(200).IsRequired();
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.PasswordHash).HasMaxLength(200).IsRequired();
            e.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
            e.Property(u => u.TotpSecret).HasMaxLength(500);
            e.Property(u => u.RecoveryCodes).HasMaxLength(2000);
        });

        modelBuilder.Entity<AutoSnapshotConfig>(e =>
        {
            e.ToTable("AutoSnapshotConfigs");
            e.Property(c => c.Frequency).HasMaxLength(20).IsRequired();
            e.Property(c => c.TimeOfDay).HasMaxLength(5).IsRequired();
        });

        modelBuilder.Entity<ApiToken>(entity =>
        {
            entity.ToTable("ApiTokens");
            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.TokenHash).HasMaxLength(200);
            entity.HasIndex(e => e.TokenHash);
            entity.Property(e => e.Prefix).HasMaxLength(20);
            entity.Property(e => e.Scopes).HasColumnType("TEXT").HasMaxLength(2000);
        });

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("IdempotencyRecords");
            entity.Property(record => record.Key).HasMaxLength(36).IsRequired();
            entity.Property(record => record.Operation).HasMaxLength(100).IsRequired();
            entity.Property(record => record.RequestHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(record => record.Key).IsUnique();
            entity.HasIndex(record => new { record.Operation, record.RequestHash });
        });

        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.ToTable("SystemSettings");
            entity.Property(e => e.TimeZoneId).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<ScheduledJobExecution>(entity =>
        {
            entity.ToTable("ScheduledJobExecutions");
            entity.Property(e => e.JobKey)
                .HasConversion<string>()
                .HasMaxLength(40)
                .IsRequired();
            entity.Property(e => e.ScheduledForUtc).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.ScheduleTimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ScheduledLocalDate).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(30)
                .IsRequired();
            entity.Property(e => e.StartedAtUtc).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.CompletedAtUtc).HasColumnType("TEXT");
            entity.Property(e => e.ResultCode).HasMaxLength(80);
            entity.Property(e => e.SafeMessage).HasMaxLength(500);
            entity.HasIndex(e => new { e.JobKey, e.ScheduledForUtc }).IsUnique();
            entity.HasIndex(e => e.StartedAtUtc);
            entity.HasIndex(e => new { e.JobKey, e.StartedAtUtc });
            entity.HasIndex(e => new { e.Status, e.StartedAtUtc });
        });

        ApplyUtcDateTimeConversions(modelBuilder);
    }

    /// <summary>Applies UTC persistence and UTC read semantics to every DateTime entity property.</summary>
    private static void ApplyUtcDateTimeConversions(ModelBuilder modelBuilder)
    {
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            value => value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc),
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        var nullableUtcConverter = new ValueConverter<DateTime?, DateTime?>(
            value => value.HasValue
                ? value.Value.Kind == DateTimeKind.Local
                    ? value.Value.ToUniversalTime()
                    : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
                : null,
            value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                    property.SetValueConverter(utcConverter);
                else if (property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(nullableUtcConverter);
            }
        }
    }

    /// <summary>Trims only allowlisted added or modified string properties before persistence.</summary>
    private void NormalizeTrackedStrings()
    {
        ChangeTracker.DetectChanges();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified) ||
                !PersistedStringProperties.TryGetValue(entry.Metadata.ClrType, out var propertyNames))
            {
                continue;
            }

            foreach (var propertyName in propertyNames)
            {
                var property = entry.Property(propertyName);
                if (entry.State == EntityState.Modified && !property.IsModified)
                {
                    continue;
                }

                if (property.CurrentValue is not string value)
                {
                    continue;
                }

                var trimmedValue = value.Trim();
                property.CurrentValue = trimmedValue.Length == 0 && property.Metadata.IsNullable
                    ? null
                    : trimmedValue;
            }
        }
    }
}
