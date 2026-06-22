using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Endpoints;

public static class BankAccountEndpoints
{
    public static void MapBankAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/bank-accounts");

        group.MapGet("/", async (int? page, int? pageSize, AppDbContext db) =>
        {
            var query = db.BankAccounts.AsQueryable();

            var total = await query.CountAsync();
            var p = page ?? 1;
            var ps = pageSize ?? 20;

            var items = await query
                .OrderByDescending(a => a.CreatedAt)
                .Skip((p - 1) * ps)
                .Take(ps)
                .ToListAsync();

            return Results.Ok(new { items, total, page = p, pageSize = ps });
        });

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
            await db.BankAccounts.FindAsync(id) is BankAccount a ? Results.Ok(a) : Results.NotFound());

        group.MapPost("/", async (BankAccount account, AppDbContext db) =>
        {
            var now = DateTime.UtcNow;
            account.CreatedAt = now;
            account.UpdatedAt = now;
            db.BankAccounts.Add(account);
            await db.SaveChangesAsync();
            return Results.Created($"/api/bank-accounts/{account.Id}", account);
        });

        group.MapPut("/{id:int}", async (int id, BankAccount input, AppDbContext db) =>
        {
            var account = await db.BankAccounts.FindAsync(id);
            if (account is null) return Results.NotFound();

            account.BankName = input.BankName;
            account.AccountNumber = input.AccountNumber;
            account.Balance = input.Balance;
            account.AccountType = input.AccountType;
            account.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            return Results.Ok(account);
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var account = await db.BankAccounts.FindAsync(id);
            if (account is null) return Results.NotFound();

            db.BankAccounts.Remove(account);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
