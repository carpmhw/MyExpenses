using System.Net;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Models.Requests;
using MyExpenses.Api.Models.Responses;

namespace MyExpenses.Api.Services;

/// <summary>Coordinates atomic installment financial commands against one database context.</summary>
public sealed class InstallmentCommandService
{
    private const string InstallmentPurchaseOperation = "installment-purchase";
    private const string StandaloneInstallmentOperation = "standalone-installment";
    private static readonly SemaphoreSlim CreateCommandGate = new(1, 1);

    private readonly AppDbContext _db;
    private readonly TimeZoneService _timeZoneService;

    /// <summary>Creates a command service backed by the supplied database context.</summary>
    public InstallmentCommandService(AppDbContext db, TimeZoneService timeZoneService)
    {
        _db = db;
        _timeZoneService = timeZoneService;
    }

    /// <summary>Creates a transaction, installment, payment schedule, and receipt atomically.</summary>
    public async Task<InstallmentPurchaseResponse> CreateInstallmentPurchaseAsync(
        InstallmentPurchaseRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw ValidationError("分期消費資料不可為空");

        await CreateCommandGate.WaitAsync(cancellationToken);
        try
        {
            return await CreateInstallmentPurchaseCoreAsync(request, idempotencyKey, cancellationToken);
        }
        finally
        {
            CreateCommandGate.Release();
        }
    }

    /// <summary>Executes the composite purchase while the in-process command gate is held.</summary>
    private async Task<InstallmentPurchaseResponse> CreateInstallmentPurchaseCoreAsync(
        InstallmentPurchaseRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = NormalizeIdempotencyKey(idempotencyKey);
        var normalized = NormalizePurchaseRequest(request);
        var hash = IdempotencyRequestHasher.Compute(normalized);
        var existing = await FindReceiptAsync(key, InstallmentPurchaseOperation, hash, cancellationToken);
        if (existing is not null)
            return await LoadPurchaseResponseAsync(existing, cancellationToken);

        var transactionRequest = request.Transaction ?? throw ValidationError("交易資料不可為空");
        var installmentRequest = request.Installment ?? throw ValidationError("分期資料不可為空");
        var category = await ResolveCategoryAsync(transactionRequest, cancellationToken);
        if (transactionRequest.Type.HasValue && transactionRequest.Type != TransactionType.Expense)
            throw SemanticError("分期消費只能建立支出交易");

        var paymentMethod = await ResolvePaymentMethodAsync(transactionRequest, cancellationToken);
        InstallmentCommandValidator.ValidateCreditCardPaymentMethod(paymentMethod);

        var card = await _db.CreditCards.FirstOrDefaultAsync(
            item => item.Id == installmentRequest.CardId,
            cancellationToken);
        if (card is null)
            throw NotFoundError("信用卡不存在");

        var purchaseDate = transactionRequest.Date ?? _timeZoneService.GetLocalDate();
        InstallmentCommandValidator.ValidateSchedule(transactionRequest.Amount, installmentRequest.Periods, purchaseDate);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            existing = await FindReceiptAsync(key, InstallmentPurchaseOperation, hash, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return await LoadPurchaseResponseAsync(existing, cancellationToken);
            }

            var createdTransaction = new Transaction
            {
                Type = TransactionType.Expense,
                Amount = transactionRequest.Amount,
                Date = purchaseDate,
                Description = transactionRequest.Description,
                Notes = transactionRequest.Notes,
                CategoryId = category.Id,
                PaymentMethodId = paymentMethod.Id,
            };
            _db.Transactions.Add(createdTransaction);
            await _db.SaveChangesAsync(cancellationToken);

            var installment = new Installment
            {
                TransactionId = createdTransaction.Id,
                CardId = card.Id,
                TotalAmount = createdTransaction.Amount,
                Periods = installmentRequest.Periods,
                PerPeriod = InstallmentScheduleCalculator.CalculateAmounts(
                    createdTransaction.Amount,
                    installmentRequest.Periods)[0],
                PurchaseDate = createdTransaction.Date,
                Description = createdTransaction.Description,
                CreatedAt = DateTime.UtcNow,
            };
            _db.Installments.Add(installment);
            await _db.SaveChangesAsync(cancellationToken);

            var payments = BuildPayments(installment, card);
            _db.InstallmentPayments.AddRange(payments);
            await _db.SaveChangesAsync(cancellationToken);

            _db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = key,
                Operation = InstallmentPurchaseOperation,
                RequestHash = hash,
                TransactionId = createdTransaction.Id,
                InstallmentId = installment.Id,
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return await LoadPurchaseResponseAsync(
                createdTransaction.Id,
                installment.Id,
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            existing = await FindReceiptAsync(key, InstallmentPurchaseOperation, hash, cancellationToken);
            if (existing is not null)
                return await LoadPurchaseResponseAsync(existing, cancellationToken);

            throw ConflictError("分期消費無法完成，請稍後重試");
        }
    }

    /// <summary>Creates a standalone installment and its complete payment schedule atomically.</summary>
    public async Task<InstallmentCommandResponse> CreateStandaloneInstallmentAsync(
        CreateStandaloneInstallmentRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw ValidationError("分期資料不可為空");

        await CreateCommandGate.WaitAsync(cancellationToken);
        try
        {
            return await CreateStandaloneInstallmentCoreAsync(request, idempotencyKey, cancellationToken);
        }
        finally
        {
            CreateCommandGate.Release();
        }
    }

    /// <summary>Executes standalone installment creation while the in-process command gate is held.</summary>
    private async Task<InstallmentCommandResponse> CreateStandaloneInstallmentCoreAsync(
        CreateStandaloneInstallmentRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = NormalizeIdempotencyKey(idempotencyKey);
        var normalized = NormalizeStandaloneRequest(request);
        var hash = IdempotencyRequestHasher.Compute(normalized);
        var existing = await FindReceiptAsync(key, StandaloneInstallmentOperation, hash, cancellationToken);
        if (existing is not null)
            return await LoadInstallmentAsync(existing.InstallmentId, cancellationToken);

        InstallmentCommandValidator.ValidateSchedule(request.TotalAmount, request.Periods, request.PurchaseDate);
        if (!request.CardId.HasValue)
            throw SemanticError("請選擇信用卡");

        var card = await _db.CreditCards.FirstOrDefaultAsync(
            item => item.Id == request.CardId.Value,
            cancellationToken);
        if (card is null)
            throw NotFoundError("信用卡不存在");

        if (request.TransactionId.HasValue && !await _db.Transactions.AnyAsync(
                item => item.Id == request.TransactionId.Value,
                cancellationToken))
        {
            throw NotFoundError("關聯交易不存在");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            existing = await FindReceiptAsync(key, StandaloneInstallmentOperation, hash, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return await LoadInstallmentAsync(existing.InstallmentId, cancellationToken);
            }

            var installment = new Installment
            {
                TransactionId = request.TransactionId,
                CardId = card.Id,
                TotalAmount = request.TotalAmount,
                Periods = request.Periods,
                PerPeriod = InstallmentScheduleCalculator.CalculateAmounts(request.TotalAmount, request.Periods)[0],
                PurchaseDate = request.PurchaseDate,
                Description = request.Description,
                CreatedAt = DateTime.UtcNow,
            };
            _db.Installments.Add(installment);
            await _db.SaveChangesAsync(cancellationToken);

            _db.InstallmentPayments.AddRange(BuildPayments(installment, card));
            await _db.SaveChangesAsync(cancellationToken);

            _db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = key,
                Operation = StandaloneInstallmentOperation,
                RequestHash = hash,
                InstallmentId = installment.Id,
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return await LoadInstallmentAsync(installment.Id, cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            existing = await FindReceiptAsync(key, StandaloneInstallmentOperation, hash, cancellationToken);
            if (existing is not null)
                return await LoadInstallmentAsync(existing.InstallmentId, cancellationToken);

            throw ConflictError("分期無法完成，請稍後重試");
        }
    }

    /// <summary>Updates an unpaid installment schedule atomically and preserves paid periods.</summary>
    public async Task<InstallmentCommandResponse> UpdateInstallmentScheduleAsync(
        int id,
        UpdateInstallmentScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        var installment = await _db.Installments
            .Include(item => item.Payments.OrderBy(payment => payment.Period))
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (installment is null)
            throw NotFoundError("分期不存在");

        InstallmentCommandValidator.ValidateSchedule(request.TotalAmount, request.Periods, request.PurchaseDate);
        var paidPayments = installment.Payments.Where(payment => payment.IsPaid).ToList();
        if (paidPayments.Count > request.Periods)
            throw ValidationError("新的期數不可少於已繳期數");

        if (paidPayments.Count > 0 && HasPaidScheduleConflict(installment, request))
            throw ValidationError("已有繳款記錄，不可修改分期排程欄位");

        CreditCard? card = null;
        if (request.CardId.HasValue)
        {
            card = await _db.CreditCards.FirstOrDefaultAsync(
                item => item.Id == request.CardId.Value,
                cancellationToken);
            if (card is null)
                throw NotFoundError("信用卡不存在");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            installment.TotalAmount = request.TotalAmount;
            installment.Periods = request.Periods;
            installment.PerPeriod = InstallmentScheduleCalculator.CalculateAmounts(
                request.TotalAmount,
                request.Periods)[0];
            installment.PurchaseDate = request.PurchaseDate;
            installment.Description = request.Description;
            installment.CardId = request.CardId;

            var unpaidPayments = installment.Payments.Where(payment => !payment.IsPaid).ToList();
            _db.InstallmentPayments.RemoveRange(unpaidPayments);
            await _db.SaveChangesAsync(cancellationToken);

            var unpaidTotal = request.TotalAmount - paidPayments.Sum(payment => payment.Amount);
            if (unpaidTotal < 0)
                throw ValidationError("新的總金額不可小於已繳金額");

            var remainingPeriods = request.Periods - paidPayments.Count;
            if (remainingPeriods > 0)
            {
                var amounts = remainingPeriods == 1
                    ? new[] { unpaidTotal }
                    : InstallmentScheduleCalculator.CalculateAmounts(unpaidTotal, remainingPeriods).ToArray();
                for (var index = 0; index < remainingPeriods; index++)
                {
                    var period = paidPayments.Count + index + 1;
                    _db.InstallmentPayments.Add(new InstallmentPayment
                    {
                        InstallmentId = id,
                        Period = period,
                        Amount = amounts[index],
                        DueDate = card is null
                            ? null
                            : InstallmentScheduleCalculator.CalculateDueDate(
                                request.PurchaseDate,
                                card.StatementDay,
                                card.DueDay,
                                period),
                    });
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await LoadInstallmentAsync(id, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>Sets a payment target state and returns the refreshed derived installment summary.</summary>
    public async Task<InstallmentCommandResponse> SetInstallmentPaymentStateAsync(
        int installmentId,
        int paymentId,
        SetInstallmentPaymentStateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.IsPaid is null)
            throw ValidationError("請指定付款狀態");

        var payment = await _db.InstallmentPayments.FirstOrDefaultAsync(
            item => item.Id == paymentId && item.InstallmentId == installmentId,
            cancellationToken);
        if (payment is null)
            throw NotFoundError("付款期數不存在");

        try
        {
            InstallmentPaymentMarker.SetPaidState(payment, request.IsPaid.Value, request.PaidDate);
        }
        catch (ArgumentException exception)
        {
            throw ValidationError(exception.Message);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return await LoadInstallmentAsync(installmentId, cancellationToken);
    }

    /// <summary>Returns the canonical installment purchase payload used for idempotency hashing.</summary>
    private object NormalizePurchaseRequest(InstallmentPurchaseRequest request)
    {
        var transaction = request.Transaction ?? new CreateTransactionRequest();
        return new
        {
            transaction = new
            {
                type = TransactionType.Expense,
                amount = transaction.Amount,
                date = (transaction.Date ?? _timeZoneService.GetLocalDate()).ToString("yyyy-MM-dd"),
                description = transaction.Description?.Trim(),
                notes = transaction.Notes?.Trim(),
                categoryId = transaction.CategoryId,
                categoryCode = transaction.CategoryCode?.Trim(),
                category = transaction.Category?.Trim(),
                paymentMethodId = transaction.PaymentMethodId,
                paymentMethodCode = transaction.PaymentMethodCode?.Trim(),
                paymentMethod = transaction.PaymentMethod?.Trim(),
            },
            installment = new
            {
                cardId = request.Installment?.CardId ?? 0,
                periods = request.Installment?.Periods ?? 0,
            },
        };
    }

    /// <summary>Returns the canonical standalone installment payload used for idempotency hashing.</summary>
    private static object NormalizeStandaloneRequest(CreateStandaloneInstallmentRequest request)
        => new
        {
            transactionId = request.TransactionId,
            cardId = request.CardId,
            totalAmount = request.TotalAmount,
            periods = request.Periods,
            purchaseDate = request.PurchaseDate.ToString("yyyy-MM-dd"),
            description = request.Description?.Trim(),
        };

    /// <summary>Builds the complete payment schedule for a persisted installment.</summary>
    private static IReadOnlyList<InstallmentPayment> BuildPayments(Installment installment, CreditCard card)
    {
        var amounts = InstallmentScheduleCalculator.CalculateAmounts(installment.TotalAmount, installment.Periods);
        return amounts.Select((amount, index) => new InstallmentPayment
        {
            InstallmentId = installment.Id,
            Period = index + 1,
            Amount = amount,
            IsPaid = false,
            DueDate = InstallmentScheduleCalculator.CalculateDueDate(
                installment.PurchaseDate,
                card.StatementDay,
                card.DueDay,
                index + 1),
        }).ToList();
    }

    /// <summary>Resolves the requested category and ensures it is present.</summary>
    private async Task<Category> ResolveCategoryAsync(
        CreateTransactionRequest request,
        CancellationToken cancellationToken)
    {
        Category? category = null;
        if (request.CategoryId.HasValue)
            category = await _db.Categories.FirstOrDefaultAsync(
                item => item.Id == request.CategoryId.Value,
                cancellationToken);
        else if (!string.IsNullOrWhiteSpace(request.CategoryCode))
            category = await _db.Categories.FirstOrDefaultAsync(
                item => item.SystemCode == request.CategoryCode.Trim(),
                cancellationToken);
        else if (!string.IsNullOrWhiteSpace(request.Category))
            category = await _db.Categories.FirstOrDefaultAsync(
                item => item.Name == request.Category.Trim(),
                cancellationToken);

        if (category is null)
            throw NotFoundError("分類不存在");
        InstallmentCommandValidator.ValidateExpenseCategory(category);

        return category;
    }

    /// <summary>Resolves the requested payment method and ensures it is present.</summary>
    private async Task<PaymentMethod> ResolvePaymentMethodAsync(
        CreateTransactionRequest request,
        CancellationToken cancellationToken)
    {
        PaymentMethod? paymentMethod = null;
        if (request.PaymentMethodId.HasValue)
            paymentMethod = await _db.PaymentMethods.FirstOrDefaultAsync(
                item => item.Id == request.PaymentMethodId.Value,
                cancellationToken);
        else if (!string.IsNullOrWhiteSpace(request.PaymentMethodCode))
            paymentMethod = await _db.PaymentMethods.FirstOrDefaultAsync(
                item => item.SystemCode == request.PaymentMethodCode.Trim(),
                cancellationToken);
        else if (!string.IsNullOrWhiteSpace(request.PaymentMethod))
            paymentMethod = await _db.PaymentMethods.FirstOrDefaultAsync(
                item => item.Name == request.PaymentMethod.Trim(),
                cancellationToken);

        if (paymentMethod is null)
            throw NotFoundError("支付方式不存在");

        return paymentMethod;
    }

    /// <summary>Determines whether a paid installment forbids the requested schedule changes.</summary>
    private static bool HasPaidScheduleConflict(
        Installment installment,
        UpdateInstallmentScheduleRequest request)
        => request.TotalAmount != installment.TotalAmount
            || request.Periods != installment.Periods
            || request.CardId != installment.CardId
            || request.PurchaseDate != installment.PurchaseDate;

    /// <summary>Normalizes and validates a client idempotency key.</summary>
    private static string NormalizeIdempotencyKey(string? idempotencyKey)
    {
        if (!Guid.TryParse(idempotencyKey, out var key))
            throw ValidationError("Idempotency-Key 必須為有效 UUID");
        return key.ToString("D");
    }

    /// <summary>Finds a receipt and verifies that its operation and payload match the retry.</summary>
    private async Task<IdempotencyRecord?> FindReceiptAsync(
        string key,
        string operation,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var receipt = await _db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(
            item => item.Key == key,
            cancellationToken);
        if (receipt is null)
            return null;
        if (receipt.Operation != operation || receipt.RequestHash != requestHash)
            throw ConflictError("Idempotency-Key 已用於不同的請求");
        return receipt;
    }

    /// <summary>Loads a canonical composite response from persisted identifiers.</summary>
    private async Task<InstallmentPurchaseResponse> LoadPurchaseResponseAsync(
        IdempotencyRecord receipt,
        CancellationToken cancellationToken)
    {
        if (!receipt.TransactionId.HasValue || !receipt.InstallmentId.HasValue)
            throw ConflictError("Idempotency receipt 缺少結果識別碼");
        return await LoadPurchaseResponseAsync(receipt.TransactionId.Value, receipt.InstallmentId.Value, cancellationToken);
    }

    /// <summary>Loads a canonical composite response after a successful transaction.</summary>
    private async Task<InstallmentPurchaseResponse> LoadPurchaseResponseAsync(
        int transactionId,
        int installmentId,
        CancellationToken cancellationToken)
    {
        var transaction = await _db.Transactions
            .Include(item => item.Category)
            .Include(item => item.PaymentMethod)
            .FirstAsync(item => item.Id == transactionId, cancellationToken);
        var installment = await LoadInstallmentAsync(installmentId, cancellationToken);
        return new InstallmentPurchaseResponse(ToTransactionResponse(transaction), installment);
    }

    /// <summary>Loads an installment and maps its current aggregate to a cycle-free response.</summary>
    private async Task<InstallmentCommandResponse> LoadInstallmentAsync(int? installmentId, CancellationToken cancellationToken)
    {
        if (!installmentId.HasValue)
            throw ConflictError("Idempotency receipt 缺少分期識別碼");
        var installment = await _db.Installments
            .Include(item => item.Transaction).ThenInclude(item => item!.Category)
            .Include(item => item.Transaction).ThenInclude(item => item!.PaymentMethod)
            .Include(item => item.Card)
            .Include(item => item.Payments.OrderBy(payment => payment.Period))
            .FirstAsync(item => item.Id == installmentId.Value, cancellationToken);
        return ToInstallmentResponse(installment);
    }

    /// <summary>Maps a transaction entity without exposing reverse navigation collections.</summary>
    private static TransactionCommandResponse ToTransactionResponse(Transaction transaction)
        => new(
            transaction.Id,
            transaction.Type,
            transaction.Amount,
            transaction.Date,
            transaction.Description,
            transaction.Notes,
            transaction.CategoryId,
            transaction.PaymentMethodId,
            transaction.CreatedAt);

    /// <summary>Maps a credit-card entity without exposing its bill collection.</summary>
    private static CreditCardCommandResponse? ToCardResponse(CreditCard? card)
        => card is null
            ? null
            : new(
                card.Id,
                card.BankName,
                card.LastFourDigits,
                card.CardNetwork,
                card.StatementDay,
                card.DueDay,
                card.CreditLimit);

    /// <summary>Maps an installment entity and its payments without exposing parent navigation cycles.</summary>
    private static InstallmentCommandResponse ToInstallmentResponse(Installment installment)
        => new(
            installment.Id,
            installment.TransactionId,
            installment.CardId,
            installment.TotalAmount,
            installment.Periods,
            installment.PerPeriod,
            installment.RemainingPeriods,
            installment.PurchaseDate,
            installment.CreatedAt,
            installment.Status,
            installment.Description,
            installment.Transaction is null ? null : ToTransactionResponse(installment.Transaction),
            ToCardResponse(installment.Card),
            installment.Payments
                .OrderBy(payment => payment.Period)
                .Select(payment => new InstallmentPaymentCommandResponse(
                    payment.Id,
                    payment.InstallmentId,
                    payment.Period,
                    payment.Amount,
                    payment.PaidDate,
                    payment.IsPaid,
                    payment.DueDate))
                .ToList());

    /// <summary>Creates a validation failure for malformed financial input.</summary>
    private static FinancialCommandException ValidationError(string detail)
        => new((int)HttpStatusCode.BadRequest, "Invalid financial command", detail);

    /// <summary>Creates a not-found failure for a missing referenced resource.</summary>
    private static FinancialCommandException NotFoundError(string detail)
        => new((int)HttpStatusCode.NotFound, "Financial resource not found", detail);

    /// <summary>Creates an unprocessable-entity failure for incompatible financial data.</summary>
    private static FinancialCommandException SemanticError(string detail)
        => new((int)HttpStatusCode.UnprocessableEntity, "Invalid financial relationship", detail);

    /// <summary>Creates a conflict failure for an idempotency or persistence conflict.</summary>
    private static FinancialCommandException ConflictError(string detail)
        => new((int)HttpStatusCode.Conflict, "Financial command conflict", detail);
}
