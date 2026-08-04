using System.Globalization;
using Microsoft.Data.Sqlite;
using MyExpenses.Api.Options;

namespace MyExpenses.Api.Services;

/// <summary>描述一次離線 SQLite restore 操作的結果。</summary>
public sealed record SqliteRestoreResult(
    bool Succeeded,
    string ActiveDatabasePath,
    string? RollbackPath,
    SqliteBackupMetadata? Metadata,
    string? FailureReason);

/// <summary>驗證 backup 並以 rollback copy 與 atomic replacement 還原 SQLite database。</summary>
public sealed class SqliteRestoreService
{
    private const string EfMigrationsTableName = "__EFMigrationsHistory";
    private readonly SqliteBackupOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>建立使用指定 SQLite 路徑與時間來源的 restore service。</summary>
    public SqliteRestoreService(SqliteBackupOptions options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.DatabasePath))
            throw new ArgumentException("SQLite database path is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.BackupDirectory))
            throw new ArgumentException("Backup directory is required.", nameof(options));

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>驗證選定 backup、保留 current rollback copy 並 atomic replace active database。</summary>
    public async Task<SqliteRestoreResult> RestoreVerifiedBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var activeDatabasePath = Path.GetFullPath(_options.DatabasePath);
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return Failure(activeDatabasePath, null, null, "Backup path is required.");
        }

        var selectedBackupPath = Path.GetFullPath(backupPath);
        if (string.Equals(activeDatabasePath, selectedBackupPath, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(activeDatabasePath, null, null, "Backup path must differ from the active database path.");
        }

        var temporaryOutputPath = BuildTemporaryPath(activeDatabasePath, "restore");
        var rollbackTemporaryPath = (string?)null;
        var rollbackPath = (string?)null;
        SqliteBackupMetadata? metadata = null;
        var stagedSidecars = new List<(string OriginalPath, string StagedPath)>();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(activeDatabasePath)!);
            metadata = await ValidateBackupAsync(selectedBackupPath, cancellationToken);

            await CopyDatabaseAsync(selectedBackupPath, temporaryOutputPath, cancellationToken);
            var copiedMetadata = await ValidateBackupAsync(temporaryOutputPath, cancellationToken);
            if (!string.Equals(copiedMetadata.MigrationIdentity, metadata.MigrationIdentity, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Temporary restore output metadata does not match the selected backup.");
            }

            FlushToDisk(temporaryOutputPath);
            SetPrivateFilePermissions(temporaryOutputPath);

            if (File.Exists(activeDatabasePath))
            {
                rollbackPath = BuildRollbackPath(activeDatabasePath);
                rollbackTemporaryPath = BuildTemporaryPath(rollbackPath, "rollback");
                await CopyDatabaseAsync(activeDatabasePath, rollbackTemporaryPath, cancellationToken);
                await VerifyIntegrityAsync(rollbackTemporaryPath, cancellationToken);
                FlushToDisk(rollbackTemporaryPath);
                SetPrivateFilePermissions(rollbackTemporaryPath);
                File.Move(rollbackTemporaryPath, rollbackPath, overwrite: false);
                rollbackTemporaryPath = null;
                stagedSidecars = StageSqliteSidecars(activeDatabasePath, rollbackPath);
            }

            ReplaceAtomically(temporaryOutputPath, activeDatabasePath);
            temporaryOutputPath = string.Empty;

            return new SqliteRestoreResult(
                Succeeded: true,
                ActiveDatabasePath: activeDatabasePath,
                RollbackPath: rollbackPath,
                Metadata: metadata,
                FailureReason: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestoreStagedSidecars(stagedSidecars);
            throw;
        }
        catch (Exception exception)
        {
            RestoreStagedSidecars(stagedSidecars);
            return Failure(activeDatabasePath, rollbackPath, metadata, exception.Message);
        }
        finally
        {
            DeleteTemporaryFile(temporaryOutputPath);
            DeleteTemporaryFile(rollbackTemporaryPath);
        }
    }

    /// <summary>驗證 backup 的 SQLite integrity、嵌入 metadata、schema version 與 EF migration history。</summary>
    private static async Task<SqliteBackupMetadata> ValidateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("Selected backup does not exist.", backupPath);

        var metadata = await SqliteBackupService.ReadMetadataAsync(backupPath, cancellationToken)
            ?? throw new InvalidDataException("Selected backup is missing embedded metadata.");
        if (!string.Equals(metadata.IntegrityCheck, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Selected backup metadata is not verified.");
        if (metadata.CreatedAtUtc == default || metadata.VerifiedAtUtc == default ||
            metadata.VerifiedAtUtc < metadata.CreatedAtUtc)
        {
            throw new InvalidDataException("Selected backup metadata timestamps are invalid.");
        }
        if (string.IsNullOrWhiteSpace(metadata.MigrationIdentity) || metadata.SourceSchemaVersion < 0)
            throw new InvalidDataException("Selected backup metadata schema identity is invalid.");

        await using var connection = new SqliteConnection(BuildConnectionString(backupPath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(cancellationToken);
        await VerifyIntegrityAsync(connection, cancellationToken);

        var schemaVersion = await ReadSchemaVersionAsync(connection, cancellationToken);
        if (schemaVersion < 1)
            throw new InvalidDataException("Selected backup does not contain a valid SQLite schema.");

        var latestMigration = await ReadLatestMigrationAsync(connection, cancellationToken);
        if (!string.Equals(latestMigration, metadata.MigrationIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Selected backup migration metadata does not match the embedded EF migration history.");
        }

        return metadata;
    }

    /// <summary>使用 SQLite backup primitive 將來源 database 複製到 temporary output。</summary>
    private static async Task CopyDatabaseAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        CreatePrivateFile(destinationPath);
        try
        {
            await using var source = new SqliteConnection(BuildConnectionString(sourcePath, SqliteOpenMode.ReadOnly));
            await using var destination = new SqliteConnection(
                BuildConnectionString(destinationPath, SqliteOpenMode.ReadWriteCreate));
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            source.BackupDatabase(destination);
            cancellationToken.ThrowIfCancellationRequested();
            await destination.CloseAsync();
            await source.CloseAsync();
        }
        catch
        {
            DeleteTemporaryFile(destinationPath);
            throw;
        }
    }

    /// <summary>對指定 SQLite connection 執行 integrity check，失敗時中止 restore。</summary>
    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            results.Add(reader.GetString(0));

        var result = string.Join("; ", results);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite integrity check failed: {result}");
    }

    /// <summary>以唯讀 connection 驗證指定 temporary database 的 integrity。</summary>
    private static async Task VerifyIntegrityAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(cancellationToken);
        await VerifyIntegrityAsync(connection, cancellationToken);
    }

    /// <summary>讀取 SQLite schema version，確認 metadata 指向的 schema 仍存在。</summary>
    private static async Task<long> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA schema_version";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    /// <summary>讀取 backup 內嵌 EF migration history 的最新 migration identity。</summary>
    private static async Task<string?> ReadLatestMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        tableCommand.Parameters.AddWithValue("$name", EfMigrationsTableName);
        if (Convert.ToInt32(
                await tableCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidDataException("Selected backup is missing EF migration history.");
        }

        await using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText =
            $"SELECT MigrationId FROM [{EfMigrationsTableName}] ORDER BY MigrationId DESC LIMIT 1";
        return (string?)await migrationCommand.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>建立同一檔案系統中的 rollback path，避免跨 volume rename 失去 atomic 性質。</summary>
    private string BuildRollbackPath(string activeDatabasePath)
    {
        var timestamp = _timeProvider.GetUtcNow().UtcDateTime.ToString(
            "yyyyMMdd'T'HHmmssfffffff'Z'",
            CultureInfo.InvariantCulture);
        return $"{activeDatabasePath}.rollback-{timestamp}-{Guid.NewGuid():N}.db";
    }

    /// <summary>建立與 target 位於同一目錄的 temporary path。</summary>
    private static string BuildTemporaryPath(string targetPath, string operation)
        => $"{targetPath}.{operation}-{Guid.NewGuid():N}.tmp";

    /// <summary>建立 SQLite connection string 並關閉 pooling，避免 temporary 檔案殘留鎖定。</summary>
    private static string BuildConnectionString(string databasePath, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        return builder.ToString();
    }

    /// <summary>建立 rollback 前暫存的檔案並限制為 owner read/write。</summary>
    private static void CreatePrivateFile(string path)
    {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 1))
        {
        }
        SetPrivateFilePermissions(path);
    }

    /// <summary>在支援 Unix mode 的平台移除 group 與 other 的檔案權限。</summary>
    private static void SetPrivateFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>將 temporary database flush 至磁碟後才進行 atomic replacement。</summary>
    private static void FlushToDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>在同一檔案系統內以 rename 或 replace 完成 active database 的 atomic publication。</summary>
    private static void ReplaceAtomically(string temporaryPath, string activePath)
    {
        if (File.Exists(activePath))
            File.Replace(temporaryPath, activePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(temporaryPath, activePath, overwrite: false);
    }

    /// <summary>暫存 active database 的 WAL 與 shared-memory sidecar，避免污染 restored database。</summary>
    private static List<(string OriginalPath, string StagedPath)> StageSqliteSidecars(
        string activeDatabasePath,
        string rollbackPath)
    {
        var stagedSidecars = new List<(string OriginalPath, string StagedPath)>();
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var originalPath = activeDatabasePath + suffix;
            if (!File.Exists(originalPath))
                continue;

            var stagedPath = rollbackPath + suffix;
            try
            {
                File.Move(originalPath, stagedPath, overwrite: false);
                stagedSidecars.Add((originalPath, stagedPath));
            }
            catch
            {
                RestoreStagedSidecars(stagedSidecars);
                throw;
            }
        }

        return stagedSidecars;
    }

    /// <summary>atomic replacement 失敗時將 sidecar 還原到 active database 路徑。</summary>
    private static void RestoreStagedSidecars(
        IReadOnlyList<(string OriginalPath, string StagedPath)> stagedSidecars)
    {
        for (var index = stagedSidecars.Count - 1; index >= 0; index--)
        {
            var (originalPath, stagedPath) = stagedSidecars[index];
            try
            {
                if (File.Exists(stagedPath) && !File.Exists(originalPath))
                    File.Move(stagedPath, originalPath, overwrite: false);
            }
            catch (IOException)
            {
                // sidecar 恢復失敗不能覆蓋原始 restore 錯誤，保留檔案供 operator 處理。
            }
            catch (UnauthorizedAccessException)
            {
                // 權限錯誤同樣只保留給 operator 處理，不再嘗試修改 active database。
            }
        }
    }

    /// <summary>清理失敗操作留下的 temporary 檔案，不影響 active 或 rollback copy。</summary>
    private static void DeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // 清理失敗不應遮蔽原始 restore 失敗。
        }
        catch (UnauthorizedAccessException)
        {
            // 權限錯誤交由 operator 清理，不修改 active database。
        }
    }

    /// <summary>建立不含 secrets 的 restore 失敗結果。</summary>
    private static SqliteRestoreResult Failure(
        string activeDatabasePath,
        string? rollbackPath,
        SqliteBackupMetadata? metadata,
        string reason)
        => new(
            Succeeded: false,
            ActiveDatabasePath: activeDatabasePath,
            RollbackPath: rollbackPath,
            Metadata: metadata,
            FailureReason: reason);
}
