using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class BankAccountEndpoints
{
    public static void MapBankAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/bank-accounts");

        group.MapGet("/", async (int? page, int? pageSize, string? bankName, AppDbContext db) =>
            Results.Ok(await ListBankAccountsAsync(page, pageSize, bankName, db)));

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
            await db.BankAccounts.FindAsync(id) is BankAccount a ? Results.Ok(a) : Results.NotFound());

        group.MapPost("/", async (BankAccount account, AppDbContext db) =>
        {
            var validationResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(account, new ValidationContext(account), validationResults, true))
            {
                return Results.BadRequest(new { error = validationResults[0].ErrorMessage });
            }

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

            var validationResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(input, new ValidationContext(input), validationResults, true))
            {
                return Results.BadRequest(new { error = validationResults[0].ErrorMessage });
            }

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

    /// <summary>Lists bank accounts with optional bank name filtering, pagination, and filtered balance totals.</summary>
    public static async Task<BankAccountListResponse> ListBankAccountsAsync(int? page, int? pageSize, string? bankName, AppDbContext db)
    {
        var query = db.BankAccounts.AsQueryable();
        var trimmedBankName = bankName?.Trim();
        if (!string.IsNullOrEmpty(trimmedBankName))
        {
            query = query.Where(a => a.BankName.Contains(trimmedBankName));
        }

        var total = await query.CountAsync();
        var totalBalance = await query.Select(a => (decimal?)a.Balance).SumAsync() ?? 0m;
        var p = PaginationPolicy.NormalizePage(page);
        var ps = PaginationPolicy.NormalizePageSize(pageSize);

        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((p - 1) * ps)
            .Take(ps)
            .ToListAsync();

        return new BankAccountListResponse(items, total, p, ps, totalBalance);
    }
}

public sealed record BankAccountListResponse(IReadOnlyList<BankAccount> Items, int Total, int Page, int PageSize, decimal TotalBalance);
