using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class WithdrawalEndpoints
{
    /// <summary>對應提領相關端點</summary>
    public static void MapWithdrawalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/withdrawals");

        group.MapGet("/", async (DateOnly? startDate, DateOnly? endDate, int page, int pageSize, AppDbContext db) =>
            Results.Ok(await ListWithdrawalsAsync(startDate, endDate, page, pageSize, db)));

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
            await db.Withdrawals.Include(w => w.BankAccount).FirstOrDefaultAsync(w => w.Id == id) is Withdrawal w
                ? Results.Ok(w) : Results.NotFound());

        group.MapPost("/", async (Withdrawal withdrawal, AppDbContext db) =>
        {
            db.Withdrawals.Add(withdrawal);
            await db.SaveChangesAsync();
            return Results.Created($"/api/withdrawals/{withdrawal.Id}", withdrawal);
        });

        group.MapPut("/{id:int}", async (int id, Withdrawal input, AppDbContext db) =>
        {
            var withdrawal = await db.Withdrawals.FindAsync(id);
            if (withdrawal is null) return Results.NotFound();

            withdrawal.Amount = input.Amount;
            withdrawal.Date = input.Date;
            withdrawal.Description = input.Description;
            withdrawal.BankAccountId = input.BankAccountId;

            await db.SaveChangesAsync();
            return Results.Ok(withdrawal);
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var withdrawal = await db.Withdrawals.FindAsync(id);
            if (withdrawal is null) return Results.NotFound();

            db.Withdrawals.Remove(withdrawal);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    /// <summary>Returns a paginated withdrawal list with aggregates over the complete date-filtered result.</summary>
    public static async Task<WithdrawalListResponse> ListWithdrawalsAsync(
        DateOnly? startDate,
        DateOnly? endDate,
        int? page,
        int? pageSize,
        AppDbContext db)
    {
        var query = db.Withdrawals.AsQueryable();
        if (startDate.HasValue)
            query = query.Where(withdrawal => withdrawal.Date >= startDate.Value);
        if (endDate.HasValue)
            query = query.Where(withdrawal => withdrawal.Date <= endDate.Value);

        var count = await query.CountAsync();
        var totalAmount = await query
            .SumAsync(withdrawal => (decimal?)withdrawal.Amount) ?? 0m;
        var maxAmount = await query
            .Select(withdrawal => (decimal?)withdrawal.Amount)
            .MaxAsync() ?? 0m;
        var averageAmount = count == 0 ? 0m : totalAmount / count;
        var p = PaginationPolicy.NormalizePage(page);
        var ps = PaginationPolicy.NormalizePageSize(pageSize);
        var items = await query
            .Include(withdrawal => withdrawal.BankAccount)
            .OrderByDescending(withdrawal => withdrawal.Date)
            .Skip((p - 1) * ps)
            .Take(ps)
            .ToListAsync();

        return new WithdrawalListResponse(
            items,
            count,
            p,
            ps,
            new WithdrawalListSummary(totalAmount, count, averageAmount, maxAmount));
    }
}

public sealed record WithdrawalListResponse(
    IReadOnlyList<Withdrawal> Items,
    int Total,
    int Page,
    int PageSize,
    WithdrawalListSummary Summary);

public sealed record WithdrawalListSummary(
    decimal TotalAmount,
    int Count,
    decimal AverageAmount,
    decimal MaxAmount);
