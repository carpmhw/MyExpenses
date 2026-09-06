using System.Data;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;

namespace MyExpenses.Api.Services;

/// <summary>Checks legacy installment data before applying schedule integrity migrations.</summary>
public static class InstallmentIntegrityPreflight
{
    /// <summary>Rejects duplicate installment periods without modifying any financial rows.</summary>
    public static async Task ValidateAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(db, "InstallmentPayments", cancellationToken))
            return;

        var duplicate = await db.InstallmentPayments
            .AsNoTracking()
            .GroupBy(payment => new { payment.InstallmentId, payment.Period })
            .Where(group => group.Count() > 1)
            .Select(group => new { group.Key.InstallmentId, group.Key.Period })
            .OrderBy(group => group.InstallmentId)
            .ThenBy(group => group.Period)
            .FirstOrDefaultAsync(cancellationToken);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Installment {duplicate.InstallmentId} contains duplicate payment period {duplicate.Period}. " +
                "Create a verified database backup and reconcile the duplicate financial rows before migrating.");
        }
    }

    /// <summary>Checks SQLite metadata without assuming the application schema has been created.</summary>
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
}
