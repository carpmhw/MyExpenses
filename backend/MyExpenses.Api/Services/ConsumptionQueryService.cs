using System.Net;
using System.Data;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>Builds the read-only cross-source consumption view used by agents.</summary>
public sealed class ConsumptionQueryService
{
    private const string RepaymentCategoryCode = "living";
    private const string RepaymentDescriptionMarker = "信用卡帳單";

    private readonly AppDbContext _db;
    private readonly TimeZoneService _timeZoneService;

    /// <summary>建立 consumption 查詢服務。</summary>
    public ConsumptionQueryService(AppDbContext db, TimeZoneService timeZoneService)
    {
        _db = db;
        _timeZoneService = timeZoneService;
    }

    /// <summary>查詢指定期間的完整消費集合、摘要與資料涵蓋範圍。</summary>
    public async Task<ConsumptionQueryResponse> QueryAsync(
        DateOnly? startDate,
        DateOnly? endDate,
        string? source,
        int? categoryId,
        string? search,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        var (validatedStartDate, validatedEndDate) = ValidateDateRange(startDate, endDate);
        var validatedSource = ValidateSource(source);
        var validatedPage = ValidatePage(page);
        var validatedPageSize = ValidatePageSize(pageSize);
        if (categoryId is <= 0)
            throw ValidationError("categoryId 必須是正整數");
        if (validatedSource == ConsumptionSource.CreditCard && categoryId.HasValue)
            throw ValidationError("信用卡消費沒有分類，不能搭配 categoryId 篩選");

        await using var snapshot = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var linkedInstallments = await _db.Installments
            .AsNoTracking()
            .Select(item => new { item.Id, item.TransactionId })
            .ToListAsync(cancellationToken);
        var linkedTransactionIds = linkedInstallments
            .Where(item => item.TransactionId.HasValue)
            .Select(item => item.TransactionId!.Value)
            .ToHashSet();
        var warnings = linkedInstallments
            .Where(item => item.TransactionId.HasValue)
            .GroupBy(item => item.TransactionId!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => $"Transaction {group.Key} is referenced by multiple installment records")
            .ToList();

        var ordinary = await _db.Transactions
            .AsNoTracking()
            .Include(item => item.Category)
            .Include(item => item.PaymentMethod)
            .Where(item => item.Type == TransactionType.Expense)
            .Where(item => !linkedTransactionIds.Contains(item.Id))
            .Where(item => item.Date >= validatedStartDate && item.Date <= validatedEndDate)
            .ToListAsync(cancellationToken);
        ordinary = ordinary.Where(item => !IsCreditCardRepayment(item)).ToList();

        var credit = await _db.Installments
            .AsNoTracking()
            .Include(item => item.Card)
            .Where(item => item.PurchaseDate >= validatedStartDate && item.PurchaseDate <= validatedEndDate)
            .ToListAsync(cancellationToken);

        IEnumerable<ConsumptionItem> items = ordinary.Select(ToOrdinaryItem);
        if (categoryId.HasValue)
            items = items.Where(item => item.CategoryId == categoryId.Value);
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
            items = items.Where(item => ContainsText(item.Description, normalizedSearch)
                || ContainsText(item.Notes, normalizedSearch));
        if (validatedSource == ConsumptionSource.CreditCard)
            items = Enumerable.Empty<ConsumptionItem>();

        var creditItems = credit.Select(ToCreditCardItem);
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
            creditItems = creditItems.Where(item => ContainsText(item.Description, normalizedSearch));
        if (validatedSource == ConsumptionSource.Ordinary || categoryId.HasValue)
            creditItems = Enumerable.Empty<ConsumptionItem>();

        var allItems = items
            .Concat(creditItems)
            .OrderByDescending(item => item.Date)
            .ThenBy(item => item.SourceType, StringComparer.Ordinal)
            .ThenByDescending(item => item.SourceId)
            .ToList();
        var summary = new ConsumptionSummary(
            allItems.Sum(item => item.Amount),
            allItems.Where(item => item.SourceType == SourceName(ConsumptionSource.Ordinary)).Sum(item => item.Amount),
            allItems.Where(item => item.SourceType == SourceName(ConsumptionSource.CreditCard)).Sum(item => item.Amount),
            allItems.Count);
        var offset = (long)(validatedPage - 1) * validatedPageSize;
        var pagedItems = allItems
            .Skip((int)Math.Min(offset, allItems.Count))
            .Take(validatedPageSize)
            .ToList();

        await snapshot.CommitAsync(cancellationToken);

        return new ConsumptionQueryResponse(
            pagedItems,
            allItems.Count,
            validatedPage,
            validatedPageSize,
            "consumption",
            new ConsumptionPeriod(validatedStartDate, validatedEndDate),
            _timeZoneService.TimeZoneId,
            new ConsumptionFilters(
                SourceName(validatedSource),
                categoryId,
                normalizedSearch),
            summary,
            new ConsumptionCoverage(
                false,
                "信用卡消費目前沒有分類；分類篩選只涵蓋普通交易",
                "依消費日期認列信用卡消費總額，並排除生活分類且描述含信用卡帳單的繳款",
                "只涵蓋資料庫中已記錄且符合條件的消費"),
            warnings);
    }

    /// <summary>判斷普通支出是否為信用卡帳單繳款。</summary>
    private static bool IsCreditCardRepayment(Transaction transaction)
        => transaction.Type == TransactionType.Expense
            && transaction.Category.SystemCode == RepaymentCategoryCode
            && transaction.Description?.Contains(RepaymentDescriptionMarker, StringComparison.Ordinal) == true;

    /// <summary>將普通交易轉為來源可辨識的 consumption item。</summary>
    private static ConsumptionItem ToOrdinaryItem(Transaction transaction)
        => new(
            SourceName(ConsumptionSource.Ordinary),
            transaction.Id,
            transaction.Date,
            transaction.Amount,
            transaction.Description,
            transaction.Notes,
            transaction.CategoryId,
            transaction.Category.Name,
            transaction.Category.SystemCode,
            transaction.PaymentMethod?.Id,
            transaction.PaymentMethod?.Name,
            null,
            transaction.Id);

    /// <summary>將信用卡分期轉為以購買日及總額認列的 consumption item。</summary>
    private static ConsumptionItem ToCreditCardItem(Installment installment)
        => new(
            SourceName(ConsumptionSource.CreditCard),
            installment.Id,
            installment.PurchaseDate,
            installment.TotalAmount,
            installment.Description,
            null,
            null,
            null,
            null,
            null,
            null,
            installment.Card is null
                ? null
                : new CreditCardConsumptionCard(
                    installment.Card.Id,
                    installment.Card.BankName,
                    installment.Card.LastFourDigits),
            installment.TransactionId);

    /// <summary>判斷字串是否包含查詢文字。</summary>
    private static bool ContainsText(string? value, string search)
        => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>驗證並正規化消費查詢日期範圍。</summary>
    private static (DateOnly StartDate, DateOnly EndDate) ValidateDateRange(DateOnly? startDate, DateOnly? endDate)
    {
        if (!startDate.HasValue || !endDate.HasValue)
            throw ValidationError("startDate 與 endDate 為必填欄位");
        if (endDate.Value < startDate.Value)
            throw ValidationError("endDate 不可早於 startDate");
        return (startDate.Value, endDate.Value);
    }

    /// <summary>驗證 consumption 查詢來源參數。</summary>
    private static ConsumptionSource ValidateSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return ConsumptionSource.All;

        return source.Trim().ToLowerInvariant() switch
        {
            "all" => ConsumptionSource.All,
            "ordinary" => ConsumptionSource.Ordinary,
            "credit_card" => ConsumptionSource.CreditCard,
            _ => throw ValidationError("source 必須是 all、ordinary 或 credit_card"),
        };
    }

    /// <summary>將來源列舉轉為公開 API 契約使用的 snake_case 名稱。</summary>
    private static string SourceName(ConsumptionSource source)
        => source switch
        {
            ConsumptionSource.All => "all",
            ConsumptionSource.Ordinary => "ordinary",
            ConsumptionSource.CreditCard => "credit_card",
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

    /// <summary>驗證 consumption 查詢頁碼。</summary>
    private static int ValidatePage(int? page)
    {
        if (page is <= 0)
            throw ValidationError("page 必須是正整數");
        return page ?? 1;
    }

    /// <summary>驗證 consumption 查詢頁面大小。</summary>
    private static int ValidatePageSize(int? pageSize)
    {
        if (pageSize is <= 0 or > 100)
            throw ValidationError("pageSize 必須介於 1 與 100 之間");
        return pageSize ?? 20;
    }

    /// <summary>建立 consumption 查詢參數錯誤。</summary>
    private static FinancialCommandException ValidationError(string detail)
        => new((int)HttpStatusCode.BadRequest, "Invalid consumption query", detail);
}

/// <summary>Consumption 查詢可用的資料來源。</summary>
public enum ConsumptionSource
{
    All,
    Ordinary,
    CreditCard,
}

/// <summary>跨來源 consumption 查詢回應。</summary>
public sealed record ConsumptionQueryResponse(
    IReadOnlyList<ConsumptionItem> Items,
    int Total,
    int Page,
    int PageSize,
    string Basis,
    ConsumptionPeriod Period,
    string TimeZoneId,
    ConsumptionFilters Filters,
    ConsumptionSummary Summary,
    ConsumptionCoverage Coverage,
    IReadOnlyList<string> Warnings);

/// <summary>Consumption 查詢中的單筆來源資料。</summary>
public sealed record ConsumptionItem(
    string SourceType,
    int SourceId,
    DateOnly Date,
    decimal Amount,
    string? Description,
    string? Notes,
    int? CategoryId,
    string? CategoryName,
    string? CategorySystemCode,
    int? PaymentMethodId,
    string? PaymentMethodName,
    CreditCardConsumptionCard? Card,
    int? TransactionId);

/// <summary>Consumption 查詢的日期區間。</summary>
public sealed record ConsumptionPeriod(DateOnly StartDate, DateOnly EndDate);

/// <summary>Consumption 查詢實際套用的篩選條件。</summary>
public sealed record ConsumptionFilters(string Source, int? CategoryId, string? Search);

/// <summary>Consumption 查詢完整結果集合的摘要。</summary>
public sealed record ConsumptionSummary(
    decimal TotalAmount,
    decimal OrdinaryAmount,
    decimal CreditCardAmount,
    int Count);

/// <summary>Consumption 查詢的資料涵蓋與會計口徑說明。</summary>
public sealed record ConsumptionCoverage(
    bool CreditCardCategoriesAvailable,
    string CategoryNote,
    string RecognitionNote,
    string CompletenessNote);

/// <summary>Consumption 查詢中的信用卡識別資訊。</summary>
public sealed record CreditCardConsumptionCard(int Id, string BankName, string LastFourDigits);
