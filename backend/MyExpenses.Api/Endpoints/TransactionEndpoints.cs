using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Models.Requests;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class TransactionEndpoints
{
    public static void MapTransactionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/transactions");

        group.MapGet("/", async (int? categoryId, DateOnly? startDate, DateOnly? endDate,
            string? search, TransactionType? type, int? page, int? pageSize, int? limit, AppDbContext db) =>
        {
            var query = BuildFilteredQuery(db, categoryId, startDate, endDate, search, type);

            var safeLimit = PaginationPolicy.NormalizeLimit(limit);
            if (safeLimit.HasValue)
            {
                var limitedItems = await query
                    .OrderByDescending(t => t.Date)
                    .Include(t => t.Category)
                    .Include(t => t.PaymentMethod)
                    .Take(safeLimit.Value)
                    .ToListAsync();
                return Results.Ok(limitedItems);
            }

            return Results.Ok(await ListTransactionsAsync(
                categoryId,
                startDate,
                endDate,
                search,
                type,
                page,
                pageSize,
                db));
        })
        .RequireApiTokenScope(ApiTokenScopes.TransactionsRead);

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
        {
            var transaction = await db.Transactions.Include(t => t.Category).Include(t => t.PaymentMethod).FirstOrDefaultAsync(t => t.Id == id);
            return transaction is not null ? Results.Ok(transaction) : Results.NotFound();
        })
        .RequireApiTokenScope(ApiTokenScopes.TransactionsRead);

        group.MapPost("/", async (CreateTransactionRequest request, AppDbContext db, TimeZoneService timeZoneService) =>
        {
            int? resolvedCategoryId = request.CategoryId;

            if (resolvedCategoryId.HasValue)
            {
                var categoryExists = await db.Categories.AnyAsync(c => c.Id == resolvedCategoryId.Value);
                if (!categoryExists) return Results.BadRequest($"CategoryId '{resolvedCategoryId}' not found");
            }

            if (resolvedCategoryId is null && !string.IsNullOrEmpty(request.CategoryCode))
            {
                var cat = await db.Categories.FirstOrDefaultAsync(c => c.SystemCode == request.CategoryCode);
                if (cat is null) return Results.BadRequest($"CategoryCode '{request.CategoryCode}' not found");
                resolvedCategoryId = cat.Id;
                request.Type ??= cat.Type == CategoryType.Income ? TransactionType.Income : TransactionType.Expense;
            }

            if (resolvedCategoryId is null && !string.IsNullOrEmpty(request.Category))
            {
                var cat = await db.Categories.FirstOrDefaultAsync(c => c.Name == request.Category);
                if (cat is null) return Results.BadRequest($"Category '{request.Category}' not found");
                resolvedCategoryId = cat.Id;
                request.Type ??= cat.Type == CategoryType.Income ? TransactionType.Income : TransactionType.Expense;
            }

            if (request.Type is null)
                return Results.BadRequest("Transaction type is required");
            if (resolvedCategoryId is null)
                return Results.BadRequest("Category is required");

            int? resolvedPaymentMethodId = request.PaymentMethodId;

            if (resolvedPaymentMethodId is null && !string.IsNullOrEmpty(request.PaymentMethodCode))
            {
                var pm = await db.PaymentMethods.FirstOrDefaultAsync(p => p.SystemCode == request.PaymentMethodCode);
                if (pm is not null) resolvedPaymentMethodId = pm.Id;
            }

            if (resolvedPaymentMethodId is null && !string.IsNullOrEmpty(request.PaymentMethod))
            {
                var pm = await db.PaymentMethods.FirstOrDefaultAsync(p => p.Name == request.PaymentMethod);
                if (pm is not null) resolvedPaymentMethodId = pm.Id;
            }

            var transaction = new Transaction
            {
                Type = request.Type.Value,
                Amount = request.Amount,
                Date = request.Date ?? timeZoneService.GetLocalDate(),
                Description = request.Description,
                Notes = request.Notes,
                CategoryId = resolvedCategoryId.Value,
                PaymentMethodId = resolvedPaymentMethodId
            };

            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();

            await db.Entry(transaction).Reference(t => t.Category).LoadAsync();
            await db.Entry(transaction).Reference(t => t.PaymentMethod).LoadAsync();
            return Results.Created($"/api/transactions/{transaction.Id}", transaction);
        })
        .RequireApiTokenScope(ApiTokenScopes.TransactionsWrite);

        group.MapPut("/{id:int}", async (int id, CreateTransactionRequest request, AppDbContext db) =>
        {
            var transaction = await db.Transactions.FindAsync(id);
            if (transaction is null) return Results.NotFound();

            int? resolvedCategoryId = request.CategoryId;

            if (resolvedCategoryId.HasValue)
            {
                var categoryExists = await db.Categories.AnyAsync(c => c.Id == resolvedCategoryId.Value);
                if (!categoryExists) return Results.BadRequest($"CategoryId '{resolvedCategoryId}' not found");
            }

            if (resolvedCategoryId is null && !string.IsNullOrEmpty(request.CategoryCode))
            {
                var cat = await db.Categories.FirstOrDefaultAsync(c => c.SystemCode == request.CategoryCode);
                if (cat is null) return Results.BadRequest($"CategoryCode '{request.CategoryCode}' not found");
                resolvedCategoryId = cat.Id;
                request.Type ??= cat.Type == CategoryType.Income ? TransactionType.Income : TransactionType.Expense;
            }

            if (resolvedCategoryId is null && !string.IsNullOrEmpty(request.Category))
            {
                var cat = await db.Categories.FirstOrDefaultAsync(c => c.Name == request.Category);
                if (cat is null) return Results.BadRequest($"Category '{request.Category}' not found");
                resolvedCategoryId = cat.Id;
                request.Type ??= cat.Type == CategoryType.Income ? TransactionType.Income : TransactionType.Expense;
            }

            int? resolvedPaymentMethodId = request.PaymentMethodId;

            if (resolvedPaymentMethodId is null && !string.IsNullOrEmpty(request.PaymentMethodCode))
            {
                var pm = await db.PaymentMethods.FirstOrDefaultAsync(p => p.SystemCode == request.PaymentMethodCode);
                if (pm is not null) resolvedPaymentMethodId = pm.Id;
            }

            if (resolvedPaymentMethodId is null && !string.IsNullOrEmpty(request.PaymentMethod))
            {
                var pm = await db.PaymentMethods.FirstOrDefaultAsync(p => p.Name == request.PaymentMethod);
                if (pm is not null) resolvedPaymentMethodId = pm.Id;
            }

            transaction.Type = request.Type ?? transaction.Type;
            transaction.Amount = request.Amount;
            transaction.Date = request.Date ?? transaction.Date;
            transaction.Description = request.Description;
            transaction.Notes = request.Notes;
            transaction.CategoryId = resolvedCategoryId ?? transaction.CategoryId;
            transaction.PaymentMethodId = resolvedPaymentMethodId ?? transaction.PaymentMethodId;

            await db.SaveChangesAsync();
            return Results.Ok(transaction);
        })
        .RequireApiTokenScope(ApiTokenScopes.TransactionsWrite);

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var transaction = await db.Transactions.FindAsync(id);
            if (transaction is null) return Results.NotFound();

            transaction.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
        .RequireApiTokenScope(ApiTokenScopes.TransactionsDelete);

        group.MapPost("/{id:int}/undo", async (int id, AppDbContext db) =>
        {
            var transaction = await db.Transactions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt != null);

            if (transaction is null) return Results.NotFound();

            transaction.DeletedAt = null;
            await db.SaveChangesAsync();
            return Results.Ok(transaction);
        })
        .RequireApiTokenScope(ApiTokenScopes.TransactionsUndo);
    }

    /// <summary>Returns a paginated transaction list with aggregates over the complete filtered result.</summary>
    public static async Task<TransactionListResponse> ListTransactionsAsync(
        int? categoryId,
        DateOnly? startDate,
        DateOnly? endDate,
        string? search,
        TransactionType? type,
        int? page,
        int? pageSize,
        AppDbContext db)
    {
        var query = BuildFilteredQuery(db, categoryId, startDate, endDate, search, type);
        var totalIncome = await query
            .Where(transaction => transaction.Type == TransactionType.Income)
            .SumAsync(transaction => (decimal?)transaction.Amount) ?? 0m;
        var totalExpense = await query
            .Where(transaction => transaction.Type == TransactionType.Expense)
            .SumAsync(transaction => (decimal?)transaction.Amount) ?? 0m;
        var count = await query.CountAsync();
        var maxAmount = await query
            .Select(transaction => (decimal?)transaction.Amount)
            .MaxAsync() ?? 0m;
        var p = PaginationPolicy.NormalizePage(page);
        var ps = PaginationPolicy.NormalizePageSize(pageSize);
        var items = await query
            .OrderByDescending(transaction => transaction.Date)
            .Include(transaction => transaction.Category)
            .Include(transaction => transaction.PaymentMethod)
            .Skip((p - 1) * ps)
            .Take(ps)
            .ToListAsync();

        var totalAmount = totalIncome + totalExpense;
        var dailyAverage = type.HasValue
            ? Math.Round((type == TransactionType.Income ? totalIncome : totalExpense) / GetDayCount(startDate, endDate), 2)
            : 0m;

        return new TransactionListResponse(
            items,
            count,
            p,
            ps,
            new TransactionListSummary(
                totalAmount,
                totalIncome,
                totalExpense,
                count,
                dailyAverage,
                maxAmount));
    }

    /// <summary>Builds the unpaged transaction query shared by list items and summary aggregates.</summary>
    private static IQueryable<Transaction> BuildFilteredQuery(
        AppDbContext db,
        int? categoryId,
        DateOnly? startDate,
        DateOnly? endDate,
        string? search,
        TransactionType? type)
    {
        var query = db.Transactions.AsQueryable();

        if (categoryId.HasValue)
            query = query.Where(transaction => transaction.CategoryId == categoryId.Value);
        if (startDate.HasValue)
            query = query.Where(transaction => transaction.Date >= startDate.Value);
        if (endDate.HasValue)
            query = query.Where(transaction => transaction.Date <= endDate.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(transaction =>
                (transaction.Description != null && transaction.Description.Contains(search))
                || (transaction.Notes != null && transaction.Notes.Contains(search)));
        }
        if (type.HasValue)
            query = query.Where(transaction => transaction.Type == type.Value);

        return query;
    }

    /// <summary>Calculates the inclusive calendar-day count used for transaction daily averages.</summary>
    private static int GetDayCount(DateOnly? startDate, DateOnly? endDate)
    {
        if (!startDate.HasValue || !endDate.HasValue)
            return 1;

        return Math.Max(1, endDate.Value.DayNumber - startDate.Value.DayNumber + 1);
    }
}

public sealed record TransactionListResponse(
    IReadOnlyList<Transaction> Items,
    int Total,
    int Page,
    int PageSize,
    TransactionListSummary Summary);

public sealed record TransactionListSummary(
    decimal TotalAmount,
    decimal TotalIncome,
    decimal TotalExpense,
    int Count,
    decimal DailyAverage,
    decimal MaxAmount);
