using System.Globalization;
using Microsoft.Data.Sqlite;
using MyExpenses.Api.Options;

namespace MyExpenses.Api.Services;

/// <summary>描述已通過 SQLite integrity check 的 backup metadata。</summary>
public sealed record SqliteBackupMetadata(
    string BackupPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset VerifiedAtUtc,
    string MigrationIdentity,
    long SourceSchemaVersion,
    string IntegrityCheck);

/// <summary>描述一次 backup 發布操作的結果。</summary>
public sealed record SqliteBackupResult(
    bool Succeeded,
    string? BackupPath,
    SqliteBackupMetadata? Metadata,
    string? FailureReason);

/// <summary>使用 SQLite backup primitive 建立可驗證且可原子發布的 database backup。</summary>
public sealed class SqliteBackupService
{
    private const string BackupFilePrefix = "myexpenses-backup";
    private const string MetadataTableName = "__MyExpensesBackupMetadata";
    private readonly SqliteBackupOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>建立使用指定設定與時間來源的 backup service。</summary>
    public SqliteBackupService(SqliteBackupOptions options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.DatabasePath))
            throw new ArgumentException("SQLite database path is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.BackupDirectory))
            throw new ArgumentException("Backup directory is required.", nameof(options));
        if (_options.RetentionLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Retention limit must be at least one.");

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>建立 live SQLite database 的 consistent backup，驗證後才發布並執行 retention。</summary>
    public async Task<SqliteBackupResult> CreateVerifiedBackupAsync(
        string migrationIdentity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(migrationIdentity))
        {
            return new SqliteBackupResult(
                Succeeded: false,
                BackupPath: null,
                Metadata: null,
                FailureReason: "Migration identity is required.");
        }

        var createdAtUtc = _timeProvider.GetUtcNow();
        var finalPath = BuildBackupPath(createdAtUtc);
        var temporaryPath = BuildTemporaryPath(finalPath);

        try
        {
            Directory.CreateDirectory(_options.BackupDirectory);
            CreatePrivateFile(temporaryPath);

            var sourceConnectionString = BuildConnectionString(_options.DatabasePath, SqliteOpenMode.ReadOnly);
            var destinationConnectionString = BuildConnectionString(temporaryPath, SqliteOpenMode.ReadWriteCreate);
            long sourceSchemaVersion;
            SqliteBackupMetadata metadata;

            await using (var source = new SqliteConnection(sourceConnectionString))
            await using (var destination = new SqliteConnection(destinationConnectionString))
            {
                await source.OpenAsync(cancellationToken);
                await destination.OpenAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                source.BackupDatabase(destination);
                cancellationToken.ThrowIfCancellationRequested();

                sourceSchemaVersion = await ReadSchemaVersionAsync(source, cancellationToken);
                await CreateMetadataTableAsync(
                    destination,
                    createdAtUtc,
                    migrationIdentity,
                    sourceSchemaVersion,
                    cancellationToken);

                var firstIntegrityCheck = await RunIntegrityCheckAsync(destination, cancellationToken);
                if (!string.Equals(firstIntegrityCheck, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"SQLite backup integrity check failed: {firstIntegrityCheck}");
                }

                var verifiedAtUtc = _timeProvider.GetUtcNow();
                await MarkMetadataVerifiedAsync(destination, verifiedAtUtc, cancellationToken);
                var finalIntegrityCheck = await RunIntegrityCheckAsync(destination, cancellationToken);
                if (!string.Equals(finalIntegrityCheck, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"SQLite backup integrity check failed after metadata write: {finalIntegrityCheck}");
                }

                metadata = new SqliteBackupMetadata(
                    BackupPath: finalPath,
                    CreatedAtUtc: createdAtUtc,
                    VerifiedAtUtc: verifiedAtUtc,
                    MigrationIdentity: migrationIdentity,
                    SourceSchemaVersion: sourceSchemaVersion,
                    IntegrityCheck: finalIntegrityCheck);

                await destination.CloseAsync();
                await source.CloseAsync();
            }

            FlushToDisk(temporaryPath);
            File.Move(temporaryPath, finalPath, overwrite: false);
            SetPrivateFilePermissions(finalPath);

            try
            {
                await RetainVerifiedBackupsAsync(cancellationToken);
            }
            catch (Exception retentionException) when (retentionException is not OperationCanceledException)
            {
                return new SqliteBackupResult(
                    Succeeded: false,
                    BackupPath: finalPath,
                    Metadata: metadata,
                    FailureReason: $"Backup published but retention cleanup failed: {retentionException.Message}");
            }

            return new SqliteBackupResult(
                Succeeded: true,
                BackupPath: finalPath,
                Metadata: metadata,
                FailureReason: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SqliteBackupResult(
                Succeeded: false,
                BackupPath: null,
                Metadata: null,
                FailureReason: exception.Message);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    /// <summary>讀取 backup 內嵌的 metadata，供 retention 與後續 restore 驗證重用。</summary>
    public static async Task<SqliteBackupMetadata?> ReadMetadataAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
            return null;

        await using var connection = new SqliteConnection(
            BuildConnectionString(backupPath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(cancellationToken);

        await using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            tableCommand.Parameters.AddWithValue("$name", MetadataTableName);
            if (Convert.ToInt32(await tableCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
                return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT CreatedAtUtc, VerifiedAtUtc, MigrationIdentity, SourceSchemaVersion, IntegrityCheck FROM [{MetadataTableName}] WHERE Id = 1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var createdAtUtc = DateTimeOffset.Parse(
            reader.GetString(0),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var verifiedAtUtc = DateTimeOffset.Parse(
            reader.GetString(1),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        return new SqliteBackupMetadata(
            BackupPath: backupPath,
            CreatedAtUtc: createdAtUtc,
            VerifiedAtUtc: verifiedAtUtc,
            MigrationIdentity: reader.GetString(2),
            SourceSchemaVersion: reader.GetInt64(3),
            IntegrityCheck: reader.GetString(4));
    }

    /// <summary>建立帶有隨機尾碼的最終 backup 路徑，避免相同時間的操作互相覆寫。</summary>
    private string BuildBackupPath(DateTimeOffset createdAtUtc)
    {
        var timestamp = createdAtUtc.UtcDateTime.ToString(
            "yyyyMMdd'T'HHmmssfffffff'Z'",
            CultureInfo.InvariantCulture);
        return Path.Combine(
            _options.BackupDirectory,
            $"{BackupFilePrefix}-{timestamp}-{Guid.NewGuid():N}.db");
    }

    /// <summary>建立與最終檔案位於同一目錄的 temporary output 路徑以支援原子 rename。</summary>
    private static string BuildTemporaryPath(string finalPath)
        => $"{finalPath}.{Guid.NewGuid():N}.tmp";

    /// <summary>建立 SQLite connection string 並關閉 pooling 以避免 temporary 檔案被鎖定。</summary>
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

    /// <summary>讀取來源 database 的 SQLite schema version 作為額外 recovery metadata。</summary>
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

    /// <summary>在 temporary backup 內寫入建立時間、migration identity 與待驗證 metadata。</summary>
    private static async Task CreateMetadataTableAsync(
        SqliteConnection connection,
        DateTimeOffset createdAtUtc,
        string migrationIdentity,
        long sourceSchemaVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS [{MetadataTableName}] (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                CreatedAtUtc TEXT NOT NULL,
                VerifiedAtUtc TEXT NOT NULL DEFAULT '',
                MigrationIdentity TEXT NOT NULL,
                SourceSchemaVersion INTEGER NOT NULL,
                IntegrityCheck TEXT NOT NULL
            );
            DELETE FROM [{MetadataTableName}];
            INSERT INTO [{MetadataTableName}]
                (Id, CreatedAtUtc, VerifiedAtUtc, MigrationIdentity, SourceSchemaVersion, IntegrityCheck)
            VALUES (1, $created_at, '', $migration_identity, $schema_version, 'pending');
            """;
        command.Parameters.AddWithValue("$created_at", createdAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$migration_identity", migrationIdentity);
        command.Parameters.AddWithValue("$schema_version", sourceSchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>執行 SQLite integrity check 並合併所有回傳列供呼叫端判斷。</summary>
    private static async Task<string> RunIntegrityCheckAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            results.Add(reader.GetString(0));
        return string.Join("; ", results);
    }

    /// <summary>將 integrity check 成功與 verified timestamp 寫入 backup metadata。</summary>
    private static async Task MarkMetadataVerifiedAsync(
        SqliteConnection connection,
        DateTimeOffset verifiedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE [{MetadataTableName}] SET VerifiedAtUtc = $verified_at, IntegrityCheck = 'ok' WHERE Id = 1";
        command.Parameters.AddWithValue("$verified_at", verifiedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>只保留最新的 verified backups，且不處理沒有成功 metadata 的檔案。</summary>
    private async Task RetainVerifiedBackupsAsync(CancellationToken cancellationToken)
    {
        var verifiedBackups = new List<SqliteBackupMetadata>();
        foreach (var backupPath in Directory.EnumerateFiles(
                     _options.BackupDirectory,
                     $"{BackupFilePrefix}-*.db",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                var metadata = await ReadMetadataAsync(backupPath, cancellationToken);
                if (metadata is not null &&
                    string.Equals(metadata.IntegrityCheck, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    verifiedBackups.Add(metadata);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 無法讀取的檔案不屬於 verified set，保留它以避免清理動作破壞 recovery point。
            }
        }

        var filesToDelete = verifiedBackups
            .OrderByDescending(metadata => metadata.VerifiedAtUtc)
            .ThenByDescending(metadata => metadata.BackupPath, StringComparer.Ordinal)
            .Skip(_options.RetentionLimit)
            .Select(metadata => metadata.BackupPath)
            .ToArray();

        foreach (var backupPath in filesToDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(backupPath);
        }
    }

    /// <summary>建立 temporary SQLite 檔案並立即限制為 owner read/write。</summary>
    private static void CreatePrivateFile(string path)
    {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 1))
        {
        }
        SetPrivateFilePermissions(path);
    }

    /// <summary>在支援 Unix mode 的平台移除 group 與 other 的所有檔案權限。</summary>
    private static void SetPrivateFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>將 temporary SQLite 檔案內容 flush 至磁碟後才進行 atomic publication。</summary>
    private static void FlushToDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>清理失敗操作留下的 temporary output，且不影響既有 verified backups。</summary>
    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // 清理失敗不應遮蔽原始 backup 失敗，也不應刪除任何既有 backup。
        }
        catch (UnauthorizedAccessException)
        {
            // 權限錯誤同樣只保留給 operator 處理，不改動既有 recovery point。
        }
    }
}
