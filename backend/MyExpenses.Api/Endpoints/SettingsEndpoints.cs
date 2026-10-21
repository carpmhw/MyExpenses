using Microsoft.AspNetCore.Mvc;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models.Requests;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class SettingsEndpoints
{
    /// <summary>註冊需要 owner 登入的系統時區與交易預設設定端點。</summary>
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings").RequireAuthorization();

        // 未附 API token scope，維持偏好設定僅供 owner 登入憑證使用。
        group.MapGet("/transaction-defaults", async (
            [FromServices] TransactionDefaultsService defaults,
            CancellationToken cancellationToken) => Results.Ok(await defaults.ReadAsync(cancellationToken)));

        group.MapGet("/timezone", (TimeZoneService timeZoneService) =>
            Results.Ok(new { timeZoneId = timeZoneService.TimeZoneId }));
        group.MapGet("/time-zone", (TimeZoneService timeZoneService) =>
            Results.Ok(new { timeZoneId = timeZoneService.TimeZoneId }));

        group.MapPut("/timezone", UpdateTimeZoneAsync);
        group.MapPut("/time-zone", UpdateTimeZoneAsync);
    }

    /// <summary>Validates, persists, and activates a new system time zone.</summary>
    private static async Task<IResult> UpdateTimeZoneAsync(
        UpdateTimeZoneRequest request,
        AppDbContext db,
        TimeZoneService timeZoneService,
        CancellationToken cancellationToken)
    {
        var updated = await timeZoneService.UpdateAsync(db, request.TimeZoneId, cancellationToken);
        return updated
            ? Results.Ok(new { timeZoneId = timeZoneService.TimeZoneId })
            : Results.BadRequest(new { message = "Unsupported system time zone" });
    }
}
