using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class ConsumptionEndpoints
{
    /// <summary>映射跨普通交易與信用卡消費的唯讀查詢 endpoint。</summary>
    public static void MapConsumptionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/consumption", async (
            DateOnly? startDate,
            DateOnly? endDate,
            string? source,
            int? categoryId,
            string? search,
            int? page,
            int? pageSize,
            ConsumptionQueryService queryService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await queryService.QueryAsync(
                    startDate,
                    endDate,
                    source,
                    categoryId,
                    search,
                    page,
                    pageSize,
                    cancellationToken));
            }
            catch (FinancialCommandException exception)
            {
                return Results.Problem(
                    statusCode: exception.StatusCode,
                    title: exception.Title,
                    detail: exception.Detail);
            }
        }).RequireApiTokenScope(ApiTokenScopes.TransactionsRead);
    }
}
