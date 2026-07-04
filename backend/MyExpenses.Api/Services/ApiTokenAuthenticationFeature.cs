namespace MyExpenses.Api.Services;

public static class ApiTokenAuthenticationFeature
{
    private const string ItemKey = "MyExpenses.AuthenticatedWithApiToken";
    private const string ScopesItemKey = "MyExpenses.ApiTokenScopes";

    /// <summary>Marks the request as authenticated by a verified API token and stores its scopes.</summary>
    public static void MarkAuthenticated(HttpContext context, IEnumerable<string> scopes)
    {
        context.Items[ItemKey] = true;
        context.Items[ScopesItemKey] = scopes.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Returns whether the request was authenticated by a verified API token.</summary>
    public static bool IsAuthenticated(HttpContext context)
    {
        return context.Items.TryGetValue(ItemKey, out var value) && value is true;
    }

    /// <summary>Returns whether the verified API token includes the required scope.</summary>
    public static bool HasScope(HttpContext context, string requiredScope)
    {
        return context.Items.TryGetValue(ScopesItemKey, out var value)
            && value is IReadOnlySet<string> scopes
            && scopes.Contains(requiredScope);
    }
}
