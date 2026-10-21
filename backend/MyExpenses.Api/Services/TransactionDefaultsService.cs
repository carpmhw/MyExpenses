using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>透過 scoped context 讀寫固定預設，保留其他設定與既有金融資料。</summary>
public sealed class TransactionDefaultsService(AppDbContext db)
{
    /// <summary>從伺服器讀取已初始化的 singleton 預設，不使用程序內快取。</summary>
    public Task<TransactionDefaults> ReadAsync(CancellationToken cancellationToken = default)
        => db.SystemSettings.AsNoTracking()
            .Where(setting => setting.Id == SystemSetting.SingletonId)
            .Select(setting => new TransactionDefaults(setting.DefaultBankAccountId, setting.DefaultCreditCardId))
            .SingleAsync(cancellationToken);

    /// <summary>指定銀行帳戶為預設，目標不存在時回傳空值且不改變設定。</summary>
    public Task<TransactionDefaults?> SetBankAccountAsync(int id, CancellationToken cancellationToken = default)
        => SetAsync(id, true, cancellationToken);

    /// <summary>指定信用卡為預設，目標不存在時回傳空值且不改變設定。</summary>
    public Task<TransactionDefaults?> SetCreditCardAsync(int id, CancellationToken cancellationToken = default)
        => SetAsync(id, false, cancellationToken);

    /// <summary>在同一交易中驗證目標並只保存指定類別，失敗時由交易回滾。</summary>
    private async Task<TransactionDefaults?> SetAsync(int id, bool bankAccount, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var exists = bankAccount
            ? await db.BankAccounts.AnyAsync(account => account.Id == id, cancellationToken)
            : await db.CreditCards.AnyAsync(card => card.Id == id, cancellationToken);
        if (!exists) return null;

        var setting = await db.SystemSettings.SingleAsync(item => item.Id == SystemSetting.SingletonId, cancellationToken);
        if (bankAccount)
        {
            setting.DefaultBankAccountId = id;
            db.Entry(setting).Property(item => item.DefaultBankAccountId).IsModified = true;
        }
        else
        {
            setting.DefaultCreditCardId = id;
            db.Entry(setting).Property(item => item.DefaultCreditCardId).IsModified = true;
        }
        await db.SaveChangesAsync(cancellationToken);
        var result = await ReadAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
