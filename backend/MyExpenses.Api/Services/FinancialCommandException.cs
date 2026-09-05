namespace MyExpenses.Api.Services;

/// <summary>Represents an expected financial command failure that can be mapped to HTTP ProblemDetails.</summary>
public sealed class FinancialCommandException : Exception
{
    /// <summary>建立帶有預期 HTTP 狀態碼與機器可讀錯誤碼的命令失敗。</summary>
    public FinancialCommandException(int statusCode, string title, string detail, string? code = null)
        : base(detail)
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        Code = code;
    }

    public int StatusCode { get; }
    public string Title { get; }
    public string Detail { get; }
    public string? Code { get; }
}
