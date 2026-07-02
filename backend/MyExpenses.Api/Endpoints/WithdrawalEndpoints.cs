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
        {
            var query = db.Withdrawals.Include(w => w.BankAccount).AsQueryable();

            if (startDate.HasValue)
                query = query.Where(w => w.Date >= startDate.Value);
            if (endDate.HasValue)
                query = query.Where(w => w.Date <= endDate.Value);

            var total = await query.CountAsync();
            page = PaginationPolicy.NormalizePage(page);
            pageSize = PaginationPolicy.NormalizePageSize(pageSize);

            var items = await query
                .OrderByDescending(w => w.Date)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Results.Ok(new { items, total, page, pageSize });
        });

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
}
