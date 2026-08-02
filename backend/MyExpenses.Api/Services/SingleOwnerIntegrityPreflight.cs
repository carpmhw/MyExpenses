using System.Data;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;

namespace MyExpenses.Api.Services;

/// <summary>在 migration 前確認既有資料庫仍符合單一 owner invariant。</summary>
public static class SingleOwnerIntegrityPreflight
{
    /// <summary>拒絕多 owner legacy database，且不修改任何既有資料。</summary>
    public static async Task ValidateAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(db, "Users", cancellationToken))
            return;

        var ownerCount = await GetUserCountAsync(db, cancellationToken);
        if (ownerCount > 1)
        {
            throw new InvalidOperationException(
                $"Database contains more than one owner ({ownerCount} users). " +
                "Create a verified database backup and reconcile the legacy users before migrating.");
        }
    }

    /// <summary>透過 SQLite metadata 檢查 Users table 是否已存在。</summary>
    private static async Task<bool> TableExistsAsync(
        AppDbContext db,
        string tableName,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    /// <summary>讀取 Users row count，讓 startup 在 migration 前判斷是否已初始化。</summary>
    public static async Task<int> GetUserCountAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(db, "Users", cancellationToken))
            return 0;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Users";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }
}
