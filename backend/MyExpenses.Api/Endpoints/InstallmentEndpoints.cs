using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Models.Requests;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class InstallmentEndpoints
{
    /// <summary>Maps installment queries and atomic financial command endpoints.</summary>
    public static void MapInstallmentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/installment-purchases", async (
            InstallmentPurchaseRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            InstallmentCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await commandService.CreateInstallmentPurchaseAsync(
                    request,
                    idempotencyKey,
                    cancellationToken);
                return Results.Created($"/api/installments/{result.Installment.Id}", result);
            }
            catch (FinancialCommandException exception)
            {
                return ToProblem(exception);
            }
        }).RequireApiTokenScope(ApiTokenScopes.TransactionsWrite);

        var group = app.MapGroup("/api/installments");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            int? cardId,
            DateOnly? dateStart,
            DateOnly? dateEnd,
            string? status,
            AppDbContext db,
            TimeZoneService timeZoneService,
            CancellationToken cancellationToken) =>
            Results.Ok(await ListInstallmentsAsync(
                page,
                pageSize,
                cardId,
                dateStart,
                dateEnd,
                status,
                db,
                timeZoneService,
                cancellationToken)))
            .RequireApiTokenScope(ApiTokenScopes.TransactionsRead);

        group.MapGet("/{id:int}", async (int id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var installment = await db.Installments
                .Include(item => item.Transaction).ThenInclude(item => item!.Category)
                .Include(item => item.Transaction).ThenInclude(item => item!.PaymentMethod)
                .Include(item => item.Card)
                .Include(item => item.Payments.OrderBy(payment => payment.Period))
                .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

            return installment is not null ? Results.Ok(installment) : Results.NotFound();
        }).RequireApiTokenScope(ApiTokenScopes.TransactionsRead);

        group.MapPost("/", async (
            CreateStandaloneInstallmentRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            InstallmentCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var installment = await commandService.CreateStandaloneInstallmentAsync(
                    request,
                    idempotencyKey,
                    cancellationToken);
                return Results.Created($"/api/installments/{installment.Id}", installment);
            }
            catch (FinancialCommandException exception)
            {
                return ToProblem(exception);
            }
        }).RequireApiTokenScope(ApiTokenScopes.TransactionsWrite);

        group.MapPut("/{id:int}", async (
            int id,
            UpdateInstallmentScheduleRequest request,
            InstallmentCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var installment = await commandService.UpdateInstallmentScheduleAsync(id, request, cancellationToken);
                return Results.Ok(installment);
            }
            catch (FinancialCommandException exception)
            {
                return ToProblem(exception);
            }
        }).RequireApiTokenScope(ApiTokenScopes.TransactionsWrite);

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var installment = await db.Installments.FindAsync([id], cancellationToken);
            if (installment is null)
                return Results.NotFound();

            db.Installments.Remove(installment);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }).RequireApiTokenScope(ApiTokenScopes.TransactionsDelete);

        group.MapPatch("/{id:int}/payments/{paymentId:int}", async (
            int id,
            int paymentId,
            SetInstallmentPaymentStateRequest? request,
            InstallmentCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (request is null)
                    throw new FinancialCommandException(400, "Invalid financial command", "付款狀態不可為空");

                var installment = await commandService.SetInstallmentPaymentStateAsync(
                    id,
                    paymentId,
                    request,
                    cancellationToken);
                return Results.Ok(installment);
            }
            catch (FinancialCommandException exception)
            {
                return ToProblem(exception);
            }
        }).RequireApiTokenScope(ApiTokenScopes.TransactionsWrite);
    }

    /// <summary>Returns a paginated installment list with complete filtered counts and due-payment aggregates.</summary>
    public static async Task<InstallmentListResponse> ListInstallmentsAsync(
        int? page,
        int? pageSize,
        int? cardId,
        DateOnly? dateStart,
        DateOnly? dateEnd,
        string? status,
        AppDbContext db,
        TimeZoneService? timeZoneService = null,
        CancellationToken cancellationToken = default)
    {
        var query = BuildFilteredQuery(db, cardId, dateStart, dateEnd, status);
        var totalCount = await query.CountAsync(cancellationToken);
        var activeCount = await query
            .CountAsync(installment => installment.Payments.Any(payment => !payment.IsPaid), cancellationToken);

        var localDate = timeZoneService?.GetLocalDate() ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var currentMonthStart = new DateOnly(localDate.Year, localDate.Month, 1);
        var currentMonthEnd = currentMonthStart.AddMonths(1).AddDays(-1);
        var matchingInstallmentIds = query.Select(installment => installment.Id);
        var duePayments = db.InstallmentPayments
            .Where(payment =>
                !payment.IsPaid
                && payment.DueDate.HasValue
                && payment.DueDate.Value >= currentMonthStart
                && payment.DueDate.Value <= currentMonthEnd
                && matchingInstallmentIds.Contains(payment.InstallmentId));
        var dueAmount = await duePayments
            .SumAsync(payment => (decimal?)payment.Amount, cancellationToken) ?? 0m;
        var duePaymentCount = await duePayments.CountAsync(cancellationToken);

        var normalizedPage = PaginationPolicy.NormalizePage(page);
        var normalizedPageSize = PaginationPolicy.NormalizePageSize(pageSize);
        var items = await query
            .Include(installment => installment.Transaction)
            .Include(installment => installment.Card)
            .Include(installment => installment.Payments.OrderBy(payment => payment.Period))
            .OrderByDescending(installment => installment.PurchaseDate)
            .ThenByDescending(installment => installment.CreatedAt)
            .ThenByDescending(installment => installment.Id)
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(cancellationToken);

        return new InstallmentListResponse(
            items,
            totalCount,
            normalizedPage,
            normalizedPageSize,
            new InstallmentListSummary(totalCount, activeCount, dueAmount, duePaymentCount));
    }

    /// <summary>Builds the unpaged installment query shared by item and summary calculations.</summary>
    private static IQueryable<Installment> BuildFilteredQuery(
        AppDbContext db,
        int? cardId,
        DateOnly? dateStart,
        DateOnly? dateEnd,
        string? status)
    {
        var query = db.Installments.AsQueryable();

        if (cardId.HasValue)
            query = query.Where(installment => installment.CardId == cardId.Value);

        var hasDateFilter = dateStart.HasValue || dateEnd.HasValue;
        var statusFilter = InstallmentStatus.Active;
        var hasExplicitStatus = !string.IsNullOrEmpty(status)
            && Enum.TryParse(status, true, out statusFilter);

        if (hasDateFilter)
        {
            if (dateStart.HasValue && dateEnd.HasValue)
            {
                query = query.Where(installment =>
                    (installment.PurchaseDate >= dateStart.Value && installment.PurchaseDate <= dateEnd.Value)
                    || installment.Payments.Any(payment => !payment.IsPaid));
            }
            else if (dateStart.HasValue)
            {
                query = query.Where(installment =>
                    installment.PurchaseDate >= dateStart.Value
                    || installment.Payments.Any(payment => !payment.IsPaid));
            }
            else
            {
                query = query.Where(installment =>
                    installment.PurchaseDate <= dateEnd!.Value
                    || installment.Payments.Any(payment => !payment.IsPaid));
            }
        }

        if (hasExplicitStatus)
        {
            query = statusFilter == InstallmentStatus.Active
                ? query.Where(installment => installment.Payments.Any(payment => !payment.IsPaid))
                : query.Where(installment => !installment.Payments.Any(payment => !payment.IsPaid));
        }

        return query;
    }

    /// <summary>Maps an expected financial command failure to a safe ProblemDetails response.</summary>
    private static IResult ToProblem(FinancialCommandException exception)
        => Results.Problem(
            statusCode: exception.StatusCode,
            title: exception.Title,
            detail: exception.Detail);
}

public sealed record InstallmentListResponse(
    IReadOnlyList<Installment> Items,
    int Total,
    int Page,
    int PageSize,
    InstallmentListSummary Summary);

public sealed record InstallmentListSummary(
    int TotalCount,
    int ActiveCount,
    decimal DueAmount,
    int DuePaymentCount);
