using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Endpoints;

public static class CategoryEndpoints
{
    public static void MapCategoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/categories");

        group.MapGet("/", async (int? page, int? pageSize, bool? all, AppDbContext db) =>
        {
            var query = db.Categories.OrderBy(c => c.SortOrder);

            if (all == true)
            {
                return Results.Ok(await query.ToListAsync());
            }

            int p = page ?? 1;
            int ps = pageSize ?? 20;
            var items = await query.Skip((p - 1) * ps).Take(ps).ToListAsync();
            var total = await query.CountAsync();
            return Results.Ok(new { items, total, page = p, pageSize = ps });
        });

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
            await db.Categories.FindAsync(id) is Category c ? Results.Ok(c) : Results.NotFound());

        group.MapPost("/", async (Category category, AppDbContext db) =>
        {
            db.Categories.Add(category);
            await db.SaveChangesAsync();
            return Results.Created($"/api/categories/{category.Id}", category);
        });

        group.MapPut("/{id:int}", async (int id, Category input, AppDbContext db) =>
        {
            var category = await db.Categories.FindAsync(id);
            if (category is null) return Results.NotFound();

            category.Name = input.Name;
            category.Type = input.Type;
            category.Icon = input.Icon;
            category.Color = input.Color;
            category.SortOrder = input.SortOrder;

            await db.SaveChangesAsync();
            return Results.Ok(category);
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var category = await db.Categories.FindAsync(id);
            if (category is null) return Results.NotFound();

            db.Categories.Remove(category);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        group.MapPost("/restore-defaults", async (AppDbContext db) =>
        {
            var defaults = new List<Category>
            {
                new() { Name = "薪資收入", Type = CategoryType.Income, Icon = "Wallet", Color = "#10B981", SortOrder = 1, SystemCode = "salary" },
                new() { Name = "兼職收入", Type = CategoryType.Income, Icon = "Briefcase", Color = "#059669", SortOrder = 2, SystemCode = "part-time" },
                new() { Name = "投資收益", Type = CategoryType.Income, Icon = "TrendingUp", Color = "#2563EB", SortOrder = 3, SystemCode = "investment" },
                new() { Name = "獎金", Type = CategoryType.Income, Icon = "Gift", Color = "#D97706", SortOrder = 4, SystemCode = "bonus" },
                new() { Name = "其他收入", Type = CategoryType.Income, Icon = "PlusCircle", Color = "#6D28D9", SortOrder = 5, SystemCode = "other-income" },
                new() { Name = "飲食", Type = CategoryType.Expense, Icon = "Utensils", Color = "#DC2626", SortOrder = 10, SystemCode = "meal" },
                new() { Name = "交通", Type = CategoryType.Expense, Icon = "Car", Color = "#2563EB", SortOrder = 11, SystemCode = "transport" },
                new() { Name = "生活", Type = CategoryType.Expense, Icon = "Home", Color = "#7C3AED", SortOrder = 12, SystemCode = "living" },
                new() { Name = "娛樂", Type = CategoryType.Expense, Icon = "Film", Color = "#D97706", SortOrder = 13, SystemCode = "entertainment" },
                new() { Name = "通訊", Type = CategoryType.Expense, Icon = "Smartphone", Color = "#0891B2", SortOrder = 14, SystemCode = "telecom" },
                new() { Name = "教育", Type = CategoryType.Expense, Icon = "BookOpen", Color = "#4F46E5", SortOrder = 15, SystemCode = "education" },
                new() { Name = "醫療", Type = CategoryType.Expense, Icon = "HeartPulse", Color = "#E11D48", SortOrder = 16, SystemCode = "medical" },
                new() { Name = "其他", Type = CategoryType.Expense, Icon = "MoreHorizontal", Color = "#64748B", SortOrder = 20, SystemCode = "other-expense" },
            };

            var existing = await db.Categories.Where(c => c.SystemCode != null).ToDictionaryAsync(c => c.SystemCode!);

            foreach (var def in defaults)
            {
                if (existing.TryGetValue(def.SystemCode!, out var existingCat))
                {
                    existingCat.Name = def.Name;
                    existingCat.Type = def.Type;
                    existingCat.Icon = def.Icon;
                    existingCat.Color = def.Color;
                    existingCat.SortOrder = def.SortOrder;
                }
                else
                {
                    db.Categories.Add(def);
                }
            }

            await db.SaveChangesAsync();

            var items = await db.Categories.OrderBy(c => c.SortOrder).ToListAsync();
            return Results.Ok(new { items, total = items.Count });
        });
    }
}
