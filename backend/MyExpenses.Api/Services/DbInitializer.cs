using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

public static class DbInitializer
{
    public static async Task SeedReferenceDataAsync(AppDbContext db)
    {
        if (await db.Categories.AnyAsync()) return;

        var categories = new List<Category>
        {
            new() { Name = "薪資收入", Type = CategoryType.Income, Icon = "Wallet", Color = "#10B981", SortOrder = 1, SystemCode = "salary" },
            new() { Name = "兼職收入", Type = CategoryType.Income, Icon = "Briefcase", Color = "#059669", SortOrder = 2, SystemCode = "part-time" },
            new() { Name = "投資收益", Type = CategoryType.Income, Icon = "TrendingUp", Color = "#2563EB", SortOrder = 3, SystemCode = "investment" },
            new() { Name = "獎金", Type = CategoryType.Income, Icon = "Gift", Color = "#D97706", SortOrder = 4, SystemCode = "bonus" },
            new() { Name = "其他收入", Type = CategoryType.Income, Icon = "PlusCircle", Color = "#6D28D9", SortOrder = 5, SystemCode = "other-income" },
            new() { Name = "飲食", Type = CategoryType.Expense, Icon = "Utensils", Color = "#DC2626", SortOrder = 10, SystemCode = "food" },
            new() { Name = "交通", Type = CategoryType.Expense, Icon = "Car", Color = "#2563EB", SortOrder = 11, SystemCode = "transport" },
            new() { Name = "生活", Type = CategoryType.Expense, Icon = "Home", Color = "#7C3AED", SortOrder = 12, SystemCode = "living" },
            new() { Name = "娛樂", Type = CategoryType.Expense, Icon = "Film", Color = "#D97706", SortOrder = 13, SystemCode = "entertainment" },
            new() { Name = "通訊", Type = CategoryType.Expense, Icon = "Smartphone", Color = "#0891B2", SortOrder = 14, SystemCode = "telecom" },
            new() { Name = "教育", Type = CategoryType.Expense, Icon = "BookOpen", Color = "#4F46E5", SortOrder = 15, SystemCode = "education" },
            new() { Name = "醫療", Type = CategoryType.Expense, Icon = "HeartPulse", Color = "#E11D48", SortOrder = 16, SystemCode = "medical" },
            new() { Name = "其他", Type = CategoryType.Expense, Icon = "MoreHorizontal", Color = "#64748B", SortOrder = 20, SystemCode = "other-expense" },
        };

        db.Categories.AddRange(categories);
        await db.SaveChangesAsync();

        var paymentMethods = new List<PaymentMethod>
        {
            new() { Name = "現金", Icon = "Banknote", Color = "#10B981", SortOrder = 1, SystemCode = "cash" },
            new() { Name = "信用卡", Icon = "CreditCard", Color = "#3B82F6", SortOrder = 2, SystemCode = "credit-card" },
            new() { Name = "Line Pay", Icon = "Smartphone", Color = "#06B6D4", SortOrder = 3, SystemCode = "line-pay" },
            new() { Name = "銀行轉帳", Icon = "Building2", Color = "#8B5CF6", SortOrder = 4, SystemCode = "bank-transfer" },
            new() { Name = "悠遊卡", Icon = "Wallet", Color = "#F59E0B", SortOrder = 5, SystemCode = "easy-card" },
            new() { Name = "其他", Icon = "MoreHorizontal", Color = "#6B7280", SortOrder = 6, SystemCode = "other" },
        };

        db.PaymentMethods.AddRange(paymentMethods);
        await db.SaveChangesAsync();

        if (!await db.AutoSnapshotConfigs.AnyAsync())
        {
            db.AutoSnapshotConfigs.Add(new AutoSnapshotConfig
            {
                IsEnabled = false,
                Frequency = "Daily",
                TimeOfDay = "08:00",
            });
            await db.SaveChangesAsync();
        }
    }

    public static async Task SeedSampleDataAsync(AppDbContext db)
    {
        var categories = await db.Categories.ToListAsync();
        var paymentMethods = await db.PaymentMethods.ToListAsync();
        var pmByName = paymentMethods.ToDictionary(p => p.Name);
        var rng = Random.Shared;
        var today = DateTime.Today;
        var transactions = new List<Transaction>();

        for (var month = -5; month <= 0; month++)
        {
            var monthStart = new DateTime(today.Year, today.Month, 1).AddMonths(month);
            var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);

            var incomeCats = categories.Where(c => c.Type == CategoryType.Income).ToList();
            var expenseCats = categories.Where(c => c.Type == CategoryType.Expense).ToList();

            transactions.Add(new Transaction
            {
                Type = TransactionType.Income,
                Amount = 52000 + rng.Next(-3000, 3000),
                Date = DateOnly.FromDateTime(monthStart.AddDays(5)),
                CategoryId = incomeCats[0].Id,
                Description = "月薪",
                PaymentMethodId = pmByName["銀行轉帳"].Id,
            });

            if (rng.NextDouble() > 0.5)
            {
                transactions.Add(new Transaction
                {
                    Type = TransactionType.Income,
                    Amount = 5000 + rng.Next(-1000, 2000),
                    Date = DateOnly.FromDateTime(monthStart.AddDays(15)),
                    CategoryId = incomeCats[1].Id,
                    Description = "接案收入",
                    PaymentMethodId = pmByName["銀行轉帳"].Id,
                });
            }

            var expenseDays = new HashSet<int>();
            for (var i = 0; i < rng.Next(8, 15); i++)
            {
                var day = rng.Next(1, daysInMonth + 1);
                if (!expenseDays.Add(day)) continue;

                var cat = expenseCats[rng.Next(expenseCats.Count)];
                transactions.Add(new Transaction
                {
                    Type = TransactionType.Expense,
                    Amount = cat.Name switch
                    {
                        "飲食" => rng.Next(50, 500),
                        "交通" => rng.Next(30, 200),
                        "生活" => rng.Next(200, 3000),
                        "娛樂" => rng.Next(100, 1500),
                        "通訊" => rng.Next(300, 1500),
                        _ => rng.Next(100, 1000),
                    },
                    Date = DateOnly.FromDateTime(monthStart.AddDays(day - 1)),
                    CategoryId = cat.Id,
                    Description = cat.Name switch
                    {
                        "飲食" => new[] { "早餐", "午餐", "晚餐", "咖啡", "聚餐" }[rng.Next(5)],
                        "交通" => new[] { "捷運", "加油", "停車費", "計程車" }[rng.Next(4)],
                        "生活" => new[] { "水電費", "超市", "日用品", "房租" }[rng.Next(4)],
                        "娛樂" => new[] { "電影", "遊戲", "串流", "健身房" }[rng.Next(4)],
                        _ => "一般消費",
                    },
                    PaymentMethodId = new[] { pmByName["現金"].Id, pmByName["信用卡"].Id, pmByName["Line Pay"].Id }[rng.Next(3)],
                });
            }
        }

        db.Transactions.AddRange(transactions);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var bankAccounts = new List<BankAccount>
        {
            new() { BankName = "中國信託", AccountNumber = "12345", Balance = 128500, AccountType = "活期存款", CreatedAt = now.AddMonths(-10), UpdatedAt = now.AddDays(-20) },
            new() { BankName = "國泰世華", AccountNumber = "67890", Balance = 45200, AccountType = "活期存款", CreatedAt = now.AddMonths(-8), UpdatedAt = now.AddDays(-11) },
            new() { BankName = "玉山銀行", AccountNumber = "54321", Balance = 320000, AccountType = "定存", CreatedAt = now.AddMonths(-4), UpdatedAt = now.AddDays(-15) },
        };

        db.BankAccounts.AddRange(bankAccounts);
        await db.SaveChangesAsync();

        var creditCards = new List<CreditCard>
        {
            new() { BankName = "中國信託", LastFourDigits = "1234", CardNetwork = "VISA", StatementDay = 15, DueDay = 23, CreditLimit = 150000, Notes = "主力消費卡", CreatedAt = now.AddMonths(-12), UpdatedAt = now.AddDays(-27) },
            new() { BankName = "國泰世華", LastFourDigits = "9012", CardNetwork = "Mastercard", StatementDay = 5, DueDay = 13, CreditLimit = 200000, Notes = "網購與訂閱", CreatedAt = now.AddMonths(-9), UpdatedAt = now.AddDays(-10) },
            new() { BankName = "玉山銀行", LastFourDigits = "3456", CardNetwork = "JCB", StatementDay = 25, DueDay = 3, CreditLimit = 100000, Notes = "旅遊備用", CreatedAt = now.AddMonths(-6), UpdatedAt = now.AddDays(-22) },
        };

        db.CreditCards.AddRange(creditCards);
        await db.SaveChangesAsync();
    }
}
