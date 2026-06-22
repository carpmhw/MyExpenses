using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Endpoints;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/reports");

        group.MapGet("/income-expense-trend", async (DateOnly? dateStart, DateOnly? dateEnd, AppDbContext db) =>
        {
            var start = dateStart ?? new DateOnly(DateTime.UtcNow.Year, 1, 1);
            var end = dateEnd ?? new DateOnly(DateTime.UtcNow.Year, 12, 31);

            var data = await db.Transactions
                .Where(t => t.Date >= start && t.Date <= end)
                .GroupBy(t => new { t.Date.Year, t.Date.Month })
                .Select(g => new
                {
                    Year = g.Key.Year,
                    Month = g.Key.Month,
                    Income = g.Where(t => t.Type == TransactionType.Income).Sum(t => t.Amount),
                    Expense = g.Where(t => t.Type == TransactionType.Expense).Sum(t => t.Amount)
                })
                .OrderBy(x => x.Year).ThenBy(x => x.Month)
                .ToListAsync();

            var result = data.Select(x => new
            {
                Month = $"{x.Year:D4}/{x.Month:D2}",
                Income = x.Income,
                Expense = x.Expense
            });

            return Results.Ok(result);
        });

        group.MapGet("/category-distribution", async (DateOnly? dateStart, DateOnly? dateEnd, AppDbContext db) =>
        {
            var start = dateStart ?? new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
            var end = dateEnd ?? start.AddMonths(1).AddDays(-1);

            var totalExpense = await db.Transactions
                .Where(t => t.Type == TransactionType.Expense && t.Date >= start && t.Date <= end)
                .SumAsync(t => t.Amount);

            if (totalExpense == 0)
                return Results.Ok(Array.Empty<object>());

            var data = await db.Transactions
                .Where(t => t.Type == TransactionType.Expense && t.Date >= start && t.Date <= end)
                .GroupBy(t => new { t.CategoryId, t.Category.Name, t.Category.Color, t.Category.Icon })
                .Select(g => new
                {
                    CategoryId = g.Key.CategoryId,
                    CategoryName = g.Key.Name,
                    Color = g.Key.Color,
                    Icon = g.Key.Icon,
                    Total = g.Sum(t => t.Amount),
                    Percentage = g.Sum(t => t.Amount) / totalExpense * 100
                })
                .OrderByDescending(x => x.Total)
                .ToListAsync();

            return Results.Ok(data);
        });

        group.MapGet("/net-worth", async (AppDbContext db) =>
        {
            var bankAccounts = await db.BankAccounts.ToListAsync();
            var stocks = await db.Stocks.ToListAsync();

            var totalBankBalance = bankAccounts.Sum(b => b.Balance);
            var totalStockValue = stocks.Sum(s => s.Shares * s.CurrentPrice);
            var totalAssets = totalBankBalance + totalStockValue;

            var unpaidInstallments = await db.InstallmentPayments
                .Where(p => !p.IsPaid)
                .SumAsync(p => p.Amount);

            return Results.Ok(new
            {
                TotalAssets = totalAssets,
                TotalLiabilities = unpaidInstallments,
                NetWorth = totalAssets - unpaidInstallments,
                BankAccounts = bankAccounts.Select(b => new
                {
                    b.BankName,
                    b.AccountNumber,
                    b.Balance
                }),
                Stocks = stocks.Select(s => new
                {
                    s.Name,
                    s.Symbol,
                    s.Shares,
                    s.CurrentPrice,
                    MarketValue = s.Shares * s.CurrentPrice
                })
            });
        });

        group.MapGet("/installment-forecast", async (int? months, AppDbContext db) =>
        {
            var forecastMonths = months ?? 6;
            var today = DateTime.UtcNow.Date;

            var unpaidPayments = await db.InstallmentPayments
                .Include(p => p.Installment).ThenInclude(i => i!.Card)
                .Where(p => !p.IsPaid && p.DueDate != null)
                .ToListAsync();

            var forecast = new List<object>();
            for (var i = 0; i < forecastMonths; i++)
            {
                var monthStart = new DateTime(today.Year, today.Month, 1).AddMonths(i);
                var monthEnd = monthStart.AddMonths(1).AddDays(-1);
                var monthStartDate = DateOnly.FromDateTime(monthStart);
                var monthEndDate = DateOnly.FromDateTime(monthEnd);

                var monthPayments = unpaidPayments
                    .Where(p => p.DueDate!.Value >= monthStartDate && p.DueDate.Value <= monthEndDate)
                    .ToList();

                forecast.Add(new
                {
                    Month = $"{monthStart.Year:D4}/{monthStart.Month:D2}",
                    TotalAmount = monthPayments.Sum(p => p.Amount),
                    Payments = monthPayments.Select(p => new
                    {
                        CardBankName = p.Installment.Card?.BankName ?? "",
                        Description = p.Installment.Description,
                        Period = p.Period,
                        Amount = p.Amount,
                        DueDate = p.DueDate!.Value.ToString("yyyy-MM-dd")
                    })
                });
            }

            return Results.Ok(forecast);
        });

        group.MapGet("/monthly-summary", async (int? year, int? month, AppDbContext db) =>
        {
            var now = DateTime.UtcNow;
            var y = year ?? now.Year;
            var m = month ?? now.Month;
            var start = new DateOnly(y, m, 1);
            var end = start.AddMonths(1).AddDays(-1);

            var totalIncome = await db.Transactions
                .Where(t => t.Type == TransactionType.Income && t.Date >= start && t.Date <= end)
                .SumAsync(t => t.Amount);

            var totalExpense = await db.Transactions
                .Where(t => t.Type == TransactionType.Expense && t.Date >= start && t.Date <= end)
                .SumAsync(t => t.Amount);

            var totalBankBalance = await db.BankAccounts.SumAsync(b => b.Balance);

            return Results.Ok(new
            {
                TotalIncome = totalIncome,
                TotalExpense = totalExpense,
                TotalBankBalance = totalBankBalance
            });
        });
    }
}
