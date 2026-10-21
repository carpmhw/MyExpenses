using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class CreditCardEndpoints
{
    /// <summary>註冊信用卡 CRUD 與僅限 owner 登入的固定預設操作。</summary>
    public static void MapCreditCardEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/credit-cards");

        // 預設操作獨立於一般卡片編輯，且不開放既有 API token scopes 使用。
        group.MapPut("/{id:int}/default", async Task<IResult> (
            int id, [FromServices] TransactionDefaultsService defaults, CancellationToken cancellationToken) =>
        {
            if (id <= 0) return Results.BadRequest(new { error = "信用卡 ID 必須大於零" });
            var result = await defaults.SetCreditCardAsync(id, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization();

        group.MapGet("/", async (int? page, int? pageSize, AppDbContext db) =>
        {
            var query = db.CreditCards.AsQueryable();

            var total = await query.CountAsync();
            var p = PaginationPolicy.NormalizePage(page);
            var ps = PaginationPolicy.NormalizePageSize(pageSize);

            var items = await query
                .OrderByDescending(c => c.CreatedAt)
                .Skip((p - 1) * ps)
                .Take(ps)
                .ToListAsync();

            return Results.Ok(new { items, total, page = p, pageSize = ps });
        }).RequireApiTokenScope(ApiTokenScopes.CreditCardsRead);

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
            await db.CreditCards.FindAsync(id) is CreditCard c ? Results.Ok(c) : Results.NotFound())
            .RequireApiTokenScope(ApiTokenScopes.CreditCardsRead);

        group.MapPost("/", async (CreditCard card, AppDbContext db) =>
        {
            var validationResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(card, new ValidationContext(card), validationResults, true))
            {
                return Results.BadRequest(new { error = validationResults[0].ErrorMessage });
            }

            card.CreatedAt = DateTime.UtcNow;
            card.UpdatedAt = DateTime.UtcNow;
            db.CreditCards.Add(card);
            await db.SaveChangesAsync();
            return Results.Created($"/api/credit-cards/{card.Id}", card);
        });

        group.MapPut("/{id:int}", async (int id, CreditCard input, AppDbContext db) =>
        {
            var card = await db.CreditCards.FindAsync(id);
            if (card is null) return Results.NotFound();

            var validationResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(input, new ValidationContext(input), validationResults, true))
            {
                return Results.BadRequest(new { error = validationResults[0].ErrorMessage });
            }

            card.BankName = input.BankName;
            card.LastFourDigits = input.LastFourDigits;
            card.CardNetwork = input.CardNetwork;
            card.StatementDay = input.StatementDay;
            card.DueDay = input.DueDay;
            card.CreditLimit = input.CreditLimit;
            card.Notes = input.Notes;
            card.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            return Results.Ok(card);
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var card = await db.CreditCards.FindAsync(id);
            if (card is null) return Results.NotFound();

            db.CreditCards.Remove(card);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
