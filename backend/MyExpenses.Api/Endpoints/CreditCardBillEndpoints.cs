using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Endpoints;

public static class CreditCardBillEndpoints
{
    /// <summary>註冊信用卡帳單相關 API 端點</summary>
    public static void MapCreditCardBillEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/credit-card-bills");

        group.MapGet("/", async (int? cardId, bool? isPaid, DateOnly? dateStart, DateOnly? dateEnd, AppDbContext db) =>
        {
            var query = db.CreditCardBills.Include(b => b.Card).AsQueryable();
            if (cardId.HasValue)
                query = query.Where(b => b.CardId == cardId.Value);
            if (isPaid.HasValue)
                query = query.Where(b => b.IsPaid == isPaid.Value);
            if (dateStart.HasValue)
                query = query.Where(b => b.DueDate >= dateStart.Value);
            if (dateEnd.HasValue)
                query = query.Where(b => b.DueDate <= dateEnd.Value);
            return await query.OrderByDescending(b => b.DueDate).ToListAsync();
        });

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
            await db.CreditCardBills.Include(b => b.Card).FirstOrDefaultAsync(b => b.Id == id) is CreditCardBill b
                ? Results.Ok(b) : Results.NotFound());

        group.MapPost("/", async (CreditCardBill bill, AppDbContext db) =>
        {
            db.CreditCardBills.Add(bill);
            await db.SaveChangesAsync();
            return Results.Created($"/api/credit-card-bills/{bill.Id}", bill);
        });

        group.MapPut("/{id:int}", async (int id, CreditCardBill input, AppDbContext db) =>
        {
            var bill = await db.CreditCardBills.FindAsync(id);
            if (bill is null) return Results.NotFound();

            bill.TotalAmount = input.TotalAmount;
            bill.PaidAmount = input.PaidAmount;
            bill.DueDate = input.DueDate;
            bill.IsPaid = input.IsPaid;

            await db.SaveChangesAsync();
            return Results.Ok(bill);
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var bill = await db.CreditCardBills.FindAsync(id);
            if (bill is null) return Results.NotFound();

            db.CreditCardBills.Remove(bill);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
