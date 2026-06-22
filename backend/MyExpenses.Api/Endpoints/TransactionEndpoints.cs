using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Models.Requests;

namespace MyExpenses.Api.Endpoints;

public static class TransactionEndpoints
{
    public static void MapTransactionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/transactions");

        group.MapGet("/", async (int? categoryId, DateOnly? startDate, DateOnly? endDate,
            string? search, TransactionType? type, int? page, int? pageSize, int? limit, AppDbContext db) =>
        {
            var query = db.Transactions.AsQueryable();

            if (categoryId.HasValue) query = query.Where(t => t.CategoryId == categoryId);
            if (startDate.HasValue) query = query.Where(t => t.Date >= startDate);
            if (endDate.HasValue) query = query.Where(t => t.Date <= endDate);
            if (!string.IsNullOrEmpty(search))
                query = query.Where(t => t.Description!.Contains(search) || t.Notes!.Contains(search));
            if (type.HasValue) query = query.Where(t => t.Type == type);

            query = query.OrderByDescending(t => t.Date);

            query = query.Include(t => t.Category).Include(t => t.PaymentMethod);

            if (limit.HasValue)
            {
                return Results.Ok(await query.Take(limit.Value).ToListAsync());
            }

            int p = page ?? 1;
            int ps = pageSize ?? 20;
            var items = await query.Skip((p - 1) * ps).Take(ps).ToListAsync();
            var total = await query.CountAsync();
            return Results.Ok(new { items, total, page = p, pageSize = ps });
        });

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
        {
            var transaction = await db.Transactions.Include(t => t.Category).Include(t => t.PaymentMethod).FirstOrDefaultAsync(t => t.Id == id);
            return transaction is not null ? Results.Ok(transaction) : Results.NotFound();
        });

        group.MapPost("/", async (CreateTransactionRequest request, AppDbContext db) =>
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
                Date = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow),
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
        });

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
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var transaction = await db.Transactions.FindAsync(id);
            if (transaction is null) return Results.NotFound();

            transaction.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/undo", async (int id, AppDbContext db) =>
        {
            var transaction = await db.Transactions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt != null);

            if (transaction is null) return Results.NotFound();

            transaction.DeletedAt = null;
            await db.SaveChangesAsync();
            return Results.Ok(transaction);
        });
    }
}
