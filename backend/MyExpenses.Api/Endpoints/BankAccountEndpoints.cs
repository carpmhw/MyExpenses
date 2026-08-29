using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

/// <summary>提供銀行帳戶 CRUD 與 TWD 基準列表估值端點。</summary>
public static class BankAccountEndpoints
{
    /// <summary>註冊銀行帳戶查詢、建立、更新與刪除端點。</summary>
    public static void MapBankAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/bank-accounts");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            string? bankName,
            AppDbContext db,
            [FromServices] IExchangeRateService exchangeRateService) =>
            Results.Ok(await ListBankAccountsAsync(page, pageSize, bankName, db, exchangeRateService)));

        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
            await db.BankAccounts.FindAsync(id) is BankAccount account
                ? Results.Ok(account)
                : Results.NotFound());

        group.MapPost("/", async (BankAccount account, AppDbContext db) =>
        {
            if (!TryValidateAccount(account, out var error))
                return Results.BadRequest(new { error });

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
            if (account is null)
                return Results.NotFound();
            if (!TryValidateAccount(input, out var error))
                return Results.BadRequest(new { error });

            account.BankName = input.BankName;
            account.AccountNumber = input.AccountNumber;
            account.Balance = input.Balance;
            account.AccountType = input.AccountType;
            account.CurrencyCode = input.CurrencyCode;
            account.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            return Results.Ok(account);
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var account = await db.BankAccounts.FindAsync(id);
            if (account is null)
                return Results.NotFound();

            db.BankAccounts.Remove(account);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    /// <summary>列出篩選結果並以 TWD 計算不受分頁限制的總額。</summary>
    public static async Task<BankAccountListResponse> ListBankAccountsAsync(
        int? page,
        int? pageSize,
        string? bankName,
        AppDbContext db,
        IExchangeRateService? exchangeRateService = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.BankAccounts.AsNoTracking().AsQueryable();
        var trimmedBankName = bankName?.Trim();
        if (!string.IsNullOrEmpty(trimmedBankName))
            query = query.Where(account => account.BankName.Contains(trimmedBankName));

        var rows = await query
            .Select(account => new BankAccountListProjection(
                account.Id,
                account.BankName,
                account.AccountNumber,
                account.Balance,
                account.AccountType,
                account.CurrencyCode,
                account.CreatedAt,
                account.UpdatedAt))
            .ToListAsync();
        var total = rows.Count;
        var p = PaginationPolicy.NormalizePage(page);
        var ps = PaginationPolicy.NormalizePageSize(pageSize);
        var orderedRows = rows
            .OrderByDescending(account => account.CreatedAt)
            .ThenByDescending(account => account.Id)
            .ToList();

        var requiresExchangeRate = orderedRows.Any(account =>
            CurrencyPolicy.NormalizeOrDefault(account.CurrencyCode) != CurrencyPolicy.BaseCurrencyCode);
        ExchangeRateSnapshot? exchangeRateSnapshot = null;
        if (requiresExchangeRate && exchangeRateService is not null)
        {
            try
            {
                exchangeRateSnapshot = await exchangeRateService.GetSnapshotAsync();
            }
            catch (ExchangeRateUnavailableException)
            {
                exchangeRateSnapshot = null;
            }
        }

        var valuedRows = orderedRows
            .Select(account => ValueAccount(account, exchangeRateService, exchangeRateSnapshot))
            .ToList();
        var conversionAvailable = !requiresExchangeRate || valuedRows.All(row => row.ConvertedBalance.HasValue);
        decimal? totalBalanceInBaseCurrency = conversionAvailable
            ? valuedRows.Sum(row => row.ConvertedBalance ?? 0m)
            : null;
        var items = valuedRows
            .Skip((p - 1) * ps)
            .Take(ps)
            .Select(row => row.Item)
            .ToList();

        return new BankAccountListResponse(
            items,
            total,
            p,
            ps,
            CurrencyPolicy.BaseCurrencyCode,
            totalBalanceInBaseCurrency,
            exchangeRateSnapshot?.UpdatedAtUtc,
            exchangeRateSnapshot?.IsStale ?? false,
            conversionAvailable);
    }

    /// <summary>正規化並驗證銀行帳戶輸入的欄位與支援貨幣。</summary>
    private static bool TryValidateAccount(BankAccount account, out string? error)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!CurrencyPolicy.TryNormalize(account.CurrencyCode, out var normalizedCurrencyCode))
        {
            error = "不支援的貨幣代碼";
            return false;
        }

        account.CurrencyCode = normalizedCurrencyCode;
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(
                account,
                new ValidationContext(account),
                validationResults,
                validateAllProperties: true))
        {
            error = validationResults[0].ErrorMessage ?? "銀行帳戶資料無效";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>將單一帳戶映射為原幣與可用 TWD 折合值。</summary>
    private static ValuedBankAccount ValueAccount(
        BankAccountListProjection account,
        IExchangeRateService? exchangeRateService,
        ExchangeRateSnapshot? exchangeRateSnapshot)
    {
        var currencyCode = CurrencyPolicy.NormalizeOrDefault(account.CurrencyCode);
        var convertedBalance = currencyCode == CurrencyPolicy.BaseCurrencyCode
            ? account.Balance
            : exchangeRateService is not null && exchangeRateSnapshot is not null
                ? exchangeRateService.ConvertToBase(account.Balance, currencyCode, exchangeRateSnapshot)
                : null;
        return new ValuedBankAccount(
            new BankAccountListItem(
                account.Id,
                account.BankName,
                account.AccountNumber,
                account.Balance,
                account.AccountType,
                account.CreatedAt,
                account.UpdatedAt,
                currencyCode,
                convertedBalance),
            convertedBalance);
    }

    /// <summary>保存列表估值所需的最小資料庫投影。</summary>
    private sealed record BankAccountListProjection(
        int Id,
        string BankName,
        string AccountNumber,
        decimal Balance,
        string AccountType,
        string CurrencyCode,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    /// <summary>保存列表 item 與其換算結果的內部值。</summary>
    private sealed record ValuedBankAccount(
        BankAccountListItem Item,
        decimal? ConvertedBalance);
}

/// <summary>銀行帳戶列表中同時包含原幣與即時 TWD 估值的 item。</summary>
public sealed record BankAccountListItem(
    int Id,
    string BankName,
    string AccountNumber,
    decimal Balance,
    string AccountType,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string CurrencyCode,
    decimal? ConvertedBalance);

/// <summary>銀行帳戶列表 response 與完整篩選範圍的換算 metadata。</summary>
public sealed record BankAccountListResponse(
    IReadOnlyList<BankAccountListItem> Items,
    int Total,
    int Page,
    int PageSize,
    string BaseCurrency,
    decimal? TotalBalanceInBaseCurrency,
    DateTime? ExchangeRateUpdatedAt,
    bool ExchangeRateIsStale,
    bool ConversionAvailable);
