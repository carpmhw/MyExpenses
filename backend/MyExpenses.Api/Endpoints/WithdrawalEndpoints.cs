using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class WithdrawalEndpoints
{
    /// <summary>對應提領相關端點</summary>
    public static void MapWithdrawalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/withdrawals");

        group.MapGet("/", async (DateOnly? startDate, DateOnly? endDate, int page, int pageSize, AppDbContext db, IServiceProvider services) =>
        {
            try
            {
                return Results.Ok(await ListWithdrawalsAsync(
                    startDate,
                    endDate,
                    page,
                    pageSize,
                    db,
                    services.GetService<IExchangeRateService>()));
            }
            catch (ExchangeRateUnavailableException exception)
            {
                return Results.Problem(
                    detail: exception.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
            await db.Withdrawals.Include(w => w.BankAccount).FirstOrDefaultAsync(w => w.Id == id) is Withdrawal w
                ? Results.Ok(w) : Results.NotFound());

        group.MapPost("/", async (Withdrawal withdrawal, AppDbContext db) =>
        {
            db.Withdrawals.Add(withdrawal);
            await db.SaveChangesAsync();
            return Results.Created($"/api/withdrawals/{withdrawal.Id}", withdrawal);
        });

        group.MapPut("/{id:int}", async (int id, Withdrawal input, AppDbContext db) =>
        {
            var withdrawal = await db.Withdrawals.FindAsync(id);
            if (withdrawal is null) return Results.NotFound();

            withdrawal.Amount = input.Amount;
            withdrawal.Date = input.Date;
            withdrawal.Description = input.Description;
            withdrawal.BankAccountId = input.BankAccountId;

            await db.SaveChangesAsync();
            return Results.Ok(withdrawal);
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var withdrawal = await db.Withdrawals.FindAsync(id);
            if (withdrawal is null) return Results.NotFound();

            db.Withdrawals.Remove(withdrawal);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    /// <summary>列出日期篩選結果並以 TWD 計算完整範圍的提款摘要。</summary>
    public static async Task<WithdrawalListResponse> ListWithdrawalsAsync(
        DateOnly? startDate,
        DateOnly? endDate,
        int? page,
        int? pageSize,
        AppDbContext db,
        IExchangeRateService? exchangeRateService = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.Withdrawals
            .Include(withdrawal => withdrawal.BankAccount)
            .AsQueryable();
        if (startDate.HasValue)
            query = query.Where(withdrawal => withdrawal.Date >= startDate.Value);
        if (endDate.HasValue)
            query = query.Where(withdrawal => withdrawal.Date <= endDate.Value);

        var allWithdrawals = await query
            .OrderByDescending(withdrawal => withdrawal.Date)
            .ThenByDescending(withdrawal => withdrawal.Id)
            .ToListAsync();
        var exchangeRateSnapshot = await ExchangeRateSnapshotResolver.ResolveForAccountsAsync(
            allWithdrawals.Select(withdrawal => withdrawal.BankAccount),
            exchangeRateService);
        var convertedAmounts = allWithdrawals
            .Select(withdrawal => ConvertWithdrawalAmount(withdrawal, exchangeRateService, exchangeRateSnapshot))
            .ToList();
        var count = allWithdrawals.Count;
        var totalAmount = convertedAmounts.Sum();
        var maxAmount = convertedAmounts.Count == 0 ? 0m : convertedAmounts.Max();
        var averageAmount = count == 0 ? 0m : totalAmount / count;
        var p = PaginationPolicy.NormalizePage(page);
        var ps = PaginationPolicy.NormalizePageSize(pageSize);
        var items = allWithdrawals
            .Skip((p - 1) * ps)
            .Take(ps)
            .ToList();

        return new WithdrawalListResponse(
            items,
            count,
            p,
            ps,
            new WithdrawalListSummary(
                totalAmount,
                count,
                averageAmount,
                maxAmount,
                CurrencyPolicy.BaseCurrencyCode,
                exchangeRateSnapshot.UpdatedAtUtc == DateTime.UnixEpoch ? null : exchangeRateSnapshot.UpdatedAtUtc,
                exchangeRateSnapshot.IsStale,
                true,
                totalAmount));
    }

    /// <summary>以提款關聯帳戶幣別將單筆提款換算為 TWD。</summary>
    private static decimal ConvertWithdrawalAmount(
        Withdrawal withdrawal,
        IExchangeRateService? exchangeRateService,
        ExchangeRateSnapshot exchangeRateSnapshot)
    {
        var currencyCode = CurrencyPolicy.NormalizeOrDefault(withdrawal.BankAccount.CurrencyCode);
        if (currencyCode == CurrencyPolicy.BaseCurrencyCode)
            return withdrawal.Amount;
        if (exchangeRateService is null)
            throw new ExchangeRateUnavailableException("存在外幣提款但未設定匯率服務");

        return exchangeRateService.ConvertToBase(
                   withdrawal.Amount,
                   currencyCode,
                   exchangeRateSnapshot)
               ?? throw new ExchangeRateUnavailableException($"缺少 {currencyCode} 匯率，無法產生提款摘要");
    }
}

public sealed record WithdrawalListResponse(
    IReadOnlyList<Withdrawal> Items,
    int Total,
    int Page,
    int PageSize,
    WithdrawalListSummary Summary);

public sealed record WithdrawalListSummary(
    decimal TotalAmount,
    int Count,
    decimal AverageAmount,
    decimal MaxAmount,
    string BaseCurrency,
    DateTime? ExchangeRateUpdatedAt,
    bool ExchangeRateIsStale,
    bool ConversionAvailable,
    decimal TotalAmountInBaseCurrency);
