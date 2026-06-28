using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Models.Requests;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class InstallmentEndpoints
{
    public static void MapInstallmentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/installments");

        group.MapGet("/", async (int? page, int? pageSize, int? cardId, DateOnly? dateStart, DateOnly? dateEnd, string? status, AppDbContext db) =>
        {
            var query = db.Installments
                .Include(i => i.Transaction)
                .Include(i => i.Card)
                .Include(i => i.Payments)
                .AsQueryable();

            if (cardId.HasValue)
                query = query.Where(i => i.CardId == cardId.Value);

            var hasDateFilter = dateStart.HasValue || dateEnd.HasValue;
            InstallmentStatus statusFilter = InstallmentStatus.Active;
            var hasExplicitStatus = !string.IsNullOrEmpty(status) && Enum.TryParse<InstallmentStatus>(status, true, out statusFilter);

            if (hasDateFilter)
            {
                if (hasExplicitStatus)
                {
                    if (dateStart.HasValue)
                        query = query.Where(i => i.PurchaseDate >= dateStart.Value);
                    if (dateEnd.HasValue)
                        query = query.Where(i => i.PurchaseDate <= dateEnd.Value);
                }
                else
                {
                    if (dateStart.HasValue && dateEnd.HasValue)
                        query = query.Where(i => (i.PurchaseDate >= dateStart.Value && i.PurchaseDate <= dateEnd.Value) || i.Status == InstallmentStatus.Active);
                    else if (dateStart.HasValue)
                        query = query.Where(i => i.PurchaseDate >= dateStart.Value || i.Status == InstallmentStatus.Active);
                    else
                        query = query.Where(i => i.PurchaseDate <= dateEnd!.Value || i.Status == InstallmentStatus.Active);
                }
            }

            if (hasExplicitStatus)
                query = query.Where(i => i.Status == statusFilter);

            var total = await query.CountAsync();
            var p = page ?? 1;
            var ps = pageSize ?? 20;

            var items = await query
                .OrderByDescending(i => i.PurchaseDate)
                .ThenByDescending(i => i.CreatedAt)
                .Skip((p - 1) * ps)
                .Take(ps)
                .ToListAsync();

            return Results.Ok(new { items, total, page = p, pageSize = ps });
        });

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
        {
            var installment = await db.Installments
                .Include(i => i.Transaction)
                .Include(i => i.Card)
                .Include(i => i.Payments.OrderBy(p => p.Period))
                .FirstOrDefaultAsync(i => i.Id == id);

            return installment is not null ? Results.Ok(installment) : Results.NotFound();
        });

        group.MapPost("/", async (Installment installment, AppDbContext db) =>
        {
            if (installment.PurchaseDate == default)
                return Results.BadRequest(new { error = "請選擇刷卡日期" });

            installment.CreatedAt = DateTime.UtcNow;
            installment.RemainingPeriods = installment.Periods;
            installment.Status = InstallmentStatus.Active;
            installment.PerPeriod = Math.Floor(installment.TotalAmount / installment.Periods);

            CreditCard? card = null;
            if (installment.CardId.HasValue)
                card = await db.CreditCards.FindAsync(installment.CardId.Value);

            db.Installments.Add(installment);
            await db.SaveChangesAsync();

            var perPeriod = installment.PerPeriod;
            var remainder = installment.TotalAmount - perPeriod * installment.Periods;

            for (int p = 1; p <= installment.Periods; p++)
            {
                var amount = p == installment.Periods ? perPeriod + remainder : perPeriod;

                DateOnly? dueDate = null;
                if (card is not null)
                    dueDate = InstallmentScheduleCalculator.CalculateDueDate(installment.PurchaseDate, card.StatementDay, card.DueDay, p);

                db.InstallmentPayments.Add(new InstallmentPayment
                {
                    InstallmentId = installment.Id,
                    Period = p,
                    Amount = amount,
                    IsPaid = false,
                    DueDate = dueDate,
                });
            }

            await db.SaveChangesAsync();

            var result = await db.Installments
                .Include(i => i.Transaction)
                .Include(i => i.Card)
                .Include(i => i.Payments)
                .FirstAsync(i => i.Id == installment.Id);

            return Results.Created($"/api/installments/{installment.Id}", result);
        });

        group.MapPut("/{id:int}", async (int id, Installment input, AppDbContext db) =>
        {
            var installment = await db.Installments
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (installment is null) return Results.NotFound();

            if (input.PurchaseDate == default)
                return Results.BadRequest(new { error = "請選擇刷卡日期" });

            var hasPaidPayments = installment.Payments.Any(p => p.IsPaid);

            if (hasPaidPayments)
            {
                if (input.TotalAmount != installment.TotalAmount)
                    return Results.BadRequest(new { error = "已有繳款記錄，不可修改總金額" });
                if (input.Periods != installment.Periods)
                    return Results.BadRequest(new { error = "已有繳款記錄，不可修改期數" });
                if (input.CardId != installment.CardId)
                    return Results.BadRequest(new { error = "已有繳款記錄，不可修改信用卡" });
                if (input.PurchaseDate != installment.PurchaseDate)
                    return Results.BadRequest(new { error = "已有繳款記錄，不可修改刷卡日期" });
            }

            installment.TotalAmount = input.TotalAmount;
            installment.Periods = input.Periods;
            installment.PerPeriod = Math.Floor(input.TotalAmount / input.Periods);
            installment.PurchaseDate = input.PurchaseDate;
            installment.Description = input.Description;
            installment.CardId = input.CardId;

            CreditCard? card = null;
            if (input.CardId.HasValue)
                card = await db.CreditCards.FindAsync(input.CardId.Value);

            var paidPayments = installment.Payments.Where(p => p.IsPaid).ToList();
            var unpaidPayments = installment.Payments.Where(p => !p.IsPaid).ToList();
            var paidCount = paidPayments.Count;

            db.InstallmentPayments.RemoveRange(unpaidPayments);

            var unpaidCount = input.Periods - paidCount;

            if (unpaidCount > 0)
            {
                var unpaidTotal = input.TotalAmount - paidPayments.Sum(p => p.Amount);
                if (unpaidTotal < 0) unpaidTotal = 0;

                var perPeriod = unpaidTotal > 0 ? Math.Floor(unpaidTotal / unpaidCount) : 0;
                var remainder = unpaidTotal - perPeriod * unpaidCount;

                for (int p = 1; p <= unpaidCount; p++)
                {
                    var amount = p == unpaidCount ? perPeriod + remainder : perPeriod;
                    var periodIndex = paidCount + p;

                    DateOnly? dueDate = null;
                    if (card is not null)
                        dueDate = InstallmentScheduleCalculator.CalculateDueDate(installment.PurchaseDate, card.StatementDay, card.DueDay, periodIndex);

                    db.InstallmentPayments.Add(new InstallmentPayment
                    {
                        InstallmentId = id,
                        Period = periodIndex,
                        Amount = amount,
                        IsPaid = false,
                        DueDate = dueDate,
                    });
                }
            }

            installment.RemainingPeriods = unpaidCount > 0 ? unpaidCount : 0;
            installment.Status = installment.RemainingPeriods == 0 ? InstallmentStatus.PaidOff : InstallmentStatus.Active;

            await db.SaveChangesAsync();

            var result = await db.Installments
                .Include(i => i.Transaction)
                .Include(i => i.Card)
                .Include(i => i.Payments.OrderBy(p => p.Period))
                .FirstAsync(i => i.Id == id);

            return Results.Ok(result);
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var installment = await db.Installments.FindAsync(id);
            if (installment is null) return Results.NotFound();

            db.Installments.Remove(installment);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        group.MapPatch("/{id:int}/payments/{paymentId:int}", async (int id, int paymentId, [FromBody] MarkInstallmentPaymentRequest? request, AppDbContext db) =>
        {
            var payment = await db.InstallmentPayments
                .FirstOrDefaultAsync(p => p.Id == paymentId && p.InstallmentId == id);

            if (payment is null) return Results.NotFound();

            try
            {
                InstallmentPaymentMarker.TogglePaid(payment, request?.PaidDate);
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }

            await db.SaveChangesAsync();

            var installment = await db.Installments.FindAsync(id);
            if (installment is not null)
            {
                installment.RemainingPeriods = await db.InstallmentPayments
                    .CountAsync(p => p.InstallmentId == id && !p.IsPaid);

                installment.Status = installment.RemainingPeriods == 0
                    ? InstallmentStatus.PaidOff
                    : InstallmentStatus.Active;

                await db.SaveChangesAsync();
            }

            return Results.Ok(payment);
        });
    }
}
