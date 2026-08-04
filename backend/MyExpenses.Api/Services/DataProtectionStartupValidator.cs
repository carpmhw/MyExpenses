using MyExpenses.Api.Options;

namespace MyExpenses.Api.Services;

/// <summary>在 Production 接受請求前驗證 Data Protection key directory 的讀寫能力與檔案權限。</summary>
public sealed class DataProtectionStartupValidator
{
    private readonly PersistentDataProtectionOptions _options;
    private readonly bool _isProduction;

    /// <summary>建立指定 Data Protection 設定與執行環境的 validator。</summary>
    public DataProtectionStartupValidator(
        PersistentDataProtectionOptions options,
        bool isProduction)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _isProduction = isProduction;
    }

    /// <summary>驗證 key directory 可建立、寫入、讀取，且既有 key 檔不會被其他使用者讀取。</summary>
    public void Validate()
    {
        if (!_isProduction)
            return;

        if (string.IsNullOrWhiteSpace(_options.KeyDirectory))
        {
            throw new InvalidOperationException(
                "DataProtection:KeyDirectory must be configured in Production.");
        }

        try
        {
            Directory.CreateDirectory(_options.KeyDirectory);
            ValidateExistingKeyFilePermissions();

            var probePath = Path.Combine(
                _options.KeyDirectory,
                $".myexpenses-dataprotection-probe-{Guid.NewGuid():N}");
            try
            {
                CreatePrivateProbeFile(probePath);
                using var stream = new FileStream(
                    probePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                _ = stream.ReadByte();
            }
            finally
            {
                DeleteProbeFile(probePath);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException(
                "Data Protection key directory must be readable and writable in Production.",
                exception);
        }
    }

    /// <summary>檢查既有 XML key 檔案不可含有 group 或 other 權限。</summary>
    private void ValidateExistingKeyFilePermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        foreach (var keyPath in Directory.EnumerateFiles(_options.KeyDirectory, "*.xml"))
        {
            var mode = File.GetUnixFileMode(keyPath);
            var sharedPermissions = UnixFileMode.GroupRead
                | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute;
            if ((mode & sharedPermissions) != UnixFileMode.None)
            {
                throw new InvalidOperationException(
                    "Data Protection key files must be private to the application identity.");
            }
        }
    }

    /// <summary>建立只允許 owner 讀寫的 startup probe，避免以權限不足的目錄啟動。</summary>
    private static void CreatePrivateProbeFile(string path)
    {
        using (var stream = new FileStream(
                   path,
                   FileMode.CreateNew,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            stream.WriteByte(1);
            stream.Flush(flushToDisk: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>刪除 startup probe，且不讓清理失敗遮蔽原始 permission validation 結果。</summary>
    private static void DeleteProbeFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // probe 不是 key material，清理失敗交由下次啟動或 operator 處理。
        }
        catch (UnauthorizedAccessException)
        {
            // probe 不是 key material，清理失敗不應覆蓋原始讀寫錯誤。
        }
    }
}
