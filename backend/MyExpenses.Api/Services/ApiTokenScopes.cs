using System.Text.Json;

namespace MyExpenses.Api.Services;

public static class ApiTokenScopes
{
    public const string TransactionsRead = "transactions:read";
    public const string TransactionsWrite = "transactions:write";
    public const string TransactionsDelete = "transactions:delete";
    public const string TransactionsUndo = "transactions:undo";
    public const string CategoriesRead = "categories:read";
    public const string PaymentMethodsRead = "payment-methods:read";
    public const string ReportsRead = "reports:read";

    /// <summary>Parses serialized API token scopes and fails closed to an empty set on invalid input.</summary>
    public static IReadOnlySet<string> Parse(string? serializedScopes)
    {
        if (string.IsNullOrWhiteSpace(serializedScopes))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            var scopes = JsonSerializer.Deserialize<string[]>(serializedScopes);
            if (scopes is null)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            return scopes
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
