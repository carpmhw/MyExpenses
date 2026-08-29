using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

/// <summary>提供受認證保護的 TWD 基準匯率查詢端點。</summary>
public static class ExchangeRateEndpoints
{
    /// <summary>註冊只負責映射共用匯率服務結果的 HTTP endpoint。</summary>
    public static void MapExchangeRateEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/exchange-rates").RequireAuthorization();

        group.MapGet("/", async (IExchangeRateService exchangeRateService, CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await exchangeRateService.GetSnapshotAsync(cancellationToken);
                return Results.Ok(new ExchangeRateApiResponse(
                    snapshot.BaseCurrencyCode,
                    snapshot.Rates,
                    snapshot.UpdatedAtUtc,
                    snapshot.IsStale,
                    snapshot.IsStale ? "使用過期快取資料" : null));
            }
            catch (ExchangeRateUnavailableException exception)
            {
                return Results.Problem(
                    detail: exception.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}

/// <summary>匯率 API 的穩定 response contract。</summary>
public sealed record ExchangeRateApiResponse(
    string Base,
    IReadOnlyDictionary<string, decimal> Rates,
    DateTime UpdatedAt,
    bool IsStale,
    string? Warning);
