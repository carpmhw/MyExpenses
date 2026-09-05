using System.Net;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Models.Requests;

namespace MyExpenses.Api.Services;

/// <summary>Coordinates ordinary transaction creation with optional idempotency protection.</summary>
public sealed class TransactionCommandService
{
    private const string TransactionCreateOperation = "transaction.create";
    private static readonly ConditionalWeakTable<DbConnection, SemaphoreSlim> CreateCommandGates = new();
    private readonly AppDbContext _db;
    private readonly TimeZoneService _timeZoneService;

    /// <summary>建立普通交易命令服務。</summary>
    public TransactionCommandService(AppDbContext db, TimeZoneService timeZoneService)
    {
        _db = db;
        _timeZoneService = timeZoneService;
    }

    /// <summary>依是否提供冪等 key 選擇相容的舊路徑或受保護的 keyed 路徑。</summary>
    public async Task<TransactionCommandResult> CreateAsync(
        CreateTransactionRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw ValidationError("交易資料不可為空");

        return idempotencyKey is null
            ? await CreateLegacyAsync(request, cancellationToken)
            : await CreateIdempotentAsync(request, idempotencyKey, cancellationToken);
    }

    /// <summary>保留既有未帶 key 的普通交易新增行為。</summary>
    private async Task<TransactionCommandResult> CreateLegacyAsync(
        CreateTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var resolvedCategoryId = await ResolveLegacyCategoryIdAsync(request, cancellationToken);
        if (request.Type is null)
            throw ValidationError("Transaction type is required");
        if (resolvedCategoryId is null)
            throw ValidationError("Category is required");

        var resolvedPaymentMethodId = await ResolveLegacyPaymentMethodIdAsync(request, cancellationToken);
        var transaction = new Transaction
        {
            Type = request.Type.Value,
            Amount = request.Amount,
            Date = request.Date ?? _timeZoneService.GetLocalDate(),
            Description = request.Description,
            Notes = request.Notes,
            CategoryId = resolvedCategoryId.Value,
            PaymentMethodId = resolvedPaymentMethodId,
        };

        _db.Transactions.Add(transaction);
        await _db.SaveChangesAsync(cancellationToken);
        await LoadReferencesAsync(transaction, cancellationToken);
        return new TransactionCommandResult(transaction, false);
    }

    /// <summary>執行帶冪等 key 的普通交易新增或收據重播。</summary>
    private async Task<TransactionCommandResult> CreateIdempotentAsync(
        CreateTransactionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var commandGate = CreateCommandGates.GetValue(
            _db.Database.GetDbConnection(),
            static _ => new SemaphoreSlim(1, 1));
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            return await CreateIdempotentCoreAsync(request, idempotencyKey, cancellationToken);
        }
        finally
        {
            commandGate.Release();
        }
    }

    /// <summary>在普通交易命令閘門內執行收據核對與原子新增。</summary>
    private async Task<TransactionCommandResult> CreateIdempotentCoreAsync(
        CreateTransactionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = NormalizeIdempotencyKey(idempotencyKey);
        ValidateKeyedEnvelope(request);
        var normalized = NormalizeKeyedRequest(request);
        var hash = IdempotencyRequestHasher.Compute(normalized);

        var existing = await FindReceiptAsync(key, hash, cancellationToken);
        if (existing is not null)
            return new TransactionCommandResult(
                await LoadReceiptTransactionAsync(existing, cancellationToken),
                true);

        var category = await ResolveKeyedCategoryAsync(request, cancellationToken);
        var paymentMethod = await ResolveKeyedPaymentMethodAsync(request, cancellationToken);
        if (paymentMethod.SystemCode == "credit-card")
            throw SemanticError("信用卡消費請使用獨立信用卡交易流程");
        if (category.Type == CategoryType.Income && request.Type != TransactionType.Income
            || category.Type == CategoryType.Expense && request.Type != TransactionType.Expense)
        {
            throw SemanticError("交易類型與分類不相容");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            existing = await FindReceiptAsync(key, hash, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new TransactionCommandResult(
                    await LoadReceiptTransactionAsync(existing, cancellationToken),
                    true);
            }

            var created = new Transaction
            {
                Type = request.Type!.Value,
                Amount = request.Amount,
                Date = request.Date!.Value,
                Description = request.Description.Trim(),
                Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                CategoryId = category.Id,
                PaymentMethodId = paymentMethod.Id,
            };
            _db.Transactions.Add(created);
            await _db.SaveChangesAsync(cancellationToken);

            _db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = key,
                Operation = TransactionCreateOperation,
                RequestHash = hash,
                TransactionId = created.Id,
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await LoadReferencesAsync(created, cancellationToken);
            return new TransactionCommandResult(created, false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            existing = await FindReceiptAsync(key, hash, cancellationToken);
            if (existing is not null)
            {
                return new TransactionCommandResult(
                    await LoadReceiptTransactionAsync(existing, cancellationToken),
                    true);
            }

            throw ConflictError("交易無法完成，請稍後重試");
        }
    }

    /// <summary>驗證 keyed 命令需要固定日期、分類、付款方式與交易型別。</summary>
    private static void ValidateKeyedEnvelope(CreateTransactionRequest request)
    {
        if (!request.Date.HasValue)
            throw ValidationError("Idempotent transaction requires an explicit date");
        if (!request.CategoryId.HasValue)
            throw ValidationError("Idempotent transaction requires a category ID");
        if (!request.PaymentMethodId.HasValue)
            throw ValidationError("Idempotent transaction requires a payment method ID");
        if (!request.Type.HasValue)
            throw ValidationError("Transaction type is required");
        if (!Enum.IsDefined(request.Type.Value))
            throw ValidationError("Transaction type must be Income or Expense");
        if (request.Amount <= 0)
            throw ValidationError("Amount must be greater than 0");
        if (string.IsNullOrWhiteSpace(request.Description))
            throw ValidationError("Description is required");
    }

    /// <summary>建立使用固定識別碼的 keyed canonical payload。</summary>
    private static object NormalizeKeyedRequest(CreateTransactionRequest request)
        => new
        {
            type = request.Type!.Value,
            amount = request.Amount,
            date = request.Date!.Value.ToString("yyyy-MM-dd"),
            description = request.Description.Trim(),
            notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            categoryId = request.CategoryId!.Value,
            paymentMethodId = request.PaymentMethodId!.Value,
        };

    /// <summary>解析舊路徑支援的分類欄位。</summary>
    private async Task<int?> ResolveLegacyCategoryIdAsync(
        CreateTransactionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CategoryId.HasValue)
        {
            var exists = await _db.Categories.AnyAsync(
                category => category.Id == request.CategoryId.Value,
                cancellationToken);
            if (!exists)
                throw ValidationError($"CategoryId '{request.CategoryId}' not found");
            return request.CategoryId;
        }

        if (!string.IsNullOrEmpty(request.CategoryCode))
        {
            var category = await _db.Categories.FirstOrDefaultAsync(
                item => item.SystemCode == request.CategoryCode,
                cancellationToken);
            if (category is null)
                throw ValidationError($"CategoryCode '{request.CategoryCode}' not found");
            request.Type ??= category.Type == CategoryType.Income
                ? TransactionType.Income
                : TransactionType.Expense;
            return category.Id;
        }

        if (!string.IsNullOrEmpty(request.Category))
        {
            var category = await _db.Categories.FirstOrDefaultAsync(
                item => item.Name == request.Category,
                cancellationToken);
            if (category is null)
                throw ValidationError($"Category '{request.Category}' not found");
            request.Type ??= category.Type == CategoryType.Income
                ? TransactionType.Income
                : TransactionType.Expense;
            return category.Id;
        }

        return null;
    }

    /// <summary>保留舊路徑的顯式付款方式識別碼，僅略過找不到的名稱與代碼。</summary>
    private async Task<int?> ResolveLegacyPaymentMethodIdAsync(
        CreateTransactionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PaymentMethodId.HasValue)
            return request.PaymentMethodId;

        if (!string.IsNullOrEmpty(request.PaymentMethodCode))
        {
            var paymentMethod = await _db.PaymentMethods.FirstOrDefaultAsync(
                item => item.SystemCode == request.PaymentMethodCode,
                cancellationToken);
            if (paymentMethod is not null)
                return paymentMethod.Id;
        }

        if (!string.IsNullOrEmpty(request.PaymentMethod))
        {
            var paymentMethod = await _db.PaymentMethods.FirstOrDefaultAsync(
                item => item.Name == request.PaymentMethod,
                cancellationToken);
            if (paymentMethod is not null)
                return paymentMethod.Id;
        }

        return null;
    }

    /// <summary>依固定分類識別碼載入 keyed 命令的分類。</summary>
    private async Task<Category> ResolveKeyedCategoryAsync(
        CreateTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var category = await _db.Categories.FirstOrDefaultAsync(
            item => item.Id == request.CategoryId!.Value,
            cancellationToken);
        if (category is null)
            throw NotFoundError("分類不存在");
        return category;
    }

    /// <summary>依固定付款方式識別碼載入 keyed 命令的付款方式。</summary>
    private async Task<PaymentMethod> ResolveKeyedPaymentMethodAsync(
        CreateTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var paymentMethod = await _db.PaymentMethods.FirstOrDefaultAsync(
            item => item.Id == request.PaymentMethodId!.Value,
            cancellationToken);
        if (paymentMethod is null)
            throw NotFoundError("支付方式不存在");
        return paymentMethod;
    }

    /// <summary>載入交易的分類與付款方式導覽資料。</summary>
    private async Task LoadReferencesAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        await _db.Entry(transaction).Reference(item => item.Category).LoadAsync(cancellationToken);
        await _db.Entry(transaction).Reference(item => item.PaymentMethod).LoadAsync(cancellationToken);
    }

    /// <summary>正規化並驗證冪等 key。</summary>
    private static string NormalizeIdempotencyKey(string idempotencyKey)
    {
        if (!Guid.TryParse(idempotencyKey, out var key))
            throw ValidationError("Idempotency-Key 必須為有效 UUID");
        return key.ToString("D");
    }

    /// <summary>依 key 核對 operation 與 canonical payload hash，先處理已提交收據。</summary>
    private async Task<IdempotencyRecord?> FindReceiptAsync(
        string key,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var receipt = await _db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(
            item => item.Key == key,
            cancellationToken);
        if (receipt is null)
            return null;
        if (receipt.Operation != TransactionCreateOperation || receipt.RequestHash != requestHash)
            throw ConflictError("Idempotency-Key 已用於不同的請求");
        return receipt;
    }

    /// <summary>載入 receipt 指向的目前普通交易，刪除後回傳安全的 410。</summary>
    private async Task<Transaction> LoadReceiptTransactionAsync(
        IdempotencyRecord receipt,
        CancellationToken cancellationToken)
    {
        if (!receipt.TransactionId.HasValue)
            throw ConflictError("Idempotency receipt 缺少交易識別碼");

        var transaction = await _db.Transactions
            .Include(item => item.Category)
            .Include(item => item.PaymentMethod)
            .FirstOrDefaultAsync(item => item.Id == receipt.TransactionId.Value, cancellationToken);
        if (transaction is null)
            throw new FinancialCommandException(
                (int)HttpStatusCode.Gone,
                "Financial result unavailable",
                "Idempotent transaction result is no longer available",
                "result_unavailable");
        return transaction;
    }

    /// <summary>建立格式錯誤的財務命令例外。</summary>
    private static FinancialCommandException ValidationError(string detail)
        => new((int)HttpStatusCode.BadRequest, "Invalid financial command", detail);

    /// <summary>建立找不到參考資料的財務命令例外。</summary>
    private static FinancialCommandException NotFoundError(string detail)
        => new((int)HttpStatusCode.NotFound, "Financial resource not found", detail);

    /// <summary>建立交易語意不相容的財務命令例外。</summary>
    private static FinancialCommandException SemanticError(string detail)
        => new((int)HttpStatusCode.UnprocessableEntity, "Invalid financial relationship", detail);

    /// <summary>建立冪等或持久化衝突的財務命令例外。</summary>
    private static FinancialCommandException ConflictError(string detail)
        => new((int)HttpStatusCode.Conflict, "Financial command conflict", detail);
}

/// <summary>描述普通交易命令的 canonical 結果與是否為冪等重播。</summary>
public sealed record TransactionCommandResult(Transaction Transaction, bool Replayed);
