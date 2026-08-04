using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;

namespace MyExpenses.Api.Services;

/// <summary>依序執行 database preflight、recovery point、migration、seed 與 readiness。</summary>
public sealed class DatabaseStartupCoordinator : IStartupReadiness
{
    private readonly SqliteBackupService _backupService;

    /// <summary>建立使用指定 SQLite backup service 的 startup coordinator。</summary>
    public DatabaseStartupCoordinator(SqliteBackupService backupService)
    {
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
    }

    /// <summary>表示 startup 的 migration 與初始化階段是否已全部成功完成。</summary>
    public bool IsReady { get; private set; }

    /// <summary>在任何資料變更前執行 preflight，並在既有資料庫 migration 前建立 verified backup。</summary>
    public async Task InitializeAsync(
        AppDbContext db,
        Func<AppDbContext, CancellationToken, Task> seedAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(seedAsync);
        IsReady = false;

        await SingleOwnerIntegrityPreflight.ValidateAsync(db, cancellationToken);
        await InstallmentIntegrityPreflight.ValidateAsync(db, cancellationToken);

        var existingDatabase = IsExistingDatabaseFile(db);
        var pendingMigrations = (await db.Database
            .GetPendingMigrationsAsync(cancellationToken))
            .ToArray();

        if (existingDatabase && pendingMigrations.Length > 0)
        {
            var appliedMigrations = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
            var sourceMigration = appliedMigrations.LastOrDefault() ?? "empty-database";
            var backup = await _backupService.CreateVerifiedBackupAsync(sourceMigration, cancellationToken);
            if (!backup.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Pre-migration database backup failed; migrations were not applied. {backup.FailureReason}");
            }
        }

        await db.Database.MigrateAsync(cancellationToken);
        await seedAsync(db, cancellationToken);
        IsReady = true;
    }

    /// <summary>在查詢 pending migrations 前判斷 SQLite 檔案是否原本已存在。</summary>
    private static bool IsExistingDatabaseFile(AppDbContext db)
    {
        var dataSource = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) ||
            string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return File.Exists(Path.GetFullPath(dataSource));
    }
}
