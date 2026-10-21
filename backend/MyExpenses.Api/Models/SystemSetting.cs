namespace MyExpenses.Api.Models;

/// <summary>Stores the singleton settings used by the primary financial dataset.</summary>
public class SystemSetting
{
    /// <summary>The fixed key used for the singleton settings row.</summary>
    public const int SingletonId = 1;

    /// <summary>Gets or sets the singleton row identifier.</summary>
    public int Id { get; set; } = SingletonId;

    /// <summary>Gets or sets the persisted IANA system time zone identifier.</summary>
    public string TimeZoneId { get; set; } = "Asia/Taipei";

    /// <summary>取得或設定新增提款時預選的銀行帳戶，未設定時為空。</summary>
    public int? DefaultBankAccountId { get; set; }

    /// <summary>取得或設定新增信用卡交易時預選的卡片，未設定時為空。</summary>
    public int? DefaultCreditCardId { get; set; }
}
