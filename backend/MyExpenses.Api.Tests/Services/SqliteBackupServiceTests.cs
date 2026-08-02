using Microsoft.Data.Sqlite;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class SqliteBackupServiceTests
{
    /// <summary>驗證 backup 能從仍在使用 WAL 的資料庫複製 committed data 並寫入完整 metadata。</summary>
    [Fact]
    public async Task CreateVerifiedBackupAsync_CopiesCommittedWalDataAndPublishesMetadata()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "MyExpenses.db");
        await using var source = await CreateWalDatabaseAsync(databasePath);
        var backupDirectory = Path.Combine(temporaryDirectory.RootPath, "backups");
        var service = CreateService(databasePath, backupDirectory);

        var result = await service.CreateVerifiedBackupAsync("20260802090418_AddAtomicFinancialCommands");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        await using var backup = new SqliteConnection($"Data Source={result.BackupPath};Mode=ReadOnly");
        await backup.OpenAsync();
        await using var command = backup.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Records";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);

        var metadata = await SqliteBackupService.ReadMetadataAsync(result.BackupPath);
        Assert.NotNull(metadata);
        Assert.Equal("20260802090418_AddAtomicFinancialCommands", metadata.MigrationIdentity);
        Assert.Equal("ok", metadata.IntegrityCheck);
        Assert.NotEqual(default, metadata.CreatedAtUtc);
        Assert.NotEqual(default, metadata.VerifiedAtUtc);
    }

    /// <summary>驗證 destination 不可用時 backup 會回報失敗且不會覆寫 destination。</summary>
    [Fact]
    public async Task CreateVerifiedBackupAsync_ReturnsFailureWhenDestinationIsUnavailable()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "MyExpenses.db");
        await using var source = await CreateWalDatabaseAsync(databasePath);
        var blockedDestination = Path.Combine(temporaryDirectory.RootPath, "destination");
        await File.WriteAllTextAsync(blockedDestination, "keep this file");
        var service = CreateService(databasePath, blockedDestination);

        var result = await service.CreateVerifiedBackupAsync("schema-1");

        Assert.False(result.Succeeded);
        Assert.Null(result.BackupPath);
        Assert.Equal("keep this file", await File.ReadAllTextAsync(blockedDestination));
    }

    /// <summary>驗證已發布 backup 檔案不會以 world-readable mode 建立。</summary>
    [Fact]
    public async Task CreateVerifiedBackupAsync_UsesPrivateFilePermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "MyExpenses.db");
        await using var source = await CreateWalDatabaseAsync(databasePath);
        var service = CreateService(databasePath, Path.Combine(temporaryDirectory.RootPath, "backups"));

        var result = await service.CreateVerifiedBackupAsync("schema-1");

        Assert.True(result.Succeeded, result.FailureReason);
        var mode = File.GetUnixFileMode(result.BackupPath!);
        Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherWrite |
            UnixFileMode.OtherExecute));
    }

    /// <summary>驗證新的 backup 失敗時既有 verified backup 仍保持不變。</summary>
    [Fact]
    public async Task CreateVerifiedBackupAsync_PreservesPriorVerifiedBackupWhenNewBackupFails()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "MyExpenses.db");
        await using var source = await CreateWalDatabaseAsync(databasePath);
        var backupDirectory = Path.Combine(temporaryDirectory.RootPath, "backups");
        var service = CreateService(databasePath, backupDirectory);
        var prior = await service.CreateVerifiedBackupAsync("schema-1");
        var priorBytes = await File.ReadAllBytesAsync(prior.BackupPath!);

        var failedService = CreateService(
            Path.Combine(temporaryDirectory.RootPath, "missing", "MyExpenses.db"),
            backupDirectory);
        var failed = await failedService.CreateVerifiedBackupAsync("schema-2");

        Assert.False(failed.Succeeded);
        var backups = Directory.GetFiles(backupDirectory, "*.db");
        Assert.Single(backups);
        Assert.Equal(prior.BackupPath, backups[0]);
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(backups[0]));
    }

    /// <summary>驗證 retention 只保留設定數量且每個保留檔案都已驗證。</summary>
    [Fact]
    public async Task CreateVerifiedBackupAsync_RetainsConfiguredNumberOfVerifiedBackups()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "MyExpenses.db");
        await using var source = await CreateWalDatabaseAsync(databasePath);
        var backupDirectory = Path.Combine(temporaryDirectory.RootPath, "backups");
        var service = CreateService(databasePath, backupDirectory, retentionLimit: 2);

        await service.CreateVerifiedBackupAsync("schema-1");
        await service.CreateVerifiedBackupAsync("schema-2");
        await service.CreateVerifiedBackupAsync("schema-3");

        var backups = Directory.GetFiles(backupDirectory, "*.db");
        Assert.Equal(2, backups.Length);
        foreach (var backupPath in backups)
        {
            var metadata = await SqliteBackupService.ReadMetadataAsync(backupPath);
            Assert.NotNull(metadata);
            Assert.Equal("ok", metadata.IntegrityCheck);
        }
    }

    /// <summary>驗證無效的新 backup 不會觸發 retention 而刪除既有 verified backups。</summary>
    [Fact]
    public async Task CreateVerifiedBackupAsync_DoesNotRotateWhenNewBackupIsInvalid()
    {
        await using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.RootPath, "MyExpenses.db");
        await using var source = await CreateWalDatabaseAsync(databasePath);
        var backupDirectory = Path.Combine(temporaryDirectory.RootPath, "backups");
        var service = CreateService(databasePath, backupDirectory, retentionLimit: 2);
        await service.CreateVerifiedBackupAsync("schema-1");
        await service.CreateVerifiedBackupAsync("schema-2");
        var priorBackups = Directory.GetFiles(backupDirectory, "*.db").Order().ToArray();

        var failedService = CreateService(
            Path.Combine(temporaryDirectory.RootPath, "missing", "MyExpenses.db"),
            backupDirectory,
            retentionLimit: 1);
        var failed = await failedService.CreateVerifiedBackupAsync("schema-invalid");

        Assert.False(failed.Succeeded);
        Assert.Equal(priorBackups, Directory.GetFiles(backupDirectory, "*.db").Order());
    }

    /// <summary>建立測試用 backup service 並套用指定 retention 設定。</summary>
    private static SqliteBackupService CreateService(
        string databasePath,
        string backupDirectory,
        int retentionLimit = 7)
        => new(new SqliteBackupOptions
        {
            DatabasePath = databasePath,
            BackupDirectory = backupDirectory,
            RetentionLimit = retentionLimit,
        });

    /// <summary>建立保持開啟的 WAL SQLite database，模擬線上應用程式的使用狀態。</summary>
    private static async Task<SqliteConnection> CreateWalDatabaseAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Cache=Shared");
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
            await pragma.ExecuteNonQueryAsync();
        }

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "CREATE TABLE Records (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);";
            await schema.ExecuteNonQueryAsync();
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO Records (Value) VALUES ('committed in wal');";
            await insert.ExecuteNonQueryAsync();
        }

        return connection;
    }

    /// <summary>提供每個測試獨立且自動清理的暫存目錄。</summary>
    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        /// <summary>建立測試暫存目錄。</summary>
        public TemporaryDirectory()
        {
            RootPath = Directory.CreateTempSubdirectory("myexpenses-backup-tests-").FullName;
        }

        /// <summary>取得測試暫存目錄的絕對路徑。</summary>
        public string RootPath { get; }

        /// <summary>刪除測試產生的所有資料。</summary>
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
