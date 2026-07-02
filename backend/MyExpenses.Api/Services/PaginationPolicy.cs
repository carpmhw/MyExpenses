namespace MyExpenses.Api.Services;

public static class PaginationPolicy
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>Returns a safe one-based page number for list endpoints.</summary>
    public static int NormalizePage(int? page)
    {
        return page is > 0 ? page.Value : DefaultPage;
    }

    /// <summary>Returns a safe page size bounded by the default and maximum list sizes.</summary>
    public static int NormalizePageSize(int? pageSize)
    {
        if (pageSize is not > 0)
        {
            return DefaultPageSize;
        }

        return Math.Min(pageSize.Value, MaxPageSize);
    }

    /// <summary>Returns a safe item limit for optional limit query parameters.</summary>
    public static int? NormalizeLimit(int? limit)
    {
        return limit.HasValue ? NormalizePageSize(limit.Value) : null;
    }
}
