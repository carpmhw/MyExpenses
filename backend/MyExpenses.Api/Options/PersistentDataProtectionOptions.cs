namespace MyExpenses.Api.Options;

/// <summary>定義 Production Data Protection 的穩定 discriminator 與持久化 key directory。</summary>
public sealed class PersistentDataProtectionOptions
{
    /// <summary>取得設定區段名稱。</summary>
    public const string SectionName = "DataProtection";

    /// <summary>取得跨 application recreation 必須維持不變的 application name。</summary>
    public string ApplicationName { get; init; } = "MyExpenses";

    /// <summary>取得保存 Data Protection key ring 的目錄。</summary>
    public string KeyDirectory { get; init; } = string.Empty;
}
