namespace MyExpenses.Api.Services;

/// <summary>Represents an expected financial command failure that can be mapped to HTTP ProblemDetails.</summary>
public sealed class FinancialCommandException : Exception
{
    /// <summary>Creates a command failure with the intended HTTP status code.</summary>
    public FinancialCommandException(int statusCode, string title, string detail)
        : base(detail)
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
    }

    public int StatusCode { get; }
    public string Title { get; }
    public string Detail { get; }
}
