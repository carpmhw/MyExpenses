using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Installment> Installments => Set<Installment>();
    public DbSet<InstallmentPayment> InstallmentPayments => Set<InstallmentPayment>();
    public DbSet<CreditCard> CreditCards => Set<CreditCard>();
    public DbSet<CreditCardBill> CreditCardBills => Set<CreditCardBill>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<Stock> Stocks => Set<Stock>();
    public DbSet<Withdrawal> Withdrawals => Set<Withdrawal>();
    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();
    public DbSet<SnapshotBatch> SnapshotBatches => Set<SnapshotBatch>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AutoSnapshotConfig> AutoSnapshotConfigs => Set<AutoSnapshotConfig>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

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
            e.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
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
        });

        modelBuilder.Entity<CreditCard>(e =>
        {
            e.ToTable("CreditCards");
            e.Property(c => c.BankName).HasMaxLength(100).IsRequired();
            e.Property(c => c.LastFourDigits).HasMaxLength(4).IsRequired();
            e.Property(c => c.CreditLimit).HasColumnType("decimal(18,2)");
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
            e.Property(s => s.Shares).HasColumnType("decimal(18,4)");
            e.Property(s => s.BuyPrice).HasColumnType("decimal(18,2)");
            e.Property(s => s.CurrentPrice).HasColumnType("decimal(18,2)");
            e.Property(s => s.Broker).HasMaxLength(100);
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
            e.Property(s => s.TotalNetWorth).HasColumnType("decimal(18,2)");
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
            e.ToTable("Users");
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
    }
}
