namespace MyExpenses.Api.Options;

/// <summary>定義 SQLite backup 的來源、目的地與 verified backup 保留數量。</summary>
public sealed class SqliteBackupOptions
{
    /// <summary>取得設定區段名稱，供未來 startup 或 operator command 綁定使用。</summary>
    public const string SectionName = "SqliteBackup";

    /// <summary>取得目前使用中的 SQLite database 檔案路徑。</summary>
    public string DatabasePath { get; init; } = "MyExpenses.db";

    /// <summary>取得 verified backup 的目的目錄。</summary>
    public string BackupDirectory { get; init; } = "backups";

    /// <summary>取得成功驗證後最多保留的 backup 數量。</summary>
    public int RetentionLimit { get; init; } = 7;
}
