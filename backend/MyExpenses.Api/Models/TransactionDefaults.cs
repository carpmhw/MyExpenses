namespace MyExpenses.Api.Models;

/// <summary>保存新增提款與信用卡交易使用的兩個固定預設識別碼。</summary>
public sealed record TransactionDefaults(int? DefaultBankAccountId, int? DefaultCreditCardId);
