namespace MyExpenses.Api.Options;

/// <summary>
/// 時區設定選項
/// </summary>
public class TimeZoneOptions
{
    public const string SectionName = "TimeZone";

    /// <summary>預設時區（IANA 格式，如 "Asia/Taipei"），當環境變數 TZ 未設定時使用</summary>
    public string Default { get; set; } = "Asia/Taipei";
}
