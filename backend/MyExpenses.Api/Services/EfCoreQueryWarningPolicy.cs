using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MyExpenses.Api.Services;

/// <summary>集中管理 EF Core Query 10103 的非 Production 診斷政策。</summary>
public static class EfCoreQueryWarningPolicy
{
    /// <summary>在非 Production 將無條件 First 查詢警告升級為例外。</summary>
    public static void Configure(DbContextOptionsBuilder options, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (isProduction)
            return;

        options.ConfigureWarnings(warnings =>
            warnings.Throw(CoreEventId.FirstWithoutOrderByAndFilterWarning));
    }
}
