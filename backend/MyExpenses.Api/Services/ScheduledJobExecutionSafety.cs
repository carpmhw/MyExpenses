namespace MyExpenses.Api.Services;

/// <summary>集中限制 execution result code 與使用者可見安全摘要的內容。</summary>
public static class ScheduledJobExecutionSafety
{
    private static readonly string[] SensitiveMarkers =
    [
        "http://",
        "https://",
        "authorization",
        "cookie",
        "header",
        "payload",
        "redirect",
        "location:",
        "connection string",
        "token",
        "password",
        "secret",
        "stack trace",
    ];

    /// <summary>清理 bounded machine-readable result code。</summary>
    public static string? SanitizeResultCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return normalized.Length <= 80
            && normalized.All(character => char.IsLetterOrDigit(character) || character is '_' or '-')
            ? normalized
            : null;
    }

    /// <summary>清理不應包含技術細節或敏感標記的使用者可見摘要。</summary>
    public static string? SanitizeSafeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (SensitiveMarkers.Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return "排程結果摘要已省略技術細節";
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }
}
