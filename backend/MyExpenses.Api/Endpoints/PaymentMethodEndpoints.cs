using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class PaymentMethodEndpoints
{
    public static void MapPaymentMethodEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/payment-methods");

        group.MapGet("/", async (int? page, int? pageSize, bool? all, AppDbContext db) =>
        {
            var query = db.PaymentMethods.OrderBy(p => p.SortOrder).AsQueryable();

            if (all == true)
            {
                var allItems = await query.ToListAsync();
                return Results.Ok(allItems);
            }

            var total = await query.CountAsync();
            var p = page ?? 1;
            var ps = pageSize ?? 20;

            var items = await query
                .Skip((p - 1) * ps)
                .Take(ps)
                .ToListAsync();

            return Results.Ok(new { items, total, page = p, pageSize = ps });
        })
        .RequireApiTokenScope(ApiTokenScopes.PaymentMethodsRead);

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
            await db.PaymentMethods.FindAsync(id) is PaymentMethod p ? Results.Ok(p) : Results.NotFound())
            .RequireApiTokenScope(ApiTokenScopes.PaymentMethodsRead);

        group.MapPost("/", async (PaymentMethod pm, AppDbContext db) =>
        {
            db.PaymentMethods.Add(pm);
            await db.SaveChangesAsync();
            return Results.Created($"/api/payment-methods/{pm.Id}", pm);
        });

        group.MapPut("/{id:int}", async (int id, PaymentMethod input, AppDbContext db) =>
        {
            var pm = await db.PaymentMethods.FindAsync(id);
            if (pm is null) return Results.NotFound();

            pm.Name = input.Name;
            pm.Icon = input.Icon;
            pm.Color = input.Color;
            pm.SortOrder = input.SortOrder;

            await db.SaveChangesAsync();
            return Results.Ok(pm);
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var pm = await db.PaymentMethods.FindAsync(id);
            if (pm is null) return Results.NotFound();

            var transactionCount = await db.Transactions.CountAsync(t => t.PaymentMethodId == id);
            if (transactionCount > 0)
                return Results.Conflict(new { error = $"此支付方式有 {transactionCount} 筆交易使用中，請先修改交易" });

            db.PaymentMethods.Remove(pm);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        group.MapPost("/restore-defaults", async (AppDbContext db) =>
        {
            var defaults = new List<PaymentMethod>
            {
                new() { Name = "現金", Icon = "Banknote", Color = "#10B981", SortOrder = 1, SystemCode = "cash" },
                new() { Name = "信用卡", Icon = "CreditCard", Color = "#3B82F6", SortOrder = 2, SystemCode = "credit-card" },
                new() { Name = "Line Pay", Icon = "Smartphone", Color = "#06B6D4", SortOrder = 3, SystemCode = "line-pay" },
                new() { Name = "銀行轉帳", Icon = "Building2", Color = "#8B5CF6", SortOrder = 4, SystemCode = "bank-transfer" },
                new() { Name = "悠遊卡", Icon = "Wallet", Color = "#F59E0B", SortOrder = 5, SystemCode = "easy-card" },
                new() { Name = "其他", Icon = "MoreHorizontal", Color = "#6B7280", SortOrder = 6, SystemCode = "other" },
            };

            var existing = await db.PaymentMethods.Where(p => p.SystemCode != null).ToDictionaryAsync(p => p.SystemCode!);

            foreach (var def in defaults)
            {
                if (existing.TryGetValue(def.SystemCode!, out var existingPm))
                {
                    existingPm.Name = def.Name;
                    existingPm.Icon = def.Icon;
                    existingPm.Color = def.Color;
                    existingPm.SortOrder = def.SortOrder;
                }
                else
                {
                    db.PaymentMethods.Add(def);
                }
            }

            await db.SaveChangesAsync();

            var items = await db.PaymentMethods.OrderBy(p => p.SortOrder).ToListAsync();
            return Results.Ok(new { items, total = items.Count });
        });
    }
}
